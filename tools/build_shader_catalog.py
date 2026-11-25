#!/usr/bin/env python3
"""Read-only extractor for Xenoblade Chronicles 2 (Switch 2 Edition 3.0.1) shaders.

Scans bf2.arh / bf2.ard (plus Torna / DLC romfs images if present, strictly read-only)
and extracts every unique Ampere/NVN shader binary (VS, FS, GS, TCS, TES, CS) from
Monolith Soft SPCH (HCPS/SLCT/NVSD) containers into a compact SNXS v1 catalog
(shader_cache/xc2_shaders.snxs) for native startup pre-compilation.
"""
import argparse
import hashlib
import os
import pathlib
import struct
import sys
import time
import zlib

SCRIPT_DIR = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(SCRIPT_DIR))
import xc2_archive  # noqa: E402

NVN_CTRL_MAGIC = 0x98761234
# Order priority: global shader packs first (highest draw frequency), then maps, then models/DLC.


def fnv1a64(data: bytes, h: int = 14695981039346656037) -> int:
    # Fast chunked FNV-1a? Actually Python loop on 30 MB is slow; use blake2b for dedup and
    # compute FNV-1a only over header/table or use 64-bit blake2b directly as catalog_hash!
    for b in data:
        h = ((h ^ b) * 1099511628211) & 0xFFFFFFFFFFFFFFFF
    return h


def extract_spch_shaders(buf: bytes):
    """Parse all HCPS (SPCH) shader packs inside buf -> list of (nvn_stage, early_z, lx, ly, lz, shm, code)."""
    out = []
    pos = 0
    blen = len(buf)
    while True:
        base = buf.find(b"HCPS", pos)
        if base < 0 or base + 0x38 > blen:
            break
        ver, slct_tbl_off, slct_cnt = struct.unpack_from("<III", buf, base + 4)
        slct_base, slct_sz = struct.unpack_from("<II", buf, base + 0x18)
        xvb_off, xvb_sz = struct.unpack_from("<II", buf, base + 0x20)
        nvn_off, nvn_sz = struct.unpack_from("<II", buf, base + 0x28)
        if (
            slct_cnt == 0
            or slct_cnt > 20000
            or xvb_sz == 0
            or nvn_sz == 0
            or base + xvb_off + xvb_sz > blen
            or base + nvn_off + nvn_sz > blen
            or base + slct_tbl_off + slct_cnt * 8 > blen
        ):
            pos = base + 4
            continue

        for si in range(slct_cnt):
            rel = struct.unpack_from("<I", buf, base + slct_tbl_off + si * 8)[0]
            so = base + slct_base + rel
            if so + 0x40 > blen or buf[so : so + 4] != b"SLCT":
                continue
            prog_cnt, prog_off = struct.unpack_from("<II", buf, so + 0x10)
            nvn_rel, nvn_len, xvb_rel, xvb_len = struct.unpack_from("<IIII", buf, so + 0x28)
            if prog_cnt > 50000 or so + prog_off + prog_cnt * 8 > blen:
                continue
            cur_xvb = base + xvb_off + xvb_rel
            cur_nvn = base + nvn_off + nvn_rel
            for pi in range(prog_cnt):
                poff, psz = struct.unpack_from("<II", buf, so + prog_off + pi * 8)
                p = so + poff
                if p + 0x78 > blen:
                    continue
                nvsd_off, nstages = struct.unpack_from("<II", buf, p + 0x18)
                prog_xvb_sz, prog_nvn_sz = struct.unpack_from("<II", buf, p + 0x68)
                nvsd = p + nvsd_off
                if (
                    0 < nstages <= 6
                    and nvsd >= 8
                    and nvsd + nstages * 20 <= blen
                    and buf[nvsd - 8 : nvsd - 4] == b"NVSD"
                ):
                    for s in range(nstages):
                        st, doff, dsz, coff, csz = struct.unpack_from("<5I", buf, nvsd + s * 20)
                        cabs = cur_nvn + coff
                        dabs = cur_xvb + doff
                        if (
                            cabs + 0x68 <= blen
                            and dabs + dsz <= blen
                            and struct.unpack_from("<I", buf, cabs)[0] == NVN_CTRL_MAGIC
                        ):
                            code_off = struct.unpack_from("<I", buf, cabs + 0x34)[0]
                            ctrl_st = (struct.unpack_from("<I", buf, cabs + 0x54)[0] >> 16) & 0xFF
                            if ctrl_st >= 6 or code_off >= dsz:
                                continue
                            ez = 0
                            lx = ly = lz = shm = 0
                            if ctrl_st == 1:
                                ez = 1 if ((struct.unpack_from("<I", buf, cabs + 0x58)[0] >> 24) & 0xFF) != 0 else 0
                            elif ctrl_st == 5:
                                lx, ly, lz, shm = struct.unpack_from("<4I", buf, cabs + 0x58)
                                lx = max(1, min(lx, 1024))
                                ly = max(1, min(ly, 1024))
                                lz = max(1, min(lz, 1024))
                                shm &= 0x3FFFF
                                if shm > 49152:
                                    shm = 0
                            code = buf[dabs + code_off : dabs + dsz]
                            if 0x80 <= len(code) <= 0x10000:
                                out.append((ctrl_st, ez, lx, ly, lz, shm, code))
                cur_xvb += prog_xvb_sz
                cur_nvn += prog_nvn_sz
        pos = base + xvb_off + xvb_sz
    return out


def extract_from_xbc_stream(raw: bytes):
    out = []
    if b"HCPS" in raw:
        out.extend(extract_spch_shaders(raw))
    pos = 0
    rlen = len(raw)
    while True:
        idx = raw.find(b"xbc1", pos)
        if idx < 0 or idx + 48 > rlen:
            break
        ver, usz, csz = struct.unpack_from("<III", raw, idx + 4)
        if ver == 1 and 0 < csz < 64_000_000 and idx + 48 + csz <= rlen:
            try:
                dec = zlib.decompress(raw[idx + 48 : idx + 48 + csz])
                if b"HCPS" in dec:
                    out.extend(extract_spch_shaders(dec))
            except Exception:
                pass
            pos = idx + 48 + csz
        else:
            pos = idx + 4
    return out


def scan_embedded_arh_ard(f, arh_bytes: bytes, ard_base_off: int, add_fn):
    """Parse ARH1/ARD embedded inside a Switch RomFS image (e.g., Torna aoc1.arh + aoc1.ard)."""
    if arh_bytes[:4] != b"arh1":
        return 0
    count, so, sl, po, pl, fo, fn, key = struct.unpack_from("<8I", arh_bytes, 8)
    kbytes = (key ^ 0xF3F35353).to_bytes(4, "little")

    def decode(off, size):
        b = bytearray(arh_bytes[off : off + size])
        for i in range(size // 4 * 4):
            b[i] ^= kbytes[i % 4]
        return b

    strings, nodes = decode(so, sl), decode(po, pl)
    added = 0
    for i in range(count):
        nxt, prev = struct.unpack_from("<ii", nodes, i * 8)
        if nxt >= 0 or prev < 0 or prev >= count:
            continue
        chars = []
        cur = i
        for _ in range(2048):
            parent = struct.unpack_from("<i", nodes, cur * 8 + 4)[0]
            if parent < 0:
                break
            if parent >= count:
                chars = []
                break
            base = struct.unpack_from("<i", nodes, parent * 8)[0]
            c = base ^ cur
            if not 0 < c < 128:
                chars = []
                break
            chars.append(c)
            cur = parent
        if not chars or not 0 < -nxt < len(strings):
            continue
        end = strings.index(0, -nxt)
        name = (bytes(reversed(chars)) + strings[-nxt:end]).decode("ascii", errors="ignore")
        low = name.lower()
        if not low.endswith((".wishp", ".wismt", ".wismda", ".wimdo")):
            continue
        fid = struct.unpack_from("<I", strings, end + 1)[0]
        if fid >= fn:
            continue
        eoff, esz, eexp, eflags, _ = struct.unpack_from("<Q4I", arh_bytes, fo + fid * 24)
        f.seek(ard_base_off + eoff)
        b = f.read(esz + 48)
        if eflags & 2:
            if b[:4] != b"xbc1":
                continue
            _, comp = struct.unpack_from("<II", b, 8)
            raw = zlib.decompress(b[48 : 48 + comp])
        else:
            raw = b[:esz]
        shs = extract_spch_shaders(raw) if low.endswith(".wishp") else extract_from_xbc_stream(raw)
        for item in shs:
            added += add_fn(item)
    return added


def scan_romfs_image(romfs_path: pathlib.Path, add_fn):
    """Scan Switch RomFS image (Torna / DLC .romfs, read-only) for xbc1/HCPS shader packs and embedded ARH/ARD."""
    if not romfs_path.is_file():
        return 0
    size = romfs_path.stat().st_size
    if size < 1024:
        return 0
    with romfs_path.open("rb") as f:
        hdr = f.read(0x50)
        if len(hdr) < 0x50:
            return 0
        hdr_sz, dh_off, dh_sz, dm_off, dm_sz, fh_off, fh_sz, fm_off, fm_sz, data_off = struct.unpack_from(
            "<10Q", hdr, 0
        )
        if hdr_sz != 0x50 or fm_off + fm_sz > size or data_off > size:
            return 0
        f.seek(fm_off)
        fmeta = f.read(fm_sz)
        added = 0
        arhs = {}
        ards = {}
        off = 0
        while off + 0x20 <= len(fmeta):
            parent, sib, foff, fsz, fhash, nlen = struct.unpack_from("<IIQQII", fmeta, off)
            if nlen > 256 or off + 0x20 + nlen > len(fmeta):
                break
            name = fmeta[off + 0x20 : off + 0x20 + nlen].decode("utf-8", errors="ignore")
            low = name.lower()
            if low.endswith(".arh") and 0 < fsz < 64_000_000:
                arhs[low[:-4]] = (data_off + foff, fsz)
            elif low.endswith(".ard") and fsz > 0:
                ards[low[:-4]] = (data_off + foff, fsz)
            elif low.endswith((".wishp", ".wismt", ".wismda", ".wimdo")) and 0 < fsz < 128_000_000:
                f.seek(data_off + foff)
                raw = f.read(fsz)
                shs = extract_spch_shaders(raw) if low.endswith(".wishp") else extract_from_xbc_stream(raw)
                for item in shs:
                    added += add_fn(item)
            step = (0x20 + nlen + 3) & ~3
            off += step
        for stem, (arh_off, arh_sz) in arhs.items():
            if stem in ards:
                f.seek(arh_off)
                arh_bytes = f.read(arh_sz)
                added += scan_embedded_arh_ard(f, arh_bytes, ards[stem][0], add_fn)
        return added


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data-dir", required=True, help="Path to xenoblade2_data directory (read-only)")
    ap.add_argument("--out", required=True, help="Output .snxs file path")
    args = ap.parse_args()

    t0 = time.perf_counter()
    data_dir = pathlib.Path(args.data_dir)
    romfs_dir = data_dir / "romfs"
    out_path = pathlib.Path(args.out)

    seen = set()
    ordered = []
    stage_counts = {}

    def add_shader(item):
        st, ez, lx, ly, lz, shm, code = item
        if st != 5:
            # Keep only Switch 2 Ampere v4 graphics shaders (skip legacy Switch 1 Maxwell v3 0x12345678 blobs in DLCs)
            if len(code) < 0x80:
                return 0
            w0 = struct.unpack_from("<I", code, 0)[0]
            if ((w0 >> 5) & 0x1F) != 4:
                if len(code) > 0xB0 and ((struct.unpack_from("<I", code, 0x30)[0] >> 5) & 0x1F) == 4:
                    code = code[0x30:]
                    item = (st, ez, lx, ly, lz, shm, code)
                else:
                    return 0
        h = hashlib.blake2b(code, digest_size=16).digest()
        key = (st, ez, lx, ly, lz, shm, h)
        if key in seen:
            return 0
        seen.add(key)
        ordered.append(item)
        stage_counts[st] = stage_counts.get(st, 0) + 1
        return 1

    arc = xc2_archive.Archive(romfs_dir)
    entries = list(arc.entries())

    wishps = sorted([(p, m) for p, m in entries if p.lower().endswith(".wishp")])
    wismdas = sorted([(p, m) for p, m in entries if p.lower().endswith(".wismda")])
    wismts = sorted([(p, m) for p, m in entries if p.lower().endswith(".wismt")])
    wimdos = sorted([(p, m) for p, m in entries if p.lower().endswith(".wimdo")])

    n_wishp = 0
    for p, m in wishps:
        for item in extract_spch_shaders(arc.read(m)):
            n_wishp += add_shader(item)
    print(f"[1/4] Global .wishp ({len(wishps)} files): +{n_wishp} unique shaders ({time.perf_counter()-t0:.1f}s)", flush=True)

    n_da = 0
    for i, (p, m) in enumerate(wismdas, 1):
        for item in extract_from_xbc_stream(arc.read(m)):
            n_da += add_shader(item)
    print(f"[2/4] Map .wismda ({len(wismdas)} files): +{n_da} unique shaders (total {len(ordered)}, {time.perf_counter()-t0:.1f}s)", flush=True)

    n_mt = 0
    for p, m in wismts:
        for item in extract_from_xbc_stream(arc.read(m)):
            n_mt += add_shader(item)
    for p, m in wimdos:
        raw = arc.read(m)
        if b"HCPS" in raw:
            for item in extract_spch_shaders(raw):
                n_mt += add_shader(item)
    print(f"[3/4] Models .wismt/.wimdo ({len(wismts)}+{len(wimdos)} files): +{n_mt} unique shaders (total {len(ordered)}, {time.perf_counter()-t0:.1f}s)", flush=True)

    # Scan Torna & DLC romfs images (read-only)
    n_dlc = 0
    addons_dir = data_dir / "addons"
    if addons_dir.is_dir():
        for rf in sorted(addons_dir.glob("*.romfs")):
            if rf.stat().st_size > 1_000_000:
                got = scan_romfs_image(rf, add_shader)
                n_dlc += got
                if got:
                    print(f"      DLC {rf.name}: +{got} unique shaders", flush=True)
    print(f"[4/4] Addons/Torna: +{n_dlc} unique shaders (total {len(ordered)}, {time.perf_counter()-t0:.1f}s)", flush=True)

    # Serialize SNXS v1 atomically
    body = bytearray()
    for st, ez, lx, ly, lz, shm, code in ordered:
        body += struct.pack("<BBHHHII", st, ez, lx, ly, lz, shm, len(code))
        body += code
    cat_hash = struct.unpack("<Q", hashlib.blake2b(body, digest_size=8).digest())[0]
    header = struct.pack("<4sIIIQ", b"SNXS", 1, len(ordered), 0, cat_hash)

    out_path.parent.mkdir(parents=True, exist_ok=True)
    tmp = out_path.with_suffix(out_path.suffix + ".tmp")
    with tmp.open("wb") as f:
        f.write(header)
        f.write(body)
    os.replace(tmp, out_path)

    total_mb = (len(header) + len(body)) / (1024 * 1024)
    print(
        f"DONE: wrote {out_path} ({total_mb:.2f} MB), {len(ordered)} unique shaders, "
        f"stages={stage_counts}, hash=0x{cat_hash:016X}, elapsed={time.perf_counter()-t0:.1f}s"
    )


if __name__ == "__main__":
    main()
