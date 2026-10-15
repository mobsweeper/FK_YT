# FK_YT 1.0

Windows용 개인 YouTube 뷰어. 웹페이지 대신 필요한 목록과 플레이어만 띄웁니다.

**영상 연속재생** 재생 조작, 화질, 자막, 다음 영상 목록을 한 화면에 모았습니다. 플레이어만 따로 띄우고 싶으면 팝아웃을 누르면 됩니다.

![영상 재생과 하단 조작 버튼, 다음 영상 목록이 표시된 FK_YT](assets/screenshots/playback-ko.png)

| 홈 추천 | 시크릿 모드 | 설정·로그인 연결 |
| :---: | :---: | :---: |
| [![FK_YT 홈 추천 목록 로딩 화면](assets/screenshots/home-ko.png)](assets/screenshots/home-ko.png) | [![FK_YT 시크릿 모드 시작 화면](assets/screenshots/incognito-ko.png)](assets/screenshots/incognito-ko.png) | [![FK_YT 설정·로그인 연결 화면](assets/screenshots/settings-ko.png)](assets/screenshots/settings-ko.png) |
| 홈 추천 목록을 불러오는 화면입니다. | 로그인 쿠키를 사용하지 않고, 시청 기록과 이어보기 위치를 저장하지 않습니다. | 로그인 연결, 시청 기록 전송, 멤버십 영상 숨기기를 설정합니다. |

*계정 정보와 일부 썸네일은 가렸습니다. 화면 속 영상·이미지의 권리는 각 권리자에게 있습니다.*

> **1.0 배포 현황 · 2026-10-16 확인**
> 설치형 EXE와 포터블 ZIP 제작 및 아래 범위의 로컬 검증을 마쳤습니다. 현재 저장소는 **비공개**, `v1.0` 릴리스는 **드래프트**이며, 아직 일반 사용자가 다운로드할 수 있는 공개 릴리스는 없습니다. 드래프트에는 외부 구성 요소의 대응 소스 3개와 해시 파일만 첨부되어 있습니다. [파일·해시·공개 전 남은 작업](docs/RELEASE-1.0.md)을 확인해 주세요.

## 1. 언어 선택

[한국어](readme_KR.md) · [English](readme_EN.md)

앱 UI는 한국어·영어·일본어를 지원합니다. UI 언어를 바꿔도 YouTube 계정의 지역 설정은 바뀌지 않습니다.

## 2. 프로젝트 소개

FK_YT는 YouTube 웹페이지를 앱 안에 상주시켜 두지 않습니다. C# WPF로 화면을 만들고, Python 작업자와 yt-dlp로 필요한 정보를 가져오며, 영상은 mpv로 재생합니다. Deno는 yt-dlp의 JavaScript 처리를 지원하고, FFmpeg는 다운로드한 영상·음성의 결합 등에 사용합니다.

홈 추천·구독·검색·Shorts·채널·재생목록을 탐색하고, 같은 창에서 영상과 댓글을 볼 수 있습니다. 화질·자막 선택, 자동재생, 팝아웃, 영화관 모드, 다운로드도 제공합니다. 서버와 계정이 허용하는 범위에서 동작하며, 모든 영상에서 모든 기능이 제공되는 것은 아닙니다.

필요 이상의 작업을 줄이기 위해 현재 소스에는 다음 제한을 두었습니다.

| 항목 | 현재 설정 |
| --- | --- |
| 목록 조회 | 한 번에 최대 90개, 아래로 이동하면 다음 묶음 조회 |
| 썸네일 메모리 캐시 | 추정 이미지 크기 기준 96MiB |
| 썸네일 디스크 캐시 | 512MiB, 최대 8,192개 |
| 썸네일 요청 | 동시 4개, 대기 최대 128개 |
| mpv 버퍼 설정 | `cache-secs=15`, `demuxer-max-bytes=32MiB`, `demuxer-max-back-bytes=8MiB` |
| 다음 영상 준비 | 자동재생 대상 1개, 현재 영상의 남은 시간이 60초 이하일 때 준비 시도 |
| 댓글 | 버튼을 눌렀을 때 최대 100개 조회 |
| 화질·전체 자막 목록 | 메뉴를 열 때 조회하고, 사용 가능한 기존 결과는 재사용 |

**이 수치는 개별 캐시와 요청의 설정값이며, 앱 전체 RAM·GPU 메모리·CPU 사용량의 상한이 아닙니다.** 실제 사용량은 해상도, 코덱, 디코더, 영상 길이와 실행 환경에 따라 달라집니다. Firefox보다 항상 적은 자원을 쓴다거나 자동재생이 완전히 무끊김이라고 보장하지 않습니다.

## 3. 만든 이유

유튜브 좀 오래 틀어놨다고 Firefox가 램을 계속 잡아먹길래 빡쳐서 만들게됨.

FK_YT 코드랑 지금 보고 있는 이 글 전부 AI한테 시킴. 만들라고 했더니 만들어줌 ㅋㅋ

그냥 내가 쓰려고 만든 거라 A/S 없을 수 있음.

위 이야기는 제작자가 자기 PC에서 겪은 일입니다. Firefox 전체의 메모리 누수를 입증했다는 뜻은 아닙니다. AI로 작성했다는 설명은 FK_YT 자체 코드와 문서에 대한 것이며, mpv·yt-dlp 등 외부 프로젝트는 각 프로젝트의 작업입니다. 지속적인 유지보수나 향후 YouTube 변경에 대한 대응은 보장하지 않습니다.

## 4. 계정 및 개인정보

**FK_YT 자체에는 개발자 서버로 이용 기록이나 로그인 정보를 수집·전송하는 기능을 넣지 않았습니다.** 다만 영상을 보려면 외부 서비스와 통신합니다.

| 구분 | 처리 방식 |
| --- | --- |
| 설정·시청 기록·이어보기 | 앱의 `data/` 폴더에 로컬 저장 |
| 로그인 세션 | Firefox 프로필 또는 Netscape 형식 쿠키 파일에서 연결. 앱에 저장하는 세션은 Windows 사용자 계정에 묶어 DPAPI로 암호화 |
| YouTube 요청 | 목록·재생·댓글 조회 등에 YouTube/Google 및 영상·이미지 CDN 사용. 로그인 기능에는 선택한 세션 사용 |
| YouTube 시청 기록 | 일반 모드에서 기록 반영 설정이 켜져 있으면 실제 재생을 기준으로 계정 기록 전송 시도. 기본 설정은 켜짐 |
| 좋아요·싫어요·추천 피드백 | 사용자가 해당 동작을 선택하면 YouTube 계정에 요청 |
| Return YouTube Dislike | 수치 조회 시 영상 ID로 RYD API 요청. 이 경로에는 앱의 YouTube 로그인 쿠키를 붙이지 않음 |
| 빌드 도구 다운로드 | 소스 빌드 시 스크립트가 의존 도구와 NuGet 패키지 제공처에 접속 |

RYD와 YouTube 등 접속한 서비스는 일반 네트워크 요청의 IP 주소 등을 볼 수 있습니다. RYD의 싫어요 수는 추정치입니다.

시크릿 모드에서는 로그인 쿠키를 사용하지 않고 앱의 시청 기록·이어보기를 저장하지 않습니다. YouTube 계정에 기록이나 평가를 쓰는 기능도 사용하지 않습니다. **IP를 숨기는 기능은 아니며, 직접 다운로드한 파일까지 없애주지는 않습니다.**

보안에는 한계가 있습니다. 도구가 읽는 동안 쿠키가 임시 평문 파일로 존재할 수 있고, Firefox 세션을 추출하는 과정에서 원본 쿠키 DB의 임시 복사본이 생길 수 있습니다. 저장된 쿠키의 암호화가 감염된 PC나 같은 사용자 권한의 악성 프로그램까지 막아주는 것은 아닙니다. **YouTube용 Firefox 전용 프로필을 권장합니다.** `data/`, 쿠키 파일, 개인백업 ZIP은 공유하거나 GitHub에 올리지 마세요.

## 5. 실행 환경

- 설치기가 허용하는 최소 조건: **Windows 10 22H2, 빌드 19045 이상, x64(AMD64)**. Windows 11 x64도 대상입니다. x86·ARM64는 설치 대상이 아닙니다. “Windows 10 전체 지원”이라는 뜻은 아닙니다.
- 실제 설치·실행·업데이트·기본 제거 시험: **Windows 10 Pro 22H2 x64, 19045.6456**. 개발 도구가 있는 PC에서 확인했으며, 깨끗한 Windows 11 표준 사용자 환경은 아직 시험하지 않았습니다.
- 설치 허용 조건과 공급자의 지원 정책은 별개입니다. 테스트한 Windows 10 Pro 22H2를 .NET 10의 공식 지원 환경이라고 주장하지 않습니다. 에디션·수명 주기는 [.NET 10 지원 OS 표](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)를 확인하세요.
- 일반 사용자는 **.NET·Python·Deno·mpv·FFmpeg나 SDK를 따로 설치할 필요가 없습니다.** 실행에 쓰는 런타임·도구는 앱 폴더에 포함되어 있습니다. 설치기는 Windows 기본 64비트 PowerShell을 사용합니다.
- 로그인 연결에는 Firefox 또는 직접 준비한 Netscape 형식 쿠키 파일을 사용합니다. 로그인 없이 검색·일부 영상 재생도 가능합니다.

아래는 최종 clean 포터블과 이를 그대로 넣은 설치기의 실제 구성입니다. 예전 다운로드 대상을 기록한 [의존성 잠금 파일](dependencies.lock.json)만으로 만든 표가 아닙니다.

| 배포에 포함된 구성 요소 | 버전 / 빌드 | 역할 |
| --- | --- | --- |
| .NET / Windows Desktop Runtime | 둘 다 **10.0.13**, self-contained win-x64 | WPF 앱 실행 |
| Python | **3.13.16**, embedded x64 | 목록·계정 등 작업자 |
| yt-dlp EXE / Python 패키지 | **2026.08.19 / 2026.8.19** | 추출·메타데이터·다운로드 |
| yt-dlp EXE 내부 Python | **3.10.11** | 위 작업자 Python과 별개의 번들 |
| yt-dlp-ejs | **0.8.0** | JavaScript 처리 지원 |
| Deno | **2.9.7** | JavaScript 런타임 |
| mpv | **0.41.0-dev-gec38361c56-fkyt** | FK_YT에서 준비한 빌드; 영상 재생 |
| FFmpeg / ffprobe | **9.0.2**, MSYS2 UCRT64 `9.0.2-1` | GPLv3 활성화 공유 라이브러리 빌드 |
| Microsoft.Data.Sqlite / SQLitePCLRaw | **10.0.13 / 3.0.5** | 로컬 DB |
| 네이티브 SQLite 패키지 | **3.53.4** | Python에 포함된 SQLite와는 별개 |

빌드 전용 도구는 **.NET SDK 10.0.402**, 압축 해제용 **7zr 26.04**, 설치기 제작용 **NSIS 3.13**입니다. SDK·컴파일러·7zr는 사용자용 앱 폴더에 설치하지 않습니다. 설치기는 NSIS **Unicode / Modern UI 2(MUI2), 영어 한 언어, zlib 압축**을 사용합니다. NSIS의 x86 Unicode 실행 엔진이 x64 앱을 설치하는 구조입니다.

## 6. 설치 및 구동

### 일반 사용자: 어떤 파일을 받으면 되나요?

**간단하게 쓰려면 `1.0 portable.zip`을 권장합니다.** EXE 설치기는 포함된 검증 스크립트로 파일별 SHA-256을 확인하므로 이 과정에 시간이 걸릴 수 있습니다. 포터블은 설치 절차 없이 새 폴더에 전부 압축을 풀고 `FK_YT.exe`를 실행하면 됩니다. **두 방식의 앱·재생 기능과 포함된 실행 도구는 같습니다.** 설치 위치 설정, 바로가기 생성, Windows 앱 목록을 통한 제거가 편하다면 **`FK_YT-1.0-Setup-x64.exe`**를 선택하세요. 포터블에서도 배포 해시 확인은 권장합니다.

두 파일은 로컬에 완성되어 있지만 **아직 GitHub에 첨부·공개하지 않았습니다.** 공개되면 [GitHub Releases](https://github.com/mobsweeper/FK_YT/releases)의 `v1.0`에서 받는 방식입니다. `Code → Download ZIP`과 `1.0 source.zip`은 개발용 소스입니다.

| 구분 | 설치형 EXE | 포터블 ZIP |
| --- | --- | --- |
| 시작 방법 | EXE 실행 → 영어 안내에 따라 설치 → `Launch FK_YT` 또는 시작 메뉴의 FK_YT | 새 폴더에 **전부** 압축 해제 → `FK_YT` 폴더의 `FK_YT.exe` 실행 |
| 기본 위치 | `%LOCALAPPDATA%\Programs\FK_YT` | 직접 압축을 푼, 쓰기 가능한 폴더 |
| 관리자 권한 | 필요 없음; 현재 Windows 사용자용 | 필요 없음; 쓰기 가능한 위치 사용 |
| 바로가기 | 시작 메뉴 생성, 바탕화면은 선택 사항(기본 꺼짐) | 자동 생성하지 않음 |
| 데이터 | `%LOCALAPPDATA%\Programs\FK_YT\data` — 경로를 바꾸면 `<설치 폴더>\data` | `<압축 해제한 FK_YT 폴더>\data` |
| 제거 | Windows의 앱 제거 또는 설치 폴더의 `Uninstall.exe` | 앱을 닫고 폴더 삭제; `data`도 지우면 데이터까지 삭제됨 |

설치 위치는 비어 있는 폴더나 이 설치기가 관리하는 기존 설치 폴더여야 합니다. 포터블 폴더 위에 바로 설치하지 마세요. 설치 경로는 100자 이내여야 합니다. `runtime/`, `worker/`, DLL과 라이선스 고지는 그대로 두세요.

**설치 자체는 오프라인으로 가능합니다.** 필요한 런타임을 인터넷에서 추가로 받는 설치기가 아닙니다. YouTube 탐색·스트리밍·댓글·다운로드·RYD 수치 조회에는 인터넷이 필요합니다. 앱의 한국어·영어·일본어 선택은 영어 설치 화면과 별개이며 계정·지역 설정을 바꾸지 않습니다.

### 업데이트와 데이터 보존

- **설치형:** 앱을 종료하고 새 설치기를 실행해 **기존 설치 위치**를 선택합니다. 설정·로그인 정보·기록·이어보기와 직접 추가한 파일은 보존합니다. 실행 중인 앱을 닫지 않으면 업데이트가 거부될 수 있습니다. 자동 온라인 업데이트 기능은 없습니다.
- **포터블:** 앱을 종료하고 기존 폴더를 먼저 보관합니다. 새 ZIP은 별도의 빈 폴더에 풀고, 보관한 `data/`를 새 `FK_YT/` 안에 복사한 뒤 실행하세요. 설치형으로 옮길 때도 설치를 마치고 앱이 닫힌 상태에서 복사합니다.
- 로그인 정보는 Windows 사용자 계정에 묶여 있습니다. 다른 PC·Windows 계정으로 옮기면 로그인 연결을 다시 해야 할 수 있습니다.

### 제거할 때 무엇이 남나요?

설치형은 Windows **설정 → 앱 → FK_YT → 제거** 또는 `Uninstall.exe`로 제거합니다. 기본값은 **설정·로그인 정보·시청 기록·이어보기 보존**입니다. `Also permanently delete this installation's data folder`를 선택하고 추가 확인에 동의할 때만 해당 `data/`도 삭제합니다. 직접 추가하거나 변경한 파일은 남을 수 있습니다. 앱 밖에 저장한 다운로드 파일은 별도입니다.

기본 제거 후 남은 `data/` 폴더는 자동으로 새 설치본으로 인식하지 않습니다. 다시 설치할 때는 데이터를 먼저 별도로 보관하고 빈 폴더에 설치한 뒤, 앱을 닫고 `data/`를 복원하세요.

### 파일 확인과 다운로드 명령

최종 설치 EXE는 **코드 서명 없음(`NotSigned`)**으로 확인했습니다. 실제 배포 다운로드 후의 SmartScreen 경고 유무·문구는 시험하지 않았으므로 특정 경고가 반드시 뜬다고 단정하지 않습니다. [SHA256SUMS.txt](SHA256SUMS.txt)는 현재 설치 EXE와 포터블 ZIP 두 파일의 해시입니다. PowerShell에서 `Get-FileHash -Algorithm SHA256 -LiteralPath '.\FK_YT-1.0-Setup-x64.exe'`로 비교할 수 있습니다. 해시는 파일 일치 확인용이며 코드 서명을 대신하지는 않습니다.

아래는 **`v1.0` 게시와 해당 파일 첨부가 끝난 뒤에만** 사용하는 명령입니다. 지금은 비공개 드래프트이고 EXE·포터블 자산도 없어서 일반 사용자의 다운로드 명령으로 사용할 수 없습니다.

```powershell
gh release view v1.0 --repo mobsweeper/FK_YT
gh release download v1.0 --repo mobsweeper/FK_YT --pattern '1.0 portable.zip' --dir .\FK_YT-download
# 설치형을 원하면 위 다운로드 대신:
gh release download v1.0 --repo mobsweeper/FK_YT --pattern 'FK_YT-1.0-Setup-x64.exe' --dir .\FK_YT-download
```

### 개발자: 저장소 빌드와 최종 배포본은 구분해 주세요

**GitHub의 앱·작업자 코드에도 최종 clean 배포본과 같은 진단 코드 제거를 반영했습니다.** 데모·스모크 실행 경로와 진단 전용 파일을 제거했으며, 일반 오류 처리와 프로세스 정리는 유지했습니다. 기존 `1.0 source.zip`과 비교한 범위, 문서·패키징 도구의 차이는 [배포 안내](docs/RELEASE-1.0.md#source-and-build-boundaries)에 적었습니다. 앱 코드가 일치해도 아래 Bootstrap이 최종 미디어 바이너리를 재현하는 것은 아닙니다.

현재 저장소의 앱을 컴파일하려면 Git, **PowerShell 7**, 인터넷이 필요합니다. 다음 예제는 GitHub CLI도 사용합니다. 비공개 저장소 접근 권한이 있고 필요하다면 먼저 `gh auth login`으로 로그인하세요. 일반 사용자는 이 과정을 할 필요가 없습니다.

```powershell
gh repo clone mobsweeper/FK_YT
Set-Location FK_YT
pwsh -NoProfile -File .\scripts\Bootstrap.ps1 -Component Dotnet
pwsh -NoProfile -File .\scripts\Build.ps1
```

이 명령은 프로젝트 내부에 SDK를 준비하고 앱을 빌드합니다. 실행용 외부 도구를 전부 준비하는 명령은 아닙니다. `Bootstrap.ps1`의 전체 실행과 `Bootstrap-Ffmpeg.ps1`은 **예전 공급사 mpv·FFmpeg**를 준비하므로 최종 clean 런타임 복원용으로 사용하면 안 됩니다. `Publish.ps1`은 준비된 개발용 런타임으로 새 `dist/` 하위 폴더에 self-contained 실행 폴더를 만들지만, 최종 clean 패키지·NSIS 설치기·GitHub 릴리스를 만들지는 않습니다.

최종 앱 재빌드는 `1.0 source.zip`의 `release-1.0/BUILDING.txt`, 설치기 제작은 별도 `FK_YT-1.0-installer-source.zip`의 `docs/BUILDING.md`를 따릅니다. 설치기 소스는 이 저장소에 들어 있지 않습니다. 이 ZIP들도 아직 공개되지 않았습니다.

현재 저장소에는 C# 테스트 프로젝트가 없으므로 `Build.ps1 -Test`를 안내하지 않습니다. 진단 코드 제거 후 기존 SDK와 로컬 패키지 캐시를 사용한 잠금 빌드는 경고·오류 없이 통과했고, Python 오프라인 테스트 238개도 통과했습니다. 전체 Bootstrap 다운로드나 실제 앱 재생을 다시 시험한 것은 아닙니다. 다운로드·해시 검증 실패를 검증 해제로 넘기지 마세요.

도구 설치 안내: [PowerShell](https://learn.microsoft.com/en-us/powershell/scripting/install/install-powershell-on-windows) · [Git](https://git-scm.com/install/windows) · [GitHub CLI](https://github.com/cli/cli#installation)

## 7. 기본 사용법

### Firefox에 YouTube 전용 프로필 만들기

평소 쓰는 Firefox 프로필을 그대로 연결하기보다 **YouTube에만 로그인한 새 프로필**을 쓰는 것을 권장합니다.

1. Firefox 주소창에 `about:profiles`를 입력합니다.
2. **새 프로필 만들기 / Create a New Profile**를 선택하고 이름을 `FK_YT`처럼 알아보기 쉽게 정합니다. 특별한 이유가 없다면 폴더는 기본 위치를 사용합니다.
3. 새 프로필을 만든 뒤, 평소 프로필이 계속 기본값인지 확인합니다. 필요하면 기존 프로필에서 **기본 프로필로 설정**을 누릅니다.
4. 새 프로필의 **새 브라우저에서 프로필 실행 / Launch profile in new browser**를 눌러 엽니다.
5. 그 창에서 YouTube에 로그인합니다. 전용 프로필에는 다른 사이트 로그인을 추가하거나 기존 프로필 데이터를 가져오지 않는 편이 목적에 맞습니다. Mozilla 계정 동기화는 필수가 아닙니다.
6. FK_YT의 **설정 · 로그인**에서 해당 프로필을 고르고 **Firefox 세션 연결 / 갱신**을 누릅니다.
7. 목록에 없다면 **프로필 폴더 선택**을 누릅니다. `about:profiles`에 표시된 새 프로필의 **루트 디렉터리**를 선택하세요. 캐시용 로컬 디렉터리와 다릅니다.

프로필 관리 기능의 자세한 설명은 [Mozilla 공식 안내](https://support.mozilla.org/en-US/kb/profile-manager-create-remove-switch-firefox-profiles)를 참고하세요. 로그인 연결이 만료되면 전용 프로필에서 다시 로그인한 뒤 FK_YT에서 갱신합니다.

### 탐색·재생

- 검색창에 검색어 또는 YouTube 영상·채널·재생목록 URL을 넣습니다. 로그인하면 홈 추천·구독 등 계정 기능을 사용할 수 있습니다.
- 왼쪽 **YouTube 채널**에서 같은 Google 계정에 속한 채널을 전환합니다. UI 언어 선택은 YouTube의 지역 설정과 별개입니다.
- 영상 카드나 재생목록을 열어 재생합니다. 채널 이름·아이콘을 누르면 채널 영상 목록으로 이동합니다. 정렬과 홈 분류는 서버가 제공하는 항목만 표시합니다.
- 기본 화질은 **최고 화질 (자동)**입니다. 숫자 화질은 해당 해상도 이하로 제한합니다. 영상에 그 해상도가 없으면 제한 이하에서 사용 가능한 화질을 선택합니다.
- **자막 ▾**에서 끄기·제작자 자막·자동 생성 및 번역 항목을 선택합니다. 지원 언어는 해당 영상이 제공하는 목록에 따르며 영어·한국어·일본어를 우선 표시합니다.
- **댓글 보기**를 눌러야 댓글을 조회합니다. 인기순·최신순을 선택할 수 있습니다.
- 다음 영상 자동재생을 켜면 목록의 다음 항목으로 넘어갑니다. 다음 영상 준비에 실패하면 일반 로딩으로 이어질 수 있습니다.
- **팝아웃**으로 별도 창을 열고 **복귀**로 돌려놓습니다. 영화관 아이콘은 주변 UI를 줄이고, 전체화면·팝아웃에서는 유휴 시 컨트롤이 숨겨집니다.
- **공유** 또는 영상 카드 우클릭으로 영상 링크를 복사합니다. **다운로드**는 사용 권한이 있는 콘텐츠에 사용하세요.
- 로컬 이어보기와 YouTube 계정의 기록은 별개입니다. 기록 전송이 성공해도 정확한 재생 위치 동기화는 보장하지 않으며, YouTube에는 완주로 표시될 수 있습니다.

## 8. 프로젝트 구조

```text
README.md               저장소 첫 화면, 언어별 안내 링크
readme_KR.md            한국어 안내
readme_EN.md            English guide
assets/screenshots/    계정 정보를 가린 README 스크린샷
docs/RELEASE-1.0.md     배포 파일·해시·대응 소스·검증 범위
src/                    C# WPF 앱, 플레이어 제어, 계정·저장 서비스
worker/                 Python 작업자와 관련 테스트
scripts/                개발용 의존 도구 준비·빌드·로컬 Publish
dependencies.lock.json  개발 도구와 과거 공급사 런타임의 잠금 정보
SHA256SUMS.txt          현재 설치 EXE·포터블 ZIP 두 파일의 해시
LICENSE                 FK_YT 자체 코드·문서의 MIT 라이선스
```

`tools/`, `runtime/`, `dist/`, `data/`는 로컬 빌드·실행 과정에서 생기는 폴더입니다. 설치기 소스는 별도 `FK_YT-1.0-installer-source.zip`으로 준비되어 있으며, 이 저장소에는 없습니다. 사용자 데이터나 개인백업은 배포 파일에 포함하지 않습니다.

## 9. 제한 사항

- **인증:** 쿠키가 만료되거나 추가 인증이 요구되면 다시 연결해야 합니다. 연령·멤버십·지역 등 접근 제한을 우회하는 기능은 제공하지 않습니다. 일반 콘텐츠 경고 처리가 로그인이나 성인 인증을 대신하지 않습니다.
- **YouTube 변경:** 비공식 추출 경로에 의존하는 부분이 있어 사이트 변경으로 목록·재생·댓글·기록 기능이 고장 날 수 있습니다.
- **자막:** 언어 목록을 얻었더라도 실제 자막 요청이 429, 만료, 제공 중단 등으로 실패할 수 있습니다. 모든 자동번역 언어의 로딩을 보장하지 않습니다.
- **화질·재생:** 계정과 서버가 제공하는 포맷, GPU와 코덱 지원, 네트워크에 따라 달라집니다. 자동재생에도 버퍼링이 생길 수 있습니다.
- **계정 동작:** 추천 변화, 시청 기록 반영 시점, 다른 기기의 이어보기 위치는 보장하지 않습니다. RYD 추정치가 YouTube 내부 수치와 같다는 보장도 없습니다.
- **지원:** Windows 10 22H2 외 환경과 모든 DPI·GPU 조합을 검증한 것은 아닙니다. 지속적인 업데이트나 문제 해결을 보장하지 않습니다.

문제를 제보할 때는 앱 버전, Windows 버전, 시크릿 모드 여부, 재현 순서와 화면에 나온 오류를 알려주세요. 영상 URL은 공유 가능한 경우에만 포함하고, 쿠키·인증 토큰·`data/`·개인백업은 첨부하지 마세요.

## 10. YouTube 이용약관과 계정 위험

FK_YT는 YouTube/Google의 공식 앱이 아니며 승인·제휴 관계가 없습니다. 자동화된 조회, 비공식 접근, 다운로드 등은 사용 방식에 따라 [YouTube 이용약관](https://www.youtube.com/static?template=terms&hl=en)과 충돌할 수 있습니다.

불필요한 요청을 줄이도록 설계했지만, YouTube의 승인이나 약관 준수를 보장하는 프로그램은 아닙니다. 요청 제한·기능 차단·계정 제재의 발생 가능성과 확률은 확인할 수 없습니다. 요청 수가 적다는 이유로 **“제재 위험이 상당히 낮다”라고 입증된 것은 아닙니다.**

사용 전 약관과 콘텐츠 이용 권한을 확인하세요. Firefox 전용 프로필은 쿠키 노출 범위를 줄이기 위한 것으로, 계정 제재를 막아주는 장치가 아닙니다.

## 11. 라이선스와 외부 프로젝트

**FK_YT 자체 코드와 문서는 [MIT License](LICENSE)로 배포합니다.** 외부 프로젝트에는 각 프로젝트의 라이선스가 적용됩니다.

근데 제작자는 그게 뭔지 모를 거 같습니다.

아래 경로는 **설치 또는 압축 해제한 앱 폴더 기준**입니다. 전체 구성 요소·저작권·라이선스 전문은 `THIRD-PARTY-NOTICES.txt`, `notices/`, 각 런타임 폴더에 보존했습니다. 이 표는 그 원문을 대신하지 않습니다.

| 실제 포함 구성 요소 | 적용 라이선스 요약 / 고지 위치 |
| --- | --- |
| .NET / WPF, Microsoft.Data.Sqlite | MIT 및 포함 구성 요소·Microsoft 배포 조건; `notices/dotnet/`, `notices/nuget/` |
| SQLitePCLRaw / SQLite | Apache-2.0 / public domain; `notices/nuget/` |
| Python | PSF 및 역사적·포함 라이브러리 조건; `runtime/python/LICENSE.txt`, `notices/python/` |
| yt-dlp Python 패키지 / EXE | 패키지는 Unlicense, PyInstaller EXE 결합 배포물은 GPLv3-or-later; `runtime/yt-dlp/`의 `LICENSE`, `THIRD_PARTY_LICENSES.txt`, `SOURCE-AND-LICENSE.txt`, `notices/` |
| yt-dlp-ejs / Meriyah / astring | Unlicense / ISC / MIT; `runtime/python/Lib/site-packages/yt_dlp_ejs/yt/solver/`의 원문 고지 |
| Deno와 포함 구성 요소 | MIT 및 구성 요소별 조건; `runtime/deno/LICENSE.md`, `notices/deno/`. MPL 대상 소스는 `notices/deno/third-party/sources/mpl/`에 포함 |
| mpv / FFmpeg와 연결 라이브러리 | GPLv2-or-later mpv 코어와 GPLv3 활성화 FFmpeg를 결합한 배포는 GPLv3-or-later 및 라이브러리별 조건; `notices/media/`의 전문·빌드/소스 인덱스 |
| NSIS / MUI2 / zlib / 플러그인 | 주로 zlib/libpng; 실제 사용한 `System`, `nsExec`, `nsDialogs`와 SystemCall의 MIT 예외 원문 포함. **설치형에 추가된** `notices/installer/nsis/` |

yt-dlp 소스와 번들 EXE의 구분은 [프로젝트의 라이선스 설명](https://github.com/yt-dlp/yt-dlp#licensing), NSIS 구성 요소별 조건은 [NSIS 라이선스 안내](https://nsis.sourceforge.io/License)에서도 확인할 수 있습니다. 7zr는 빌드 전용이며 사용자용 배포본에는 없습니다. RYD는 외부 서비스이며 서버 소프트웨어를 번들로 배포하는 것이 아닙니다.

최종 mpv는 원본 커밋에 빌드 식별용 버전 메타데이터 변경을 적용해 FK_YT에서 만든 바이너리입니다. FFmpeg는 MSYS2 UCRT64 빌드와 해당 의존 라이브러리 묶음입니다. 예전 Bootstrap의 shinchiro/Gyan 바이너리와 같다고 표시하지 않습니다. 원본과 수정 부분의 고지·빌드 지침을 함께 보존했습니다.

대응 소스는 **FK_YT에서 준비한 모음**인 `FK_YT_yt-dlp.zip`, `FK_YT_mpv-FFmpeg-part1.zip`, `FK_YT_mpv-FFmpeg-part2.zip`으로 제공할 준비가 되어 있습니다. 각 프로젝트가 직접 배포한 공식 ZIP이라는 뜻은 아닙니다. 미디어 두 파일은 분할 압축 볼륨이 아닌 **독립 ZIP**이므로 둘 다 받아 **같은 폴더에 part1 → part2 순서로** 풉니다. 앱 실행에는 이 소스 ZIP이 필요하지 않습니다.

현재 대응 소스는 비공개 드래프트에만 첨부되어 있습니다. 공개 바이너리와 같은 접근 권한으로 받을 수 있는 실제 주소는 아직 검증하지 않았습니다. [배포 안내](docs/RELEASE-1.0.md)에 현재 파일명·해시·이전 고지와의 대응 관계, 게시 후 받는 명령을 적었습니다. 기존 고지는 보존하고 설치형의 `INSTALLER-SOURCE-ACCESS.txt`로 새 이름·해시를 보완했습니다.

번들 고지·실제 바이너리 구성·소스 파일 해시의 대조는 완료했습니다. **전체 바이너리의 동일 바이트 재빌드, 일부 의존성의 과거 잠금 파일 완전 복원, 공개 소스 접근 검증은 완료하지 않았습니다.** 소스 묶음의 일부 보조 링크도 정정이 필요합니다. 고지 원문을 넣었다는 것만으로 모든 법적 문제가 없다고 보장하지 않으며, 공개 전 남은 작업은 배포 안내에 구분했습니다.

## 12. 기타

| 항목 | 내용 |
| --- | --- |
| 코드 작성 | AI한테 시킴 |
| 이 글 작성 | 이것도 AI한테 시킴 |
| 에이전트에게 디버깅받은 횟수 (추정) | **약 165회** |
| 버그 0개 인증 | 그런 건 없음 |

2026-10-16에 확인한 병렬 검토 **11라운드 × 15개 검토 작업** 기준. 같은 에이전트에게 다시 맡긴 것도 포함했고, 메인의 자체 점검·외부 단독 리뷰·개별 추가 질문은 제외했습니다. 서로 다른 AI 165종이라는 뜻도, 버그 165개를 고쳤다는 뜻도 아님 ㅋㅋ

*정적 코드 검사만 집계한 횟수입니다. 실제 실행·재생 등을 확인한 동적 검사는 집계 불가.*
