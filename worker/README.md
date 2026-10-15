# FK_YT feed worker

`feed_worker.py` is a list subprocess using the yt-dlp Python embedding API. Keep it alive while the current list remains active. It maintains one `YoutubeDL` instance and one lazy `entries` iterator, including YouTube's continuation state. Browsing never downloads videos, extracts individual video URLs, fetches comments, or marks a video watched. A separate explicit `recommendation.action` request can submit the user's recommendation feedback through this same cursor.

Launch it with the bundled interpreter:

```powershell
& .\runtime\python\python.exe -u .\worker\feed_worker.py
```

Use UTF-8 newline-delimited JSON on stdin/stdout. Diagnostics are suppressed, and upstream exception text, cookie values, and profile paths are never emitted. stderr is reserved for safe diagnostics. The frontend must serialize requests, keep one pagination request in flight, and discard responses with an obsolete generation.

Requests:

```json
{"command":"feed.open","requestId":"open-1","generation":1,"source":"home","batchSize":90,"cookiePath":null}
{"command":"feed.next","requestId":"next-1","generation":1,"batchSize":90}
{"command":"feed.select","requestId":"select-1","generation":2,"previousGeneration":1,"source":"home","filterId":"opaque-server-handle","batchSize":90}
{"command":"feed.close","requestId":"close-1","generation":1}
{"command":"ping","requestId":"ping-1","generation":1}
```

`feed.open` replaces the current cursor and returns the first batch. `feed.next` consumes the existing cursor; it does not rerun extraction or fetch the earlier pages again. A batch contains up to 90 items, with one normalized item retained as lookahead so `hasMore` is accurate. Unsupported entries and duplicates are skipped with a bounded scan/time budget. A terminal failure ends the cursor and requires a fresh open. If any items have already streamed, including a complete batch before lookahead fails, the response is `done` with `hasMore:false`, the delivered `count`, and a safe `warning:{code,message}`. Keep and cache those cards, display the warning, and require refresh. Failures before any item still return `error`.

Sources: `home`, `shorts`, `subscriptions`, `playlists`, `watchlater`, `history`, `search:query`, or a YouTube channel/playlist URL. Bare channel URLs open `/videos`; explicit channel tabs are limited to `videos`, `shorts`, `streams`, and `playlists`, with no extra path segments. HTTP and scheme-less list URLs are normalized to HTTPS. A watch URL with `list` opens that list. Generated `RD` Mixes preserve the watch-side seed video instead of being rewritten to an unsupported ordinary playlist page; `RD` plus an exact 11-character video ID can recover that seed. The frontend now prioritizes `list` URLs in its search box. Home and subscriptions use their actual feed URLs, and search uses `ytsearchall:query`, preserving lazy pagination without a 90-item total limit. URL-result redirects are followed a maximum of four times and cannot lead to an individual video extraction.

Events:

```json
{"type":"item","requestId":"open-1","generation":1,"item":{"id":"video-id","title":"Title","url":"https://www.youtube.com/watch?v=video-id","channel":"Channel","thumbnail":"","duration":null,"viewCount":null,"kind":"video"}}
{"type":"done","requestId":"open-1","generation":1,"hasMore":true,"count":90}
{"type":"error","requestId":"open-1","generation":1,"code":"network","message":"목록을 불러오는 중 연결에 실패했습니다. 다시 시도해 주세요."}
{"type":"pong","requestId":"ping-1","generation":1}
```

`kind` is `video`, `playlist`, or `channel`. Missing metadata is allowed. Deduplication by kind and ID spans all batches in home, Shorts, subscriptions, and search; actual playlist duplicates retain their order. `count` is the item-event count for that request. `feed.close` returns a done event with count zero and hasMore false. Error codes include `auth`, `network`, `unsupported`, `unavailable`, `internal`, and feedback-specific failures.

Home and channel-video adapters emit `metadata` before item events on open/selection, containing `title`, `filterKind`, optional `channel:{id,title,url,thumbnail,description}`, and `filters:[{id,label,selected}]`. These are YouTube's actual home chips or channel sort endpoints, never local first-page filtering. `feed.select` retains the worker/account cookie lease, validates the previous generation/source/account, resets the cursor, lookahead, and deduplication, and publishes new opaque handles. Handles remain stable during pagination and expire on explicit selection/open/close. Send the same account context when selecting. An expired/failed cursor needs a new `feed.open`.

Flat cards may include `channelId`, `channelUrl`, and `channelThumbnail`. Channel metadata comes from card renderers or the already requested watch-next response; it does not trigger individual-video extraction or another avatar lookup. Missing avatars use a UI placeholder. Only explicit validated channel identities enable navigation. Channel ordering choices are exposed only when the server provides them, including oldest when available. API commands are restricted to read-only browse/navigation endpoints; UI loading commands embedded in chip endpoints are validated and discarded.

`feed.open` also accepts `hideMembersOnly` (strict boolean, default true) and `accountContext` for the selected YouTube channel. The filter uses extractor availability metadata, excludes matching items before batch counting, and remains fixed for that cursor. It does not perform individual video checks. `youtube_session.py` validates selected identity data and prepares channel cookies; `shorts_feed.py` reads the authenticated Shorts entrance and reel sequence continuations without player requests.

When a card provides an explicit supported feedback endpoint, the item includes `recommendationActions` containing opaque per-cursor handles. The frontend sends `recommendation.action` with `generation`, `videoId`, `action`, `actionHandle`, and the same `accountContext`. The worker validates all bindings and consumes a handle once. Raw feedback tokens stay in worker memory; the frontend excludes handles from saved history/feed metadata. Only an explicit success response produces `feedback.result` with `success:true`. Ambiguous failures are not retried automatically. Incognito mode does not offer these writes.

`related_feed.py` is a separate bounded read helper for the selected video's watch-page recommendations. One JSON input line supplies `url`, an app-managed `cookiePath` (or null), `accountContext` (or null), and `maxItems` (up to 30). It calls WEB `next`, parses only the recommendation region and its continuations, and returns `type:related` with flat `items`. It also returns the current video's optional plain-text `description` from that existing first response for the title popup; no description-specific extraction is added. It excludes the source video and duplicates, limits pagination, and does not fall back to search/home. It does not fetch comments, player streams, or watched history. In incognito both cookie and account context are null. The process suppresses cookie saving and uses fixed safe error messages.

`cookiePath` must be an app-owned per-process temporary lease created by `SessionService.Acquire()`, never the user's original import file. yt-dlp reads and may update this lease directly; the C# owner must delete it after normal exit or forced Job termination. The worker deliberately makes no additional plaintext cookie copy. Replace/restart the worker when authentication changes, because yt-dlp caches its cookie jar. Preserve an idle cursor during video/comment detail navigation. Open a fresh cursor for a different source/query or refresh; generations prevent late results from being appended to the replacement list.

`export_session.py` runs only after the user clicks the frontend's Firefox connection action. It reads the chosen Firefox profile via yt-dlp, filters the in-memory cookie jar to `youtube.com` / `google.com` and their subdomains, then atomically writes only those filtered cookies to the requested Netscape output file. No all-site cookie file is written. The frontend should DPAPI-protect this temporary output and delete it immediately.

```powershell
& .\runtime\python\python.exe .\worker\export_session.py --profile "USER_SELECTED_PROFILE" --output "APP_TEMP_OUTPUT"
```

Success stdout is only `{"success":true,"count":N}`. Failures have a nonzero exit status and a fixed safe stderr message. This helper does not read yt-dlp CLI user configuration. Upstream SQLite copies and filtered staging files use a unique `export-*` child inside the app-owned `cookies-UUID.work` directory beside the output. An existing real work directory is allowed, but files, symlinks and junctions are rejected. The helper removes its own child on ordinary exit, preserving pre-existing contents; the C# owner removes its UUID container after forced termination and cleans abandoned containers at startup.

Offline fixtures require only Python's standard library and do not touch installed browser profiles or the network:

```powershell
& .\runtime\python\python.exe -m unittest discover -s worker -p test_feed_worker.py -v
```

Run `test_*.py` to include the channel, Shorts, and related-feed suites. The tests cover at least 270 lazy entries across three 90-item batches, feed deduplication, playlist duplicate order and Mix seeds, bounded redirects, end-of-feed, stale generations, malformed NDJSON, safe failures, no individual extraction, member filters, recommendation action bindings, selected channel context, related-response scope, preservation of original cookie files, and export-domain filtering. Actual authenticated feeds and YouTube-side history/feedback behavior require separate user-session validation.

Playback stream preparation and downloads are C# services outside this list worker. They use explicit per-video yt-dlp invocations with `no-mark-watched`; only the next autoplay target is prepared. The mpv process handles bounded stream prefetch, while download uses the bundled FFmpeg to merge selected video/audio into MKV.

## Explicit video ratings

`related_feed.py` now adds `rating: {value, canRate}` to the normal related result by reading the existing first `next` response. Values are `like`, `dislike`, `none`, or `unknown`. Only the primary watch toolbar and its bound status entity are parsed; recommendation/comment buttons are excluded.

The same one-shot stdin protocol accepts `command: "rating.get"` to read the current video's rating, or `command: "rating.set"` with `rating: "like" | "dislike" | "none"` after an explicit UI click. Both return `type: "rating"`, `sourceVideoId`, `value`, and `canRate`. A write requires a valid authenticated selected session and the video's matching rating capability. It sends one fixed `like/like`, `like/dislike`, or `like/removelike` request with `target.videoId`, then reads back the state. Only a matching readback returns `verified: true`; uncertain writes are never retried automatically. No renderer-provided endpoint URL is executed. Explicit `incognito: true` disables cookies and rejects writes; the frontend also blocks all private-mode writes.

Return YouTube Dislike is a separate public GET in the C# service. It never receives this worker's cookie path, account context, Authorization header, or votes. Its counts are estimates and must be attributed to https://returnyoutubedislike.com/.

## On-demand subtitle catalog

`selected_ytdlp.py` accepts `{"accountContext": null, "operation": "subtitle_catalog"}` on its existing stdin pipe (or the selected account context). The app supplies the validated video URL using its normal argument list. This operation simulates extraction, disables watched history and file output, and requests no caption files. It returns only `id`, `subtitles`, and `automatic_captions`, with one supported timedtext URL per language and kind. Translation variants from different source languages are compacted to one representative per target language; original server URLs are retained unchanged. Only HTTPS `www.youtube.com/api/timedtext` URLs are allowed, and the combined catalog is capped at 1024 entries.

The frontend checks the response video ID and URL again. It keeps the catalog in memory for the current video/account for five minutes and downloads only the caption explicitly selected by the user. Signed URLs must not be logged or persisted. The regular adapter operation without `operation` retains its existing behavior. Caption enumeration and caption availability are separate: YouTube may return a rate limit or unavailable response when the selected caption is fetched.

## Warning acknowledgement and authentication

The selected extraction paths acknowledge ordinary video warnings using `contentCheckOk` and `racyCheckOk`. This does not provide login or age verification. An authentication-required response remains an authentication failure; a real guest check of `ImxdRYvbi3Y` returned `LOGIN_REQUIRED` with zero playable streams.

The C# player displays an authentication notice. With `AutoPlayNext` enabled it waits seven seconds before trying the next video; the cancel button, disabling autoplay, returning to the list, selecting another video, and changing accounts cancel that wait. With no next video it stops. This scheduling is outside the worker. Incognito extraction keeps both cookies and account context null even when warnings or authentication failures occur.

Card-link copying is a local UI/clipboard action and submits no worker command. Title-popup text is read-only plain text. Existing-liked-video validation used only `rating.get` and the ordinary related read: no account writes, original settings/encrypted sessions/database changes, or retained temporary session copies.
