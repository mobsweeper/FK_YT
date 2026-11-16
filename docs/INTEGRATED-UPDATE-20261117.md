# Integrated update — 2026-11-17

`FK_YT-integrated-restoration-20261117` combines the account and original-audio playback fixes with the responsive interface, manual queue and notification-area behavior. This is a local package, not a newly published GitHub release. Earlier release notes describe their named builds.

## Restored and retained

- The responsive browser layout, compact navigation, grouped settings and sidebar account controls are restored. The lower sidebar order is audio-only, incognito, channel, channel refresh and account status.
- Video context menus provide play next, append and remove-from-queue actions. Display, preloading and next playback use the same candidate. Duplicate manual entries remain separate, and an unsuccessful entry is retained as held for retry.
- Closing the main window hides it in the notification area when an icon is available. The current media, position and native video window are retained. Use the icon to restore the window, control playback or exit. If registration fails, closing follows normal shutdown instead of leaving an inaccessible hidden application.
- Changing future playback preferences invalidates the prepared next item without stopping the current media when that cleanup is rejected. If cleanup cannot be confirmed, additional preloading is suppressed for that current video; the next item can still start through the regular playback path.
- Selected-account validation, prepared-item ownership, same-account fallback after failed adoption, original-audio preference, same-height HLS ranking, delayed-quality cancellation and live transport handling are retained.
- The independent popout still lets the main window browse and search while playback continues. Returning the popout preserves its media and native player. Existing account and incognito boundaries remain in place.

## Quick guide / 사용 안내 / 使い方

### 한국어

ZIP을 새 폴더에 풀고 `FK_YT/FK_YT.exe`를 실행하세요. 개인백업에는 캡처한 설정·로그인 정보·기록이 들어 있고, 배포용에는 들어 있지 않습니다. 개인백업은 본인만 보관하세요. 로그인 정보는 Windows 사용자에 묶여 있으며 Firefox 프로필 자체는 포함되지 않습니다.

영상 우클릭 메뉴에서 **다음에 재생 / 대기열에 추가 / 대기열에서 제거**를 사용할 수 있습니다. 이는 로컬 재생 순서 조작이며 YouTube의 관심 없음·채널 추천 안 함과 별개입니다. 수동 목록을 모두 재생하면 마지막 영상의 추천으로 이어집니다. 실패한 항목은 보류 표시 후 다시 추가할 수 있습니다.

본창 **X는 알림 영역으로 숨기기**입니다. 팝아웃도 함께 숨기며 현재 재생은 유지합니다. 알림 영역의 FK_YT 아이콘에서 복원하거나 **종료**를 선택하세요. 본창을 최소화하는 동작은 독립 팝아웃을 닫지 않습니다. 트레이에 있는 동안 다음 영상은 오디오 전용으로 시작하며, 지금 재생 중인 영상의 모드를 즉시 바꾸지는 않습니다. 복원 후 다음 영상부터 저장된 오디오 전용 설정을 따릅니다.

기존에 설치된 앱과 데이터는 이 ZIP 제작으로 변경되지 않았습니다. 기존 앱을 종료한 뒤 새 폴더의 앱을 실행하세요. 사용하던 폴더 위에 덮어쓰지 않으면 이전 버전으로 돌아갈 수 있습니다.

### English

Extract the ZIP into a new folder and run `FK_YT/FK_YT.exe`. The personal backup includes captured settings, sign-in data and history; the portable distribution does not. Keep the personal backup private. Sign-in encryption is tied to the Windows user, and the Firefox profile itself is not included.

Right-click a video to play it next, append it or remove it from the queue. These actions change the local playback sequence, separately from YouTube recommendation feedback. Recommendations follow the last video after a manual queue ends. Failed entries are held and can be queued again.

**The main window's X hides the app in the notification area.** A visible popout is hidden with it, while current playback continues. Restore or fully exit using the FK_YT notification icon. Minimizing the main window does not close the independent popout. While in the tray, subsequent items start in audio-only mode; the current item is not restarted. After restoring, subsequent items follow the saved audio-only setting.

The existing installation and its data were not replaced. Exit the old application before using this new copy. Keeping it in a separate folder preserves the option to return to the previous version.

### 日本語

ZIPを新しいフォルダーに展開し、`FK_YT/FK_YT.exe`を起動してください。個人バックアップには保存時点の設定・ログイン情報・履歴が含まれます。配布用には含まれません。個人バックアップは公開しないでください。ログイン情報の暗号化はWindowsユーザーに紐づき、Firefoxプロファイル自体は含まれません。

動画の右クリックメニューから、次に再生・キューに追加・キューから削除を選べます。これはローカルの再生順序の変更であり、YouTubeへのおすすめフィードバックとは別です。手動キューの終了後は最後の動画のおすすめに続きます。失敗した項目は保留され、再びキューへ追加できます。

**メインウィンドウのXは通知領域に隠す操作です。** ポップアウトも一緒に隠れますが、現在の再生は続きます。FK_YTの通知アイコンから復元または終了してください。メインウィンドウの最小化では独立ポップアウトは閉じません。トレイにある間は次の動画から音声のみで再生し、現在の動画は再起動しません。復元後の次の動画からは保存された音声設定に従います。

既存のアプリとデータは変更していません。旧アプリを終了してから新しいコピーを起動してください。別フォルダーに保管すると、以前のバージョンに戻せます。

## Known limits

Original-audio identification depends on the extractor's metadata. The reported dubbed video's URL was unavailable, so that specific video and its audible language remain unverified. Controlled catalogs confirm original-audio preference where the marker is supplied.

The native format expression still prefers an eligible original split video/audio pair over an original muxed rendition. If the split video is 720p and an original muxed rendition is 1080p, that path can select 720p. The verified VOD HLS catalog can choose the higher original rendition, but the native expression does not compare the two alternatives globally. Replacing it with a broader expression can select a dub or lower another valid original rendition; this limitation is documented rather than hidden by a partial change.

Long sessions, every live stream, and every online language/format combination are not covered by bounded tests. Preloading reduces some transition work; it does not guarantee a zero-delay transition for every server response.

## Verification

The exact product DLL passed 130 bounded regression checks: 24 media/audio parser cases, 16 pinned yt-dlp selection-preservation cases, 21 playback contracts, 3 local IPC adoption cases, 4 delayed-quality cases, 24 queue/tray cases, 10 WPF UI cases and 28 popout-browsing cases. The unresolved mixed-quality example described above is recorded separately, not counted as a fixed passing case. UI tests used isolated data and real WPF windows; tray registration used the Windows shell. Recovery used a synthetic TaskbarCreated message, without restarting Explorer.

Two actual videos played through the same selected account with 1080p frames and stereo audio streams, each advancing more than ten seconds. The second adopted the prepared playlist entry in the same mpv process without fallback extraction. This was a muted, bounded test with a temporary session copy; it does not establish human-audible language correctness or long-session reliability. No account settings, original cookies or installation files were changed. No explicit watch-history or rating submission was made by the probe.

## Build, packages and licenses

Product DLL SHA-256: `7F295A22A896B539CD3008C39B9DA974600D1D6A9327EA8C64024A633F49A2C8`.

The self-contained Windows x64 Release build uses .NET Runtime 10.0.13; the SDK is needed only for source builds. yt-dlp EXE and Python package 2026.10.26, Python 3.13.16, EJS 0.8.0, Deno 2.9.7 and the existing custom mpv/FFmpeg binaries are unchanged. No account reset, cookie edit, UI-language reset or account-region change is part of this update. Test harnesses, personal data and internal review records are excluded from the portable and source packages.

- `FK_YT-integrated-restoration-20261117-portable.zip`: application, runtimes, user guides and license notices.
- `FK_YT-integrated-restoration-20261117-personal-backup.zip`: the same application plus private saved data.
- `FK_YT-integrated-restoration-20261117-source.zip`: editable application source and the existing yt-dlp source/build collection under `third-party/yt-dlp-2026.10.26/`.

Check the archives against `SHA256SUMS.txt`. The source retains the Bootstrap/Build/Publish scripts; historical `scripts/Package.py` is not the recipe for these local update ZIPs.

FK_YT is MIT. External components retain their own licenses. The standalone Windows yt-dlp EXE is GPL-3.0-or-later; its separate Python wheel is Unlicense. Read `THIRD-PARTY-NOTICES.txt`, `runtime/yt-dlp/SOURCE-AND-LICENSE.txt`, `SOURCE-ACCESS.txt` and `notices/media/SOURCE-ACCESS.txt`. Original copyright/license texts and the 51 upstream source/build archives are retained. Both independent mpv/FFmpeg source ZIPs described in the media notice are required and extract into the same folder. Historical notices retain their original names and hashes; current source access identifies this build's source ZIP and hash. External binaries were not rebuilt, and the previously documented reproducibility limits remain.
