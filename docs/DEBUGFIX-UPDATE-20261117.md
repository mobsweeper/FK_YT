# Playback follow-up — 2026-11-17

`FK_YT-integrated-debugfix-20261117` follows `FK_YT-integrated-restoration-20261117`. This is a local package; it has not been published as a new GitHub release. Earlier notes describe their named builds.

## Changes

This update addresses two playback boundaries: terminal events during prepared-video adoption and navigation history during overlapping video requests. The responsive interface, manual queue, tray behavior, independent popout, selected-account validation, original-audio preference and delayed-quality protections from the preceding integrated build are retained.

- If a prepared item ends or fails before its adoption is committed, the existing fallback path remains available. Once committed, EOF and stream errors are handled even while the resume command or property refresh is pending. The player no longer leaves that item marked as playing after losing its terminal event.
- The temporary playback cookie lease stays alive while adoption can still fall back to native extraction. It becomes eligible for early release only after adoption commits to the direct stream. This changes temporary-file lifetime, not the original saved cookies.
- Previous-video history follows successful playback requests rather than canceled or failed selections. It survives a quality reload replacing an initial load, including a pending Previous action, without recording a video that never started or consuming history twice. Returning to the last successful item also works after a failed direct-URL selection.

## 사용 안내 / Usage / 使い方

한국어: 새 폴더에 ZIP을 풀고 `FK_YT/FK_YT.exe`를 실행하세요. 본창 X는 알림 영역으로 숨기기이며 완전 종료는 트레이 메뉴의 종료입니다. 개인백업에는 캡처 시점의 설정·로그인 정보·기록이 포함되므로 본인만 보관하세요. 배포용에는 개인 데이터가 없습니다. 로그인 데이터는 Windows 사용자에 묶여 있습니다. 이전 앱을 종료한 뒤 새 앱을 실행하세요.

English: Extract into a new folder and run `FK_YT/FK_YT.exe`, after exiting the previous copy. The main window's X hides it in the notification area; use the tray menu to fully exit. Keep the personal backup private: it contains captured settings, sign-in data and history. The portable distribution contains no personal data. Sign-in encryption is tied to the Windows user.

日本語: ZIPを新しいフォルダーに展開し、旧アプリを終了してから `FK_YT/FK_YT.exe` を起動してください。メインウィンドウのXは通知領域に隠す操作です。完全に終了するには通知アイコンのメニューを使います。個人バックアップには保存時点の設定・ログイン情報・履歴が含まれるため公開しないでください。配布用には個人データがなく、ログインの暗号化はWindowsユーザーに紐づきます。

The preceding `RELEASE-INTEGRATED-20261117.md` in the application contains the full trilingual queue/tray guide. Source readers can use `INTEGRATED-UPDATE-20261117.md` in this documentation folder.

## Verification

The Release build passed 144 offline regression checks against the exact product DLL, including compiled WPF UI, native tray registration and synthetic IPC cases. A separate online check using an isolated copy of the saved account played two videos at 1920 × 1080 for more than ten seconds each. The second video adopted the prepared entry in the same mpv process with zero fallback extractions. This was muted verification, not a listening comparison or a long-session test.

Product DLL SHA-256: `5AB7E171CAC9521A3D26936542E28210BECE4806804EBC41E4EA77FB2DB0702E`.

## Known limits

The native yt-dlp format expression can still prefer a lower-resolution original split pair over a higher-resolution original muxed rendition. Broadening the selector without a shared format classifier can select dubbed audio or reject valid video-only streams. This known condition remains documented rather than treated as fixed. Original-audio identification depends on upstream metadata. The previously reported dubbed video was unavailable for listening comparison.

## Packages and licenses

- `FK_YT-integrated-debugfix-20261117-portable.zip`: application, required runtimes, guides and license notices.
- `FK_YT-integrated-debugfix-20261117-personal-backup.zip`: the same application plus the owner's saved data; the Firefox profile itself is not included.
- `FK_YT-integrated-debugfix-20261117-source.zip`: editable source and the existing yt-dlp source/build collection.

Use the accompanying `SHA256SUMS.txt`. This update does not replace an installed application, edit the original account cookies or change account region. The self-contained Windows x64 bundle includes .NET Runtime 10.0.13; an SDK is needed only for source builds. External runtime versions and binaries are unchanged from the preceding integrated package.

FK_YT is MIT; external projects retain their own licenses. Original copyright and license texts are preserved. Read `THIRD-PARTY-NOTICES.txt`, `runtime/yt-dlp/SOURCE-AND-LICENSE.txt`, `SOURCE-ACCESS.txt` and `notices/media/SOURCE-ACCESS.txt` in the application. The current source-access notice identifies this build's source archive and hash. The source ZIP retains the 51 upstream source/build archives collected for the bundled yt-dlp EXE; the separate Python wheel has its own license. Both independent mpv/FFmpeg source ZIPs described in the media notice are needed and extract into the same folder. Historical notices remain intact. External binaries were not rebuilt, and previously documented reproducibility limits remain.
