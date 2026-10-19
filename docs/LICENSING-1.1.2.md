# FK_YT 1.1.2 — 라이선스와 대응소스 / Licenses and corresponding source

**FK_YT 자체 코드·문서는 MIT입니다. 외부 구성요소는 각 원래 라이선스를 유지합니다.** 이 문서는 1.1.2 배포 파일과 대응소스 연결을 안내하며 원래 조건을 변경하지 않습니다.

**FK_YT-owned code and documentation use MIT. Third-party components retain their original terms.** The repository `LICENSE`, MIT text in `license.md`, original component notices and `LICENSES-SUPPLEMENT-1.1.txt` are unchanged. Retained package notices headed `FK_YT contributors` remain alongside the repository's `mobsweeper` notice.

## 배포 파일과 소스 접근 / Release files and source access

The release location is [FK_YT v1.1.2](https://github.com/mobsweeper/FK_YT/releases/tag/v1.1.2); check that page for current file availability. `FK_YT-1.1.2-source.zip` is the matching common application source for `FK_YT-1.1.2-portable.zip` and `FK_YT-1.1.2-Setup-x64.exe`; the installer also has `FK_YT-1.1.2-installer-source.zip`. Use that release's `SHA256SUMS.txt` for final file hashes. See [RELEASE-1.1.2.md](RELEASE-1.1.2.md) for installation and [SOURCE-ACCESS-1.1.2.txt](SOURCE-ACCESS-1.1.2.txt) for direct source mapping.

외부 대응소스 ZIP 3개도 같은 [v1.1.2 릴리스](https://github.com/mobsweeper/FK_YT/releases/tag/v1.1.2)의 배포 구성에 포함하며 기존 소스 모음과 바이트·해시가 같습니다. 현재 파일 접근 가능 여부는 해당 페이지에서 확인하세요. 바이너리를 네트워크로 제공할 때는 대응소스도 동등한 복사 접근으로 추가 소스 접근 비용 없이 제공하고, 바이너리를 제공하는 동안 그 접근을 유지해야 합니다.

## 동봉 구성요소 / Retained components

Paths below are relative to the extracted application folder. Complete component indexes and original terms control. Conservatively retained notices for build/test/other-platform dependencies do not establish that all such components are linked into the Windows binaries, or that all alternative licenses apply simultaneously.

| Component | Terms and retained material |
| --- | --- |
| .NET / Windows Desktop Runtime 10.0.13 | MIT and included distribution/component notices; `notices/dotnet/` |
| Microsoft.Data.Sqlite 10.0.13; SQLitePCLRaw 3.0.5; native SQLite 3.53.4 | MIT; Apache-2.0; public domain, respectively; `notices/nuget/` |
| CPython 3.13.16 and included libraries | PSF and retained historical/library terms; `runtime/python/LICENSE.txt`, `notices/python/` |
| Python yt-dlp 2026.8.19 | Unlicense; package records and `THIRD-PARTY-NOTICES.txt` |
| Standalone yt-dlp.exe 2026.08.19 | PyInstaller combined distribution: GPLv3-or-later and credited component terms; `runtime/yt-dlp/LICENSE`, `THIRD_PARTY_LICENSES.txt`, `SOURCE-AND-LICENSE.txt`, component notices |
| yt-dlp-ejs 0.8.0 | Unlicense; Meriyah ISC and astring MIT notices; `runtime/python/Lib/site-packages/yt_dlp_ejs/yt/solver/` |
| Deno 2.9.7 | MIT and separately licensed components, including applicable MPL terms; `runtime/deno/LICENSE.md`, `notices/deno/third-party/`, seven source archives in `sources/mpl/` |
| mpv 0.41.0-dev-gec38361c56-fkyt | GPLv2-or-later core; supplied combination with GPLv3-enabled FFmpeg is GPLv3-or-later; `notices/media/` and both media source ZIPs |
| FFmpeg / ffprobe 9.0.2, MSYS2 UCRT64 9.0.2-1 | GPLv3-enabled build and separately applicable linked-library terms; `notices/media/` and both media source ZIPs |

The standalone yt-dlp EXE and Python package are different distributions. This FFmpeg is not an LGPL-only build. FK_YT communicates with these media tools as separate processes; their code is not relabelled MIT. The installer source retains original NSIS/MUI2/plugin notices and exact toolchain pins.

## 대응소스와 보존 / Matching source and preservation

The application source keeps production source, locks and required build/packaging controls. It excludes FK_YT tests, build outputs and personal data. This does not remove or change upstream source archives, tests, workflows, interface definitions, patches, original notices or recipes in the external collections. See [BUILD-1.1.2.md](BUILD-1.1.2.md) for the application build and the separate installer-source archive for its build controls.

The unchanged external collections are `FK_YT_yt-dlp.zip`, `FK_YT_mpv-FFmpeg-part1.zip` and `FK_YT_mpv-FFmpeg-part2.zip`. Both media ZIPs are independent archives, not split volumes: extract part1 then part2 into the same new folder. Their duplicated top-level README/index records are identical in the retained publication repack. They are FK_YT-prepared source collections, not official upstream ZIPs under these names. All three hashes and historical notice mappings remain in [SOURCE-ACCESS-1.1.2.txt](SOURCE-ACCESS-1.1.2.txt).

Keep MIT, copyright, license and NOTICE texts, source-change notices, patches and build controls. The mpv collection separately records the `MPV_VERSION` metadata change. Original 1.0/preview documents remain historical records. `LICENSES-SUPPLEMENT-1.1.txt` retains the recovered `difflib 0.4.0`, `winapi-*-pc-windows-gnu 0.4.0` and `simd_helpers 0.1.0` notices with their recorded origins and text hashes; the last is labelled as an official LICENSE-only follow-up.

Modified redistributions need notices and corresponding source for their own binaries. A link to an upstream latest branch is not a substitute. Later experimental tool downloads require sources and notices for those versions. See [GPLv3 section 6](https://www.gnu.org/licenses/gpl-3.0.html#section6) and the [GNU source-access FAQ](https://www.gnu.org/licenses/gpl-faq.html#SourceAndBinaryOnDifferentSites) for the network source-access arrangement.

## 검증 한계 / Existing verification limits

The bundled external runtime and source bytes retain the existing source mapping and digests. The 1.1.2 application and worker code have their own matching common application-source archive. Hash agreement identifies files; it does not establish a complete independent rebuild or runtime compatibility.

The Windows source collection retains the existing exclusion of four macOS-only crate blobs with unresolved standalone notice evidence: `block 0.1.6`, `malloc_buf 0.0.6`, `objc-foundation 0.1.1`, `objc_id 0.1.1`. Their original cross-platform lock entries remain. No new external-source exclusion is introduced; this is not an all-target offline source set.

The supplier's exact historical final librsvg `Cargo.lock` was not captured. The supplied upstream lock, original recipes/source records and labelled reconstruction of the Windows changes remain. A complete external-cohort rebuild and independent supplier-package GPG verification are not established. Matching hashes alone do not substitute for corresponding source, and lack of a bit-identical rebuild alone does not establish a missing source file.

This guide adds no license restriction or guarantee for every jurisdiction, patent, trademark or use of downloaded content. Original grants and conditions control.
