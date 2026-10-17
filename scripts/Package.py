"""Assemble FK_YT 1.1.1 portable from the pinned 1.0 ZIP and a normal publish.

Only the four application files, eleven production workers, public 1.1.1 documents,
and two generated metadata files may change. All other base payload bytes remain
unchanged. This recipe never builds, launches, downloads, or publishes anything.
Run with Python 3.10 or newer; the output parent directory must already exist.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import struct
import sys
import xml.etree.ElementTree as ET
import zipfile

VERSION = "1.1.1"
BASE_SHA256 = "05fd94e0901e22903db33d3bc4ee9f57bfb0509b93fb5010ef1aa6ad5df25d72"
SOURCE_ASSETS = [
    {"name": "FK_YT_yt-dlp.zip", "bytes": 165234763,
     "sha256": "e22f4df8dcb40205dc9b2b2ade373a6d6f3d3e5adab786b36f628999504c8ab2"},
    {"name": "FK_YT_mpv-FFmpeg-part1.zip", "bytes": 1608074669,
     "sha256": "b53ab130c5f1102874f7edbdfc49b0baa30fa1eb1251ad476e9e3624512c4129"},
    {"name": "FK_YT_mpv-FFmpeg-part2.zip", "bytes": 118680866,
     "sha256": "5f499d855e975df0f8c72e0e2a3d3949b13f8f121a9c241273ca2b1c0337cc53"},
]
PREFIX = "FK_YT/"
APP_FILES = ("FK_YT.exe", "FK_YT.dll", "FK_YT.deps.json", "FK_YT.runtimeconfig.json")
WORKER_NAMES = (
    "feed_worker.py", "export_session.py", "youtube_session.py", "selected_ytdlp.py",
    "toolchain_bootstrap.py", "shorts_feed.py", "recommendation_actions.py",
    "related_feed.py", "home_feed.py", "channel_feed.py", "channel_metadata.py",
)
DOCUMENTS = {
    "docs/FEATURES-1.1.1.md": "FEATURES-1.1.1.md",
    "docs/guides/사용설명서-1.1.1.txt": "사용설명서-1.1.1.txt",
    "docs/guides/User-guide-1.1.1.txt": "User-guide-1.1.1.txt",
    "docs/guides/ご利用ガイド-1.1.1.txt": "ご利用ガイド-1.1.1.txt",
    "license.md": "license.md",
    "docs/RELEASE-1.1.1.md": "RELEASE-1.1.1.md",
    "docs/LICENSING-1.1.1.md": "LICENSING-1.1.1.md",
    "docs/SOURCE-ACCESS-1.1.1.txt": "SOURCE-ACCESS-1.1.1.txt",
    "docs/LICENSES-SUPPLEMENT-1.1.txt": "LICENSES-SUPPLEMENT-1.1.txt",
}
GENERATED = {"file-manifest.json", "distribution-provenance.json"}
REPLACEMENT_ALLOWLIST = set(APP_FILES) | {"worker/" + n for n in WORKER_NAMES} | set(DOCUMENTS.values()) | GENERATED
FRAMEWORKS = {"Microsoft.NETCore.App": "10.0.13", "Microsoft.WindowsDesktop.App": "10.0.13"}
PRIVATE_ROOTS = {".git", ".codex", ".agents", "data", "tests", "artifacts", "tools", "bin", "obj", "__pycache__"}
RESERVED = {"CON", "PRN", "AUX", "NUL", *("COM" + str(i) for i in range(1, 10)), *("LPT" + str(i) for i in range(1, 10))}
CHUNK = 1024 * 1024
STAMP = (2026, 10, 17, 0, 0, 0)


class PackageError(RuntimeError):
    pass


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def encode(value: object) -> bytes:
    return (json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode("utf-8")


def safe_parts(name: str) -> list[str]:
    if not isinstance(name, str) or not name or "\\" in name or name.startswith("/"):
        raise PackageError("Unsafe relative payload path")
    parts = name.split("/")
    for part in parts:
        if (part in ("", ".", "..") or part.endswith((" ", "."))
                or any(ord(c) < 32 or c in ':<>"|?*' for c in part)
                or part.split(".")[0].upper() in RESERVED):
            raise PackageError("Unsafe relative payload path: " + repr(name))
    return parts


def checked_path(path: Path, *, file: bool = False, directory: bool = False, new: bool = False) -> Path:
    """Use lexical absolute paths and lstat, so links are rejected before reads."""
    path = Path(os.path.abspath(path))
    for current in reversed((path, *path.parents)):
        try:
            info = current.lstat()
        except FileNotFoundError:
            if current == path and new:
                continue
            raise PackageError("Missing input or output parent: " + str(current)) from None
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise PackageError("Linked or reparse input/output refused: " + str(current))
        if current != path and not stat.S_ISDIR(info.st_mode):
            raise PackageError("Input/output parent is not a directory")
        if current == path:
            if new:
                raise PackageError("Output already exists; choose a new ZIP path")
            if file and not stat.S_ISREG(info.st_mode):
                raise PackageError("Regular input file required: " + str(path))
            if directory and not stat.S_ISDIR(info.st_mode):
                raise PackageError("Input directory required: " + str(path))
    return path


def file_sha(path: Path) -> str:
    result = hashlib.sha256()
    with checked_path(path, file=True).open("rb") as stream:
        for block in iter(lambda: stream.read(CHUNK), b""):
            result.update(block)
    return result.hexdigest()


def record(name: str, data: bytes) -> dict:
    safe_parts(name)
    return {"path": name, "bytes": len(data), "sha256": digest(data)}


def read_source(root: Path, name: str, snapshots: dict) -> bytes:
    data = checked_path(root.joinpath(*safe_parts(name)), file=True).read_bytes()
    snapshots[name] = record(name, data)
    return data


def indexed(records: list) -> dict:
    result = {}
    folded = set()
    for item in records:
        name = item["path"]
        safe_parts(name)
        if name.casefold() in folded or type(item["bytes"]) is not int or item["bytes"] < 0 or not re.fullmatch(r"[0-9a-f]{64}", item["sha256"]):
            raise PackageError("Duplicate or invalid manifest record")
        folded.add(name.casefold())
        result[name] = {key: item[key] for key in ("path", "bytes", "sha256")}
    return result


def zip_inventory(archive: zipfile.ZipFile) -> dict[str, zipfile.ZipInfo]:
    result = {}
    kinds = {}
    for info in archive.infolist():
        full = info.filename[:-1] if info.is_dir() else info.filename
        parts = safe_parts(full)
        if len(parts) < 2 or parts[0] != PREFIX[:-1]:
            raise PackageError("ZIP must have one FK_YT root")
        name = "/".join(parts[1:])
        key = name.casefold()
        if key in kinds:
            raise PackageError("Duplicate or case-insensitive ZIP collision: " + name)
        mode = (info.external_attr >> 16) & 0xFFFF
        expected_kind = stat.S_IFDIR if info.is_dir() else stat.S_IFREG
        if (stat.S_IFMT(mode) not in (0, expected_kind) or info.external_attr & 0x400
                or info.flag_bits & 1):
            raise PackageError("Linked, special, or encrypted ZIP member refused: " + name)
        kinds[key] = info.is_dir()
        if parts[1].casefold() in PRIVATE_ROOTS or name.lower().endswith((".pdb", ".pyc", ".pyo")) or name.casefold().startswith("worker/test_"):
            raise PackageError("Private, test, or debug payload refused: " + name)
        if not info.is_dir():
            result[name] = info
    for key in kinds:
        for parent in ("/".join(key.split("/")[:i]) for i in range(1, len(key.split("/")))):
            if parent in kinds and not kinds[parent]:
                raise PackageError("ZIP file/directory collision")
    return result


def archive_records(archive: zipfile.ZipFile, inventory: dict) -> dict:
    result = {}
    for name, info in inventory.items():
        hasher = hashlib.sha256()
        size = 0
        with archive.open(info) as stream:
            for block in iter(lambda: stream.read(CHUNK), b""):
                size += len(block)
                hasher.update(block)
        if size != info.file_size:
            raise PackageError("ZIP length mismatch")
        result[name] = {"path": name, "bytes": size, "sha256": hasher.hexdigest()}
    return result


def verified_archive(archive: zipfile.ZipFile) -> tuple[dict, dict]:
    inventory = zip_inventory(archive)
    actual = archive_records(archive, inventory)
    manifest = json.loads(archive.read(PREFIX + "file-manifest.json"))
    if (manifest.get("schema") != 1 or manifest.get("pathBase") != PREFIX
            or manifest.get("hashAlgorithm") != "SHA-256" or manifest.get("selfExcluded") != "file-manifest.json"):
        raise PackageError("Unexpected portable file manifest format")
    if indexed(manifest["files"]) != {name: item for name, item in actual.items() if name != "file-manifest.json"}:
        raise PackageError("Full ZIP manifest/hash coverage mismatch")
    return inventory, actual


def dll_version(data: bytes) -> str:
    """Read PE VERSIONINFO statically, without loading or executing the DLL."""
    def unpack(fmt: str, offset: int):
        size = struct.calcsize(fmt)
        if offset < 0 or offset + size > len(data):
            raise PackageError("Invalid DLL PE version resource bounds")
        return struct.unpack_from(fmt, data, offset)

    if data[:2] != b"MZ":
        raise PackageError("Application DLL is not a PE image")
    pe = unpack("<I", 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        raise PackageError("Application DLL has no PE header")
    section_count = unpack("<H", pe + 6)[0]
    optional_size = unpack("<H", pe + 20)[0]
    optional = pe + 24
    magic = unpack("<H", optional)[0]
    directory = optional + {0x10B: 96, 0x20B: 112}.get(magic, -len(data) - 1)
    if directory < optional or directory + 24 > optional + optional_size:
        raise PackageError("Application DLL has no PE resource directory")
    resource_rva, resource_size = unpack("<II", directory + 16)
    sections = [unpack("<IIII", optional + optional_size + i * 40 + 8) for i in range(section_count)]

    def mapped(rva: int, size: int) -> int:
        for virtual_size, virtual_address, raw_size, raw_offset in sections:
            delta = rva - virtual_address
            if 0 <= delta and delta + size <= raw_size:
                offset = raw_offset + delta
                if offset + size <= len(data):
                    return offset
        raise PackageError("Invalid application DLL resource address")

    root = mapped(resource_rva, resource_size)

    def entries(relative: int):
        if relative < 0 or relative + 16 > resource_size:
            raise PackageError("Invalid DLL resource directory")
        named, count = unpack("<HH", root + relative + 12)
        if relative + 16 + (named + count) * 8 > resource_size:
            raise PackageError("Invalid DLL resource entries")
        return [unpack("<II", root + relative + 16 + i * 8) for i in range(named + count)]

    types = [target for name, target in entries(0) if name == 16]
    blobs = []
    for target in types:
        if not target & 0x80000000:
            raise PackageError("Invalid DLL VERSIONINFO type")
        for _, name_target in entries(target & 0x7FFFFFFF):
            if not name_target & 0x80000000:
                raise PackageError("Invalid DLL VERSIONINFO name")
            for _, language_target in entries(name_target & 0x7FFFFFFF):
                if language_target & 0x80000000 or language_target + 16 > resource_size:
                    raise PackageError("Invalid DLL VERSIONINFO language")
                rva, size = unpack("<II", root + language_target)
                offset = mapped(rva, size)
                blobs.append(data[offset:offset + size])
    versions = set()
    key = "VS_VERSION_INFO\0".encode("utf-16le")
    for blob in blobs:
        if len(blob) < 6 + len(key) or blob[6:6 + len(key)] != key:
            raise PackageError("Invalid DLL VERSIONINFO value")
        length, value_length, _ = struct.unpack_from("<HHH", blob)
        value_offset = (6 + len(key) + 3) & ~3
        if length > len(blob) or value_length != 52 or value_offset + 52 > length:
            raise PackageError("Invalid DLL fixed version length")
        fixed = struct.unpack_from("<13I", blob, value_offset)
        if fixed[0] != 0xFEEF04BD:
            raise PackageError("Invalid DLL fixed version signature")
        version = (fixed[2] >> 16, fixed[2] & 0xFFFF, fixed[3] >> 16, fixed[3] & 0xFFFF)
        product = (fixed[4] >> 16, fixed[4] & 0xFFFF, fixed[5] >> 16, fixed[5] & 0xFFFF)
        if product != version:
            raise PackageError("DLL file and product versions differ")
        versions.add(".".join(map(str, version)))
    if versions != {VERSION + ".0"}:
        raise PackageError("Application DLL version must be " + VERSION + ".0")
    return next(iter(versions))


def check_publish(overlays: dict, source_root: Path, snapshots: dict) -> None:
    project = ET.fromstring(read_source(source_root, "src/FkYouTube/FkYouTube.csproj", snapshots))
    for tag, expected in (("AssemblyName", "FK_YT"), ("Version", VERSION), ("AssemblyVersion", VERSION + ".0"), ("FileVersion", VERSION + ".0")):
        if [node.text for node in project.iter(tag)] != [expected]:
            raise PackageError("Public project version/name mismatch: " + tag)
    publish_script = read_source(source_root, "scripts/Publish.ps1", snapshots).decode("utf-8-sig")
    worker_loops = re.findall(r"foreach\s*\(\s*\$workerFile\s+in\s+@\((.*?)\)\s*\)", publish_script, re.S | re.I)
    if not worker_loops or any(tuple(re.findall(r"'([^']+)'", loop)) != WORKER_NAMES for loop in worker_loops):
        raise PackageError("Publish.ps1 production worker allowlist differs from this recipe")
    deps = json.loads(overlays["FK_YT.deps.json"])
    app_key = "FK_YT/" + VERSION
    if set(key for key in deps["libraries"] if key.startswith("FK_YT/")) != {app_key}:
        raise PackageError("Published dependency application version mismatch")
    if deps["runtimeTarget"]["name"] != ".NETCoreApp,Version=v10.0/win-x64" or not any(app_key in target for target in deps["targets"].values()):
        raise PackageError("Published application must target .NET 10 win-x64")
    frameworks = json.loads(overlays["FK_YT.runtimeconfig.json"])["runtimeOptions"]
    included = frameworks.get("includedFrameworks", [])
    if frameworks.get("tfm") != "net10.0" or len(included) != 2 or {item["name"]: item["version"] for item in included} != FRAMEWORKS or "framework" in frameworks or "frameworks" in frameworks:
        raise PackageError("Self-contained .NETCore/WindowsDesktop 10.0.13 runtime configuration required")
    dll_version(overlays["FK_YT.dll"])


def reject_private_paths(overlays: dict, paths: list[Path]) -> None:
    markers = {str(path) for path in paths} | {str(Path.home())}
    try:
        common = Path(os.path.commonpath(paths))
        if len(common.parts) > 1:
            markers.add(str(common))
    except ValueError:
        pass
    for path in markers:
        if path in ("/", "\\") or str(Path(path)) == Path(path).anchor:
            continue
        forms = {path, path.replace("\\", "/"), path.replace("/", "\\")}
        forms |= {form.replace("\\", "\\\\") for form in forms}
        for form in forms:
            for encoding in ("utf-8", "utf-16le"):
                marker = form.encode(encoding).lower()
                if any(marker in data.lower() for data in overlays.values()):
                    raise PackageError("Private source/build/user path in a release overlay")


def check_source_archive(path: Path, snapshots: dict) -> None:
    """Verify selected public inputs and this recipe against the supplied ZIP."""
    with zipfile.ZipFile(path) as archive:
        roots = set()
        names = set()
        files = {}
        for info in archive.infolist():
            parts = safe_parts(info.filename[:-1] if info.is_dir() else info.filename)
            roots.add(parts[0])
            key = info.filename.rstrip("/").casefold()
            mode = (info.external_attr >> 16) & 0xFFFF
            kind = stat.S_IFDIR if info.is_dir() else stat.S_IFREG
            if key in names or stat.S_IFMT(mode) not in (0, kind) or info.external_attr & 0x400 or info.flag_bits & 1:
                raise PackageError("Duplicate, linked, special, or encrypted source ZIP member")
            names.add(key)
            if len(parts) == 1 and info.is_dir():
                continue
            if len(parts) < 2 or any(part.casefold() in PRIVATE_ROOTS | {"runtime"} for part in parts[1:]):
                raise PackageError("Private/build/runtime path in public source ZIP")
            name = "/".join(parts[1:])
            if not info.is_dir():
                if name.lower().endswith((".exe", ".dll", ".pdb", ".pyc", ".pyo", ".zip")):
                    raise PackageError("Executable/build payload in public source ZIP")
                files[name] = info
        if len(roots) != 1:
            raise PackageError("Matching source ZIP must have one safe root")
        for name, expected in snapshots.items():
            if name not in files or record(name, archive.read(files[name])) != expected:
                raise PackageError("Source ZIP differs from selected public source input: " + name)
        recipe = checked_path(Path(__file__), file=True).read_bytes()
        if "scripts/Package.py" not in files or archive.read(files["scripts/Package.py"]) != recipe:
            raise PackageError("Source ZIP must include this exact scripts/Package.py recipe")


def add(archive: zipfile.ZipFile, name: str, data: bytes) -> None:
    safe_parts(name)
    info = zipfile.ZipInfo(PREFIX + name, STAMP)
    info.create_system = 3
    info.external_attr = (stat.S_IFREG | 0o644) << 16
    info.compress_type = zipfile.ZIP_DEFLATED
    archive.writestr(info, data)


def package(base: Path, publish: Path, output: Path, source_root: Path, app_source: Path) -> dict:
    base = checked_path(base, file=True)
    publish = checked_path(publish, directory=True)
    source_root = checked_path(source_root, directory=True)
    app_source = checked_path(app_source, file=True)
    output = checked_path(output, new=True)
    if output.suffix.lower() != ".zip":
        raise PackageError("Output must be a new .zip file")
    if file_sha(base) != BASE_SHA256:
        raise PackageError("Base must be the unmodified public 1.0 portable ZIP (SHA-256 pin mismatch)")
    if app_source.name != "FK_YT-1.1.1-source.zip":
        raise PackageError("Matching public source archive must be named FK_YT-1.1.1-source.zip")
    source_archive = {"path": app_source.name, "length": app_source.stat().st_size, "sha256": file_sha(app_source)}
    if any(item.name.lower().endswith(".pdb") for item in publish.iterdir()):
        raise PackageError("Normal publish input must not include PDB files")
    snapshots = {}
    overlays = {name: checked_path(publish / name, file=True).read_bytes() for name in APP_FILES}
    app_snapshot = {name: record(name, data) for name, data in overlays.items()}
    check_publish(overlays, source_root, snapshots)
    overlays.update({"worker/" + name: read_source(source_root, "worker/" + name, snapshots) for name in WORKER_NAMES})
    overlays.update({target: read_source(source_root, source, snapshots) for source, target in DOCUMENTS.items()})
    check_source_archive(app_source, snapshots)
    with zipfile.ZipFile(base) as previous:
        inventory, before = verified_archive(previous)
        prior = json.loads(previous.read(PREFIX + "distribution-provenance.json"))
        vendor_sources = prior.get("sourceAssets")
        if not isinstance(vendor_sources, list) or not vendor_sources:
            raise PackageError("Base third-party corresponding-source notices are missing")
        for item in vendor_sources:
            safe_parts(item["name"])
            if type(item["bytes"]) is not int or item["bytes"] <= 0 or not re.fullmatch(r"[0-9a-f]{64}", item["sha256"]):
                raise PackageError("Invalid base corresponding-source notice")
        if not set(APP_FILES) | GENERATED <= set(before):
            raise PackageError("Base application or metadata is missing")
        if {name for name in before if name.startswith("worker/") and name.endswith(".py")} - {"worker/" + name for name in WORKER_NAMES}:
            raise PackageError("Unexpected production worker in base")
        provenance = {
            "schema": 1, "product": "FK_YT", "version": "1.1.1", "applicationVersion": VERSION,
            "packageRole": "normal portable", "basePortable": {"version": "1.0", "sha256": BASE_SHA256},
            "applicationSource": source_archive,
            "selectedSourceInputs": {"root": "FK_YT", "version": VERSION, "files": sorted(snapshots.values(), key=lambda item: item["path"])},
            "sourceAssets": SOURCE_ASSETS, "historicalBaseSourceAssets": vendor_sources,
            "sourceAccess": "SOURCE-ACCESS-1.1.1.txt",
            "runtimeChanges": prior.get("runtimeChanges", {}),
            "fileManifest": "file-manifest.json", "appDllSha256": digest(overlays["FK_YT.dll"]),
            "applicationFiles": sorted(app_snapshot.values(), key=lambda item: item["path"]),
            "replacementAllowlist": sorted(REPLACEMENT_ALLOWLIST),
            "externalRuntimeAndOriginalLicenseBytes": "Every base file outside the replacement allowlist is preserved byte for byte, including external runtimes, original guides, licenses, notices and corresponding-source notices.",
            "verificationScope": "Package integrity, pinned base, selected source inputs, app/dependency versions, self-contained framework configuration and byte preservation. Build/source equivalence, diagnostic-code inspection and runtime behavior require separate release verification.",
        }
        overlays["distribution-provenance.json"] = encode(provenance)
        reject_private_paths(overlays, [source_root, publish, base, app_source])
        names = set(before) | set(overlays) | {"file-manifest.json"}
        if len({name.casefold() for name in names}) != len(names):
            raise PackageError("Overlay causes a case-insensitive payload collision")
        expected = {name: item for name, item in before.items() if name != "file-manifest.json"}
        expected.update({name: record(name, data) for name, data in overlays.items()})
        manifest = {"schema": 1, "pathBase": PREFIX, "hashAlgorithm": "SHA-256", "coverage": "All public portable files except this manifest", "selfExcluded": "file-manifest.json", "files": sorted(expected.values(), key=lambda item: item["path"])}
        overlays["file-manifest.json"] = encode(manifest)
        expected["file-manifest.json"] = record("file-manifest.json", overlays["file-manifest.json"])
        checked_path(output, new=True)
        with output.open("xb") as stream, zipfile.ZipFile(stream, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for name in sorted(names):
                data = overlays[name] if name in overlays else previous.read(inventory[name])
                if record(name, data) != expected[name]:
                    raise PackageError("Base changed during packaging")
                add(archive, name, data)
    with zipfile.ZipFile(checked_path(output, file=True)) as archive:
        _, after = verified_archive(archive)
    if after != expected:
        raise PackageError("Output ZIP does not match the exact input vector")
    protected = set(before) - REPLACEMENT_ALLOWLIST
    if set(before) - set(after) or any(before[name] != after[name] for name in protected):
        raise PackageError("Protected original payload bytes changed or disappeared")
    changed = sorted(name for name in set(before) | set(after) if before.get(name) != after.get(name))
    if not set(changed) <= REPLACEMENT_ALLOWLIST:
        raise PackageError("Unexpected output payload change")
    for name, expected_source in snapshots.items():
        if record(name, checked_path(source_root.joinpath(*safe_parts(name)), file=True).read_bytes()) != expected_source:
            raise PackageError("Selected source input changed during packaging")
    for name, expected_app in app_snapshot.items():
        if record(name, checked_path(publish / name, file=True).read_bytes()) != expected_app:
            raise PackageError("Application publish input changed during packaging")
    if file_sha(base) != BASE_SHA256:
        raise PackageError("Base archive changed during packaging")
    if {"path": app_source.name, "length": app_source.stat().st_size, "sha256": file_sha(app_source)} != source_archive:
        raise PackageError("Matching source archive changed during packaging")
    return {"version": VERSION, "output": output.name, "bytes": output.stat().st_size, "sha256": file_sha(output), "fileCount": len(after), "protectedBaseFilesPreserved": len(protected), "changedOrAdded": changed, "frameworks": FRAMEWORKS, "dllFileVersion": VERSION + ".0", "manifestVerified": True}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", type=Path, required=True, help="Unmodified public 1.0 portable ZIP")
    parser.add_argument("--publish", type=Path, required=True, help="Normal Release win-x64 publish directory")
    parser.add_argument("--output", type=Path, required=True, help="New portable ZIP; existing outputs are refused")
    parser.add_argument("--app-source", type=Path, required=True, help="Matching FK_YT-1.1.1-source.zip, created before this portable ZIP")
    parser.add_argument("--source-root", type=Path, default=Path(__file__).absolute().parent.parent, help="Public source root (default: parent of scripts)")
    args = parser.parse_args()
    try:
        print(json.dumps(package(args.base, args.publish, args.output, args.source_root, args.app_source), ensure_ascii=True))
    except (PackageError, OSError, ValueError, KeyError, TypeError, struct.error, ET.ParseError, zipfile.BadZipFile) as error:
        print("Packaging failed: " + str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
