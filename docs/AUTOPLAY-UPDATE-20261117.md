# Playback completion update — 2026-11-17

`FK_YT-autoplay-boundary-20261117` follows `FK_YT-integrated-debugfix-20261117`. It is a local package, not a newly published GitHub release. Earlier release notes refer to their own builds.

## Changes

- Automatic Next waits for the current UI load to finish before advancing, including an ordinary feed. A short video no longer loses its place in Previous history when EOF arrives during playback setup. Manual Next can still interrupt an in-progress request.
- Previous history and custom queue completion require successful setup and evidence that mpv actually opened the current media. A command acknowledgement alone no longer counts as playback. A queued item that fails before opening remains Held; a delayed successful load consumes its occurrence once.
- A file-loaded event remains associated with its media generation even when EOF arrives before mpv's playlist response. The final state remains ended, while the UI retains the evidence needed for history and autoplay.
- A provisional prepared item does not establish that evidence before adoption commits. Events from a discarded prepared entry cannot mark its fallback as opened.

Existing selected-account validation, original-audio preference, temporary-cookie ownership, queue occurrence identity, independent popout, tray behavior and responsive layout are retained. Component binaries are unchanged.

## Usage

한국어: 기존 앱을 트레이 메뉴에서 완전히 종료하고, 새 폴더에 ZIP을 풀어 `FK_YT/FK_YT.exe`를 실행하세요. 개인백업에는 저장된 설정·로그인 정보·기록이 포함되므로 본인만 보관하세요. 배포용에는 개인 데이터가 없습니다.

English: Fully exit the previous app from its tray menu, extract into a new folder and run `FK_YT/FK_YT.exe`. Keep the personal backup private because it contains saved settings, sign-in information and history. The portable distribution contains no personal data.

日本語: 旧アプリを通知アイコンのメニューから完全に終了し、ZIPを新しいフォルダーに展開して `FK_YT/FK_YT.exe` を起動してください。個人バックアップには設定・ログイン情報・履歴が含まれるため公開しないでください。配布用には個人データがありません。

The app's `RELEASE-INTEGRATED-20261117.md` contains the full trilingual queue/tray guide. Source readers can use `INTEGRATED-UPDATE-20261117.md` in this folder. The main window's X hides it in the tray; Exit fully closes it. Sign-in encryption is tied to the Windows user. The Firefox profile itself is not included in the personal backup.

## Known limits

The native format selector can still choose a lower-resolution original split stream over a higher-resolution original muxed stream. Also, translated-caption keys containing a source-language suffix can fail to carry the chosen target language to the next video. The latter needs coordinated selection across native extraction and prepared playback; a broad subtitle regex was not added because it can select extra source variants and change request counts. These issues remain unresolved rather than being included in the passing regression total.

## Verification

The exact release DLL passed 154 focused and existing regression checks, covering playback state, original-audio selection, quality, queue/history, popout, tray and WPF bindings. Local IPC fixtures exercise event ordering; they are separate from online playback.

A separate protected copy of the existing account passed two muted 1080p playback observations, each advancing for more than 10 seconds. Prepared playback reused the same mpv process and selected account with no fallback extraction. The four permanent data files in that copy remained unchanged, and no temporary plaintext cookie files remained after cleanup. The original account data was not edited. This is not a long-session or audible-quality test, nor a guarantee against every network interruption.

Product DLL SHA-256: `7571EF40DF012A12E8461A9F25F2402BFC9B7D8D2C9DBD292B47490A23951B22`.

## Components and licenses

The bundle remains on yt-dlp EXE/Python 2026.10.26, EJS 0.8.0, Deno 2.9.7, Python worker 3.13.16, .NET Runtime 10.0.13 and its existing custom mpv/FFmpeg binaries. New upstream versions were checked separately but have not been substituted or verified with this package. The .NET bundle is self-contained; users do not need the SDK.

FK_YT is MIT. External components retain their own terms. Read `THIRD-PARTY-NOTICES.txt`, `runtime/yt-dlp/SOURCE-AND-LICENSE.txt`, `SOURCE-ACCESS.txt` and `notices/media/SOURCE-ACCESS.txt`. Copyright and license texts remain unchanged. The matching source ZIP retains the existing 51 yt-dlp source/build archives. Both independent mpv/FFmpeg source ZIPs named in the media notice are required and extract into the same folder; they are not split-volume archives. Previously documented external-binary reproducibility limitations remain.

Use `SHA256SUMS.txt` alongside the portable, personal-backup and source ZIPs. The source-access notice inside the app identifies this build's matching source archive and digest. Test harnesses and internal review records are separate from the distribution.
