# Account channel selector update (2026-11-17)

`FK_YT-channel-catalog-fix-20261117` builds on `FK_YT-resume-layout-20261117`.

- After connecting Firefox or importing a cookie file, the channel selector keeps the full list returned for that session, including when the app has yet to verify a selected channel.
- If loading the channel list fails, the selector shows the channel currently selected in the app, when one is available. You can refresh the list with the circular arrow under the selector.
- The compact sidebar displays the account status on hover, including while the account button is disabled during an operation.

## Run the portable app

**한국어**  기존 앱의 트레이 메뉴에서 **종료**를 선택하세요. ZIP을 새 폴더에 풀고 `FK_YT/FK_YT.exe`를 실행하세요. 사이드바의 채널 선택기를 눌러 목록을 열고, 필요하면 아래쪽 회전 화살표로 목록을 갱신하세요. 창이 좁을 때는 계정 아이콘에 마우스를 올려 로그인 상태를 확인할 수 있습니다. 개인 백업에는 로그인 정보, 설정, 시청 기록이 포함됩니다. 본인만 보관하세요. 암호화한 로그인 정보는 백업을 만든 PC에서 같은 Windows 사용자 계정으로 복원하세요.

**English**  Choose **Exit** from the old app's tray menu. Extract the ZIP into a new folder and run `FK_YT/FK_YT.exe`. Open the channel selector in the sidebar and use the circular arrow below it to refresh the list. In a narrow window, hover over the account icon to see your sign-in status. Keep the personal backup private because it contains sign-in information, settings and watch history. Restore encrypted sign-in information on the same computer and with the same Windows user account that created the backup.

**日本語**  旧アプリの通知領域アイコンのメニューから **終了** を選びます。ZIP を新しいフォルダーに展開し、`FK_YT/FK_YT.exe` を起動してください。サイドバーのチャンネル選択欄から一覧を開き、その下の回転矢印で更新できます。ウィンドウが狭いときは、アカウントアイコンにマウスを重ねるとログイン状態を確認できます。個人バックアップにはログイン情報、設定、視聴履歴が含まれるため、ご自身で保管してください。暗号化されたログイン情報は、バックアップを作成した PC で同じ Windows ユーザーアカウントを使って復元してください。

Use `SHA256SUMS.txt` beside the ZIPs to verify the downloads. The portable and source ZIPs contain the files needed to run or build the app. Personal settings, encrypted sessions and watch history are stored in the personal backup only.

## Source and licenses

See `docs/BUILD-1.1.2.md` in the source ZIP for the Windows x64 application build recipe. The pinned .NET SDK is 10.0.402 and the self-contained runtime is 10.0.13. FK_YT uses the MIT license. Bundled components retain their own licenses. See `THIRD-PARTY-NOTICES.txt`, `SOURCE-ACCESS.txt`, `runtime/yt-dlp/SOURCE-AND-LICENSE.txt` and `notices/media/SOURCE-ACCESS.txt` in the app. The matching source ZIP includes the 51 yt-dlp 2026.10.26 source and build archives. The media notice names the two mpv/FFmpeg source ZIPs, which are extracted into the same folder.
