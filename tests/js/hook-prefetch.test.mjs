// Behaviour of the feature prefetch in playback-hook.js, driven through a fake browser with a
// manual clock: C4 (retry until a prefetch completed), C6 (the preroll comes first) and H (Hot:
// pre-download the pre-started transcode's playlists and first segments with hls.js's URLs).
// Run from the repository root: node --test tests/js/*.test.mjs  (see tests/js/README.md)
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
    loadHook, startPreroll, prerollTick, endPreroll, nextOnSameElement, FakeTimeRanges, HTMLVideoElement, SERVER
} from './lib/fake-browser.mjs';
import urlToolkit from './vendor/url-toolkit-2.2.5.cjs';   // hls.js 1.6.13's URL resolver (Apache-2.0)

const { buildAbsoluteURL } = urlToolkit;

const FIXTURES = path.join(path.dirname(fileURLToPath(import.meta.url)), 'fixtures');
const fixture = (name) => fs.readFileSync(path.join(FIXTURES, name), 'utf8');

const FEATURE = '18c57fdd13e408ea5ccc1f296d5ac2f5';
const BUMPER = 'b0000000000000000000000000000001';
const TRAILER = 'b0000000000000000000000000000002';
const THIRD = 'b0000000000000000000000000000003';
const FOURTH = 'b0000000000000000000000000000004';
const INTROS = [BUMPER, TRAILER, THIRD, FOURTH];

const directUpcoming = {
    FeatureId: FEATURE, IntroIds: INTROS, PlayMethod: 'DirectPlay',
    DirectPlay: { Container: 'mp4', MediaSourceId: FEATURE, ETag: 'etag1' }, PrefetchSeconds: 8, Transcode: null
};

const history = (env) => JSON.parse(JSON.stringify(env.state.history));
const buffer = (video, seconds) => { video.buffered = new FakeTimeRanges([[0, seconds]]); };

// ======================================================================= C4

test('C4: a warm cut short by a short bumper is retried on the next preroll, then deduped once it completed', async () => {
    const env = loadHook({ mode: 1, upcoming: directUpcoming });
    const el = startPreroll(env, { id: BUMPER, duration: 6 });            // 6 s bumper, fully buffered
    await prerollTick(env, el, 1.5);
    assert.equal(env.hidden.length, 1, 'the warm starts on the bumper');
    assert.match(env.hidden[0].src, new RegExp(`^${SERVER}/Videos/${FEATURE}/stream\\.mp4\\?Static=true&`));
    buffer(env.hidden[0], 1.5);
    await prerollTick(env, el, 2);                                         // 3 s left -> released
    assert.equal(env.state.active, null);
    await endPreroll(env, el);
    let h = history(env);
    assert.equal(h.length, 1);
    assert.equal(h[0].reason, 'preroll-ending');
    assert.equal(h[0].completed, false);
    assert.equal(h[0].attempt, 1);

    nextOnSameElement(env, el, { id: TRAILER, duration: 120, ahead: 20 });
    await prerollTick(env, el, 1.5);
    assert.equal(env.hidden.length, 2, 'the long trailer retries the warm');
    assert.equal(env.hidden[1].src, h[0].url, 'the same Static URL');
    buffer(env.hidden[1], 8.2);
    await prerollTick(env, el, 0.5);
    h = history(env);
    assert.equal(h[1].reason, 'buffered');
    assert.equal(h[1].completed, true);
    assert.equal(h[1].attempt, 2);

    await prerollTick(env, el, 20);
    await endPreroll(env, el);
    nextOnSameElement(env, el, { id: THIRD, duration: 120, ahead: 20 });
    await prerollTick(env, el, 5);
    assert.equal(env.hidden.length, 2, 'no further warm once one completed');
});

test('C4: a warm that reached its target counts as completed whatever released it', async () => {
    const env = loadHook({ mode: 1, upcoming: directUpcoming });
    const el = startPreroll(env, { id: BUMPER, duration: 6 });
    await prerollTick(env, el, 1.5);
    buffer(env.hidden[0], 8);
    env.fire(el, 'waiting');                                              // released for another reason
    assert.equal(history(env)[0].reason, 'preroll-waiting');
    assert.equal(history(env)[0].completed, true);
    await endPreroll(env, el);
    nextOnSameElement(env, el, { id: TRAILER, duration: 120, ahead: 20 });
    await prerollTick(env, el, 5);
    assert.equal(env.hidden.length, 1);
});

test('C4: at most three attempts per feature; an error counts as one', async () => {
    const env = loadHook({ mode: 1, upcoming: directUpcoming });
    const el = startPreroll(env, { id: BUMPER, duration: 6 });
    await prerollTick(env, el, 1.5);
    env.fire(env.hidden[0], 'error');                                     // attempt 1: error
    await prerollTick(env, el, 4.5);
    await endPreroll(env, el);
    for (const id of [TRAILER, THIRD]) {                                  // attempts 2 and 3: cut short
        nextOnSameElement(env, el, { id, duration: 6 });
        await prerollTick(env, el, 6);
        await endPreroll(env, el);
    }
    nextOnSameElement(env, el, { id: FOURTH, duration: 120, ahead: 20 });
    await prerollTick(env, el, 5);
    assert.deepEqual(history(env).map((x) => [x.reason, x.attempt]),
        [['error', 1], ['preroll-ending', 2], ['preroll-ending', 3]]);
    assert.equal(env.hidden.length, 3, 'no fourth attempt');
});

test('C4: attempts and completion are forgotten after five minutes', async () => {
    const env = loadHook({ mode: 1, upcoming: directUpcoming });
    const el = startPreroll(env, { id: BUMPER, duration: 120, ahead: 30 });
    await prerollTick(env, el, 1.5);
    buffer(env.hidden[0], 9);
    await prerollTick(env, el, 0.5);
    assert.equal(history(env)[0].completed, true);
    await endPreroll(env, el);
    await env.clock.advance(5 * 60 * 1000);
    nextOnSameElement(env, el, { id: TRAILER, duration: 120, ahead: 30 });
    await prerollTick(env, el, 1.5);
    assert.equal(env.hidden.length, 2);
});

// ======================================================================= C6

test('C6: no warm while the preroll is under-buffered; it starts once 8 s are buffered ahead', async () => {
    const env = loadHook({ mode: 1, upcoming: directUpcoming });
    const el = startPreroll(env, { id: TRAILER, duration: 60, ahead: 3 });
    await prerollTick(env, el, 6);
    assert.equal(env.hidden.length, 0, 'nothing while only 3 s are buffered ahead');
    el.ahead = 7.9;
    await prerollTick(env, el, 1);
    assert.equal(env.hidden.length, 0);
    el.ahead = 8;
    await prerollTick(env, el, 0.5);
    assert.equal(env.hidden.length, 1);
});

test('C6: no warm while the preroll lacks future data, even fully buffered', async () => {
    const env = loadHook({ mode: 1, upcoming: directUpcoming });
    const el = startPreroll(env, { id: TRAILER, duration: 60, readyState: 2 });
    await prerollTick(env, el, 4);
    assert.equal(env.hidden.length, 0);
    el.readyState = 3;
    await prerollTick(env, el, 0.5);
    assert.equal(env.hidden.length, 1);
});

for (const [event, reason] of [['waiting', 'preroll-waiting'], ['stalled', 'preroll-stalled']]) {
    test(`C6: '${event}' on the preroll releases the warm at once, and nothing more is fetched during it`, async () => {
        const env = loadHook({ mode: 1, upcoming: directUpcoming });
        const el = startPreroll(env, { id: TRAILER, duration: 60, ahead: 20 });
        await prerollTick(env, el, 1.5);
        assert.equal(env.hidden.length, 1);
        const hidden = env.hidden[0];
        env.fire(el, event);
        assert.equal(env.state.active, null);
        assert.equal(history(env)[0].reason, reason);
        assert.equal(hidden.parent, null, 'hidden element removed');
        assert.equal(hidden.src, '', 'its download dropped');
        await prerollTick(env, el, 20);                                   // well buffered again
        assert.equal(env.hidden.length, 1, 'no retry during the same preroll');
        await endPreroll(env, el);
        nextOnSameElement(env, el, { id: THIRD, duration: 60, ahead: 20 });
        await prerollTick(env, el, 1.5);
        assert.equal(env.hidden.length, 2, 'the next preroll may try again');
    });
}

test('C6: the preroll losing future data or running low releases the warm', async () => {
    const env = loadHook({ mode: 1, upcoming: directUpcoming });
    const el = startPreroll(env, { id: TRAILER, duration: 60, ahead: 20 });
    await prerollTick(env, el, 1.5);
    el.readyState = 2;
    await prerollTick(env, el, 0.25);
    assert.equal(history(env)[0].reason, 'preroll-starved');
    await endPreroll(env, el);

    nextOnSameElement(env, el, { id: THIRD, duration: 60, ahead: 20 });
    await prerollTick(env, el, 1.5);
    assert.equal(env.hidden.length, 2);
    el.ahead = 3.5;
    await prerollTick(env, el, 0.25);
    assert.equal(history(env)[1].reason, 'preroll-low-buffer');
    el.ahead = 30;
    await prerollTick(env, el, 10);
    assert.equal(env.hidden.length, 2, 'no retry during the same preroll');
});

test('C6: a stall before the warm started still keeps this preroll free of prefetches', async () => {
    const env = loadHook({ mode: 1, upcoming: directUpcoming });
    const el = startPreroll(env, { id: TRAILER, duration: 60, ahead: 2 });
    await prerollTick(env, el, 2);
    env.fire(el, 'waiting');
    el.ahead = 30;
    await prerollTick(env, el, 10);
    assert.equal(env.hidden.length, 0);
    assert.equal(env.projectionistLogs().filter((l) => /the preroll stalled - no feature prefetch during it/.test(l)).length, 1);
});

test('C6: a stall while the prefetch is being set up (config.json still loading) cancels it', async () => {
    const env = loadHook({ mode: 1, upcoming: directUpcoming, webConfigDelayMs: 1000 });
    const el = startPreroll(env, { id: TRAILER, duration: 60, ahead: 20 });
    await prerollTick(env, el, 1.25);                                     // start conditions met, config.json pending
    assert.equal(env.hidden.length, 0);
    env.fire(el, 'waiting');
    await prerollTick(env, el, 3);
    assert.equal(env.hidden.length, 0);
    assert.equal(env.state.active, null);
});

test("C6: start-up 'waiting' before the preroll's first 'playing' does not count", async () => {
    const env = loadHook({ mode: 1, upcoming: directUpcoming });
    const el = new HTMLVideoElement();
    el.src = el.currentSrc = `${SERVER}/Videos/${TRAILER}/stream.mp4?Static=true`;
    el.duration = 60;
    el.readyState = 1;
    env.fire(el, 'waiting');
    el.readyState = 4;
    el.ahead = 20;
    buffer(el, 20);
    env.fire(el, 'playing');
    await prerollTick(env, el, 1.5);
    assert.equal(env.hidden.length, 1);
});

test('C6: an hls.js (MSE) preroll needs 5 s ahead, since hls.js may keep only ~6 s', async () => {
    const env = loadHook({ mode: 1, upcoming: directUpcoming });
    const el = startPreroll(env, { duration: 60, ahead: 4.5, src: 'blob:http://192.0.2.10:8096/5d1f2c7a-0000-4000-8000-000000000001' });
    await prerollTick(env, el, 3);
    assert.equal(env.hidden.length, 0);
    el.ahead = 5.5;
    await prerollTick(env, el, 0.5);
    assert.equal(env.hidden.length, 1);
    assert.ok(env.upcomingRequests().every((r) => !/currentItemId/.test(r.url)), 'blob: sources ask without an item id');
});

test('saveData: nothing is looked up or fetched', async () => {
    const env = loadHook({ mode: 1, upcoming: directUpcoming, saveData: true });
    const el = startPreroll(env, { id: TRAILER, duration: 60 });
    await prerollTick(env, el, 5);
    assert.equal(env.upcomingRequests().length, 0);
    assert.equal(env.hidden.length, 0);
});

// ======================================================================= H

const TRANSCODING_URL = fixture('jellyfin-10.11.11-transcode.url.txt').trim();
const PAGE = `${SERVER}/web/index.html#/video`;

function hotUpcoming(url = TRANSCODING_URL, ready = true) {
    return {
        FeatureId: FEATURE, IntroIds: INTROS, PlayMethod: 'Transcode', DirectPlay: null, PrefetchSeconds: 8,
        Transcode: { Url: url, Ready: ready }
    };
}

/**
 * Serves master/main/init/segments for `transcodingUrl` at the URLs hls.js 1.6.13 would request
 * (its own url-toolkit resolution, then the URL parser XHR applies) and returns them in order.
 */
function serveHls(env, { transcodingUrl = TRANSCODING_URL, master = fixture('jellyfin-10.11.11-transcode.master.m3u8'),
    main = fixture('jellyfin-10.11.11-transcode.main.m3u8'), segments = 4, segmentSpec = {} } = {}) {
    const hlsUrl = (base, rel) => new URL(buildAbsoluteURL(base, rel, { alwaysNormalize: true })).href;
    const masterUrl = hlsUrl(PAGE, env.apiClient.getUrl(transcodingUrl));     // hls.loadSource(url)
    const variant = master.split('\n').find((l) => l && !l.startsWith('#'));
    const mainUrl = hlsUrl(masterUrl, variant);
    const init = /#EXT-X-MAP:URI="([^"]+)"/.exec(main)[1];
    const segs = main.split('\n').filter((l) => l && !l.startsWith('#'));
    const urls = { master: masterUrl, main: mainUrl, init: hlsUrl(mainUrl, init), segments: segs.map((s) => hlsUrl(mainUrl, s)) };
    env.routes[urls.master] = { body: master };
    env.routes[urls.main] = { body: main };
    env.routes[urls.init] = { body: 900 };
    urls.segments.forEach((u, i) => { env.routes[u] = segmentSpec[i] || { body: 250000 }; });
    urls.expected = [urls.master, urls.main, urls.init].concat(urls.segments.slice(0, segments));
    return urls;
}

const requested = (env) => env.mediaRequests().map((r) => new URL(r.url).href);

test('H: Hot pre-downloads master, variant, init and the first segments with the URLs hls.js requests', async () => {
    const env = loadHook({ mode: 2, upcoming: hotUpcoming() });
    const urls = serveHls(env);
    const el = startPreroll(env, { id: BUMPER, duration: 30 });
    await prerollTick(env, el, 2);
    assert.deepEqual(requested(env), urls.expected, 'master, main, init, segments 0-3 (3 s each: 9 s cover 8, +1)');
    for (const r of env.mediaRequests()) {
        assert.equal(r.init.method, 'GET');
        assert.equal(r.init.cache, undefined, 'default cache mode, so the responses land in the HTTP cache');
        assert.equal(r.init.credentials, 'same-origin', 'hls.js xhr.withCredentials=false');
        assert.equal(r.init.headers, undefined, 'no extra headers: hls.js sends none');
        assert.ok(r.init.signal, 'abortable');
    }
    const h = history(env);
    assert.equal(h.length, 1);
    assert.equal(h[0].kind, 'hls');
    assert.equal(h[0].reason, 'buffered');
    assert.equal(h[0].completed, true);
    assert.equal(h[0].buffered, 12);
    assert.equal(h[0].bytes, 900 + 4 * 250000);
    assert.deepEqual(h[0].requests.map((r) => r.kind), ['master', 'main', 'init', 'segment', 'segment', 'segment', 'segment']);
    assert.ok(env.projectionistLogs().some((l) => /pre-downloading the pre-started transcode of feature/.test(l)));
    assert.ok(env.projectionistLogs().some((l) => /released feature segment pre-download .*\(buffered, 12 s buffered, 7 requests/.test(l)));
});

test('H: a remux with ~6.5 s segments needs three segments for 8 s', async () => {
    const env = loadHook({ mode: 2, upcoming: hotUpcoming(fixture('jellyfin-10.11.11-remux.url.txt').trim()) });
    const urls = serveHls(env, {
        transcodingUrl: fixture('jellyfin-10.11.11-remux.url.txt').trim(),
        master: fixture('jellyfin-10.11.11-remux.master.m3u8'), main: fixture('jellyfin-10.11.11-remux.main.m3u8'), segments: 3
    });
    const el = startPreroll(env, { id: BUMPER, duration: 30 });
    await prerollTick(env, el, 2);
    assert.deepEqual(requested(env), urls.expected);
    assert.equal(history(env)[0].buffered, 19.3);
});

test('H: waits for Ready, asking again about every 1.5 s', async () => {
    let calls = 0;
    const env = loadHook({ mode: 2, upcoming: () => hotUpcoming(TRANSCODING_URL, ++calls > 3) });
    const urls = serveHls(env);
    const el = startPreroll(env, { id: BUMPER, duration: 60 });
    await prerollTick(env, el, 3);
    assert.equal(env.mediaRequests().length, 0, 'nothing before Ready');
    await prerollTick(env, el, 3);
    assert.deepEqual(requested(env), urls.expected);
    const at = env.upcomingRequests().map((r) => r.at);
    assert.equal(at.length, 4);
    for (let i = 1; i < at.length; i++) assert.equal(at[i] - at[i - 1], 1500);
});

test('H: gives up after a bounded number of Upcoming polls when the transcode never becomes Ready', async () => {
    const env = loadHook({ mode: 2, upcoming: hotUpcoming(TRANSCODING_URL, false) });
    serveHls(env);
    const el = startPreroll(env, { id: BUMPER, duration: 60 });
    await prerollTick(env, el, 30);
    assert.equal(env.upcomingRequests().length, 8);
    assert.equal(env.mediaRequests().length, 0);
    assert.ok(env.projectionistLogs().some((l) => /its pre-started transcode is not ready - nothing pre-downloaded/.test(l)));
});

test('H: a Transcode decision whose pre-start is not running yet (Transcode null) is polled too', async () => {
    let calls = 0;
    const env = loadHook({ mode: 2, upcoming: () => (++calls < 3 ? { ...hotUpcoming(), Transcode: null } : hotUpcoming()) });
    const urls = serveHls(env);
    const el = startPreroll(env, { id: BUMPER, duration: 60 });
    await prerollTick(env, el, 6);
    assert.deepEqual(requested(env), urls.expected);
});

test('H: Warm mode leaves transcodes alone', async () => {
    const env = loadHook({ mode: 1, upcoming: hotUpcoming() });
    serveHls(env);
    const el = startPreroll(env, { id: BUMPER, duration: 60 });
    await prerollTick(env, el, 6);
    assert.equal(env.mediaRequests().length, 0);
    assert.equal(env.upcomingRequests().length, 1);
    assert.ok(env.projectionistLogs().some((l) => /will Transcode - nothing to pre-buffer in the browser/.test(l)));
});

test('H: gated like the warm - nothing while the preroll is under-buffered', async () => {
    const env = loadHook({ mode: 2, upcoming: hotUpcoming() });
    const urls = serveHls(env);
    const el = startPreroll(env, { id: TRAILER, duration: 60, ahead: 4 });
    await prerollTick(env, el, 6);
    assert.equal(env.mediaRequests().length, 0);
    el.ahead = 9;
    await prerollTick(env, el, 1);
    assert.deepEqual(requested(env), urls.expected);
});

test("H: 'waiting' on the preroll aborts the in-flight segment; nothing more during it; the next preroll retries", async () => {
    const pending = [];
    const env = loadHook({ mode: 2, upcoming: hotUpcoming() });
    const urls = serveHls(env, { segmentSpec: { 1: { body: 250000, pending } } });
    const el = startPreroll(env, { id: TRAILER, duration: 60, ahead: 30 });
    await prerollTick(env, el, 2);
    assert.deepEqual(requested(env), urls.expected.slice(0, 5), 'stuck on segment 1');
    const seg1 = env.mediaRequests()[4];
    assert.equal(seg1.init.signal.aborted, false);
    env.fire(el, 'waiting');
    assert.equal(seg1.init.signal.aborted, true, 'in-flight request aborted');
    assert.equal(history(env)[0].reason, 'preroll-waiting');
    assert.equal(history(env)[0].completed, false);
    pending.forEach((finish) => finish());
    await prerollTick(env, el, 20);
    assert.equal(env.mediaRequests().length, 5, 'nothing more during this preroll');

    await endPreroll(env, el);
    delete env.routes[urls.segments[1]].pending;
    nextOnSameElement(env, el, { id: THIRD, duration: 60, ahead: 30 });
    await prerollTick(env, el, 2);
    assert.deepEqual(requested(env).slice(5), urls.expected, 'attempt 2 (served from the HTTP cache up to segment 0)');
    assert.equal(history(env)[1].attempt, 2);
    assert.equal(history(env)[1].completed, true);
});

test('H: no new request in the last 3 s of the preroll; the preroll ending aborts the one in flight', async () => {
    const pending = [];
    const env = loadHook({ mode: 2, upcoming: hotUpcoming() });
    const urls = serveHls(env, { segmentSpec: { 0: { body: 250000, pending } } });
    const el = startPreroll(env, { id: BUMPER, duration: 8 });
    await prerollTick(env, el, 5.5);                                      // 2.5 s left, segment 0 still loading
    assert.equal(env.state.active && env.state.active.kind, 'hls', 'an in-flight request may finish near the end');
    pending.forEach((finish) => finish());
    await prerollTick(env, el, 0.25);
    assert.deepEqual(requested(env), urls.expected.slice(0, 4), 'segment 1 is not started this late');
    assert.equal(history(env)[0].reason, 'preroll-ending');

    const pending2 = [];
    const env2 = loadHook({ mode: 2, upcoming: hotUpcoming() });
    serveHls(env2, { segmentSpec: { 0: { body: 250000, pending: pending2 } } });
    const el2 = startPreroll(env2, { id: BUMPER, duration: 8 });
    await prerollTick(env2, el2, 8);
    const seg0 = env2.mediaRequests()[3];
    await endPreroll(env2, el2);
    assert.equal(seg0.init.signal.aborted, true);
    assert.equal(history(env2)[0].reason, 'preroll-ended');
});

test('H: a completed pre-download is not repeated for the same session; a new session is fetched', async () => {
    const env = loadHook({ mode: 2, upcoming: hotUpcoming() });
    serveHls(env);
    const el = startPreroll(env, { id: BUMPER, duration: 30 });
    await prerollTick(env, el, 30);
    await endPreroll(env, el);
    assert.equal(env.mediaRequests().length, 7);
    nextOnSameElement(env, el, { id: TRAILER, duration: 30 });
    await prerollTick(env, el, 5);
    assert.equal(env.mediaRequests().length, 7, 'same TranscodingUrl: done');
    await endPreroll(env, el);

    const newSession = TRANSCODING_URL.replace('PlaySessionId=5f0c4a0e7d2b4c1a9e3f6b8d2a1c0e97', 'PlaySessionId=0000000000000000000000000000beef');
    env.upcoming = hotUpcoming(newSession);
    const master = fixture('jellyfin-10.11.11-transcode.master.m3u8').replaceAll('5f0c4a0e7d2b4c1a9e3f6b8d2a1c0e97', '0000000000000000000000000000beef');
    const main = fixture('jellyfin-10.11.11-transcode.main.m3u8').replaceAll('5f0c4a0e7d2b4c1a9e3f6b8d2a1c0e97', '0000000000000000000000000000beef');
    const urls = serveHls(env, { transcodingUrl: newSession, master, main });
    nextOnSameElement(env, el, { id: THIRD, duration: 30 });
    await prerollTick(env, el, 3);
    assert.deepEqual(requested(env).slice(7), urls.expected);
});

test('H: a master with several distinct variants is left to hls.js', async () => {
    const env = loadHook({ mode: 2, upcoming: hotUpcoming() });
    const urls = serveHls(env);
    env.routes[urls.master] = {
        body: '#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1,VIDEO-RANGE=PQ\nmain.m3u8?a=1\n#EXT-X-STREAM-INF:BANDWIDTH=1,VIDEO-RANGE=SDR\nmain.m3u8?a=1&VideoCodec=h264\n'
    };
    const el = startPreroll(env, { id: BUMPER, duration: 30 });
    await prerollTick(env, el, 3);
    assert.deepEqual(requested(env), [urls.master]);
    assert.equal(history(env)[0].reason, 'variants-2');
});

test('H: a duplicated variant URI (Jellyfin level-5.0 entrance) is one variant', async () => {
    const env = loadHook({ mode: 2, upcoming: hotUpcoming() });
    const urls = serveHls(env);
    const master = fixture('jellyfin-10.11.11-transcode.master.m3u8').trim().split('\n');
    env.routes[urls.master] = { body: master.concat(['#EXT-X-STREAM-INF:BANDWIDTH=1,CODECS="avc1.640032"', master[2], '']).join('\n') };
    const el = startPreroll(env, { id: BUMPER, duration: 30 });
    await prerollTick(env, el, 3);
    assert.deepEqual(requested(env), urls.expected);
});

test('H: an HTTP error stops the pre-download and leaves the attempt for the next preroll', async () => {
    const env = loadHook({ mode: 2, upcoming: hotUpcoming() });
    const urls = serveHls(env);
    env.routes[urls.init] = { status: 500 };
    const el = startPreroll(env, { id: BUMPER, duration: 30 });
    await prerollTick(env, el, 3);
    assert.deepEqual(requested(env), urls.expected.slice(0, 3));
    assert.equal(history(env)[0].reason, 'http-500');
    assert.equal(history(env)[0].completed, false);
});

test('H: after the first media segment a cache-only lookup checks the browser kept it; nothing extra reaches the network', async () => {
    const env = loadHook({ mode: 2, upcoming: hotUpcoming() });
    const urls = serveHls(env);
    const el = startPreroll(env, { id: BUMPER, duration: 30 });
    await prerollTick(env, el, 2);
    assert.deepEqual(requested(env), urls.expected, 'the network requests are unchanged');
    assert.equal(env.cacheChecks.length, 1, 'one check, not one per request');
    const c = env.cacheChecks[0];
    assert.equal(c.url, urls.segments[0]);
    assert.equal(c.init.cache, 'only-if-cached');
    assert.equal(c.init.mode, 'same-origin', "the only mode fetch allows with 'only-if-cached'");
    assert.equal(c.init.credentials, 'same-origin');
    assert.ok(c.init.signal, 'abortable with the rest');
    assert.equal(history(env)[0].reason, 'buffered');
});

test('H: a segment the browser does not keep (private window) stops the pre-download; that session is not retried', async () => {
    const env = loadHook({ mode: 2, upcoming: hotUpcoming() });
    const urls = serveHls(env);
    env.uncached.add(urls.segments[0]);   // e.g. Chromium incognito: entries over 1/8 of its in-memory cache
    const el = startPreroll(env, { id: BUMPER, duration: 30 });
    await prerollTick(env, el, 3);
    assert.deepEqual(requested(env), urls.expected.slice(0, 4), 'segments 1-3 would be downloaded twice: not fetched');
    const h = history(env);
    assert.equal(h[0].reason, 'not-cached');
    assert.equal(h[0].completed, false);
    assert.ok(env.projectionistLogs().some((l) => /\(not-cached, .*this browser does not cache these segments, not retried\)/.test(l)));

    await prerollTick(env, el, 27);
    await endPreroll(env, el);
    nextOnSameElement(env, el, { id: TRAILER, duration: 30 });
    await prerollTick(env, el, 5);
    assert.equal(env.mediaRequests().length, 4, 'the next preroll does not try that session again');
});

test('H: a server on another origin skips the cache check (fetch allows it only same-origin)', async () => {
    const env = loadHook({ mode: 2, upcoming: hotUpcoming(), server: 'http://198.51.100.7:8096' });
    const urls = serveHls(env);
    const el = startPreroll(env, { id: BUMPER, duration: 30 });
    await prerollTick(env, el, 2);
    assert.equal(new URL(urls.master).origin, 'http://198.51.100.7:8096');
    assert.deepEqual(requested(env), urls.expected);
    assert.equal(env.cacheChecks.length, 0);
    assert.equal(history(env)[0].reason, 'buffered');
});

test('H: includeCorsCredentials makes the requests credentialed, like hls.js xhr.withCredentials', async () => {
    const env = loadHook({ mode: 2, upcoming: hotUpcoming(), webConfig: { includeCorsCredentials: true } });
    serveHls(env);
    const el = startPreroll(env, { id: BUMPER, duration: 30 });
    await prerollTick(env, el, 3);
    assert.equal(env.mediaRequests().length, 7);
    assert.ok(env.mediaRequests().every((r) => r.init.credentials === 'include'));
});

test('H: DirectPlay in Hot mode still uses the hidden-video warm', async () => {
    const env = loadHook({ mode: 2, upcoming: directUpcoming });
    const el = startPreroll(env, { id: BUMPER, duration: 30 });
    await prerollTick(env, el, 1.5);
    assert.equal(env.hidden.length, 1);
    assert.equal(env.mediaRequests().length, 0);
});

test('H: playlist URIs resolve against the response URL after a redirect, as hls.js (xhr.responseURL) does', async () => {
    const env = loadHook({ mode: 2, upcoming: hotUpcoming() });
    const urls = serveHls(env);
    const moved = urls.master.replace('/videos/', '/moved/videos/');
    env.routes[urls.master] = { body: fixture('jellyfin-10.11.11-transcode.master.m3u8'), finalUrl: moved };
    const variant = fixture('jellyfin-10.11.11-transcode.master.m3u8').split('\n').find((l) => l && !l.startsWith('#'));
    const mainUrl = new URL(buildAbsoluteURL(moved, variant, { alwaysNormalize: true })).href;
    env.routes[mainUrl] = { body: fixture('jellyfin-10.11.11-transcode.main.m3u8') };
    const el = startPreroll(env, { id: BUMPER, duration: 30 });
    await prerollTick(env, el, 3);
    assert.equal(requested(env)[1], mainUrl);
    assert.match(requested(env)[2], /\/moved\/videos\/.*\/hls1\/main\/-1\.mp4\?/);
});
