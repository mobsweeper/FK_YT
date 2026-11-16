# Resume and compact sidebar — 2026-11-17

`FK_YT-resume-layout-20261117` includes the playback completion fixes from `FK_YT-autoplay-boundary-20261117` and a consistent compact sidebar. This is a local package, not a new GitHub release. Earlier release notes describe their own builds.

## Changes

At window widths below 1250 WPF units, the sidebar now narrows to 76 units instead of hiding some labels inside a 164-unit column. Navigation, audio/incognito, channel selection, refresh and account controls all follow this compact presentation. At 1250 or wider it returns to the 188-unit labelled sidebar.

The compact channel selector still opens a native keyboard-accessible list with readable, wrapping names. Its tooltip exposes the full selected channel. Refresh keeps its existing action and localized tooltip. The account icon retains the current sign-in status in its tooltip and accessibility text. Channel order and account/region settings are unchanged.

This package retains the earlier fix that handles file-loaded and EOF arriving before a playlist response. That event order could leave an earlier build at the end screen after a near-end resume. No new seek offset, EDL timestamp adjustment, speculative seek retry or automatic reset of saved positions was introduced.

## Usage

한국어: 기존 앱을 트레이 메뉴에서 완전히 종료한 뒤, ZIP을 새 폴더에 풀어 `FK_YT/FK_YT.exe`를 실행하세요. 창을 줄이면 왼쪽 전체가 아이콘으로 축소됩니다. 채널 아이콘을 누르면 전체 이름이 있는 목록이 열리고, 그 아래 회전 화살표가 채널 목록 갱신입니다. 아이콘에 마우스를 올리면 기능을 확인할 수 있습니다. 개인백업에는 로그인 정보·설정·기록이 포함되므로 본인만 보관하세요.

English: Fully exit the old app from its tray menu, extract this ZIP into a new folder and run `FK_YT/FK_YT.exe`. A narrow window uses an icon sidebar throughout. The channel icon opens the full channel list; the circular arrow below refreshes it. Hover over an icon for its label. Keep the personal backup private: it contains sign-in information, settings and history.

日本語: 旧アプリを通知アイコンのメニューから完全に終了し、ZIPを新しいフォルダーに展開して `FK_YT/FK_YT.exe` を起動してください。ウィンドウが狭い場合は左側全体がアイコン表示になります。チャンネルのアイコンから名前の一覧を開き、その下の回転矢印で一覧を更新できます。機能名はマウスを重ねると表示されます。個人バックアップにはログイン情報・設定・履歴が含まれるため公開しないでください。

The main window's X hides the app in the tray; Exit fully closes it. Personal sign-in encryption is tied to the Windows user. The Firefox profile itself is not included. The portable distribution and source ZIP contain no personal data.

## Evidence and limits

The final DLL passed 84 focused and preservation checks: sidebar/WPF 10, saved-resume/EOF 4, prior history 8, queue/tray 24, popout/browse 28 and autoplay ordering 10. Sidebar measurements cover three languages and five size transitions per language; six actual popup windows fit within their monitors. Keyboard and pointer paths were exercised through WPF events, rather than physical user input. No binding errors were observed. Actual Windows tray registration was checked separately from a simulated Explorer restart.

Product DLL SHA-256: `D5851BFBFA5F64A38A80A47F2ECBCC51EAF8DD037C4DC5A5DF45B31F942AB70D`.

The older installed build reproduced the near-end resume/EOF ordering problem with local IPC. The preceding playback-completion build passes the same scenario. Normal saved resume, explicit start at zero, and an observed position differing from the requested resume all continue to handle EOF. An online control resumed at 120.083 seconds for a 120-second request, reached normal EOF, then adopted the next prepared video at 0.05 seconds without a new extraction.

The final DLL was also tested with a recent saved resume point in a protected account copy: 730.52 seconds requested, 730.64 seconds observed. After a seek to the final three seconds, the video ended normally; the next prepared video began at 0.067 seconds in the same mpv process and account without fallback extraction. Both decoded at 1080p and advanced for more than 10 seconds. This was a short muted test, not a long-session or audible-quality test. The original app and account data were not edited.

The reported mismatch between actual video content and the displayed resume timestamp has not yet been reproduced. These checks do not establish its cause, and this package does not claim to fix every possible timestamp or network issue. The previous limitations concerning translated-caption preferences and original split/muxed quality selection remain.

## Components, licenses and source

External tools and runtime versions are unchanged. No upstream updates were installed. FK_YT remains MIT; external components retain their own terms. Original notices are preserved. Read `THIRD-PARTY-NOTICES.txt`, `runtime/yt-dlp/SOURCE-AND-LICENSE.txt`, `SOURCE-ACCESS.txt` and `notices/media/SOURCE-ACCESS.txt` in the app. The matching source ZIP contains the existing 51 yt-dlp source/build archives. Both independent mpv/FFmpeg source ZIPs named in the media notice are required and extract into the same folder; they are not split-volume archives.

Use the adjacent `SHA256SUMS.txt` to verify the portable, personal-backup and source ZIPs. Test harnesses and internal review records are kept outside the public packages.
