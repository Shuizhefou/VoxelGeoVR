#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""vxtool.py - Minecraft Anvil (.mca) -> normalized voxel data (.vxcell).

Zero third-party dependencies (stdlib only).  See docs/stage3 format spec:
  out/world.manifest.json   world meta + cell index
  out/blocks.json           block palette (id / color / solid)
  out/cells/*.vxcell        binary cell (8x8 chunks), deflate payload
  out/report.json           per-chunk stats for cross-checking the Unity side

Commands:
  scan     --world <save>                      stats only
  build    --world <save> --out <dir>          generate data
  validate --dir <out>                          structural + numeric check
  dump     --dir <out> --chunk cx,cz --y 62     ASCII slice
"""
import argparse
import gzip
import hashlib
import json
import math
import os
import struct
import sys
import time
import zlib
from collections import Counter

# ---------------------------------------------------------------- constants
MIN_Y = -64
WORLD_HEIGHT = 384
SY = WORLD_HEIGHT
CELL = 8                     # chunks per cell side
MAGIC = b"VXC1"
FORMAT_VERSION = 1

TAG_END, TAG_BYTE, TAG_SHORT, TAG_INT, TAG_LONG = 0, 1, 2, 3, 4
TAG_FLOAT, TAG_DOUBLE, TAG_BYTE_ARRAY, TAG_STRING = 5, 6, 7, 8
TAG_LIST, TAG_COMPOUND, TAG_INT_ARRAY, TAG_LONG_ARRAY = 9, 10, 11, 12

AIR_NAMES = ("minecraft:air", "minecraft:cave_air", "minecraft:void_air")


# ---------------------------------------------------------------- NBT reader
class Reader:
    __slots__ = ("b", "i")

    def __init__(self, buf):
        self.b = buf
        self.i = 0

    def u1(self):
        v = self.b[self.i]; self.i += 1; return v

    def i1(self):
        v = struct.unpack_from(">b", self.b, self.i)[0]; self.i += 1; return v

    def i2(self):
        v = struct.unpack_from(">h", self.b, self.i)[0]; self.i += 2; return v

    def u2(self):
        v = struct.unpack_from(">H", self.b, self.i)[0]; self.i += 2; return v

    def i4(self):
        v = struct.unpack_from(">i", self.b, self.i)[0]; self.i += 4; return v

    def i8(self):
        v = struct.unpack_from(">q", self.b, self.i)[0]; self.i += 8; return v

    def f4(self):
        v = struct.unpack_from(">f", self.b, self.i)[0]; self.i += 4; return v

    def f8(self):
        v = struct.unpack_from(">d", self.b, self.i)[0]; self.i += 8; return v

    def string(self):
        n = self.u2()
        s = self.b[self.i:self.i + n].decode("utf-8", "replace")
        self.i += n
        return s


def read_payload(r, tag):
    if tag == TAG_BYTE:
        return r.i1()
    if tag == TAG_SHORT:
        return r.i2()
    if tag == TAG_INT:
        return r.i4()
    if tag == TAG_LONG:
        return r.i8()
    if tag == TAG_FLOAT:
        return r.f4()
    if tag == TAG_DOUBLE:
        return r.f8()
    if tag == TAG_BYTE_ARRAY:
        n = r.i4(); v = r.b[r.i:r.i + n]; r.i += n; return v
    if tag == TAG_STRING:
        return r.string()
    if tag == TAG_LIST:
        et = r.u1(); n = r.i4()
        return [read_payload(r, et) for _ in range(n)]
    if tag == TAG_COMPOUND:
        out = {}
        while True:
            t = r.u1()
            if t == TAG_END:
                return out
            name = r.string()
            out[name] = read_payload(r, t)
    if tag == TAG_INT_ARRAY:
        n = r.i4(); v = struct.unpack_from(">%di" % n, r.b, r.i); r.i += 4 * n; return v
    if tag == TAG_LONG_ARRAY:
        n = r.i4()
        if n < 0:
            return ()
        v = struct.unpack_from(">%dq" % n, r.b, r.i); r.i += 8 * n; return v
    raise ValueError("bad tag %d at %d" % (tag, r.i))


def parse_nbt(raw):
    if raw[:2] == b"\x1f\x8b":
        raw = gzip.decompress(raw)
    elif raw[:1] == b"\x78":
        raw = zlib.decompress(raw)
    r = Reader(raw)
    t = r.u1()
    if t != TAG_COMPOUND:
        return None
    r.string()
    return read_payload(r, t)


def decompress_chunk(payload):
    comp = payload[0]
    body = payload[1:]
    if comp == 1:
        return gzip.decompress(body)
    if comp == 2:
        return zlib.decompress(body)
    if comp == 3:
        return body
    raise ValueError("compression %d" % comp)


def unpack_bits(longs, bits, count=4096):
    """MC packed long array -> list of values (LSB-first, no cross-long values)."""
    per = 64 // bits
    mask = (1 << bits) - 1
    out = []
    n_long = (count + per - 1) // per
    for li in range(n_long):
        v = longs[li]
        if v < 0:
            v += 1 << 64
        for k in range(per):
            out.append((v >> (k * bits)) & mask)
            if len(out) == count:
                return out
    return out


def pack_bits(vals, bits):
    per = 64 // bits
    n_long = (len(vals) + per - 1) // per
    out = []
    for li in range(n_long):
        v = 0
        base = li * per
        for k in range(per):
            idx = base + k
            if idx >= len(vals):
                break
            v |= (vals[idx] & ((1 << bits) - 1)) << (k * bits)
        out.append(v)
    return out


# ---------------------------------------------------------------- world scan
def iter_region_chunks(region_path):
    """Yield (cx, cz, chunk_nbt) for every present chunk in a .mca."""
    size = os.path.getsize(region_path)
    with open(region_path, "rb") as f:
        header = f.read(4096)
        if len(header) < 4096:
            return
        offs = struct.unpack(">1024i", header)
        for ix, off in enumerate(offs):
            if off == 0:
                continue
            sector = off >> 8
            if sector * 4096 >= size:
                continue
            f.seek(sector * 4096)
            hdr = f.read(4)
            if len(hdr) < 4:
                continue
            length = struct.unpack(">i", hdr)[0]
            if length <= 0 or length > size:
                continue
            payload = f.read(length)
            if len(payload) < length:
                continue
            try:
                nbt = parse_nbt(decompress_chunk(payload))
            except Exception as exc:  # noqa: BLE001
                print("  ! %s[%d] parse error: %r" % (os.path.basename(region_path), ix, exc))
                continue
            if not nbt:
                continue
            yield nbt.get("xPos", ix % 32), nbt.get("zPos", ix // 32), nbt


def load_blocks(path):
    with open(path, "r", encoding="utf-8") as f:
        data = json.load(f)
    by_name = {}
    for b in data["blocks"]:
        by_name[b["name"]] = b
    return data, by_name


def block_id(name, name2id, unknown):
    bid = name2id.get(name)
    if bid is None:
        if name not in unknown:
            unknown[name] = len(name2id) + len(unknown) + 1
            print("  ! unknown block %s -> fallback gray id %d" % (name, unknown[name]))
        return unknown[name]
    return bid


def decode_section(sec, name2id, unknown):
    """-> list[int] block ids (4096) or None when the section is absent."""
    bs = sec.get("block_states")
    if bs is None:
        return None
    pal = bs.get("palette") or []
    if not pal:
        return None
    ids = [block_id(p.get("Name", "minecraft:air"), name2id, unknown) for p in pal]
    data = bs.get("data")
    if not data:
        return [ids[0]] * 4096
    bits = max(4, (len(pal) - 1).bit_length())
    idx = unpack_bits(data, bits)
    return [ids[i] if i < len(ids) else 0 for i in idx]


def encode_chunk(chunk, name2id, solid_ids, unknown, band, stats_out=None):
    """Encode one chunk into the v1 record. Returns (bytes, solid_count, faces)."""
    secs = {}
    ys = []
    for sec in chunk.get("sections", []):
        y = sec.get("Y")
        if y is None:
            continue
        ids = decode_section(sec, name2id, unknown)
        if ids is None:
            continue
        secs[y] = ids
        ys.append(y)
    if not ys:
        return b"", 0, 0, None
    y0, y1 = min(ys), max(ys)

    mask = bytearray(SY * 256)
    top = [-32768] * 256
    solid_count = 0
    for y, ids in secs.items():
        base = (y * 16 - MIN_Y) * 256
        if base < 0 or base + 4096 > SY * 256:
            continue
        for i, bid in enumerate(ids):
            if bid and solid_ids.get(bid, True):
                mask[base + i] = 1
                solid_count += 1

    # top solid y per column
    for zi in range(16):
        for xi in range(16):
            col = zi * 16 + xi
            for y in range(SY - 1, -1, -1):
                if mask[y * 256 + col]:
                    top[col] = y + MIN_Y
                    break

    # 暴露面统计（与 Unity 端一致：按 chunk 的 y 窗口 [min(top)-band+1, max(top)]）
    faces = 0
    tops = [t for t in top if t != -32768]
    if tops:
        y_lo = max(min(tops) - band + 1, MIN_Y)
        y_hi = min(max(tops), MIN_Y + SY - 1)
        for y in range(y_lo, y_hi + 1):
            yi = y - MIN_Y
            base = yi * 256
            for col in range(256):
                if not mask[base + col]:
                    continue
                zi, xi = divmod(col, 16)
                for dz, dx, dy in ((-1, 0, 0), (1, 0, 0), (0, 0, -1), (0, 0, 1), (0, -1, 0), (0, 1, 0)):
                    x2, z2, y2 = xi + dx, zi + dz, yi + dy
                    if x2 < 0 or x2 > 15 or z2 < 0 or z2 > 15:
                        faces += 1
                    elif y2 < 0 or y2 >= SY:
                        faces += 1
                    elif not mask[y2 * 256 + z2 * 16 + x2]:
                        faces += 1

    out = bytearray()
    out += struct.pack("<hB", y0, y1 - y0 + 1)
    out += struct.pack("<256h", *top)
    for y in range(y0, y1 + 1):
        ids = secs.get(y)
        if ids is None:
            out += b"\x00"
            continue
        uniq = sorted(set(ids))
        if uniq == [0]:
            out += b"\x00"
            continue
        if len(uniq) == 1:
            out += struct.pack("<BH", 1, uniq[0] & 0xFFFF)
            continue
        bits = max(4, (len(uniq) - 1).bit_length())
        remap = {v: i for i, v in enumerate(uniq)}
        packed = pack_bits([remap[v] for v in ids], bits)
        out += struct.pack("<BBH", 2, bits, len(uniq))
        out += struct.pack("<%dH" % len(uniq), *[v & 0xFFFF for v in uniq])
        out += struct.pack("<%dQ" % len(packed), *packed)

    if stats_out is not None:
        stats_out["solid"] = solid_count
        stats_out["faces"] = faces
        stats_out["yMin"] = min(y for y in (t for t in top if t != -32768)) if solid_count else None
        stats_out["yMax"] = max(t for t in top if t != -32768) if solid_count else None
        stats_out["sections"] = y1 - y0 + 1
        if solid_count:
            best = max(range(256), key=lambda i: top[i])
            stats_out["topMax"] = (best % 16, best // 16, top[best])
    return bytes(out), solid_count, faces, (y0, y1)


# ---------------------------------------------------------------- cell io
def deflate(data):
    co = zlib.compressobj(6, zlib.DEFLATED, -15)
    return co.compress(data) + co.flush()


def inflate(data):
    do = zlib.decompressobj(-15)
    return do.decompress(data) + do.flush()


def write_cell(path, cell_x, cell_z, entries, palette_count):
    """entries: list of dict(cx,cz,sectionCount,solid,data) - data area assembled here."""
    data_area = bytearray()
    index = bytearray()
    for e in entries:
        off = len(data_area)
        data_area += e["data"]
        index += struct.pack("<bbBBiii", e["cx"], e["cz"], 1, e["sectionCount"],
                             off, len(e["data"]), e["solid"])
    raw = bytes(index) + bytes(data_area)
    payload = deflate(raw)
    header = struct.pack("<4sHHiiiiIi", MAGIC, FORMAT_VERSION, 32, cell_x, cell_z,
                         len(entries), palette_count, 1, len(payload))
    with open(path, "wb") as f:
        f.write(header)
        f.write(payload)
    return len(header) + len(payload)


# ---------------------------------------------------------------- commands
def read_level_dat(world):
    path = os.path.join(world, "level.dat")
    if not os.path.exists(path):
        return {}
    with open(path, "rb") as f:
        nbt = parse_nbt(f.read())
    d = (nbt or {}).get("Data", nbt or {})
    wgs = d.get("WorldGenSettings") or {}
    ver = d.get("Version") or {}
    return {
        "dataVersion": d.get("DataVersion"),
        "mcVersion": ver.get("Name"),
        "levelName": d.get("LevelName"),
        "seed": wgs.get("seed"),
        "spawn": [d.get("SpawnX"), d.get("SpawnY"), d.get("SpawnZ")],
    }


def cmd_scan(args):
    meta = read_level_dat(args.world)
    print("world: %s" % args.world)
    print("meta : %s" % json.dumps(meta, ensure_ascii=False))
    total = full = 0
    names = Counter()
    rdir = os.path.join(args.world, "region")
    for fn in sorted(os.listdir(rdir)):
        if not fn.endswith(".mca"):
            continue
        n = 0
        for cx, cz, nbt in iter_region_chunks(os.path.join(rdir, fn)):
            n += 1
            if nbt.get("Status") != "minecraft:full":
                continue
            full += 1
            for sec in nbt.get("sections", []):
                bs = sec.get("block_states") or {}
                for p in bs.get("palette", [])[:64]:
                    names[p.get("Name")] += 1
        total += n
        print("  %-14s chunks=%d" % (fn, n))
    print("total chunks entries=%d full=%d distinct_blocks=%d" % (total, full, len(names)))
    print("top:", names.most_common(10))
    return 0


def cmd_build(args):
    t0 = time.time()
    meta = read_level_dat(args.world)
    if not meta:
        print("! level.dat not readable")
    blocks_doc, by_name = load_blocks(args.blocks)
    name2id = {b["name"]: b["id"] for b in blocks_doc["blocks"]}
    solid_ids = {b["id"]: bool(b.get("solid", True)) for b in blocks_doc["blocks"]}
    palette_count = len(blocks_doc["blocks"])
    unknown = {}

    os.makedirs(args.out, exist_ok=True)
    cells_dir = os.path.join(args.out, "cells")
    os.makedirs(cells_dir, exist_ok=True)

    cells = {}       # (cellx, cellz) -> list of entries
    report = {}
    chunk_min = {}
    total_faces = 0
    total_solid = 0
    total_chunks = 0
    viewpoint = None
    viewpoint_y = -9999

    rdir = os.path.join(args.world, "region")
    files = sorted(f for f in os.listdir(rdir) if f.endswith(".mca"))
    for fn in files:
        per_file = 0
        for cx, cz, nbt in iter_region_chunks(os.path.join(rdir, fn)):
            if nbt.get("Status") != "minecraft:full":
                continue
            st = {}
            data, solid, faces, yr = encode_chunk(nbt, name2id, solid_ids, unknown, args.band, st)
            if not data:
                continue
            cellx, cellz = cx // CELL, cz // CELL
            cells.setdefault((cellx, cellz), []).append({
                "cx": cx - cellx * CELL, "cz": cz - cellz * CELL,
                "sectionCount": st["sections"], "solid": solid, "data": data,
            })
            key = "%d,%d" % (cx, cz)
            report[key] = {"solid": solid, "faces": faces,
                           "yMin": st["yMin"], "yMax": st["yMax"]}
            total_faces += faces
            total_solid += solid
            total_chunks += 1
            per_file += 1
            if st.get("topMax"):
                lx, lz, ty = st["topMax"]
                if ty > viewpoint_y:
                    viewpoint_y = ty
                    viewpoint = [cx * 16 + lx + 0.5, ty + 4.0, cz * 16 + lz + 0.5]
        print("  %-14s full chunks encoded: %d" % (fn, per_file))

    manifest_cells = []
    for (cellx, cellz) in sorted(cells):
        entries = sorted(cells[(cellx, cellz)], key=lambda e: (e["cz"], e["cx"]))
        fname = "c%d_%d.vxcell" % (cellx, cellz)
        path = os.path.join(cells_dir, fname)
        size = write_cell(path, cellx, cellz, entries, palette_count)
        cxs = [cellx * CELL + e["cx"] for e in entries]
        czs = [cellz * CELL + e["cz"] for e in entries]
        with open(path, "rb") as f:
            digest = hashlib.md5(f.read()).hexdigest()
        manifest_cells.append({
            "file": "cells/" + fname, "cellX": cellx, "cellZ": cellz,
            "chunkMinX": min(cxs), "chunkMaxX": max(cxs),
            "chunkMinZ": min(czs), "chunkMaxZ": max(czs),
            "chunks": len(entries), "bytes": size, "md5": digest,
        })
        chunk_min[(cellx, cellz)] = (min(cxs), max(cxs), min(czs), max(czs))

    all_cx = [v[0] for v in chunk_min.values()] + [v[1] for v in chunk_min.values()]
    all_cz = [v[2] for v in chunk_min.values()] + [v[3] for v in chunk_min.values()]
    bounds = {
        "chunkX": [min(all_cx), max(all_cx)], "chunkZ": [min(all_cz), max(all_cz)],
        "blockX": [min(all_cx) * 16, max(all_cx) * 16 + 15],
        "blockZ": [min(all_cz) * 16, max(all_cz) * 16 + 15],
        "blockY": [MIN_Y, MIN_Y + WORLD_HEIGHT - 1],
        "surfaceYRange": [
            min(r["yMin"] for r in report.values() if r["yMin"] is not None),
            max(r["yMax"] for r in report.values() if r["yMax"] is not None),
        ],
    }
    spawn_view = None
    if meta.get("spawn"):
        try:
            spawn_view = compute_spawn_view(args.out, manifest_cells, meta["spawn"], CELL)
        except Exception as exc:  # noqa: BLE001
            print("  ! spawn view failed: %r" % (exc,))

    manifest = {
        "formatId": "vxworld/1",
        "name": meta.get("levelName") or "world",
        "source": {
            "mcVersion": meta.get("mcVersion"), "dataVersion": meta.get("dataVersion"),
            "seed": meta.get("seed"),
            "regionMd5": [md5_file(os.path.join(rdir, f)) for f in files],
        },
        "axes": {"up": "Y", "mapping": "mc(x,0,z) -> unity(x,0,z) 同号", "blockSizeMeters": 1.0},
        "bounds": bounds,
        "spawn": {"block": meta.get("spawn"),
                  "chunk": ([meta["spawn"][0] // 16, meta["spawn"][2] // 16]
                            if meta.get("spawn") else [0, 0])},
        "viewpoint": viewpoint,
        "spawnView": spawn_view,
        "cellSize": CELL,
        "bandDefault": args.band,
        "blocks": "blocks.json",
        "cells": manifest_cells,
    }
    with open(os.path.join(args.out, "world.manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)
    with open(os.path.join(args.out, "blocks.json"), "w", encoding="utf-8") as f:
        json.dump(blocks_doc, f, ensure_ascii=False, indent=2)

    summary = {
        "world": args.world, "out": args.out, "chunks": total_chunks,
        "cells": len(manifest_cells), "solidBlocks": total_solid, "faces": total_faces,
        "facesPerChunk": round(total_faces / max(total_chunks, 1), 1),
        "bounds": bounds,
        "bytes": sum(c["bytes"] for c in manifest_cells),
        "seconds": round(time.time() - t0, 1),
        "unknownBlocks": sorted(unknown.keys()),
    }
    with open(os.path.join(args.out, "report.json"), "w", encoding="utf-8") as f:
        json.dump({"summary": summary, "chunks": report}, f, ensure_ascii=False)
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    return 0


def md5_file(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def compute_spawn_view(out_dir, manifest_cells, spawn, cell_size):
    """Pick an open, flat column near the MC spawn so the first frame is not inside a tree."""
    sx, sy, sz = spawn[0], spawn[1], spawn[2]
    cx, cz = sx // 16, sz // 16
    cellx, cellz = cx // cell_size, cz // cell_size
    info = next((c for c in manifest_cells if c["cellX"] == cellx and c["cellZ"] == cellz), None)
    if info is None:
        return None
    cell = read_cell(os.path.join(out_dir, info["file"]))
    entry = next((e for e in cell["index"]
                  if cellx * cell_size + e["cx"] == cx and cellz * cell_size + e["cz"] == cz), None)
    if entry is None:
        return None
    rec = cell["data"][entry["offset"]:entry["offset"] + entry["length"]]
    _y0, _nsec, top, _secs = decode_chunk_record(rec)
    best = None
    for lz in range(2, 14):
        for lx in range(2, 14):
            vals = [top[(lz + dz) * 16 + (lx + dx)] for dx in range(-2, 3) for dz in range(-2, 3)]
            if min(vals) == -32768:
                continue
            flat = max(vals) - min(vals)
            bx, bz = cx * 16 + lx, cz * 16 + lz
            dist = ((bx - sx) ** 2 + (bz - sz) ** 2) ** 0.5
            score = flat * 2.0 + dist * 0.15
            if best is None or score < best[0]:
                best = (score, bx, bz, top[lz * 16 + lx])
    if best is None:
        return None
    return [best[1] + 0.5, best[3] + 2.2, best[2] + 0.5]


def read_cell(path):
    with open(path, "rb") as f:
        blob = f.read()
    magic, version, hbytes, cellx, cellz, count, palette, flags, plen = struct.unpack_from("<4sHHiiiiIi", blob, 0)
    if magic != MAGIC:
        raise ValueError("bad magic in %s" % path)
    payload = blob[hbytes:hbytes + plen]
    raw = inflate(payload) if flags & 1 else payload
    index = []
    off = count * 16
    for i in range(count):
        cx, cz, eflags, secs, doff, dlen, solid = struct.unpack_from("<bbBBiii", raw, i * 16)
        index.append({"cx": cx, "cz": cz, "flags": eflags, "sections": secs,
                      "offset": doff, "length": dlen, "solid": solid})
    data = raw[off:]
    return {"cellX": cellx, "cellZ": cellz, "count": count, "palette": palette,
            "index": index, "data": data}


def decode_chunk_record(rec):
    """Parse one chunk record -> (minSectionY, sectionCount, top[256], sections)."""
    o = 0
    y0, nsec = struct.unpack_from("<hB", rec, o); o += 3
    top = list(struct.unpack_from("<256h", rec, o)); o += 512
    sections = []
    for _ in range(nsec):
        mode = rec[o]; o += 1
        if mode == 0:
            sections.append((0, None, None))
        elif mode == 1:
            bid = struct.unpack_from("<H", rec, o)[0]; o += 2
            sections.append((1, bid, None))
        elif mode == 2:
            bits, paln = struct.unpack_from("<BH", rec, o); o += 3
            pal = list(struct.unpack_from("<%dH" % paln, rec, o)); o += 2 * paln
            nlong = (4096 + (64 // bits) - 1) // (64 // bits)
            words = struct.unpack_from("<%dQ" % nlong, rec, o); o += 8 * nlong
            idx = unpack_bits(words, bits, 4096)
            sections.append((2, pal, idx))
        else:
            raise ValueError("bad section mode %d" % mode)
    return y0, nsec, top, sections


def cmd_validate(args):
    man_path = os.path.join(args.dir, "world.manifest.json")
    with open(man_path, "r", encoding="utf-8") as f:
        man = json.load(f)
    with open(os.path.join(args.dir, "report.json"), "r", encoding="utf-8") as f:
        report = json.load(f)
    blocks = man["cells"]
    errors = []
    chunks = 0
    solid_total = 0
    for c in blocks:
        path = os.path.join(args.dir, c["file"])
        if not os.path.exists(path):
            errors.append("missing %s" % c["file"]); continue
        cell = read_cell(path)
        if cell["count"] != c["chunks"]:
            errors.append("%s chunk count %d != manifest %d" % (c["file"], cell["count"], c["chunks"]))
        for e in cell["index"]:
            cx = c["cellX"] * CELL + e["cx"]
            cz = c["cellZ"] * CELL + e["cz"]
            key = "%d,%d" % (cx, cz)
            rec = cell["data"][e["offset"]:e["offset"] + e["length"]]
            try:
                y0, nsec, top, sections = decode_chunk_record(rec)
            except Exception as exc:  # noqa: BLE001
                errors.append("%s chunk %s decode: %r" % (c["file"], key, exc)); continue
            chunks += 1
            rep = report["chunks"].get(key)
            if rep is None:
                errors.append("chunk %s not in report" % key); continue
            if abs(rep["solid"] - e["solid"]) > 0:
                errors.append("chunk %s solid %d != report %d" % (key, e["solid"], rep["solid"]))
            solid_total += e["solid"]
    print("cells=%d chunks=%d solid=%d errors=%d" % (len(blocks), chunks, solid_total, len(errors)))
    for e in errors[:20]:
        print("  !", e)
    return 0 if not errors else 1


def cmd_dump(args):
    with open(os.path.join(args.dir, "world.manifest.json"), "r", encoding="utf-8") as f:
        man = json.load(f)
    cx, cz = [int(v) for v in args.chunk.split(",")]
    target = None
    for c in man["cells"]:
        if c["cellX"] == cx // CELL and c["cellZ"] == cz // CELL:
            target = c
            break
    if target is None:
        print("cell not found"); return 1
    cell = read_cell(os.path.join(args.dir, target["file"]))
    for e in cell["index"]:
        if cx - target["cellX"] * CELL == e["cx"] and cz - target["cellZ"] * CELL == e["cz"]:
            rec = cell["data"][e["offset"]:e["offset"] + e["length"]]
            y0, nsec, top, sections = decode_chunk_record(rec)
            print("chunk %d,%d  minSectionY=%d sections=%d" % (cx, cz, y0, nsec))
            for yy in range(args.y - 2, args.y + 3):
                si = (yy // 16) - y0
                if si < 0 or si >= nsec:
                    continue
                mode, a, b = sections[si]
                row = []
                for x in range(16):
                    col = 0 * 16 + x
                    if mode == 0:
                        row.append(".")
                    elif mode == 1:
                        row.append("#" if a else ".")
                    else:
                        idx = b[(yy % 16) * 256 + col]
                        row.append("#" if a[idx] else ".")
                print("y=%-4d z=0  %s" % (yy, "".join(row)))
            return 0
    print("chunk not found"); return 1


def main(argv=None):
    ap = argparse.ArgumentParser(description="Minecraft Anvil -> normalized voxel data")
    sub = ap.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("scan")
    s.add_argument("--world", required=True)
    s.set_defaults(func=cmd_scan)

    b = sub.add_parser("build")
    b.add_argument("--world", required=True)
    b.add_argument("--out", required=True)
    b.add_argument("--blocks", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "blocks.json"))
    b.add_argument("--band", type=int, default=8)
    b.set_defaults(func=cmd_build)

    v = sub.add_parser("validate")
    v.add_argument("--dir", required=True)
    v.set_defaults(func=cmd_validate)

    d = sub.add_parser("dump")
    d.add_argument("--dir", required=True)
    d.add_argument("--chunk", required=True)
    d.add_argument("--y", type=int, default=62)
    d.set_defaults(func=cmd_dump)

    args = ap.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
