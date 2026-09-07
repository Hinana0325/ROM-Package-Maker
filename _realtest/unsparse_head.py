#!/usr/bin/env python3
"""抽样解 sparse super.img 的前 N 字节，并统计 chunk 分布（用于验证 liblp 元数据解析）。"""
import struct, sys, collections

src = sys.argv[1]
out = sys.argv[2]
cap = int(sys.argv[3]) if len(sys.argv) > 3 else 4 * 1024 * 1024

f = open(src, 'rb')
hdr = f.read(28)
magic, major, minor, file_hdr_sz, chunk_hdr_sz, blk_sz, total_blks, total_chunks, crc = struct.unpack('<IHHHHIIII', hdr)
assert magic == 0xED26FF3A, hex(magic)
print(f"magic=0x{magic:08X} ver={major}.{minor} file_hdr={file_hdr_sz} chunk_hdr={chunk_hdr_sz}")
print(f"blk_sz={blk_sz} total_blks={total_blks:,} ({total_blks*blk_sz/2**30:.2f} GiB) total_chunks={total_chunks:,}")

f.seek(file_hdr_sz)
o = open(out, 'wb')
written = 0
stats = collections.Counter()
biggest_dontcare = 0
zero = b'\0' * blk_sz

for i in range(total_chunks):
    if written >= cap:
        break
    ch = f.read(12)
    if len(ch) < 12:
        break
    ctype, _, chunk_blocks, total_sz = struct.unpack('<HHII', ch)
    data_sz = total_sz - chunk_hdr_sz
    stats[ctype] += 1

    if ctype == 0xCAC1:  # raw
        n = min(chunk_blocks * blk_sz, cap - written)
        o.write(f.read(n))
        f.seek(f.tell() + (chunk_blocks * blk_sz - n))
        written += n
    elif ctype == 0xCAC2:  # fill
        val = f.read(4)
        pat = (val * (blk_sz // 4 + 1))[:blk_sz]
        n = min(chunk_blocks * blk_sz, cap - written)
        o.write((pat * (n // blk_sz + 1))[:n])
        written += n
    elif ctype == 0xCAC3:  # don't-care
        biggest_dontcare = max(biggest_dontcare, chunk_blocks * blk_sz)
        n = min(chunk_blocks * blk_sz, cap - written)
        o.write(zero[:n] if n < blk_sz else b'\0' * n)
        written += n

names = {0xCAC1: 'raw', 0xCAC2: 'fill', 0xCAC3: 'don\'t-care', 0xCAC4: 'crc32'}
print("已处理 chunk 类型统计(到 4MB 为止):", {names.get(k, hex(k)): v for k, v in stats.items()})
print(f"最大 don't-care chunk（截至已扫描部分）: {biggest_dontcare/2**20:.1f} MiB")
o.close()
print(f"输出 {written:,} 字节 -> {out}")
