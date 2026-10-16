# FK_YT 1.0 — 라이선스와 대응 소스 / Licenses and corresponding source

[한국어 안내](../readme_KR.md) · [English guide](../readme_EN.md) · [배포·검증 범위 / Distribution details](RELEASE-1.0.md)

**FK_YT 자체 코드와 문서는 MIT입니다. 외부 구성요소에는 각각의 라이선스가 적용됩니다.** 이 문서는 배포 파일에 들어 있는 원문을 찾는 안내이며, 원래 저작권자나 라이선스를 바꾸지 않습니다. 기부나 별도 사용료 조건은 추가하지 않았습니다.

**FK_YT's own code and documentation use MIT. Bundled third-party components retain their respective licenses.** This guide identifies the supplied notices and source collections; it does not replace or amend their original terms. No donation condition or additional usage fee is imposed by FK_YT.

## Original texts and scope

The repository's [LICENSE](../LICENSE) is the MIT text for FK_YT. Existing release copies headed `FK_YT contributors` are preserved; the repository's `mobsweeper` copyright line does not replace upstream authorship or those existing notices.

Paths below are relative to the extracted or installed application folder. The same external runtimes and original notices are present in the portable and installed variants.

| Component in this release | Applicable license summary | Original text / source information |
| --- | --- | --- |
| .NET / Windows Desktop Runtime 10.0.13 | MIT and the included component/distribution terms | `notices/dotnet/` |
| Microsoft.Data.Sqlite 10.0.13; SQLitePCLRaw 3.0.5; native SQLite 3.53.4 | MIT; Apache-2.0; public domain, respectively | `notices/nuget/` |
| CPython 3.13.16 and included libraries | PSF and retained historical/library terms | `runtime/python/LICENSE.txt`, `notices/python/` |
| Python yt-dlp package 2026.8.19 | Unlicense | Package license records and `THIRD-PARTY-NOTICES.txt` |
| Standalone yt-dlp EXE 2026.08.19 | Combined PyInstaller distribution: GPLv3-or-later and its credited component terms | `runtime/yt-dlp/LICENSE`, `THIRD_PARTY_LICENSES.txt`, `SOURCE-AND-LICENSE.txt`, `notices/` within that directory |
| yt-dlp-ejs 0.8.0 | Unlicense; Meriyah ISC and astring MIT notices retained | `runtime/python/Lib/site-packages/yt_dlp_ejs/yt/solver/` |
| Deno 2.9.7 | MIT plus separately licensed components, including applicable MPL terms | `runtime/deno/LICENSE.md`, `notices/deno/third-party/`; MPL source archives are included under `sources/mpl/` there |
| mpv 0.41.0-dev-gec38361c56-fkyt | GPLv2-or-later core combined with GPLv3-enabled FFmpeg and separately licensed libraries | `notices/media/`, both media source ZIPs |
| FFmpeg / ffprobe 9.0.2, MSYS2 UCRT64 9.0.2-1 | GPLv3-enabled build plus the linked libraries' own terms; not an LGPL-only bundle | `notices/media/`, both media source ZIPs |
| NSIS 3.13, MUI2 and actual installer plugins | Original NSIS/zlib notices and component exceptions, including the SystemCall MIT exception | Installed `notices/installer/nsis/`; installer source `build-support/nsis-original-notices/` |

The installer uses **zlib compression**, not the optional bzip2/LZMA engines. The original NSIS umbrella notice is nevertheless retained in full. The project's [NSIS license page](https://nsis.sourceforge.io/License) distinguishes those engines and component exceptions.

The standalone yt-dlp executable and its Unlicense Python package are different distributions; see [upstream licensing](https://github.com/yt-dlp/yt-dlp#licensing). The supplied FFmpeg configuration enables GPL components and version 3, so its terms cannot be summarized as an LGPL-only build; see [FFmpeg's explanation](https://ffmpeg.org/legal.html). FK_YT launches the media tools as separate processes; it does not relabel their executable or library code as MIT.

## Source files supplied with the binaries

Release location: [FK_YT v1.0](https://github.com/mobsweeper/FK_YT/releases/tag/v1.0). See [current distribution status](RELEASE-1.0.md#publication-status) before assuming public access. A private repository or a successful authenticated upload alone does not establish access for general recipients.

| File | Contents |
| --- | --- |
| `1.0.source.zip` | Portable app source, locked build inputs and public build guidance |
| `FK_YT-1.0-machine-app-source.zip` | Installed app source, including its per-user data-path change and build recipe |
| `FK_YT-1.0-installer-source.zip` | NSIS scripts, build helpers, tests, original notices, toolchain pins and historical build records |
| `FK_YT_yt-dlp.zip` | yt-dlp executable's corresponding-source collection and provenance |
| `FK_YT_mpv-FFmpeg-part1.zip` and `FK_YT_mpv-FFmpeg-part2.zip` | Media component sources, patches, recipes, dependency archives and indexes; both are needed |
| `machine-public-payload.zip` | Exact assembled installer input, supplied for rebuilding; not an end-user installer |

The media ZIPs are **independent archives**, not split volumes. Extract part1 and then part2 into the same new directory. Their duplicated top-level README/index records are identical. These are collections prepared by FK_YT, not official upstream ZIPs under those filenames.

Use [SOURCE-ACCESS.txt](SOURCE-ACCESS.txt) for current names, hashes and mappings from the older names embedded in unchanged packages. Use [SHA256SUMS.txt](../SHA256SUMS.txt) to identify the exact release files. A source repack changes its archive hash even when the executable source stays the same.

## Supplemental notices and completed corrections

[LICENSES-SUPPLEMENT.txt](LICENSES-SUPPLEMENT.txt) preserves the recovered upstream legal texts and their exact origins. It is also a separate release asset beside the binaries and sources.

- Exact version-associated texts were recovered for `difflib 0.4.0` and both `winapi-*-pc-windows-gnu 0.4.0` support crates.
- `simd_helpers 0.1.0` uses the official notice added in its immediate upstream LICENSE-only follow-up. It is explicitly identified as a subsequent notice rather than a file originally included in the crate.
- Four macOS-only crate blobs with unresolved standalone notice evidence were excluded from the Windows source collection. Their dependency branch is not used by the native Windows build. Original upstream cross-platform lockfiles are preserved, so this is not an offline source collection for every target platform.
- Distributor metadata and damaged reconstruction-helper URLs were repaired. Origin fields without recovery evidence are marked unknown rather than replaced by invented URLs. Retained upstream archives, patches and original legal texts were checked unchanged.
- App/installer executable source and existing runnable EXE/portable bytes were preserved. Source-side documentation, input pins and additive notices were repacked separately and have their own hashes.

The complete component indexes in the packages include conservative build/test/platform dependencies. An entry is not automatically a claim that the Windows executable links it, nor that alternative license choices all apply simultaneously.

## Redistribution

Keep FK_YT's MIT text and applicable original third-party copyright, license and NOTICE files with redistribution. Retain this supplemental mapping where unchanged packages still reference older archive names.

For this network-distribution arrangement, make the matching corresponding-source collections available alongside the binary downloads without an additional source-access fee, and give clear source directions on the download page. Preserve patches and build-control material; merely linking to an upstream project's latest branch is not the supplied source arrangement. See [GPLv3 section 6](https://www.gnu.org/licenses/gpl-3.0.html#section6) and the component's actual original terms. Modified redistributions must provide source and notices that correspond to their own changed binaries.

The same-source-location arrangement is a distribution responsibility. It is separate from whether an archive was successfully built or uploaded while private. Keep the binaries and matching source accessible together for as long as the binaries are offered under that arrangement.

## Verification limits

For librsvg 2.63.2-1, the distributed DLL hashes match the binary/source index. The supplied PKGBUILD hash matches the binary package's `.BUILDINFO`; its upstream tar and patch match the recipe's hashes, and the supplied upstream Cargo.lock matches the lock inside that tar. This check found no new concrete Windows source omission.

The supplier's exact historical final librsvg Cargo.lock was not captured; the recipe changes Windows dependencies, and the supplied reconstruction is labelled rather than presented as that original final lock. A complete bit-identical rebuild of the external tool cohort and independent supplier-package GPG verification were not performed. These remain verification limits. The [FSF FAQ](https://www.gnu.org/licenses/gpl-faq.en.html#MustSourceBuildToMatchExactHashOfBinary) distinguishes supplying the corresponding source from reproducing the exact binary hash; lack of a bit-identical rebuild alone is not evidence of a missing source file.

This guide records the checked files, terms and source arrangement. It is not a guarantee concerning every jurisdiction, patent, trademark or use of downloaded content. It adds no restriction to rights already granted by the original licenses.
