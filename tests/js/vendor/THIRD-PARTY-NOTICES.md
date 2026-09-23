# Third-party notices: tests/js/vendor

Projectionist is MIT-licensed (see [LICENSE](../../../LICENSE)). The files in this directory are
not: they are third-party code under their own licences, used only by the JavaScript tests. None of
them is part of the plugin build or of a release.

## url-toolkit 2.2.5

| | |
|---|---|
| Files | `url-toolkit-2.2.5.cjs`, `url-toolkit-2.2.5.LICENSE.txt` |
| Project | url-toolkit, https://github.com/tjenkinson/url-toolkit |
| Copyright | Copyright 2016 Tom Jenkinson |
| Licence | Apache License, Version 2.0. Full text: `url-toolkit-2.2.5.LICENSE.txt` (the package's own `LICENSE`, unchanged) |
| Source | npm package `url-toolkit@2.2.5` (https://registry.npmjs.org/url-toolkit/-/url-toolkit-2.2.5.tgz), file `src/url-toolkit.js` |
| NOTICE file | The package has none, so there are no further attribution notices to carry over |
| Changes | A header comment (licence, provenance, this list of changes) was added and the file was renamed to `.cjs`. The code after the header's marker line is the npm file byte for byte (sha256 `dae8e2f4807acdd098e5363c982ba8581f54b6da7973f69e6aa5bca90f4b7ab7`); `tests/js/hook-helpers.test.mjs` checks this |

Why this library: jellyfin-web 10.11.11 plays HLS with hls.js 1.6.13, and hls.js 1.6.13 depends on
`url-toolkit` 2.2.5 exactly and bundles it unchanged (compared against `dist/hls.mjs` of the
`hls.js@1.6.13` npm package). hls.js resolves every URI it requests with it. The tests use it to
check that `playback-hook.js` requests exactly the URLs hls.js would. No code of hls.js itself
(Apache-2.0, Copyright (c) 2017 Dailymotion) is included here.

## Not in this directory

The model of jellyfin-web 10.11.11's player URLs, `tests/js/lib/jellyfin-web-10.11.11-reference.mjs`,
contains no code from jellyfin-web (GPL-2.0-or-later) or jellyfin-apiclient (MIT). It is an
independent re-implementation of their observed behaviour, written from a specification that is
kept in the file, and is part of Projectionist under its MIT licence.
