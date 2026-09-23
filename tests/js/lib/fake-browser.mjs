// A minimal fake browser for playback-hook.js: loads the real file in a node:vm context with
// just enough DOM, a manual clock (setTimeout/setInterval/Date.now), a scriptable fetch and a
// getUrl that behaves like jellyfin-apiclient's (the reference model). No npm dependencies.
import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';
import { ApiClient } from './jellyfin-web-10.11.11-reference.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
// PROJECTIONIST_HOOK=<file> runs the suite against another copy of the hook (e.g. an older release).
export const HOOK_PATH = process.env.PROJECTIONIST_HOOK || path.resolve(HERE, '../../../src/Projectionist/Web/playback-hook.js');

export const SERVER = 'http://192.0.2.10:8096';
export const DEVICE_ID = 'TW96aWxsYS81LjAgKFgxMTsgTGludXgpfDE3MDAwMDAwMDAwMDA1';
export const TOKEN = '0123456789abcdef0123456789abcdef';

/** Lets pending promise callbacks (from both realms) run. */
export async function flush() {
    for (let i = 0; i < 20; i++) {
        await new Promise((r) => setImmediate(r));
    }
}

export class FakeClock {
    constructor(start = 1_700_000_000_000) {
        this.now = start;
        this.timers = new Map();
        this.nextId = 1;
        this.errors = [];
    }

    setTimeout(fn, ms, ...args) {
        const id = this.nextId++;
        this.timers.set(id, { at: this.now + Math.max(0, Number(ms) || 0), fn, args, every: 0 });
        return id;
    }

    setInterval(fn, ms, ...args) {
        const id = this.nextId++;
        const every = Math.max(1, Number(ms) || 0);
        this.timers.set(id, { at: this.now + every, fn, args, every });
        return id;
    }

    clear(id) {
        this.timers.delete(id);
    }

    /** Runs every timer due within `ms`, in time order, letting promises settle after each. */
    async advance(ms) {
        const end = this.now + ms;
        await flush();
        for (;;) {
            let nextId = 0;
            let next = null;
            for (const [id, t] of this.timers) {
                if (t.at <= end && (!next || t.at < next.at)) {
                    next = t;
                    nextId = id;
                }
            }
            if (!next) break;
            this.now = next.at;
            if (next.every) next.at += next.every;
            else this.timers.delete(nextId);
            try {
                next.fn(...next.args);
            } catch (e) {
                this.errors.push(e);
            }
            await flush();
        }
        this.now = end;
        await flush();
    }
}

export class FakeTimeRanges {
    constructor(ranges = []) {
        this.ranges = ranges;
    }

    get length() {
        return this.ranges.length;
    }

    start(i) {
        return this.ranges[i][0];
    }

    end(i) {
        return this.ranges[i][1];
    }
}

class FakeEventTarget {
    constructor() {
        this.listeners = {};
    }

    addEventListener(type, fn) {
        (this.listeners[type] ||= []).push(fn);
    }

    removeEventListener(type, fn) {
        const a = this.listeners[type];
        const i = a ? a.indexOf(fn) : -1;
        if (i >= 0) a.splice(i, 1);
    }

    listenerCount(type) {
        return (this.listeners[type] || []).length;
    }

    invoke(evt) {
        for (const fn of [...(this.listeners[evt.type] || [])]) fn.call(this, evt);
    }
}

class FakeElement extends FakeEventTarget {
    constructor(tag) {
        super();
        this.tagName = String(tag).toUpperCase();
        this.children = [];
        this.parent = null;
        this.attrs = new Map();
        this.style = { cssText: '' };
        const cls = new Set();
        this.classList = { add: (c) => cls.add(c), remove: (c) => cls.delete(c), contains: (c) => cls.has(c) };
        this.textContent = '';
    }

    appendChild(child) {
        child.parent = this;
        this.children.push(child);
        return child;
    }

    remove() {
        if (this.parent) {
            this.parent.children.splice(this.parent.children.indexOf(this), 1);
            this.parent = null;
        }
    }

    hasAttribute(n) {
        return this.attrs.has(n);
    }

    getAttribute(n) {
        return this.attrs.has(n) ? this.attrs.get(n) : null;
    }

    setAttribute(n, v) {
        this.attrs.set(n, String(v));
    }

    removeAttribute(n) {
        this.attrs.delete(n);
    }

    querySelectorAll() {
        return [];
    }

    querySelector() {
        return null;
    }

    closest() {
        return null;
    }
}

export class HTMLVideoElement extends FakeElement {
    constructor() {
        super('video');
        this.src = '';
        this.currentSrc = '';
        this.currentTime = 0;
        this.duration = NaN;
        this.readyState = 0;
        this.ended = false;
        this.error = null;
        this.buffered = new FakeTimeRanges();
        this.loads = 0;
    }

    removeAttribute(n) {
        super.removeAttribute(n);
        if (n === 'src') {
            this.src = '';
            this.currentSrc = '';
        }
    }

    load() {
        this.loads++;
    }
}

function response(url, spec) {
    const status = spec.status ?? 200;
    const body = spec.body ?? '';
    const bytes = typeof body === 'string' ? Buffer.from(body) : Buffer.alloc(Number(body) || 0);
    return {
        ok: status >= 200 && status < 300,
        status,
        url: spec.finalUrl ?? url,
        headers: new Map(),
        json: async () => JSON.parse(bytes.toString()),
        text: async () => bytes.toString(),
        arrayBuffer: async () => bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength),
        body: {
            cancel() {},
            getReader() {
                let sent = false;
                return {
                    read: async () => {
                        if (sent || !bytes.length) return { done: true, value: undefined };
                        sent = true;
                        return { done: false, value: new Uint8Array(bytes) };
                    },
                };
            },
        },
    };
}

function abortError() {
    const e = new Error('The operation was aborted.');
    e.name = 'AbortError';
    return e;
}

/**
 * Loads playback-hook.js into a fresh vm context.
 *   mode:      Projectionist preload mode the HookSettings endpoint reports (0 Off, 1 Warm, 2 Hot)
 *   upcoming:  object or function(url) -> object|null answering Plugins/Projectionist/Upcoming
 *   routes:    { [absoluteUrl]: { status, body, finalUrl, delayMs } | function(url, init) -> spec }
 *   webConfig: config.json body object, or null for a 404 ('throw' for a network error)
 *   webConfigDelayMs: answer config.json only after this long (fake clock)
 *   uncached:  URLs the fake HTTP cache does not keep (a private window's size limit)
 */
export function loadHook(opts = {}) {
    const clock = new FakeClock();
    const env = {
        clock,
        logs: [],
        requests: [],
        cacheChecks: [],
        cache: new Set(),
        uncached: new Set(opts.uncached || []),
        hidden: [],
        routes: opts.routes || {},
        upcoming: opts.upcoming ?? null,
        webConfig: opts.webConfig === undefined ? { includeCorsCredentials: false } : opts.webConfig,
        mode: opts.mode ?? 1,
        HTMLVideoElement,
    };

    const document = new FakeElement('#document');
    document.readyState = 'complete';
    document.baseURI = SERVER + '/web/index.html';
    document.head = new FakeElement('head');
    document.body = new FakeElement('body');
    document.documentElement = new FakeElement('html');
    document.getElementById = () => null;
    document.createElement = (tag) => {
        if (String(tag).toLowerCase() === 'video') {
            const v = new HTMLVideoElement();
            env.hidden.push(v);
            return v;
        }
        return new FakeElement(tag);
    };
    env.document = document;

    const apiClient = new ApiClient(opts.server || SERVER, DEVICE_ID, TOKEN);
    apiClient.setRequestHeaders = (h) => {
        h.Authorization = `MediaBrowser Client="Jellyfin Web", Device="Chrome", DeviceId="${DEVICE_ID}", Version="10.11.11", Token="${TOKEN}"`;
    };
    apiClient.fetch = () => Promise.resolve({ FeaturePreloadMode: env.mode, EnableSkippablePrerolls: true, SkippableAfterSeconds: 0 });
    env.apiClient = apiClient;

    function route(url, init) {
        if (url === new URL('config.json', document.baseURI).toString()) {
            if (env.webConfig === 'throw') return Promise.reject(new TypeError('Failed to fetch'));
            const res = env.webConfig ? response(url, { body: JSON.stringify(env.webConfig) }) : response(url, { status: 404 });
            if (!opts.webConfigDelayMs) return Promise.resolve(res);
            return new Promise((resolve) => clock.setTimeout(() => resolve(res), opts.webConfigDelayMs));
        }
        if (url.startsWith(apiClient.getUrl('Plugins/Projectionist/Upcoming'))) {
            const u = typeof env.upcoming === 'function' ? env.upcoming(url) : env.upcoming;
            return Promise.resolve(u ? response(url, { body: JSON.stringify(u) }) : response(url, { status: 204 }));
        }
        let spec = env.routes[url];
        if (spec === undefined) {
            try { spec = env.routes[new URL(url).href]; } catch (_) { /* not a URL */ }
        }
        if (typeof spec === 'function') spec = spec(url, init);
        if (!spec) spec = { status: 404 };
        return new Promise((resolve, reject) => {
            const signal = init && init.signal;
            if (signal && signal.aborted) {
                reject(abortError());
                return;
            }
            let done = false;
            const finish = () => {
                if (done) return;
                done = true;
                resolve(response(url, spec));
            };
            if (signal) {
                signal.addEventListener('abort', () => {
                    if (done) return;
                    done = true;
                    reject(abortError());
                });
            }
            if (spec.delayMs) clock.setTimeout(finish, spec.delayMs);
            else if (spec.pending) spec.pending.push(finish);
            else finish();
        });
    }

    const sandbox = {
        console: {
            log: (...a) => env.logs.push(a.map(String).join(' ')),
            warn: (...a) => env.logs.push(a.map(String).join(' ')),
            error: (...a) => env.logs.push(a.map(String).join(' ')),
            debug: () => {},
        },
        document,
        navigator: { connection: { saveData: !!opts.saveData } },
        HTMLVideoElement,
        MutationObserver: class {
            observe() {}
            disconnect() {}
        },
        URL,
        AbortController,
        ApiClient: apiClient,
        setTimeout: (fn, ms, ...a) => clock.setTimeout(fn, ms, ...a),
        clearTimeout: (id) => clock.clear(id),
        setInterval: (fn, ms, ...a) => clock.setInterval(fn, ms, ...a),
        clearInterval: (id) => clock.clear(id),
        fetch: (url, init) => {
            url = String(url);
            if (init && init.cache === 'only-if-cached') {
                // A cache-only lookup: answered from the fake HTTP cache (every URL fetched with a
                // 2xx answer so far, except env.uncached), never from the network.
                env.cacheChecks.push({ url, init, at: clock.now });
                if (init.mode !== 'same-origin') return Promise.reject(new TypeError("'only-if-cached' can be set only with 'same-origin' mode"));
                if (!env.cache.has(url) || env.uncached.has(url)) return Promise.reject(new TypeError('Failed to fetch'));
                return Promise.resolve(response(url, { body: 1 }));
            }
            env.requests.push({ url, init: init || {}, at: clock.now });
            return route(url, init).then((res) => {
                if (res.ok) env.cache.add(url);
                return res;
            });
        },
        __clock: clock,
        __projectionistExposeInternals: true,
    };
    sandbox.window = sandbox;
    if (opts.settings !== false) {
        sandbox.__projectionistSettings = { featurePreloadEnabled: env.mode !== 0, featurePreloadMode: env.mode, loadedAt: clock.now };
    }
    const ctx = vm.createContext(sandbox);
    vm.runInContext('Date.now = function () { return __clock.now; };', ctx);
    vm.runInContext(fs.readFileSync(HOOK_PATH, 'utf8'), ctx, { filename: 'playback-hook.js' });

    env.window = sandbox;
    env.internals = sandbox.__projectionistInternals;
    env.state = sandbox.__projectionistPrefetch;

    /** Dispatches a media event the way the browser does: document (capture) first, then the element. */
    env.fire = (el, type) => {
        const evt = { type, target: el };
        document.invoke(evt);
        el.invoke(evt);
    };
    /** Media requests the hook made (everything but config.json and Upcoming). */
    env.mediaRequests = () => env.requests.filter((r) => !/config\.json$|Plugins\/Projectionist\/Upcoming/.test(r.url));
    env.upcomingRequests = () => env.requests.filter((r) => /Plugins\/Projectionist\/Upcoming/.test(r.url));
    env.projectionistLogs = () => env.logs.filter((l) => l.startsWith('[Projectionist]'));
    return env;
}

/**
 * A playing preroll on a fresh <video>. `ahead` = seconds buffered ahead of the playhead
 * (Infinity = whole file buffered). Use prerollTick() to move it forward.
 */
export function startPreroll(env, { id, duration, ahead = Infinity, readyState = 4, src } = {}) {
    const el = new HTMLVideoElement();
    el.src = el.currentSrc = src || `${SERVER}/Videos/${id}/stream.mp4?Static=true&mediaSourceId=${id}&deviceId=d&ApiKey=t`;
    el.duration = duration;
    el.readyState = readyState;
    el.ahead = ahead;
    setBuffered(el);
    env.fire(el, 'playing');
    return el;
}

export function setBuffered(el) {
    const end = Math.min(el.duration, el.currentTime + el.ahead);
    el.buffered = new FakeTimeRanges([[0, end]]);
}

/** Plays `seconds` of the preroll in 250 ms steps on the fake clock. */
export async function prerollTick(env, el, seconds, step = 0.25) {
    for (let t = 0; t < seconds - 1e-9; t += step) {
        el.currentTime = Math.min(el.duration, el.currentTime + step);
        setBuffered(el);
        await env.clock.advance(step * 1000);
    }
}

/** The preroll ends; jellyfin-web then empties the element (resetSrc) before the next item. */
export async function endPreroll(env, el) {
    el.ended = true;
    env.fire(el, 'ended');
    el.src = el.currentSrc = '';
    env.fire(el, 'emptied');
    await env.clock.advance(0);
}

/** Reuses the element for the next item, as jellyfin-web does. */
export function nextOnSameElement(env, el, { id, duration, ahead = Infinity, readyState = 4 }) {
    el.ended = false;
    el.currentTime = 0;
    el.src = el.currentSrc = `${SERVER}/Videos/${id}/stream.mp4?Static=true&mediaSourceId=${id}&deviceId=d&ApiKey=t`;
    el.duration = duration;
    el.readyState = readyState;
    el.ahead = ahead;
    setBuffered(el);
    env.fire(el, 'playing');
    return el;
}
