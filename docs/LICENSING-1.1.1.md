# FK_YT 1.1.1 — 라이선스와 대응소스 / Licenses and corresponding source

**FK_YT 자체 코드·문서는 MIT입니다. 외부 구성요소는 각각의 원래 라이선스를 유지합니다.** 이 안내는 원문을 찾고 바이너리와 소스를 연결하기 위한 문서이며 원래 조건을 변경하지 않습니다.

**FK_YT's own code and documentation use MIT. Third-party components retain their original terms.** See [license.md](https://github.com/mobsweeper/FK_YT/blob/v1.1.1/license.md) and the unchanged repository [LICENSE](https://github.com/mobsweeper/FK_YT/blob/v1.1.1/LICENSE). Existing package notices headed `FK_YT contributors` are retained alongside the repository's `mobsweeper` notice.

## 1.1.1 배포 위치 / Release location

배포 파일과 현재 접근 상태는 [FK_YT v1.1.1](https://github.com/mobsweeper/FK_YT/releases/tag/v1.1.1)에서 확인하세요. 이 안내문이 들어 있다는 사실만으로 파일이 자동 게시되거나 공개 접근 검사가 완료되었다는 뜻은 아닙니다. 배포·검증 범위는 [RELEASE-1.1.1.md](https://github.com/mobsweeper/FK_YT/blob/v1.1.1/docs/RELEASE-1.1.1.md)에 기록합니다. 이 배포 방식에서는 실행 파일과 아래의 대응소스를 같은 릴리스에서 추가 소스 접근 비용 없이 제공합니다.

Check the v1.1.1 release above for distributed files and current access. Including this guide does not automatically publish files or verify recipient access. Binary access and matching source access must remain available together under this network-distribution arrangement. Successful local packaging or an authenticated upload alone does not verify public access.

## 동봉 구성요소와 원문 / Components and original texts

아래 경로는 실행 파일이 있는 압축 해제 폴더 또는 설치 폴더 기준입니다. 전체 구성요소와 고지 파일은 해당 인덱스를 따릅니다. 보수적으로 포함한 빌드·테스트·다른 플랫폼 의존성의 고지가 실제 Windows 바이너리에 모두 링크되었다는 뜻은 아닙니다. 선택 가능한 복수 라이선스를 모두 동시에 적용한다는 뜻도 아닙니다.

| 구성요소 / Component | 적용 조건 요약 / Terms | 원문과 소스 안내 / Retained material |
| --- | --- | --- |
| .NET / Windows Desktop Runtime 10.0.13 | MIT, included component notices and distribution terms | `notices/dotnet/` |
| Microsoft.Data.Sqlite 10.0.13; SQLitePCLRaw 3.0.5; native SQLite 3.53.4 | MIT; Apache-2.0; public domain, respectively | `notices/nuget/` |
| CPython 3.13.16 and included libraries | PSF and retained historical/library terms | `runtime/python/LICENSE.txt`, `notices/python/` |
| Python yt-dlp package 2026.8.19 | Unlicense | Package license records and `THIRD-PARTY-NOTICES.txt` |
| Standalone yt-dlp.exe 2026.08.19 | PyInstaller combined distribution: GPLv3-or-later plus credited component terms | `runtime/yt-dlp/LICENSE`, `THIRD_PARTY_LICENSES.txt`, `SOURCE-AND-LICENSE.txt`, `notices/` in that directory |
| yt-dlp-ejs 0.8.0 | Unlicense; Meriyah ISC and astring MIT notices retained | `runtime/python/Lib/site-packages/yt_dlp_ejs/yt/solver/` |
| Deno 2.9.7 | MIT and separately licensed components, including applicable MPL terms | `runtime/deno/LICENSE.md`, `notices/deno/third-party/`; seven package source archives in `sources/mpl/` |
| mpv 0.41.0-dev-gec38361c56-fkyt | GPLv2-or-later core; the supplied combination with GPLv3-enabled FFmpeg is GPLv3-or-later, with component notices retained | `notices/media/`, both media source ZIPs |
| FFmpeg / ffprobe 9.0.2, MSYS2 UCRT64 9.0.2-1 | GPLv3-enabled build and separately applicable linked-library terms | `notices/media/`, both media source ZIPs |
| NSIS 3.13, MUI2 and used installer plugins | NSIS/zlib terms and component exceptions, including the SystemCall MIT exception | Installed `notices/installer/nsis/`; installer source `build-support/nsis-original-notices/` |

The standalone yt-dlp EXE and its Python package are different distributions; [upstream licensing](https://github.com/yt-dlp/yt-dlp#licensing) explains the distinction. This FFmpeg build enables GPL and version 3; it is not an LGPL-only build. [FFmpeg's legal page](https://ffmpeg.org/legal.html) explains how enabled components affect its license. FK_YT communicates with the media tools as separate processes; their executable and library code is not relabelled MIT.

The installer uses `SetCompressor /FINAL zlib`, MUI2, `System`, `nsExec` and `nsDialogs`. The original umbrella notice is retained, including text about compression engines the installer does not use. Keeping that text does not claim that the optional bzip2 or LZMA engines are part of this installer. See the [official NSIS license](https://nsis.sourceforge.io/License) and the retained component exceptions.

## 대응소스 / Matching source files

| v1.1.1 release file | Contents and role |
| --- | --- |
| `FK_YT-1.1.1-portable.zip` | Runnable portable application and retained notices |
| `FK_YT-1.1.1-Setup-x64.exe` | Installer for the corresponding application payload |
| `FK_YT-1.1.1-source.zip` | FK_YT application source, locked inputs and public build guidance |
| `FK_YT-1.1.1-installer-source.zip` | Installer-owned source, build controls, input pins and original NSIS notices |
| `FK_YT_yt-dlp.zip` | Matching yt-dlp executable source collection and provenance |
| `FK_YT_mpv-FFmpeg-part1.zip`, `FK_YT_mpv-FFmpeg-part2.zip` | Matching media sources, patches, dependency archives, build controls and indexes; both required |
| `SHA256SUMS.txt` | Final release-file hashes |

See [SOURCE-ACCESS-1.1.1.txt](https://github.com/mobsweeper/FK_YT/blob/v1.1.1/docs/SOURCE-ACCESS-1.1.1.txt) for direct locations, all three unchanged external source hashes and mappings from historical filenames embedded in preserved notices. Archive hashes for the application and installer source are recorded in the final release [SHA256SUMS.txt](https://github.com/mobsweeper/FK_YT/releases/download/v1.1.1/SHA256SUMS.txt), avoiding a source archive having to embed its own hash.

The media ZIPs are **independent archives**, not split volumes. Download both and extract part1 followed by part2 into the same new directory. Their duplicated top-level README/index records are identical in the supplied publication repack. These ZIPs are source collections prepared by FK_YT, not official upstream releases under those filenames.

The public FK_YT application/installer source packages omit FK_YT's internal work records, test scripts and test products. This cleanup does not edit the external source collections. Upstream source archives, their tests, workflows, interface definitions, patches, original notices and build/installation recipes remain intact in the three external ZIPs. Required production build controls remain with the application and installer source.

## 보존과 재배포 / Preservation and redistribution

Retain FK_YT's MIT notice and each component's applicable copyright, license and NOTICE files. Preserve source modification notices, patches and build-control material. The mpv source collection records the `MPV_VERSION` metadata change separately from unchanged functional source. The original 1.0 notices and source records are historical material retained verbatim; this 1.1.1 guide adds current mappings. The repository LICENSE, license.md, original copyright/license texts, and LICENSES-SUPPLEMENT-1.1.txt are unchanged.

For this network distribution, offer matching corresponding source with equivalent copying access, without an extra source-access fee, and give clear directions beside binary downloads. Keep source access available while offering the binaries under that arrangement. An upstream latest-branch link is not a substitute for the matching source collection. Modified redistributions need source and change notices corresponding to their own binaries. See [GPLv3 section 6](https://www.gnu.org/licenses/gpl-3.0.html#section6) and the [GNU source-access FAQ](https://www.gnu.org/licenses/gpl-faq.html#SourceAndBinaryOnDifferentSites).

[LICENSES-SUPPLEMENT-1.1.txt](https://github.com/mobsweeper/FK_YT/blob/v1.1.1/docs/LICENSES-SUPPLEMENT-1.1.txt) retains the previously recovered `difflib 0.4.0`, `winapi-*-pc-windows-gnu 0.4.0` and `simd_helpers 0.1.0` notices, with exact origin and text hashes. The simd_helpers notice is labelled as its official LICENSE-only follow-up, not as a file originally present in that crate. No new copyright holder or origin URL is invented.

## 파일 확인 범위와 한계 / Checks and limits

The preview.6 input used for the earlier 1.1 baseline was checked directly: all 252 media files matched both the binary-side source index and the external source-side runtime map by SHA-256; all 107 distinct source entries referenced by that binary index were present in the two media ZIPs. The standalone yt-dlp.exe matched the executable hash identified by its source collection. The three external source archive hashes matched their retained publication records. Those historical checks establish that baseline file mapping, not a complete independent rebuild of every component. Release 1.1.1 preserves the same external runtime and source-collection bytes; its new app/worker code has its own matching application-source archive. Public availability and final hashes must be checked on the 1.1.1 release page.

The existing Windows source collection excludes four macOS-only crate blobs (`block 0.1.6`, `malloc_buf 0.0.6`, `objc-foundation 0.1.1`, `objc_id 0.1.1`) with unresolved standalone notice evidence. Their original cross-platform lockfile entries are retained. This is not an offline source set for every target platform; no further exclusions are introduced for 1.1.1.

The supplier's exact historical final librsvg Cargo.lock was not captured. The collection retains the upstream lock, matching source/recipe records and a labelled reconstruction of the supplier's Windows dependency changes. Existing checks found no new concrete Windows source omission; a complete external-cohort rebuild and independent supplier-package GPG verification were not performed. A matching binary hash is not itself a substitute for corresponding source, and lack of a bit-identical rebuild alone does not establish a missing source file.

This document records supplied files, checked mappings, retained terms and the distribution arrangement. It is not a guarantee for every jurisdiction, patent, trademark or use of downloaded content. Original license grants and conditions control; this guide adds no restriction.
