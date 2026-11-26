"""Verify a portable XC2 archive against its release manifest and SHA-256 sidecar."""

import argparse
import hashlib
import json
from pathlib import Path
from zipfile import ZipFile


REQUIRED = {"Xenoblade2Launcher.exe", "runtime/xenoblade2.exe", "runtime/libLLVM-23.dll"}
GAME_PACKAGES = {".nup", ".nsp", ".xci", ".nca", ".romfs", ".sav", ".keys"}


def member_name(raw: str) -> str:
    name = raw.replace("\\", "/")
    if any(part in ("", ".", "..") or ":" in part for part in name.split("/")):
        raise ValueError(f"Invalid archive path: {raw}")
    return name


def private_path_markers(roots: list[str]) -> tuple[bytes, ...]:
    markers = set()
    for root in roots:
        path = root.replace("\\", "/").rstrip("/").lower()
        if len(path) < 4:
            raise ValueError("A private path must name a directory.")
        for encoding in ("utf-8", "utf-16le"):
            markers.add(path.encode(encoding))
    return tuple(markers)


def verify(archive: Path, private_roots: list[str]) -> tuple[str, int]:
    sidecar = archive.with_name(archive.name + ".sha256")
    expected, expected_name = sidecar.read_text(encoding="ascii").strip().split(maxsplit=1)
    zip_digest = hashlib.sha256()
    with archive.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            zip_digest.update(chunk)
    actual = zip_digest.hexdigest()
    if expected_name != archive.name or expected.lower() != actual:
        raise ValueError("The archive does not match its SHA-256 sidecar.")

    markers = private_path_markers(private_roots)
    overlap = max(map(len, markers), default=1) - 1
    with ZipFile(archive) as package:
        entries = {}
        seen = set()
        for entry in package.infolist():
            if entry.filename.endswith(("/", "\\")):
                member_name(entry.filename[:-1])
                continue
            name = member_name(entry.filename)
            folded = name.casefold()
            if folded in seen:
                raise ValueError(f"Duplicate archive path: {name}")
            seen.add(folded)
            entries[name] = entry
            if Path(name).suffix.lower() in GAME_PACKAGES or folded.endswith(("/bf2.ard", "/xenoblade2_code.dll")):
                raise ValueError(f"Game data in portable archive: {name}")

        metadata = entries.pop("release-manifest.json")
        manifest_bytes = package.read(metadata)
        if any(path in manifest_bytes.lower().replace(b"\\", b"/") for path in markers):
            raise ValueError("Local profile path in release-manifest.json")
        manifest = json.loads(manifest_bytes.decode("utf-8-sig"))
        artifacts = {row["path"]: row for row in manifest["files"]}
        if len(artifacts) != len(manifest["files"]) or entries.keys() != artifacts.keys() or not REQUIRED <= entries.keys():
            raise ValueError("Archive entries do not match the release manifest.")

        for name, entry in entries.items():
            artifact = artifacts[name]
            if entry.file_size != artifact["bytes"]:
                raise ValueError(f"Size mismatch: {name}")
            digest = hashlib.sha256()
            tail = b""
            with package.open(entry) as stream:
                while chunk := stream.read(1024 * 1024):
                    digest.update(chunk)
                    if markers:
                        candidate = tail + chunk.lower().replace(b"\\", b"/")
                        if any(path in candidate for path in markers):
                            raise ValueError(f"Local profile path in {name}")
                        tail = candidate[-overlap:] if overlap else b""
            if digest.hexdigest() != artifact["sha256"].lower():
                raise ValueError(f"SHA-256 mismatch: {name}")
    return actual, len(entries)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("--private-root", action="append", default=[])
    args = parser.parse_args()
    digest, count = verify(args.archive, args.private_root)
    print(f"Verified {args.archive.name}: {count} manifest files, SHA-256 {digest}")


if __name__ == "__main__":
    main()
