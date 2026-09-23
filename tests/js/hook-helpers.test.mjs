// Pure helpers of playback-hook.js, checked against what they mirror: a reference model of
// jellyfin-web 10.11.11's player URLs (lib/jellyfin-web-10.11.11-reference.mjs, pinned to a URL
// captured from the real player) and hls.js 1.6.13's own URL resolver (vendor/url-toolkit-2.2.5.cjs).
// Run from the repository root: node --test tests/js/*.test.mjs  (see tests/js/README.md)
import test from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { loadHook, flush, FakeTimeRanges, SERVER, DEVICE_ID, TOKEN } from './lib/fake-browser.mjs';
import { ApiClient, encodeQuery, playerUrl, playerStream } from './lib/jellyfin-web-10.11.11-reference.mjs';
import urlToolkit from './vendor/url-toolkit-2.2.5.cjs';   // hls.js 1.6.13's URL resolver (Apache-2.0)

const { buildAbsoluteURL } = urlToolkit;

const HERE = path.dirname(fileURLToPath(import.meta.url));
const FIXTURES = path.join(HERE, 'fixtures');
const fixture = (name) => fs.readFileSync(path.join(FIXTURES, name), 'utf8');

const hook = loadHook().internals;
// values created inside the vm context have that realm's prototypes; compare their plain JSON
const plain = (x) => JSON.parse(JSON.stringify(x));

// ---------------------------------------------------------------- direct play URL (F9)

const DIRECT_CASES = [
    { name: 'plain mp4 with ETag', server: SERVER, container: 'mp4', etag: '7447faa59df3819851b51129cfa40a6e' },
    { name: 'upper-case container, no ETag', server: SERVER, container: 'MKV' },
    { name: 'empty ETag is omitted', server: SERVER, container: 'mp4', etag: '' },
    { name: 'probe-style container list', server: SERVER, container: 'mov,mp4,m4a,3gp,3g2,mj2', etag: 'e' },
    { name: 'BaseUrl behind a reverse proxy', server: 'https://media.example.org/jellyfin', container: 'webm', etag: 'x' },
    { name: 'server address with a trailing slash', server: 'http://192.0.2.10:8096/', container: 'mp4', etag: 'e' },
    { name: 'live stream id', server: SERVER, container: 'ts', etag: 'e', liveStreamId: 'a1b2c3_live' },
    {
        name: 'device id and token needing percent-encoding', server: SERVER, container: 'mp4', etag: 'e',
        deviceId: 'TW96aWxsYS81LjA+Lz0=|1700000000000 x&y', token: 'tok/with+plus=&amp'
    },
    { name: 'dashed media source id', server: SERVER, container: 'mp4', etag: 'e', mediaSourceId: '18c57fdd-13e4-08ea-5ccc-1f296d5ac2f5' },
];

for (const c of DIRECT_CASES) {
    test(`buildStaticUrl matches the jellyfin-web 10.11.11 player URL: ${c.name}`, () => {
        const featureId = '18c57fdd13e408ea5ccc1f296d5ac2f5';
        const api = new ApiClient(c.server, c.deviceId || DEVICE_ID, c.token || TOKEN);
        const mediaSource = {
            Id: c.mediaSourceId || featureId, Container: c.container, ETag: c.etag, LiveStreamId: c.liveStreamId,
            SupportsDirectPlay: true, SupportsDirectStream: true, SupportsTranscoding: true, Protocol: 'File'
        };
        const expected = playerUrl(api, { Id: featureId }, mediaSource);
        assert.equal(expected.playMethod, 'DirectPlay');
        const dp = { Container: c.container, MediaSourceId: mediaSource.Id, ETag: c.etag, LiveStreamId: c.liveStreamId };
        assert.equal(hook.buildStaticUrl(api, featureId, dp), expected.url);
    });
}

test('buildStaticUrl refuses what it cannot mirror', () => {
    const api = new ApiClient(SERVER, DEVICE_ID, TOKEN);
    assert.equal(hook.buildStaticUrl(api, 'f', { Container: '', MediaSourceId: 'm' }), null);
    assert.equal(hook.buildStaticUrl(api, 'f', { Container: 'mp4', MediaSourceId: '' }), null);
});

test('transcodeMasterUrl is the URL jellyfin-web 10.11.11 hands hls.js', () => {
    for (const server of [SERVER, 'https://media.example.org/jellyfin', 'http://192.0.2.10:8096/']) {
        const api = new ApiClient(server, DEVICE_ID, TOKEN);
        const transcodingUrl = fixture('jellyfin-10.11.11-transcode.url.txt').trim();
        const expected = playerUrl(api, { Id: 'x' }, {
            SupportsDirectPlay: false, SupportsDirectStream: false, SupportsTranscoding: true,
            TranscodingUrl: transcodingUrl, TranscodingSubProtocol: 'hls', Container: 'mkv'
        });
        assert.equal(expected.mimeType, 'application/x-mpegURL');
        assert.equal(hook.transcodeMasterUrl(api, transcodingUrl), expected.url);
    }
});

// The ApiKey value is replaced the way the capture replaced it.
const scrubToken = (url) => url.replace(/(ApiKey|api_key)=[^&]+/gi, '$1=<tok>');

test('reference model and buildStaticUrl reproduce a URL captured from the real jellyfin-web 10.11.11 player', () => {
    const cap = JSON.parse(fixture('jellyfin-10.11.11-directplay.capture.json'));
    const api = new ApiClient(cap.serverAddress, cap.deviceId, 'any-token');
    const model = playerUrl(api, cap.item, cap.mediaSource);
    assert.equal(model.playMethod, 'DirectPlay');
    assert.equal(scrubToken(model.url), cap.playerUrl);
    const ms = cap.mediaSource;
    const dp = { Container: ms.Container, MediaSourceId: ms.Id, ETag: ms.ETag, LiveStreamId: ms.LiveStreamId };
    assert.equal(scrubToken(hook.buildStaticUrl(api, cap.item.Id, dp)), cap.playerUrl);
});

// The model's written rules (G*/S* in lib/jellyfin-web-10.11.11-reference.mjs), so the model
// cannot drift from them unnoticed.
test('reference model follows its specification', () => {
    const api = new ApiClient('http://h:8096', 'd+v', 't/k');
    const slash = new ApiClient('http://h:8096/', 'd', 't');
    // G1, G2
    assert.throws(() => api.getUrl(''));
    assert.throws(() => new ApiClient('', 'd', 't').getUrl('x'));
    assert.equal(new ApiClient('', 'd', 't').getUrl('x', null, 'http://o'), 'http://o/x');
    assert.equal(api.getUrl('x', null, 'http://o'), 'http://o/x');
    // G3, G4, G5
    assert.equal(api.getUrl('a/b'), 'http://h:8096/a/b');
    assert.equal(api.getUrl('/a/b'), 'http://h:8096/a/b');
    assert.equal(slash.getUrl('a'), 'http://h:8096//a');
    assert.equal(slash.getUrl('/a'), 'http://h:8096//a');
    assert.equal(api.getUrl('/videos/x/master.m3u8?&A=1&b=a,b%2C'), 'http://h:8096/videos/x/master.m3u8?&A=1&b=a,b%2C');
    // G6, G7
    assert.equal(encodeQuery({ b: 1, a: 2 }), 'b=1&a=2');
    assert.equal(encodeQuery({ n: null, u: undefined, e: '', f: false, z: 0, t: true }), 'f=false&z=0&t=true');
    assert.equal(encodeQuery({ 'k y': 'a b/c+d=e&f|g é' }), 'k%20y=a%20b%2Fc%2Bd%3De%26f%7Cg%20%C3%A9');
    assert.equal(api.getUrl('a', { x: 1 }), 'http://h:8096/a?x=1');
    assert.equal(api.getUrl('a', { n: null, e: '' }), 'http://h:8096/a');
    assert.equal(api.getUrl('a', {}), 'http://h:8096/a');
    // S1
    assert.deepEqual(playerStream(api, 'Book', { Id: 'i' }, { Path: '/p.epub' }), { url: '/p.epub', mimeType: undefined, playMethod: 'DirectPlay' });
    // S2, S4: prefix, verbatim ids, lower-cased container, parameter names and order, Tag/LiveStreamId
    assert.deepEqual(
        playerStream(api, 'Video', { Id: '6E96-4d' }, { Id: 'ms-1', Container: 'MKV', ETag: 'e1', LiveStreamId: 'ls', SupportsDirectPlay: true }),
        { url: 'http://h:8096/Videos/6E96-4d/stream.mkv?Static=true&mediaSourceId=ms-1&deviceId=d%2Bv&ApiKey=t%2Fk&Tag=e1&LiveStreamId=ls', mimeType: 'video/mkv', playMethod: 'DirectPlay' });
    assert.deepEqual(
        playerStream(api, 'Audio', { Id: 'i' }, { Id: 'm', Container: 'mp3', ETag: '', LiveStreamId: '', SupportsDirectStream: true }),
        { url: 'http://h:8096/Audio/i/stream.mp3?Static=true&mediaSourceId=m&deviceId=d%2Bv&ApiKey=t%2Fk', mimeType: 'audio/mp3', playMethod: 'DirectStream' });
    assert.equal(playerUrl(new ApiClient('http://h', 'd', ''), { Id: 'i' }, { Id: 'm', Container: 'ts', SupportsDirectPlay: true }).url,
        'http://h/Videos/i/stream.ts?Static=true&mediaSourceId=m&deviceId=d');
    assert.equal(playerUrl(api, { Id: 'i' }, { Id: 'm', SupportsDirectPlay: true }).url.split('?')[0], 'http://h:8096/Videos/i/stream.');
    // S3: which case wins
    const all = { Path: '/local.mkv', StreamUrl: 'http://s/x', SupportsDirectPlay: true, SupportsTranscoding: true, TranscodingUrl: '/t', Id: 'm', Container: 'mkv' };
    assert.equal(playerUrl(api, { Id: 'i' }, { ...all, enableDirectPlay: true }).url, '/local.mkv');
    assert.deepEqual(playerUrl(api, { Id: 'i' }, all), { url: 'http://s/x', mimeType: 'video/mkv', playMethod: 'Transcode' });
    const transcode = { Container: 'mkv', SupportsTranscoding: true, TranscodingUrl: '/videos/i/master.m3u8?a=1&b=x%2Cy|z', TranscodingSubProtocol: 'hls' };
    assert.deepEqual(playerUrl(api, { Id: 'i' }, transcode), { url: 'http://h:8096/videos/i/master.m3u8?a=1&b=x%2Cy|z', mimeType: 'application/x-mpegURL', playMethod: 'Transcode' });
    assert.equal(playerUrl(api, { Id: 'i' }, { ...transcode, TranscodingSubProtocol: 'http', TranscodingContainer: 'ts' }).mimeType, 'video/ts');
    assert.deepEqual(playerUrl(api, { Id: 'i' }, { Container: 'mkv' }), { url: undefined, mimeType: 'video/mkv', playMethod: 'Transcode' });
});

test('vendored url-toolkit is url-toolkit 2.2.5 byte for byte (vendor/THIRD-PARTY-NOTICES.md)', () => {
    const text = fs.readFileSync(path.join(HERE, 'vendor', 'url-toolkit-2.2.5.cjs'), 'utf8').replace(/\r\n/g, '\n');
    const marker = '// ---- url-toolkit 2.2.5 src/url-toolkit.js, unmodified ----\n';
    assert.ok(text.includes(marker));
    const body = text.slice(text.indexOf(marker) + marker.length);
    assert.equal(crypto.createHash('sha256').update(body, 'utf8').digest('hex'),
        'dae8e2f4807acdd098e5363c982ba8581f54b6da7973f69e6aa5bca90f4b7ab7');
});

// ---------------------------------------------------------------- itemIdFromSrc

test('itemIdFromSrc', () => {
    assert.equal(hook.itemIdFromSrc(`${SERVER}/Videos/18c57fdd13e408ea5ccc1f296d5ac2f5/stream.mp4?Static=true`), '18c57fdd13e408ea5ccc1f296d5ac2f5');
    assert.equal(hook.itemIdFromSrc(`${SERVER}/videos/6E964D64-9DC3-7D18-AD40-7DF2CB001224/master.m3u8?x=1`), '6e964d649dc37d18ad407df2cb001224');
    assert.equal(hook.itemIdFromSrc('blob:http://192.0.2.10:8096/2f1b4c1e-0000-4000-8000-000000000000'), null);
    assert.equal(hook.itemIdFromSrc(`${SERVER}/Audio/18c57fdd13e408ea5ccc1f296d5ac2f5/stream.mp3`), undefined);
    assert.equal(hook.itemIdFromSrc('https://cdn.example.org/trailer.mp4'), undefined);
    assert.equal(hook.itemIdFromSrc(`${SERVER}/Videos/not-a-guid/stream.mp4`), undefined);
});

// ---------------------------------------------------------------- corsMode

async function corsModeWith(webConfig) {
    const env = loadHook({ webConfig });
    const p = env.internals.corsMode();
    await flush();
    return p;
}

test('corsMode mirrors htmlVideoPlayer / hls.js credentials', async () => {
    assert.equal(await corsModeWith({ includeCorsCredentials: true }), 'use-credentials');
    assert.equal(await corsModeWith({ includeCorsCredentials: false }), 'anonymous');
    assert.equal(await corsModeWith({}), 'anonymous');
    assert.equal(await corsModeWith(null), 'anonymous');      // 404: jellyfin-web falls back to its default config
    assert.equal(await corsModeWith('throw'), 'anonymous');   // network error: same
});

test('corsMode reads config.json next to the page, once, without the cache', async () => {
    const env = loadHook({ webConfig: { includeCorsCredentials: true } });
    await Promise.all([env.internals.corsMode(), env.internals.corsMode()]);
    const reads = env.requests.filter((r) => r.url.endsWith('config.json'));
    assert.equal(reads.length, 1);
    assert.equal(reads[0].url, `${SERVER}/web/config.json`);
    assert.equal(reads[0].init.cache, 'no-store');
});

// ---------------------------------------------------------------- playlists (H)

test('parseMasterPlaylist reads Jellyfin 10.11.11 masters', () => {
    const variants = hook.parseMasterPlaylist(fixture('jellyfin-10.11.11-transcode.master.m3u8'));
    assert.equal(variants.length, 1);
    assert.match(variants[0], /^main\.m3u8\?&DeviceId=.*&PlaySessionId=5f0c4a0e7d2b4c1a9e3f6b8d2a1c0e97&.*TranscodeReasons=DirectPlayError$/);
    assert.equal(hook.parseMasterPlaylist('not a playlist'), null);
    assert.equal(hook.isMediaPlaylist(fixture('jellyfin-10.11.11-transcode.master.m3u8')), false);
    assert.equal(hook.isMediaPlaylist(fixture('jellyfin-10.11.11-transcode.main.m3u8')), true);
});

test('parseMasterPlaylist: comment lines between STREAM-INF and URI, duplicate and distinct variants', () => {
    const dup = '#EXTM3U\r\n#EXT-X-STREAM-INF:BANDWIDTH=1,CODECS="hvc1.2.4.L153.b0"\r\n# comment\r\nmain.m3u8?a=1\r\n' +
        '#EXT-X-STREAM-INF:BANDWIDTH=1,CODECS="hvc1.2.4.L150.b0"\r\nmain.m3u8?a=1\r\n';
    assert.deepEqual(plain(hook.parseMasterPlaylist(dup)), ['main.m3u8?a=1', 'main.m3u8?a=1']);
    const hdr = '#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1,VIDEO-RANGE=PQ,CODECS="hvc1.2.4.L153.b0"\nmain.m3u8?x=1\n' +
        '#EXT-X-STREAM-INF:BANDWIDTH=1,VIDEO-RANGE=SDR,CODECS="avc1.640029"\nmain.m3u8?x=1&VideoCodec=h264&AllowVideoStreamCopy=false\n';
    assert.equal(hook.parseMasterPlaylist(hdr).length, 2);
});

test('parseMediaPlaylist reads init + segments of Jellyfin 10.11.11 fMP4 playlists', () => {
    const t = hook.parseMediaPlaylist(fixture('jellyfin-10.11.11-transcode.main.m3u8'));
    assert.equal(t.unsupported, null);
    assert.match(t.init, /^hls1\/main\/-1\.mp4\?&DeviceId=.*actualSegmentLengthTicks=0$/);
    assert.equal(t.segments.length, 8);
    assert.deepEqual(plain(t.segments.map((s) => s.duration)), [3, 3, 3, 3, 3, 3, 3, 3]);
    assert.match(t.segments[0].uri, /^hls1\/main\/0\.mp4\?/);
    const r = hook.parseMediaPlaylist(fixture('jellyfin-10.11.11-remux.main.m3u8'));
    assert.deepEqual(plain(r.segments.slice(0, 3).map((s) => s.duration)), [6.7, 5.9, 6.733]);
});

test('parseMediaPlaylist flags what the prefetch cannot mirror', () => {
    assert.equal(hook.parseMediaPlaylist('#EXTM3U\n#EXTINF:3,\n#EXT-X-BYTERANGE:100@0\nseg.mp4\n').unsupported, 'byterange');
    assert.equal(hook.parseMediaPlaylist('#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI="k"\n#EXTINF:3,\nseg.ts\n').unsupported, 'key');
    assert.equal(hook.parseMediaPlaylist('#EXTM3U\n#EXT-X-KEY:METHOD=NONE\n#EXTINF:3,\nseg.ts\n').unsupported, null);
    assert.equal(hook.parseMediaPlaylist('#EXTM3U\n#EXTINF:3,\nseg.ts\n').init, null);
});

test('selectSegments: first segments covering the seconds, plus one', () => {
    const seg = (d) => ({ uri: 's', duration: d });
    const pick = (durations, seconds) => hook.selectSegments(durations.map(seg), seconds).length;
    assert.equal(pick([3, 3, 3, 3, 3, 3], 8), 4);          // 9 s cover 8, +1
    assert.equal(pick([6.7, 5.9, 6.733, 6], 8), 3);        // 12.6 s cover 8, +1
    assert.equal(pick([6, 6], 8), 2);                      // playlist shorter than the plan
    assert.equal(pick([8, 8, 8], 8), 2);                   // exactly covered, +1
    assert.equal(pick([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 8), 12);   // no EXTINF: capped
});

// hls.js (url-toolkit, alwaysNormalize) vs the hook's new URL(uri, base): after the URL parser
// that XHR/fetch apply, both must name the very same resource.
const RESOLVE_CASES = [
    ['http://192.0.2.10:8096/videos/6e964d64-9dc3-7d18-ad40-7df2cb001224/master.m3u8?&DeviceId=a&PlaySessionId=b', 'main.m3u8?&DeviceId=a&PlaySessionId=b'],
    ['http://192.0.2.10:8096/videos/6e964d64/main.m3u8?&x=1', 'hls1/main/-1.mp4?&x=1&runtimeTicks=0&actualSegmentLengthTicks=0'],
    ['https://media.example.org/jellyfin/videos/abc/main.m3u8?a=1', 'hls1/main/0.mp4?a=1&b=%2C%7C'],
    ['http://192.0.2.10:8096//videos/abc/master.m3u8?a=1', 'main.m3u8?a=1'],                // trailing-slash server address
    ['http://192.0.2.10:8096/videos/abc/main.m3u8?a=1', '/videos/abc/hls1/main/0.mp4?a=1'],  // absolute path
    ['http://192.0.2.10:8096/videos/abc/main.m3u8?a=1', './hls1/../hls1/main/1.mp4?a=1'],    // dot segments
    ['http://192.0.2.10:8096/videos/abc/main.m3u8?a=1', '?b=2'],                             // query only
    ['http://192.0.2.10:8096/videos/abc/main.m3u8?a=1', 'https://cdn.example.org/seg.mp4'],   // absolute URL
    ['http://192.0.2.10:8096/videos/abc/main.m3u8?a=1#t=5', 'seg.mp4?q=a b|c"d'],            // chars the URL parser escapes
    ['http://[2001:db8::1]:8096/videos/abc/main.m3u8', 'hls1/main/0.mp4?x=é'],
];

for (const [base, rel] of RESOLVE_CASES) {
    test(`resolveUri equals hls.js url-toolkit resolution: ${rel}`, () => {
        const hls = new URL(buildAbsoluteURL(base, rel, { alwaysNormalize: true })).href;
        assert.equal(hook.resolveUri(rel, base), hls);
    });
}

test('planMediaRequests: exact hls.js URLs for init + the segments covering PrefetchSeconds', () => {
    const mainUrl = `${SERVER}/videos/6e964d64-9dc3-7d18-ad40-7df2cb001224/main.m3u8?&DeviceId=x`;
    const text = fixture('jellyfin-10.11.11-transcode.main.m3u8');
    const plan = hook.planMediaRequests(text, mainUrl, 8);
    assert.equal(plan.error, undefined);
    const media = hook.parseMediaPlaylist(text);
    const expected = [media.init].concat(media.segments.slice(0, 4).map((s) => s.uri))
        .map((u) => new URL(buildAbsoluteURL(mainUrl, u, { alwaysNormalize: true })).href);
    assert.deepEqual(plain(plan.requests.map((r) => r.url)), expected);
    assert.deepEqual(plain(plan.requests.map((r) => r.kind)), ['init', 'segment', 'segment', 'segment', 'segment']);
    assert.equal(hook.planMediaRequests('#EXTM3U\n', mainUrl, 8).error, 'no-segments');
    assert.equal(hook.planMediaRequests('<html>', mainUrl, 8).error, 'not-a-playlist');
});

// ---------------------------------------------------------------- preroll headroom (C6)

test('bufferedAhead / bufferedToEnd / prerollHeadroom', () => {
    const el = (ct, ranges, duration = 60, readyState = 4) => ({ currentTime: ct, buffered: new FakeTimeRanges(ranges), duration, readyState });
    assert.equal(hook.bufferedAhead(el(10, [[0, 18]])), 8);
    assert.equal(hook.bufferedAhead(el(10, [[0, 5], [12, 30]])), 0);          // playhead in a hole
    assert.equal(hook.bufferedAhead(el(10, [[0, 5], [9.95, 14]])), 4);        // range starting just ahead
    assert.equal(hook.bufferedToEnd(el(50, [[0, 59.6]])), true);
    assert.equal(hook.bufferedToEnd(el(50, [[0, 59]])), false);
    assert.equal(hook.bufferedToEnd(el(5, [[0, 6]], NaN)), false);
    assert.equal(hook.prerollHeadroom(el(10, [[0, 18]]), 8), true);
    assert.equal(hook.prerollHeadroom(el(10, [[0, 17.9]]), 8), false);
    assert.equal(hook.prerollHeadroom(el(2, [[0, 6]], 6), 8), true);          // short preroll fully buffered
    assert.equal(hook.prerollHeadroom(el(10, [[0, 60]], 60, 2), 8), false);   // readyState < HAVE_FUTURE_DATA
});
