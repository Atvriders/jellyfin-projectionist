// Reference model of how jellyfin-web 10.11.11 builds its player URLs - test-only, not shipped.
//
// This is an independent re-implementation of the OBSERVED BEHAVIOUR of jellyfin-web 10.11.11
// (createStreamInfo in its playback manager) and of the jellyfin-apiclient 1.11.0 getUrl it calls.
// It is not a copy of either project's code: the behaviour was first written down as the
// specification below (from reading the released code and from URLs captured from a real
// jellyfin-web 10.11.11 player), and the code underneath was then written from that
// specification alone. The only names shared with them are the interface the hook calls
// (ApiClient, getUrl, deviceId, accessToken) and the names that appear on the wire (URL path and
// query names, Jellyfin's MediaSource fields). It is part of Projectionist and MIT-licensed like
// the rest of this repository.
//
// The tests compare playback-hook.js against this model, so the model has to stay true to the
// real client. Two checks keep it honest (tests/js/hook-helpers.test.mjs):
//   - fixtures/jellyfin-10.11.11-directplay.capture.json: a URL the real player requested;
//     the model has to reproduce it exactly;
//   - "reference model follows its specification": assertions for the rules below.
// When the supported jellyfin-web version changes, re-derive the rules from the new release,
// update the specification and the code, and capture a new URL.
//
// ================================================================== SPECIFICATION
//
// [G] apiClient.getUrl(name, params?, serverAddress?) -> string
//   G1  An empty/missing `name` is an error (throws).
//   G2  The base is the `serverAddress` argument when given (truthy), else the client's own
//       server address. No base at all is an error (throws).
//   G3  The base is used as it is: not parsed, not trimmed, a trailing '/' is kept.
//   G4  Joining: when `name` starts with '/', base and name are concatenated directly;
//       otherwise exactly one '/' is put between them - also when the base already ends in '/',
//       which gives '//' (a server address saved with a trailing slash).
//   G5  `name` is inserted unchanged: not encoded, not parsed. A TranscodingUrl, which already
//       carries its own '?query', therefore passes through intact when no params are given.
//   G6  Query string, only when `params` is given:
//         - the params' enumerable keys, in for-in enumeration order (for the plain objects used
//           here: insertion order);
//         - an entry whose value is null, undefined or '' is left out; every other value is kept,
//           including false, 0 and true (serialised as 'false', '0', 'true');
//         - each entry is encodeURIComponent(key) + '=' + encodeURIComponent(value), so '/', '+',
//           '=', '&', '|', ' ' and non-ASCII are percent-encoded and space is '%20';
//         - entries are joined with '&'.
//   G7  '?' + query is appended only when the query string is non-empty; no bare '?'.
//
// [S] The player URL for one MediaSource (createStreamInfo) -> { url, mimeType, playMethod }
//   S1  Only item types 'Video' and 'Audio' use S2-S4. Any other type plays mediaSource.Path
//       directly (playMethod 'DirectPlay', no mime type). S5 applies to both.
//   S2  container = (mediaSource.Container, or '' when missing), lower-cased. It is used as it
//       is otherwise - a probe-style list such as 'mov,mp4,m4a,3gp,3g2,mj2' lands unchanged in the
//       path. mimeType starts as '<type lower-cased>/<container>'; playMethod starts as 'Transcode'.
//   S3  The first matching case decides the URL:
//         a. mediaSource.enableDirectPlay (a flag the client itself sets when it can open Path
//            on its own - an Http source it can reach; false for files on the server):
//            url = mediaSource.Path, playMethod 'DirectPlay';
//         b. mediaSource.StreamUrl: url = StreamUrl, playMethod stays 'Transcode';
//         c. SupportsDirectPlay or SupportsDirectStream: the static stream URL of S4, playMethod
//            'DirectPlay' when SupportsDirectPlay, else 'DirectStream';
//         d. SupportsTranscoding: url = getUrl(mediaSource.TranscodingUrl) with no params (so
//            only G4's joining applies). mimeType becomes 'application/x-mpegURL' when
//            TranscodingSubProtocol is exactly 'hls', else '<type lower-cased>/<TranscodingContainer>';
//         e. none: no URL yet.
//   S4  Static stream URL = getUrl(path, params) with
//         path   = '<Prefix>/<item.Id>/stream.<container>', Prefix 'Videos' for Video and 'Audio'
//                  for Audio (capitalised as written); item.Id is inserted verbatim - no dash
//                  removal, no case change (the server sends 32 lower-case hex digits);
//         params, in exactly this order (names are case-sensitive):
//                  Static        = true
//                  mediaSourceId = mediaSource.Id, verbatim (may be dashed or non-hex)
//                  deviceId      = apiClient.deviceId()
//                  ApiKey        = apiClient.accessToken()
//                  Tag           = mediaSource.ETag          - only when ETag is truthy
//                  LiveStreamId  = mediaSource.LiveStreamId  - only when it is truthy
//         (G6 then drops a missing device id or token; an empty ETag never appears as 'Tag=').
//   S5  Fallback: when S3 produced no URL and SupportsDirectPlay is set, url = Path, 'DirectPlay'.
//       (With S3 as it is, this cannot change a result; it is kept so the model stays complete.)
//   S6  Out of scope here (no effect on the URL): the subtitle DeliveryUrl rewriting for
//       players with useFullSubtitleUrls, and the transcoding offset ticks for non-HLS streams.
//
// ================================================================== IMPLEMENTATION

export const JELLYFIN_WEB_VERSION = '10.11.11';

const DROPPED_VALUES = [null, undefined, ''];

/** G6: the query string for `params` (without the leading '?'). */
export function encodeQuery(params) {
    const entries = [];
    for (const key in params) entries.push([key, params[key]]);
    return entries
        .filter((entry) => !DROPPED_VALUES.includes(entry[1]))
        .map(([key, value]) => `${encodeURIComponent(key)}=${encodeURIComponent(value)}`)
        .join('&');
}

/** The slice of an ApiClient that the hook and the model use: getUrl (G1-G7) and three getters. */
export class ApiClient {
    #server;
    #device;
    #token;

    constructor(serverAddress, deviceId, accessToken) {
        this.#server = serverAddress;
        this.#device = deviceId;
        this.#token = accessToken;
    }

    serverAddress() { return this.#server; }

    deviceId() { return this.#device; }

    accessToken() { return this.#token; }

    getUrl(name, params, serverAddress) {
        if (!name) throw new Error('getUrl: a name is required');                      // G1
        const base = serverAddress || this.#server;                                     // G2, G3
        if (!base) throw new Error('getUrl: no server address');
        const target = String(name).startsWith('/') ? base + name : `${base}/${name}`;  // G4, G5
        const query = params ? encodeQuery(params) : '';                                // G6
        return query === '' ? target : `${target}?${query}`;                            // G7
    }
}

// S4: [query name, value, included?] in emission order.
const STATIC_QUERY = [
    ['Static', () => true, () => true],
    ['mediaSourceId', (api, ms) => ms.Id, () => true],
    ['deviceId', (api) => api.deviceId(), () => true],
    ['ApiKey', (api) => api.accessToken(), () => true],
    ['Tag', (api, ms) => ms.ETag, (ms) => Boolean(ms.ETag)],
    ['LiveStreamId', (api, ms) => ms.LiveStreamId, (ms) => Boolean(ms.LiveStreamId)],
];

function staticStreamUrl(api, type, itemId, container, ms) {
    const query = {};
    for (const [name, value, included] of STATIC_QUERY) {
        if (included(ms)) query[name] = value(api, ms);
    }
    const prefix = type === 'Video' ? 'Videos' : 'Audio';
    return api.getUrl(`${prefix}/${itemId}/stream.${container}`, query);
}

function pickCase(ms) {                                                                // S3 order
    if (ms.enableDirectPlay) return 'local-path';
    if (ms.StreamUrl) return 'stream-url';
    if (ms.SupportsDirectPlay || ms.SupportsDirectStream) return 'static';
    if (ms.SupportsTranscoding) return 'transcode';
    return 'none';
}

/** [S]: what jellyfin-web's player would load for `mediaSource` of `item`. */
export function playerStream(api, type, item, mediaSource) {
    const ms = mediaSource;
    const result = { url: undefined, mimeType: undefined, playMethod: 'DirectPlay' };
    if (type === 'Video' || type === 'Audio') {
        const kind = type.toLowerCase();
        const container = String(ms.Container || '').toLowerCase();                    // S2
        result.mimeType = `${kind}/${container}`;
        result.playMethod = 'Transcode';
        switch (pickCase(ms)) {
            case 'local-path':
                result.url = ms.Path;
                result.playMethod = 'DirectPlay';
                break;
            case 'stream-url':
                result.url = ms.StreamUrl;
                break;
            case 'static':
                result.url = staticStreamUrl(api, type, item.Id, container, ms);
                result.playMethod = ms.SupportsDirectPlay ? 'DirectPlay' : 'DirectStream';
                break;
            case 'transcode':
                result.url = api.getUrl(ms.TranscodingUrl);
                result.mimeType = ms.TranscodingSubProtocol === 'hls'
                    ? 'application/x-mpegURL'
                    : `${kind}/${ms.TranscodingContainer}`;
                break;
            default:
                break;
        }
    } else {
        result.url = ms.Path;                                                           // S1
    }
    if (!result.url && ms.SupportsDirectPlay) {                                         // S5
        result.url = ms.Path;
        result.playMethod = 'DirectPlay';
    }
    return result;
}

/** The player URL (and mime type / play method) for a video item: [S] with type 'Video'. */
export function playerUrl(api, item, mediaSource, type = 'Video') {
    return playerStream(api, type, item, mediaSource);
}
