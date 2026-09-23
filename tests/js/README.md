# playback-hook.js tests (node:test)

Tests for the web hook (`src/Projectionist/Web/playback-hook.js`) that run without a browser and
without npm packages. They need only Node.js 20 or newer (checked with v24).

```bash
# from the repository root
node --test tests/js/*.test.mjs
```

`dotnet test` does not run them; run both.

## What is covered

- `hook-helpers.test.mjs`: the pure helpers, checked against what they mirror:
  - `buildStaticUrl` and `transcodeMasterUrl` produce byte-for-byte the URL that jellyfin-web
    10.11.11 gives its player, for several inputs. The expected URLs come from a reference model
    (`lib/jellyfin-web-10.11.11-reference.mjs`, see below), which in turn has to reproduce a URL
    captured from the real 10.11.11 player and pass checks of the rules in its specification;
  - `resolveUri` resolves playlist URIs exactly as hls.js 1.6.13 does (with url-toolkit 2.2.5, the
    resolver hls.js bundles, vendored), followed by the URL parsing that XHR/fetch apply;
  - the vendored url-toolkit is still the npm file byte for byte (a sha256 check);
  - `corsMode`, `itemIdFromSrc`, and playlist parsing and segment selection on real Jellyfin
    10.11.11 playlists;
  - the preroll headroom checks.
- `hook-prefetch.test.mjs`: prefetch behaviour on a manual clock:
  - retry until a prefetch completes, at most 3 attempts (review finding C4);
  - the preroll comes first: start only when it has buffered well ahead, and stop on
    `waiting`/`stalled`, lost future data or a low buffer (C6 / COMPAT-5);
  - the Hot HLS segment pre-download (H): waiting for Ready, the exact request sequence and URLs,
    default cache mode, credentials, abort, dedupe per session, and the stop when the browser did
    not keep the first segment in its HTTP cache (the fake fetch models a cache for
    `cache: 'only-if-cached'`; `env.uncached` lists what it refuses).

## Layout

| path | what |
|---|---|
| `lib/fake-browser.mjs` | loads the real hook in a `node:vm` context with a small fake DOM, a manual clock (`setTimeout`/`setInterval`/`Date.now`), a scriptable `fetch` and the reference model's `ApiClient` (`getUrl`) |
| `lib/jellyfin-web-10.11.11-reference.mjs` | reference model of how jellyfin-web 10.11.11 builds its player URLs (`createStreamInfo`) and of the jellyfin-apiclient 1.11.0 `getUrl` it calls. An independent re-implementation of their observed behaviour, not a copy: the file holds the written specification (rules G1-G7, S1-S6) and code written from it. Projectionist's own code, MIT |
| `vendor/url-toolkit-2.2.5.cjs` | url-toolkit 2.2.5, the URL resolver hls.js 1.6.13 bundles (Apache-2.0, Copyright 2016 Tom Jenkinson). The npm file unchanged below a licence header. Third-party, test-only |
| `vendor/url-toolkit-2.2.5.LICENSE.txt` | url-toolkit's licence (full Apache-2.0 text), as shipped in the package |
| `vendor/THIRD-PARTY-NOTICES.md` | licence, copyright, source and changes for everything in `vendor/` |
| `fixtures/jellyfin-10.11.11-*.m3u8`, `*.url.txt` | real master/variant playlists and TranscodingUrls from a Jellyfin 10.11.11 server (HEVC transcode and H.264 remux). Device id, token and PlaySessionId are replaced with placeholders, and the variant playlists are cut to 8 segments |
| `fixtures/jellyfin-10.11.11-directplay.capture.json` | a direct-play URL that the real jellyfin-web 10.11.11 player built (`player.streamInfo.url`, stock web client, Chrome headless, token replaced with `<tok>`), with the MediaSource fields it was built from |

The hook publishes its helpers on `window.__projectionistInternals` only when
`window.__projectionistExposeInternals === true` is set before it loads, which only the tests do.

`PROJECTIONIST_HOOK=/path/to/playback-hook.js node --test tests/js/*.test.mjs` runs the suite
against another copy of the hook, for example to confirm that a test fails without its fix.

## Licences

Everything here is Projectionist's own code under its MIT licence, except `vendor/`: the
url-toolkit files there keep their Apache-2.0 licence and notices (`vendor/THIRD-PARTY-NOTICES.md`).
No code from jellyfin-web (GPL-2.0-or-later) or jellyfin-apiclient (MIT) is copied into these
tests; the reference model re-implements the behaviour the tests need from a written specification.
Keep it that way: describe new behaviour in the model's specification and implement it from
there rather than pasting jellyfin-web code.

## When the supported jellyfin-web version changes

1. Re-derive the model's rules from the new release (`createStreamInfo` in
   `src/components/playback/playbackmanager.js` and the `getUrl` of the jellyfin-apiclient it
   ships), update the specification in `lib/jellyfin-web-*-reference.mjs`, then the code and the
   "reference model follows its specification" test.
2. Capture a new player URL from the real web client (`player.streamInfo.url` after a direct
   play) into the `directplay.capture.json` fixture, and refresh the playlist fixtures.
3. Check which url-toolkit version the new hls.js depends on. If it changed, take
   `src/url-toolkit.js` and `LICENSE` from that npm package, keep the header of
   `vendor/url-toolkit-*.cjs`, and update the version and sha256 there, in
   `vendor/THIRD-PARTY-NOTICES.md` and in the check in `hook-helpers.test.mjs`.
4. Re-run the suite.
