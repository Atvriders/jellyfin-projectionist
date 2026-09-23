/*
 * Projectionist client-side hook
 *
 * Sections, each independent of the others:
 *   - hides the internal "Projectionist Prerolls" library from the home screen;
 *   - loads the hook settings (Plugins/Projectionist/HookSettings) and shows the
 *     skip-preroll button on clients that expose playbackManager;
 *   - feature prefetch: while a preroll plays, pre-buffers a direct-play feature in
 *     the browser, or (Hot) pre-downloads the first segments of the feature's
 *     pre-started transcode (works on jellyfin-web 10.11, which exposes no playbackManager);
 *   - episode intros for older clients (below).
 *
 * Older Jellyfin web/desktop clients hard-code intro fetching to Movie items only:
 *   if (item.Type === 'Movie') { fetchIntros(item.Id); ... }
 * Episodes never call /Items/{id}/Intros there, so our IIntroProvider is never asked.
 * Where the playbackManager module is reachable (window.playbackManager / require),
 * this hook wraps its play() function: when the user is about to play an Episode,
 * we fetch intros for it, prepend them to the play options, and let
 * playbackManager handle the rest. Movies still work normally (Jellyfin already
 * fetches intros for them server-side). jellyfin-web 10.11 fetches intros for
 * episodes itself and exposes neither global, so there the wrapper never installs.
 */
// Top-level log so we can see in the browser console whether this script even loaded.
try { console.log('[Projectionist] hook script loaded'); } catch (_) {}

// ============== Hide the internal Projectionist library from the home screen ==============
// We can't use BlockedMediaFolders (it would prevent streaming the prerolls) so
// we hide the library tile + section client-side via CSS + a DOM mutation observer.
(function () {
    'use strict';
    var STYLE_ID = 'pjt-hide-library-style';
    var LIB_NAME = 'Projectionist Prerolls';

    function ensureStyle() {
        if (document.getElementById(STYLE_ID)) return;
        var s = document.createElement('style');
        s.id = STYLE_ID;
        // Cards in the Latest / My Media / Libraries grids carry the library
        // name in a `data-libraryid` attribute that's resolvable via the card
        // body. We can't easily map id->name in CSS, so we walk the DOM.
        // The CSS rule itself just hides anything we tag with our marker class.
        s.textContent = '.pjt-hidden-library{display:none!important;visibility:hidden!important;}';
        document.head.appendChild(s);
    }

    function hideLibraryTiles(root) {
        try {
            var nodes = (root || document).querySelectorAll(
                '.card a[href*="/web/#/list.html"], .card .cardText a, ' +
                '.card .cardText, .card .cardImageContainer'
            );
            // The reliable target: any card whose visible label text matches the
            // library name. Walk all cards on the page and tag matching ones.
            var cards = (root || document).querySelectorAll(
                '.card, .listItem, .navMenuOption, button.emby-button, a.emby-button'
            );
            cards.forEach(function (card) {
                if (card.classList.contains('pjt-hidden-library')) return;
                var text = (card.textContent || '').trim();
                // Exact-match by library name; broad-match by URL contains "ProjectionistPrerolls".
                if (text === LIB_NAME ||
                    text.indexOf(LIB_NAME) === 0 ||
                    (card.querySelector && card.querySelector('a[href*="ProjectionistPrerolls"]'))) {
                    card.classList.add('pjt-hidden-library');
                }
                // Also walk up to the parent .card if we matched a child element
                if (card.classList.contains('pjt-hidden-library')) {
                    var parentCard = card.closest && card.closest('.card');
                    if (parentCard && parentCard !== card) parentCard.classList.add('pjt-hidden-library');
                }
            });
        } catch (_) {}
    }

    function startHider() {
        ensureStyle();
        hideLibraryTiles(document);
        // Re-run when the DOM changes (Jellyfin's web client is heavily SPA-driven)
        try {
            var obs = new MutationObserver(function (mutations) {
                // Throttle: only run if at least one added node has descendants
                var any = mutations.some(function (m) { return m.addedNodes && m.addedNodes.length; });
                if (any) hideLibraryTiles(document);
            });
            obs.observe(document.body, { childList: true, subtree: true });
        } catch (_) {}
        // Periodic safety net for stubborn SPA renders
        setInterval(function () { hideLibraryTiles(document); }, 2500);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', startHider);
    } else {
        startHider();
    }
})();

// ============== Skip-preroll button overlay ==============
// Appears in the lower-right of the player while a preroll is active. Reads
// configuration from /Plugins/Projectionist/Config so the user's "min seconds
// before skip" setting is honoured.
(function () {
    'use strict';
    var BTN_ID = 'pjt-skip-btn';
    var STYLE_ID = 'pjt-skip-style';
    var minSkipSeconds = 0;
    var enabled = true;
    var prerollPaths = new Set();
    var prerollIds = new Set();
    var prerollNames = new Set();
    window.__projectionistSettings = window.__projectionistSettings || { featurePreloadEnabled: false, featurePreloadMode: 0 };

    function tryRequire(name) {
        try {
            if (window.require) return window.require(name);
            if (window.RequireJS) return window.RequireJS(name);
        } catch (_) {}
        return null;
    }

    function getPlaybackManager() {
        return window.playbackManager || tryRequire('playbackManager');
    }

    function getEvents() {
        return window.Events || tryRequire('events') || tryRequire('Events');
    }

    function ensureStyle() {
        if (document.getElementById(STYLE_ID)) return;
        var s = document.createElement('style');
        s.id = STYLE_ID;
        s.textContent =
            '#' + BTN_ID + '{position:fixed;right:32px;bottom:120px;z-index:2147483000;'
            + 'background:rgba(20,20,28,.85);color:#fff;border:1px solid rgba(229,9,20,.6);'
            + 'border-radius:6px;padding:10px 18px;font:600 13px/1 -apple-system,sans-serif;'
            + 'cursor:pointer;backdrop-filter:blur(8px);transition:opacity .2s,transform .2s;'
            + 'letter-spacing:.04em;text-transform:uppercase;}'
            + '#' + BTN_ID + ':hover{background:rgba(229,9,20,.9);}'
            + '#' + BTN_ID + '.pjt-hidden{opacity:0;pointer-events:none;transform:translateY(8px);}';
        document.head.appendChild(s);
    }

    // Settings. jellyfin-web 10.11 creates window.ApiClient asynchronously, after
    // this deferred script has already run, and the user may still be on the login
    // page. So poll (bounded) for an ApiClient that holds an access token, then
    // re-read the settings periodically so admin changes apply without a reload.
    // window.__projectionistRefreshSettings lets other modules load them on demand.
    var SETTINGS_WAIT_MS = 60000;
    var SETTINGS_POLL_MS = 500;
    var SETTINGS_RETRY_MS = 3000;
    var SETTINGS_REFRESH_MS = 5 * 60 * 1000;
    var settingsInFlight = null;

    function currentApiClient() {
        return window.ApiClient || (window.connectionManager && connectionManager.currentApiClient && connectionManager.currentApiClient()) || null;
    }

    function hasAccessToken(ac) {
        try { return typeof ac.accessToken === 'function' ? !!ac.accessToken() : true; } catch (_) { return false; }
    }

    // Resolves true once the settings were read, false when they could not be (yet).
    function loadConfig() {
        if (settingsInFlight) return settingsInFlight;
        var ac = currentApiClient();
        if (!ac || !hasAccessToken(ac)) return Promise.resolve(false);
        try {
            settingsInFlight = ac.fetch({ url: ac.getUrl('Plugins/Projectionist/HookSettings'), type: 'GET', dataType: 'json' })
                .then(function (cfg) {
                    if (!cfg) return false;
                    minSkipSeconds = cfg.SkippableAfterSeconds || cfg.skippableAfterSeconds || 0;
                    enabled = (cfg.EnableSkippablePrerolls !== undefined ? cfg.EnableSkippablePrerolls : cfg.enableSkippablePrerolls) !== false;
                    var preloadMode = parsePreloadMode(
                        cfg.FeaturePreloadMode !== undefined ? cfg.FeaturePreloadMode : cfg.featurePreloadMode,
                        (cfg.EnableFeaturePreload !== undefined ? cfg.EnableFeaturePreload : cfg.enableFeaturePreload) === true ? 1 : 0);
                    var settings = window.__projectionistSettings;
                    if (!settings.loadedAt || settings.featurePreloadMode !== preloadMode) {
                        try { console.log('[Projectionist] settings loaded: feature preload mode', ['Off', 'Warm', 'Hot'][preloadMode] || preloadMode); } catch (_) {}
                    }
                    settings.featurePreloadMode = preloadMode;
                    settings.featurePreloadEnabled = preloadMode !== 0;
                    settings.loadedAt = Date.now();
                    return true;
                }, function () { return false; })
                .then(function (ok) { settingsInFlight = null; return ok; });
        } catch (_) {
            settingsInFlight = null;
            return Promise.resolve(false);
        }
        return settingsInFlight;
    }
    window.__projectionistRefreshSettings = loadConfig;

    (function waitForApiClient() {
        var deadline = Date.now() + SETTINGS_WAIT_MS;
        (function attempt() {
            var ac = currentApiClient();
            if (!ac || !hasAccessToken(ac)) {
                if (Date.now() < deadline) setTimeout(attempt, SETTINGS_POLL_MS);
                return;
            }
            loadConfig().then(function (ok) {
                if (!ok && Date.now() < deadline) setTimeout(attempt, SETTINGS_RETRY_MS);
            });
        })();
        setInterval(loadConfig, SETTINGS_REFRESH_MS);
    })();

    function parsePreloadMode(value, fallback) {
        if (value === null || value === undefined) return fallback || 0;
        if (typeof value === 'number') return value;
        var s = String(value).toLowerCase();
        if (/^\d+$/.test(s)) return parseInt(s, 10);
        if (s === 'hot') return 2;
        if (s === 'warm') return 1;
        return 0;
    }

    function isPrerollItem(item) {
        if (!item) return false;
        // Heuristic: item belongs to "Projectionist Prerolls" library, OR its path
        // is in the set we recorded when prepending intros.
        if (item.Id && prerollIds.has(item.Id)) return true;
        if (prerollPaths.has(item.Path)) return true;
        if (item.Name && prerollNames.has(item.Name)) return true;
        // Fallback: check parent name
        if (item.SeriesName === 'Projectionist Prerolls' ||
            item.ParentName === 'Projectionist Prerolls' ||
            item.CollectionName === 'Projectionist Prerolls') return true;
        return false;
    }

    window.__projectionistMarkPreroll = function (path, id, name) {
        if (path) prerollPaths.add(path);
        if (id) prerollIds.add(id);
        if (name) prerollNames.add(name);
    };

    function showButton() {
        ensureStyle();
        var btn = document.getElementById(BTN_ID);
        if (!btn) {
            btn = document.createElement('button');
            btn.id = BTN_ID;
            btn.textContent = 'Skip';
            btn.addEventListener('click', function () {
                try {
                    var pm = getPlaybackManager();
                    if (pm && typeof pm.nextTrack === 'function') pm.nextTrack();
                    else if (pm && typeof pm.stop === 'function') pm.stop();
                } catch (_) {}
            });
            document.body.appendChild(btn);
        }
        btn.classList.remove('pjt-hidden');
    }
    function hideButton() {
        var btn = document.getElementById(BTN_ID);
        if (btn) btn.classList.add('pjt-hidden');
    }

    function attachSkipWatcher() {
        var events = getEvents();
        var pm = getPlaybackManager();
        if (!events || !pm) {
            setTimeout(attachSkipWatcher, 400);
            return;
        }
        var revealTimer = null;
        events.on(pm, 'playbackstart', function (e, player) {
            if (revealTimer) { clearTimeout(revealTimer); revealTimer = null; }
            try {
                var item = pm.currentItem(player);
                if (!isPrerollItem(item) || !enabled) {
                    hideButton();
                    return;
                }
                if (minSkipSeconds > 0) {
                    revealTimer = setTimeout(showButton, minSkipSeconds * 1000);
                } else {
                    showButton();
                }
            } catch (_) { hideButton(); }
        });
        events.on(pm, 'playbackstop', function () {
            hideButton();
            if (revealTimer) { clearTimeout(revealTimer); revealTimer = null; }
        });
    }
    attachSkipWatcher();
})();

// ============== Feature prefetch (browser) ==============
// While a preroll plays, ask the server what comes next (Plugins/Projectionist/Upcoming) and move
// the feature's first seconds into the browser before the switch:
//   - DirectPlay (Warm, Hot): a hidden muted <video preload=auto> loads the exact Static URL
//     jellyfin-web's player will request, with the same crossOrigin mode, and is released again
//     before the preroll ends. Chrome then serves the feature's first seconds from its media/HTTP
//     cache instead of starting from zero bytes at the switch.
//   - Transcode (Hot): the server pre-started the transcode and will hand the player that exact
//     session (Upcoming Transcode.Url is its TranscodingUrl). Once it is Ready, the playlists, the
//     init segment and the first media segments are downloaded with the very URLs hls.js will
//     request, so hls.js is answered from the browser's HTTP cache (the server marks that
//     session's responses cacheable). If the browser did not keep the first segment (a private
//     window's small in-memory cache), it stops there instead of downloading everything twice.
// The preroll always comes first: a prefetch starts only while the preroll is well buffered ahead
// and stops the moment it waits, stalls or runs low; after that nothing more is fetched during that
// preroll. A prefetch counts as done only once it completed; one cut short is retried on the next
// preroll, at most MAX_ATTEMPTS times. One prefetch at a time.
// Needs no jellyfin-web internals: it only listens for media events on the document.
(function () {
    'use strict';

    if (window.__projectionistPrefetchInstalled) return;
    window.__projectionistPrefetchInstalled = true;

    var TAG = '[Projectionist]';
    var MARK_ATTR = 'data-projectionist-prefetch';
    var LOOKUP_DEBOUNCE_MS = 300;
    var LOOKUP_RETRY_MS = 1500;
    var UNKNOWN_ATTEMPTS = 5;          // PlayMethod "Unknown": ask again, ~1.5 s apart
    var READY_ATTEMPTS = 8;            // Hot transcode pre-start not Ready yet: likewise
    var START_AFTER_SECONDS = 1;       // the preroll must have played this long
    var STOP_BEFORE_END_SECONDS = 3;   // release (or, for segments, request nothing new) this close to its end
    var START_AHEAD_SECONDS = 8;       // start only with this much of the preroll buffered ahead...
    var START_AHEAD_MSE_SECONDS = 5;   // ...(hls.js keeps only ~6 s ahead on fast links)...
    var KEEP_AHEAD_SECONDS = 4;        // ...and stop when it falls below this
    var BUFFERED_TO_END_SLACK = 0.5;   // or while the preroll is buffered to its end
    var HAVE_FUTURE_DATA = 3;
    var MAX_WARM_MS = 12000;
    var MAX_SEGMENTS_MS = 60000;
    var MAX_SEGMENTS = 12;
    var START_WAIT_MS = 120000;        // stop waiting for the start conditions (paused preroll)
    var OUTCOME_TTL_MS = 5 * 60 * 1000;
    var MAX_ATTEMPTS = 3;
    var CHECK_MS = 250;
    var DEFAULT_PREFETCH_SECONDS = 8;
    var PREROLL_END_EVENTS = ['ended', 'emptied', 'error', 'abort'];
    var ITEM_ID_RE = /\/videos\/([0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12})\//i;

    // Diagnostics: the prefetch in progress, the last few finished ones, and per prefetch key
    // ("direct:<featureId>" / "hls:<TranscodingUrl>") the attempts made and whether one completed.
    var state = window.__projectionistPrefetch = { active: null, history: [], outcomes: {} };
    var lookupSeq = 0;
    var handledSrc = new WeakMap();    // <video> -> currentSrc already looked up
    var stalledSrc = new WeakMap();    // <video> -> currentSrc that waited/stalled while we watched it
    var corsModePromise = null;

    function log() {
        try { console.log.apply(console, [TAG].concat(Array.prototype.slice.call(arguments))); } catch (_) {}
    }

    function getApiClient() {
        if (window.ApiClient) return window.ApiClient;
        if (window.connectionManager && typeof connectionManager.currentApiClient === 'function') {
            return connectionManager.currentApiClient();
        }
        return null;
    }

    function normalizeId(id) {
        return id ? String(id).replace(/-/g, '').toLowerCase() : '';
    }

    // Item id ("N" format) from a /Videos/{id}/... source, null for blob: sources
    // (hls.js; the server then answers for the user's device alone), undefined when
    // the source is not a Jellyfin video at all.
    function itemIdFromSrc(src) {
        if (/^blob:/i.test(src)) return null;
        var m = ITEM_ID_RE.exec(src);
        return m ? normalizeId(m[1]) : undefined;
    }

    function srcOf(el) {
        return el.currentSrc || el.src || '';
    }

    function stillPlaying(el, src) {
        return srcOf(el) === src && !el.ended && !el.error;
    }

    function secondsLeft(el) {
        var d = el.duration;
        if (!isFinite(d) || d <= 0) return null;
        return d - (el.currentTime || 0);
    }

    function bufferedFromStart(v) {
        try {
            var b = v.buffered;
            for (var i = 0; i < b.length; i++) {
                if (b.start(i) <= 0.5) return b.end(i);
            }
        } catch (_) {}
        return 0;
    }

    // End of the buffered range that holds the playback position, minus the position.
    function bufferedAhead(el) {
        var t = el.currentTime || 0;
        try {
            var b = el.buffered;
            for (var i = 0; i < b.length; i++) {
                if (b.start(i) <= t + 0.1 && b.end(i) >= t) return b.end(i) - t;
            }
        } catch (_) {}
        return 0;
    }

    function bufferedToEnd(el) {
        var d = el.duration;
        if (!isFinite(d) || d <= 0) return false;
        return (el.currentTime || 0) + bufferedAhead(el) >= d - BUFFERED_TO_END_SLACK;
    }

    // The preroll must never pay for a prefetch: it needs data for now and comfortably beyond
    // (or everything up to its end).
    function prerollHeadroom(el, minAhead) {
        if ((el.readyState || 0) < HAVE_FUTURE_DATA) return false;
        return bufferedToEnd(el) || bufferedAhead(el) >= minAhead;
    }

    function startAheadSeconds(el) {
        return /^blob:/i.test(srcOf(el)) ? START_AHEAD_MSE_SECONDS : START_AHEAD_SECONDS;
    }

    function saveData() {
        try { return !!(navigator.connection && navigator.connection.saveData); } catch (_) { return false; }
    }

    function preloadMode() {
        var s = window.__projectionistSettings || {};
        return Number(s.featurePreloadMode) || 0;
    }

    function ensureSettings() {
        var s = window.__projectionistSettings || {};
        var refresh = window.__projectionistRefreshSettings;
        if (s.loadedAt || typeof refresh !== 'function') return Promise.resolve(preloadMode());
        return Promise.resolve(refresh()).then(preloadMode, preloadMode);
    }

    function fetchUpcoming(apiClient, itemId) {
        var url = apiClient.getUrl('Plugins/Projectionist/Upcoming', itemId ? { currentItemId: itemId } : null);
        var headers = {};
        if (typeof apiClient.setRequestHeaders === 'function') {
            apiClient.setRequestHeaders(headers);
        } else if (typeof apiClient.accessToken === 'function' && apiClient.accessToken()) {
            headers['X-Emby-Token'] = apiClient.accessToken();
        }
        return fetch(url, { method: 'GET', headers: headers, credentials: 'same-origin', cache: 'no-store' })
            .then(function (res) {
                if (res.status === 204 || !res.ok) return null;
                return res.json();
            });
    }

    // htmlVideoPlayer (plugin.js setCurrentSrc) sets crossOrigin 'anonymous' for
    // server-hosted sources and, for non-HLS sources, 'use-credentials' when the web
    // config.json has includeCorsCredentials (webSettings.js; a failed read counts as
    // false). Chrome only shares media data between elements with the same CORS mode.
    // hls.js requests carry credentials (xhr.withCredentials) under the same setting.
    function corsMode() {
        if (!corsModePromise) {
            corsModePromise = fetch(new URL('config.json', document.baseURI).toString(), { cache: 'no-store', credentials: 'same-origin' })
                .then(function (res) {
                    if (!res.ok) throw new Error('config.json ' + res.status);
                    return res.json();
                })
                .then(function (cfg) { return cfg && cfg.includeCorsCredentials ? 'use-credentials' : 'anonymous'; })
                .catch(function () { return 'anonymous'; });
        }
        return corsModePromise;
    }

    // Mirrors jellyfin-web 10.11 createStreamInfo (playbackmanager.js) for a direct-play
    // video: same path, same parameters in the same insertion order, encoded by the same
    // ApiClient.getUrl. Any difference and the player's request misses the cache.
    function buildStaticUrl(apiClient, featureId, dp) {
        var container = String(dp.Container || '').toLowerCase();
        if (!container || !dp.MediaSourceId) return null;
        var directOptions = {
            Static: true,
            mediaSourceId: dp.MediaSourceId,
            deviceId: apiClient.deviceId(),
            ApiKey: apiClient.accessToken()
        };
        if (dp.ETag) directOptions.Tag = dp.ETag;
        if (dp.LiveStreamId) directOptions.LiveStreamId = dp.LiveStreamId;
        return apiClient.getUrl('Videos/' + featureId + '/stream.' + container, directOptions);
    }

    // The URL createStreamInfo hands hls.js for a transcode: apiClient.getUrl(mediaSource.TranscodingUrl).
    function transcodeMasterUrl(apiClient, transcodingUrl) {
        return apiClient.getUrl(transcodingUrl);
    }

    // ---- HLS playlists, read the way hls.js reads them ----

    function playlistLines(text) {
        var lines = String(text || '').split(/\r?\n|\r/);
        if (!/^#EXTM3U/.test(lines[0] || '')) return null;
        return lines.map(function (l) { return l.trim(); }).filter(function (l) { return l.length > 0; });
    }

    function isMediaPlaylist(text) {
        return /#EXTINF:/.test(String(text || ''));
    }

    // Variant URIs of a master playlist, in order: the first non-comment line after each
    // #EXT-X-STREAM-INF. Null when the text is not a playlist.
    function parseMasterPlaylist(text) {
        var lines = playlistLines(text);
        if (!lines) return null;
        var uris = [];
        var inf = false;
        for (var i = 1; i < lines.length; i++) {
            var line = lines[i];
            if (line.indexOf('#EXT-X-STREAM-INF:') === 0) { inf = true; continue; }
            if (line.charAt(0) === '#') continue;
            if (inf) uris.push(line);
            inf = false;
        }
        return uris;
    }

    function attrValue(line, name) {
        var m = new RegExp('[:,]' + name + '="([^"]*)"').exec(line);
        return m ? m[1] : null;
    }

    // Init segment (#EXT-X-MAP URI) and media segments (URI + #EXTINF seconds) of a media
    // playlist. `unsupported` names anything this prefetch cannot mirror (byte ranges, keys).
    function parseMediaPlaylist(text) {
        var lines = playlistLines(text);
        if (!lines) return null;
        var out = { init: null, segments: [], unsupported: null };
        var duration = null;
        for (var i = 1; i < lines.length; i++) {
            var line = lines[i];
            if (line.indexOf('#EXT-X-MAP:') === 0) {
                if (/[:,]BYTERANGE=/.test(line)) out.unsupported = 'byterange';
                if (!out.init && !out.segments.length) out.init = attrValue(line, 'URI');
                continue;
            }
            if (line.indexOf('#EXTINF:') === 0) {
                duration = parseFloat(line.substring(8));
                continue;
            }
            if (line.indexOf('#EXT-X-BYTERANGE') === 0) { out.unsupported = 'byterange'; continue; }
            if (line.indexOf('#EXT-X-KEY:') === 0 && !/METHOD=NONE/.test(line)) { out.unsupported = 'key'; continue; }
            if (line.charAt(0) === '#') continue;
            out.segments.push({ uri: line, duration: isFinite(duration) && duration > 0 ? duration : 0 });
            duration = null;
        }
        return out;
    }

    // The first segments whose #EXTINF durations cover `seconds`, plus one more.
    function selectSegments(segments, seconds) {
        var out = [];
        var covered = 0;
        for (var i = 0; i < segments.length && out.length < MAX_SEGMENTS; i++) {
            out.push(segments[i]);
            if (covered >= seconds) break;
            covered += segments[i].duration;
        }
        return out;
    }

    // hls.js resolves playlist URIs against the playlist's response URL with url-toolkit; for
    // http(s) bases that is the WHATWG resolution fetch/XHR apply anyway.
    function resolveUri(uri, base) {
        return new URL(uri, base).href;
    }

    // The requests that follow a media playlist: init segment, then the first segments.
    function planMediaRequests(text, playlistUrl, seconds) {
        var media = parseMediaPlaylist(text);
        if (!media) return { error: 'not-a-playlist' };
        if (media.unsupported) return { error: media.unsupported };
        if (!media.segments.length) return { error: 'no-segments' };
        var requests = [];
        if (media.init) requests.push({ kind: 'init', url: resolveUri(media.init, playlistUrl), seconds: 0 });
        selectSegments(media.segments, seconds).forEach(function (s) {
            requests.push({ kind: 'segment', url: resolveUri(s.uri, playlistUrl), seconds: s.duration });
        });
        return { requests: requests };
    }

    // ---- attempts: done once completed, else retried on the next preroll (MAX_ATTEMPTS) ----

    function outcomeOf(key, now) {
        var o = state.outcomes[key];
        return o && now - o.lastAttemptAt < OUTCOME_TTL_MS ? o : null;
    }

    function prefetchAllowed(key, now) {
        var o = outcomeOf(key, now);
        return !o || (!o.completed && !o.uncacheable && o.attempts < MAX_ATTEMPTS);
    }

    function noteAttempt(key, now) {
        Object.keys(state.outcomes).forEach(function (k) {
            if (!outcomeOf(k, now)) delete state.outcomes[k];
        });
        var o = state.outcomes[key] || { attempts: 0, completed: false, lastAttemptAt: 0 };
        o.attempts++;
        o.lastAttemptAt = now;
        state.outcomes[key] = o;
        return o.attempts;
    }

    // ---- preroll watch ----

    function onPlaying(e) {
        var el = e.target;
        if (!(el instanceof HTMLVideoElement) || el.hasAttribute(MARK_ATTR)) return;
        var src = srcOf(el);
        // 'playing' also fires after every pause, seek and stall: one lookup per source.
        if (!src || handledSrc.get(el) === src) return;
        handledSrc.set(el, src);
        var itemId = itemIdFromSrc(src);
        if (itemId === undefined) return;
        var seq = ++lookupSeq;
        setTimeout(function () { lookup(el, src, itemId, seq, 1); }, LOOKUP_DEBOUNCE_MS);
    }

    // 'waiting' (playback halted for data) or 'stalled' (its download made no progress) on a
    // preroll that already played: stop at once and fetch nothing more during this preroll.
    // (A 'waiting' before the first 'playing' is ordinary start-up.)
    function onStall(e) {
        var el = e.target;
        if (!(el instanceof HTMLVideoElement) || el.hasAttribute(MARK_ATTR)) return;
        var src = srcOf(el);
        if (src && handledSrc.get(el) === src) markStalled(el, src, e.type);
    }

    function markStalled(el, src, why) {
        stalledSrc.set(el, src);
        var w = state.active;
        if (w && w.preroll === el) release('preroll-' + why);
    }

    function lookup(el, src, itemId, seq, attempt) {
        if (seq !== lookupSeq || !stillPlaying(el, src) || saveData()) return;
        var mode = 0;
        ensureSettings().then(function (m) {
            mode = m;
            var apiClient = getApiClient();
            if (mode < 1 || !apiClient) return null;
            return fetchUpcoming(apiClient, itemId);
        }).then(function (info) {
            if (!info || seq !== lookupSeq || !stillPlaying(el, src)) return;
            var featureId = normalizeId(info.FeatureId);
            if (!featureId || featureId === itemId) return;
            if (itemId && Array.isArray(info.IntroIds) && info.IntroIds.map(normalizeId).indexOf(itemId) < 0) return;
            var method = String(info.PlayMethod || 'Unknown');
            var askAgain = function (limit) {
                if (attempt >= limit) return false;
                setTimeout(function () { lookup(el, src, itemId, seq, attempt + 1); }, LOOKUP_RETRY_MS);
                return true;
            };
            if (method === 'Unknown') {
                askAgain(UNKNOWN_ATTEMPTS);
                return;
            }
            if (method === 'DirectPlay' && info.DirectPlay) {
                scheduleWarm(el, src, seq, featureId, info);
                return;
            }
            if (method === 'Transcode' && mode >= 2) {
                var t = info.Transcode;
                if (t && t.Url && !prefetchAllowed('hls:' + t.Url, Date.now())) return;
                if (!t || !t.Url || t.Ready !== true) {
                    if (!askAgain(READY_ATTEMPTS)) log('upcoming feature', featureId, 'will Transcode; its pre-started transcode is not ready - nothing pre-downloaded');
                    return;
                }
                scheduleSegments(el, src, seq, featureId, info);
                return;
            }
            log('upcoming feature', featureId, 'will', method, '- nothing to pre-buffer in the browser');
        }).catch(function () {});
    }

    // Calls start() once the preroll has played START_AFTER_SECONDS and is well buffered ahead.
    function whenPrerollReady(el, src, seq, start) {
        var giveUpAt = Date.now() + START_WAIT_MS;
        (function waitForStart() {
            if (seq !== lookupSeq || !stillPlaying(el, src) || Date.now() > giveUpAt) return;
            if (stalledSrc.get(el) === src) {
                log('the preroll stalled - no feature prefetch during it');
                return;
            }
            var left = secondsLeft(el);
            if (left !== null && left <= STOP_BEFORE_END_SECONDS) return;
            if ((el.currentTime || 0) < START_AFTER_SECONDS || !prerollHeadroom(el, startAheadSeconds(el))) {
                setTimeout(waitForStart, CHECK_MS);
                return;
            }
            start();
        })();
    }

    function canStart(el, src, seq, key) {
        return seq === lookupSeq && stillPlaying(el, src) && stalledSrc.get(el) !== src &&
            !(state.active && state.active.key === key) && prefetchAllowed(key, Date.now());
    }

    function prefetchSeconds(info) {
        return Number(info.PrefetchSeconds) > 0 ? Number(info.PrefetchSeconds) : DEFAULT_PREFETCH_SECONDS;
    }

    function newPrefetch(kind, key, featureId, url, el, src, target, maxMs) {
        return {
            kind: kind, key: key, featureId: featureId, url: url, target: target, maxMs: maxMs,
            preroll: el, prerollSrc: src, startedAt: Date.now(), attempt: 0, timer: null, onPrerollDone: null
        };
    }

    function begin(w) {
        if (state.active) release('replaced');
        state.active = w;
        w.attempt = noteAttempt(w.key, w.startedAt);
        w.onPrerollDone = function () { if (state.active === w) release('preroll-ended'); };
        PREROLL_END_EVENTS.forEach(function (t) { w.preroll.addEventListener(t, w.onPrerollDone); });
        w.timer = setInterval(check, CHECK_MS);
    }

    // ---- direct play: hidden <video> ----

    function scheduleWarm(el, src, seq, featureId, info) {
        var key = 'direct:' + featureId;
        if (!canStart(el, src, seq, key)) return;
        whenPrerollReady(el, src, seq, function () { startWarm(el, src, seq, featureId, info, key); });
    }

    function startWarm(el, src, seq, featureId, info, key) {
        var url = null;
        try {
            var apiClient = getApiClient();
            url = apiClient ? buildStaticUrl(apiClient, featureId, info.DirectPlay) : null;
        } catch (_) {}
        if (!url) return;
        corsMode().then(function (crossOrigin) {
            if (!canStart(el, src, seq, key)) return;
            var v = document.createElement('video');
            v.setAttribute(MARK_ATTR, '');
            v.setAttribute('aria-hidden', 'true');
            v.tabIndex = -1;
            v.muted = true;
            v.defaultMuted = true;
            v.playsInline = true;
            v.setAttribute('playsinline', '');
            v.autoplay = false;
            v.preload = 'auto';
            v.crossOrigin = crossOrigin; // before src: it decides how the request is made
            v.style.cssText = 'position:fixed;left:0;top:0;width:1px;height:1px;opacity:0;pointer-events:none;z-index:-1;';
            var w = newPrefetch('direct', key, featureId, url, el, src, prefetchSeconds(info), MAX_WARM_MS);
            w.crossOrigin = crossOrigin;
            w.el = v;
            v.addEventListener('error', function () { if (state.active === w) release('error'); });
            begin(w);
            (document.body || document.documentElement).appendChild(v);
            v.src = url;
            log('pre-buffering direct-play feature', featureId, '(crossOrigin ' + crossOrigin + ', up to ' + w.target + ' s, attempt ' + w.attempt + ')');
        }).catch(function () {
            if (state.active && state.active.key === key) release('error');
        });
    }

    // ---- Hot transcode: the pre-started session's playlists and first segments ----

    function scheduleSegments(el, src, seq, featureId, info) {
        var key = 'hls:' + info.Transcode.Url;
        if (typeof AbortController !== 'function' || !canStart(el, src, seq, key)) return;
        whenPrerollReady(el, src, seq, function () { startSegments(el, src, seq, featureId, info, key); });
    }

    function startSegments(el, src, seq, featureId, info, key) {
        var masterUrl = null;
        try {
            var apiClient = getApiClient();
            masterUrl = apiClient ? transcodeMasterUrl(apiClient, info.Transcode.Url) : null;
        } catch (_) {}
        if (!masterUrl) return;
        corsMode().then(function (crossOrigin) {
            if (!canStart(el, src, seq, key)) return;
            var w = newPrefetch('hls', key, featureId, masterUrl, el, src, prefetchSeconds(info), MAX_SEGMENTS_MS);
            w.controller = new AbortController();
            // hls.js: xhr.withCredentials = includeCorsCredentials
            w.credentials = crossOrigin === 'use-credentials' ? 'include' : 'same-origin';
            w.requests = [];
            w.bytes = 0;
            w.seconds = 0;
            begin(w);
            log('pre-downloading the pre-started transcode of feature', featureId, '(first ' + w.target + ' s, attempt ' + w.attempt + ')');
            runSegments(w).then(function () {
                if (state.active === w) release('buffered');
            }, function (err) {
                if (state.active === w) release(err && err.prefetchReason ? err.prefetchReason : 'error');
            });
        }).catch(function () {
            if (state.active && state.active.key === key) release('error');
        });
    }

    function stopWith(reason) {
        var err = new Error(reason);
        err.prefetchReason = reason;
        return err;
    }

    // master -> variant playlist -> init -> first segments, one request at a time, bodies read in
    // full. Nothing new is requested this close to the preroll's end; release() aborts the rest.
    function runSegments(w) {
        function get(url, kind, asText) {
            if (state.active !== w) return Promise.reject(stopWith('released'));
            var left = secondsLeft(w.preroll);
            if (left !== null && left <= STOP_BEFORE_END_SECONDS) return Promise.reject(stopWith('preroll-ending'));
            var rec = { kind: kind, url: url, bytes: 0 };
            w.requests.push(rec);
            // Default cache mode on purpose: the point is to fill the HTTP cache for hls.js.
            return fetch(url, { method: 'GET', credentials: w.credentials, signal: w.controller.signal })
                .then(function (res) {
                    if (!res.ok) throw stopWith('http-' + res.status);
                    var finalUrl = res.url || url;
                    if (asText) {
                        return res.text().then(function (text) {
                            rec.bytes = text.length;
                            return { url: finalUrl, text: text };
                        });
                    }
                    return drain(res).then(function (bytes) {
                        rec.bytes = bytes;
                        w.bytes += bytes;
                        return { url: finalUrl };
                    });
                });
        }

        return get(w.url, 'master', true).then(function (master) {
            if (isMediaPlaylist(master.text)) return master;
            var variants = parseMasterPlaylist(master.text);
            if (!variants) throw stopWith('not-a-playlist');
            var distinct = variants.filter(function (u, i) { return variants.indexOf(u) === i; });
            // hls.js picks among several variants itself; the server pre-starts single-variant masters only.
            if (distinct.length !== 1) throw stopWith('variants-' + distinct.length);
            // hls.js resolves against the response URL (xhr.responseURL)
            return get(resolveUri(distinct[0], master.url), 'main', true);
        }).then(function (media) {
            var plan = planMediaRequests(media.text, media.url, w.target);
            if (plan.error) throw stopWith(plan.error);
            return plan.requests.reduce(function (chain, r) {
                return chain.then(function () {
                    return get(r.url, r.kind, false).then(function () {
                        w.seconds += r.seconds;
                        if (r.kind !== 'segment' || w.cacheChecked) return;
                        // Once, after the first media segment: did the browser keep it? A private
                        // window's in-memory cache refuses large entries (Chromium: 1/8 of at most
                        // 50 MB), so big remux segments would only be downloaded twice.
                        w.cacheChecked = true;
                        return isCached(w, r.url).then(function (hit) {
                            if (!hit) throw stopWith('not-cached');
                        });
                    });
                });
            }, Promise.resolve());
        });
    }

    // A cache-only request (never reaches the network). Same-origin only (a fetch rule for
    // 'only-if-cached'); for a server on another origin the check is skipped.
    function isCached(w, url) {
        try {
            var origin = (window.location && window.location.origin) || new URL(document.baseURI).origin;
            if (new URL(url).origin !== origin) return Promise.resolve(true);
        } catch (_) {
            return Promise.resolve(true);
        }
        return fetch(url, { method: 'GET', cache: 'only-if-cached', mode: 'same-origin', credentials: w.credentials, signal: w.controller.signal })
            .then(function (res) {
                try { if (res.body && typeof res.body.cancel === 'function') res.body.cancel(); } catch (_) {}
                return !!res.ok;
            }, function (err) {
                if (err && err.name === 'AbortError') throw err;
                return false;
            });
    }

    function drain(res) {
        if (res.body && typeof res.body.getReader === 'function') {
            var reader = res.body.getReader();
            var total = 0;
            var pump = function () {
                return reader.read().then(function (r) {
                    if (r.done) return total;
                    total += r.value ? r.value.byteLength : 0;
                    return pump();
                });
            };
            return pump();
        }
        return res.arrayBuffer().then(function (b) { return b.byteLength; });
    }

    // ---- supervision ----

    function check() {
        var w = state.active;
        if (!w) return;
        if (w.kind === 'direct' && bufferedFromStart(w.el) >= w.target) { release('buffered'); return; }
        var p = w.preroll;
        if (!stillPlaying(p, w.prerollSrc)) { release('preroll-ended'); return; }
        if ((p.readyState || 0) < HAVE_FUTURE_DATA) { markStalled(p, w.prerollSrc, 'starved'); return; }
        if (!bufferedToEnd(p) && bufferedAhead(p) < KEEP_AHEAD_SECONDS) { markStalled(p, w.prerollSrc, 'low-buffer'); return; }
        if (w.kind === 'direct') {
            var left = secondsLeft(p);
            if (left !== null && left <= STOP_BEFORE_END_SECONDS) { release('preroll-ending'); return; }
        }
        if (Date.now() - w.startedAt >= w.maxMs) release('timeout');
    }

    // Stop downloading; what was fetched stays in the browser's cache. Only a completed prefetch
    // marks its key done; anything else leaves the attempt for the next preroll to retry.
    function release(reason) {
        var w = state.active;
        if (!w) return;
        state.active = null;
        clearInterval(w.timer);
        PREROLL_END_EVENTS.forEach(function (t) { w.preroll.removeEventListener(t, w.onPrerollDone); });
        var done = {
            kind: w.kind, featureId: w.featureId, url: w.url, reason: reason, attempt: w.attempt,
            startedAt: w.startedAt, releasedAt: Date.now()
        };
        var completed;
        if (w.kind === 'direct') {
            var buffered = bufferedFromStart(w.el);
            try {
                w.el.removeAttribute('src');
                w.el.load();
            } catch (_) {}
            try { w.el.remove(); } catch (_) {}
            done.crossOrigin = w.crossOrigin;
            done.buffered = Math.round(buffered * 10) / 10;
            completed = reason === 'buffered' || buffered >= w.target;
        } else {
            try { w.controller.abort(); } catch (_) {}
            done.buffered = Math.round(w.seconds * 10) / 10;
            done.bytes = w.bytes;
            done.requests = w.requests.map(function (r) { return { kind: r.kind, url: r.url, bytes: r.bytes }; });
            completed = reason === 'buffered';
        }
        done.completed = completed;
        var o = state.outcomes[w.key];
        if (completed && o) o.completed = true;
        // Retrying would only download another segment the browser does not keep.
        if (reason === 'not-cached' && o) o.uncacheable = true;
        state.history.push(done);
        if (state.history.length > 10) state.history.shift();
        var what = w.kind === 'direct' ? 'feature pre-buffer' : 'feature segment pre-download';
        var detail = w.kind === 'direct' ? '' : ', ' + done.requests.length + ' requests, ' + Math.round(w.bytes / 1024) + ' KiB';
        log('released ' + what, w.featureId, '(' + reason + ', ' + done.buffered + ' s buffered' + detail + ' in ' + (done.releasedAt - done.startedAt) + ' ms' +
            (completed ? '' : reason === 'not-cached' ? '; this browser does not cache these segments, not retried' : '; attempt ' + w.attempt + ' of ' + MAX_ATTEMPTS) + ')');
    }

    // A player element that ends or is emptied may play the same source again later
    // (same preroll before another feature): look it up afresh then.
    function forgetSource(e) {
        if (e.target instanceof HTMLVideoElement) {
            handledSrc.delete(e.target);
            stalledSrc.delete(e.target);
        }
    }

    document.addEventListener('playing', onPlaying, true);
    document.addEventListener('waiting', onStall, true);
    document.addEventListener('stalled', onStall, true);
    document.addEventListener('ended', forgetSource, true);
    document.addEventListener('emptied', forgetSource, true);

    // Test hook: node tests (tests/js) load this file in a vm and read the pure helpers here.
    if (window.__projectionistExposeInternals === true) {
        window.__projectionistInternals = {
            itemIdFromSrc: itemIdFromSrc,
            buildStaticUrl: buildStaticUrl,
            transcodeMasterUrl: transcodeMasterUrl,
            corsMode: corsMode,
            parseMasterPlaylist: parseMasterPlaylist,
            parseMediaPlaylist: parseMediaPlaylist,
            isMediaPlaylist: isMediaPlaylist,
            selectSegments: selectSegments,
            resolveUri: resolveUri,
            planMediaRequests: planMediaRequests,
            bufferedAhead: bufferedAhead,
            bufferedToEnd: bufferedToEnd,
            prerollHeadroom: prerollHeadroom,
            prefetchAllowed: prefetchAllowed,
            state: state
        };
    }
})();

(function () {
    'use strict';

    if (window.__projectionistInstalled) {
        try { console.log('[Projectionist] already installed; skipping duplicate init'); } catch (_) {}
        return;
    }
    window.__projectionistInstalled = true;

    var TAG = '[Projectionist]';

    function getApiClient() {
        if (window.ApiClient) return window.ApiClient;
        if (window.connectionManager && typeof connectionManager.currentApiClient === 'function') {
            return connectionManager.currentApiClient();
        }
        return null;
    }

    function log() {
        try { console.log.apply(console, [TAG].concat(Array.prototype.slice.call(arguments))); } catch (_) {}
    }

    function waitForPlaybackManager(cb, attempts) {
        attempts = attempts || 0;
        // playbackManager is exposed differently in different client versions.
        var pm = window.playbackManager
            || (window.require && tryRequire('playbackManager'))
            || (window.RequireJS && tryRequire('playbackManager'));
        if (pm && typeof pm.play === 'function') {
            cb(pm);
            return;
        }
        if (attempts > 80) { // ~16s of polling
            log('gave up waiting for playbackManager');
            return;
        }
        setTimeout(function () { waitForPlaybackManager(cb, attempts + 1); }, 200);
    }

    function tryRequire(name) {
        try { return window.require(name); } catch (_) { return null; }
    }

    /**
     * Fetch the intros for the given itemId via /Items/{id}/Intros.
     * Returns a promise resolving to an array of BaseItemDto, or [] on any failure.
     */
    function fetchIntros(apiClient, itemId, userId) {
        try {
            // ApiClient.getIntros handles the right URL + auth.
            return apiClient.getIntros(itemId).then(function (res) {
                var items = (res && res.Items) || [];
                return items;
            }).catch(function () { return []; });
        } catch (e) {
            return Promise.resolve([]);
        }
    }

    function isEpisode(item) {
        return item && (item.Type === 'Episode' || item.MediaType === 'Episode');
    }

    function getFirstPlayItem(options) {
        if (!options) return null;
        if (Array.isArray(options.items) && options.items.length) return options.items[0];
        if (options.item) return options.item;
        if (options.Item) return options.Item;
        if (options.currentItem) return options.currentItem;
        return null;
    }

    function getFirstPlayId(options) {
        if (!options) return null;
        if (Array.isArray(options.ids) && options.ids.length) return options.ids[0];
        if (Array.isArray(options.itemIds) && options.itemIds.length) return options.itemIds[0];
        if (Array.isArray(options.ItemIds) && options.ItemIds.length) return options.ItemIds[0];
        return options.id || options.Id || options.itemId || options.ItemId || null;
    }

    function isVideoFeature(item) {
        return item && (item.MediaType === 'Video' ||
            item.Type === 'Movie' ||
            item.Type === 'Episode' ||
            item.Type === 'MusicVideo');
    }

    function markIntrosForSkip(intros) {
        try {
            intros.forEach(function (i) {
                if (i && typeof window.__projectionistMarkPreroll === 'function') {
                    window.__projectionistMarkPreroll(i.Path, i.Id, i.Name);
                }
            });
        } catch (_) {}
    }

    function patch(playbackManager) {
        if (playbackManager.__projectionistPatched) return;
        playbackManager.__projectionistPatched = true;
        var origPlay = playbackManager.play.bind(playbackManager);

        playbackManager.play = function (options) {
            var apiClient = getApiClient();
            if (!apiClient || !options) return origPlay(options);

            // Episodes need us to prepend intros; movies fetch intros natively,
            // but we still prefetch their intro list so the skip button can
            // recognize movie prerolls.
            var firstItem = getFirstPlayItem(options);
            var firstId = getFirstPlayId(options);
            if (typeof firstItem === 'string') {
                firstId = firstId || firstItem;
                firstItem = null;
            }

            // If we don't yet know the type, fetch it.
            var itemPromise;
            if (firstItem) {
                itemPromise = Promise.resolve(firstItem);
            } else if (firstId) {
                itemPromise = apiClient.getItem(apiClient.getCurrentUserId(), firstId)
                    .catch(function () { return null; });
            } else {
                return origPlay(options);
            }

            return itemPromise.then(function (item) {
                if (!isEpisode(item)) {
                    if (!isVideoFeature(item)) return origPlay(options);
                    return fetchIntros(apiClient, item.Id || firstId).then(function (intros) {
                        markIntrosForSkip(intros);
                        return origPlay(options);
                    });
                }

                return fetchIntros(apiClient, item.Id || firstId).then(function (intros) {
                    if (!intros.length) {
                        log('no preroll for episode', item.Name || item.Id);
                        return origPlay(options);
                    }
                    log('queueing', intros.length, 'preroll(s) before episode', item.Name || item.Id);
                    markIntrosForSkip(intros);

                    // Prepend intros to whichever input the caller used.
                    var newOptions = Object.assign({}, options);
                    if (Array.isArray(options.items)) {
                        newOptions.items = intros.concat(options.items);
                        delete newOptions.ids;
                        delete newOptions.itemIds;
                        delete newOptions.ItemIds;
                    } else if (Array.isArray(options.ids)) {
                        var introIds = intros.map(function (i) { return i.Id; });
                        newOptions.ids = introIds.concat(options.ids);
                        delete newOptions.items;
                        delete newOptions.item;
                        delete newOptions.Item;
                        delete newOptions.currentItem;
                    } else if (Array.isArray(options.itemIds)) {
                        var introItemIds = intros.map(function (i) { return i.Id; });
                        newOptions.itemIds = introItemIds.concat(options.itemIds);
                        delete newOptions.items;
                        delete newOptions.item;
                        delete newOptions.Item;
                        delete newOptions.currentItem;
                    } else if (Array.isArray(options.ItemIds)) {
                        var introUpperItemIds = intros.map(function (i) { return i.Id; });
                        newOptions.ItemIds = introUpperItemIds.concat(options.ItemIds);
                        delete newOptions.items;
                        delete newOptions.item;
                        delete newOptions.Item;
                        delete newOptions.currentItem;
                    } else {
                        newOptions.items = intros.concat([item]);
                        delete newOptions.ids;
                        delete newOptions.itemIds;
                        delete newOptions.ItemIds;
                        delete newOptions.item;
                        delete newOptions.Item;
                        delete newOptions.currentItem;
                        delete newOptions.id;
                        delete newOptions.Id;
                        delete newOptions.itemId;
                        delete newOptions.ItemId;
                    }
                    // Preserve playback start position only on the FEATURE, not on intros.
                    // playbackManager honours startPositionTicks on the first item; we
                    // don't want our preroll to skip ahead, so wipe it before play.
                    if (typeof newOptions.startPositionTicks !== 'undefined') {
                        // Save and re-apply when feature actually starts. The simplest
                        // robust approach: drop it for now — the user's resume position
                        // will still trigger via Jellyfin's normal resume on the feature.
                        delete newOptions.startPositionTicks;
                    }
                    return origPlay(newOptions);
                });
            });
        };

        log('episode preroll hook installed');
    }

    waitForPlaybackManager(patch);
})();
