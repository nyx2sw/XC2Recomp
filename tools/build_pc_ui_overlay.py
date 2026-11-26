"""Build a menu-only ARH overlay from the maintainer's local 3.0.1 menu assets."""

import argparse
from hashlib import sha256
from pathlib import Path
import shutil
import struct
from uuid import uuid4

from xc2_archive import Archive


ASSETS = (
    "/bdat/common.bdat",
    "/menu/image/mnu080_submenu_icon.wilay",
    "/menu/object/mnu080_option.uio",
)


def build(base_romfs: Path, assets: Path, output: Path) -> None:
    archive = Archive(base_romfs)
    base_size = (base_romfs / "bf2.ard").stat().st_size
    base_hash = sha256(archive.raw).hexdigest()
    index = bytearray(archive.raw)
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.exists():
        raise FileExistsError(f"Use a fresh overlay destination: {output}")

    # Stage beside the destination so a published Windows folder inherits the output directory's ACL.
    staged = output.with_name(f".{output.name}-{uuid4().hex}")
    staged.mkdir()
    try:
        romfs = staged / "romfs"
        romfs.mkdir()
        with (romfs / "bf2.ard.append").open("wb") as append:
            for name in ASSETS:
                entry = archive.lookup(name)
                if entry is None:
                    raise ValueError(f"Base archive lacks {name}")
                data = (assets / name.lstrip("/")).read_bytes()
                if not data or (name.endswith(".uio") and b"LAY_pc\0" not in data):
                    raise ValueError(f"Invalid native menu asset: {name}")
                append.write(bytes(-(base_size + append.tell()) % 16))
                offset = base_size + append.tell()
                fid = entry["fid"]
                struct.pack_into("<Q4I", index, archive.fo + fid * 24, offset, len(data), 0, 0, fid)
                append.write(data)
            append.write(bytes(-(base_size + append.tell()) % 4096 + 4096))
        (romfs / "bf2.arh").write_bytes(index)
        (staged / "base_archive.txt").write_text(f"{base_size} {base_hash}\n", encoding="ascii")
        staged.rename(output)
    finally:
        if staged.exists():
            shutil.rmtree(staged)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("base_romfs", type=Path)
    parser.add_argument("menu_assets", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    build(args.base_romfs, args.menu_assets, args.output)
