namespace RomPackageMaker.Services;

/// <summary>
/// Android sparse 镜像格式（system.img 等分区镜像常见的存储格式）的解析与生成。
/// 参考 system/core/libsparse/sparse_format.h。
/// </summary>
internal static class SparseImage
{
    public const uint SparseMagic = 0xED26FF3A;
    public const ushort ChunkTypeRaw = 0xCAC1;
    public const ushort ChunkTypeFill = 0xCAC2;
    public const ushort ChunkTypeDontCare = 0xCAC3;
    public const ushort ChunkTypeCrc32 = 0xCAC4;

    /// <summary>判断流当前位置是否为 sparse 镜像（不消费数据）。</summary>
    public static bool IsSparse(Stream stream)
    {
        long pos = stream.Position;
        try
        {
            Span<byte> buf = stackalloc byte[4];
            if (stream.Read(buf) != 4) return false;
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf) == SparseMagic;
        }
        finally
        {
            stream.Position = pos;
        }
    }

    /// <summary>将 sparse 镜像展开为原始（raw）镜像写入目标流。</summary>
    public static void Unsparse(Stream input, Stream output, IProgress<RomTaskProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var reader = new BinaryReader(input, System.Text.Encoding.Default, leaveOpen: true);

        uint magic = reader.ReadUInt32LE();
        if (magic != SparseMagic) throw new InvalidDataException("不是有效的 sparse 镜像（magic 不匹配）。");

        ushort major = reader.ReadUInt16LE();
        ushort minor = reader.ReadUInt16LE();
        ushort fileHdrSz = reader.ReadUInt16LE();
        ushort chunkHdrSz = reader.ReadUInt16LE();
        uint blkSz = reader.ReadUInt32LE();
        uint totalBlks = reader.ReadUInt32LE();
        uint totalChunks = reader.ReadUInt32LE();
        uint checksum = reader.ReadUInt32LE();
        _ = checksum;

        // 跳到首个 chunk（兼容 header 尺寸大于 28 的情况）
        if (fileHdrSz > 28) input.Seek(fileHdrSz - 28, SeekOrigin.Current);

        long outputBytes = 0;
        long totalBytes = (long)totalBlks * blkSz;
        var fillBuf = new byte[blkSz];

        for (uint i = 0; i < totalChunks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ushort chunkType = reader.ReadUInt16LE();
            reader.ReadUInt16LE(); // reserved
            uint chunkSz = reader.ReadUInt32LE(); // in blocks
            uint totalSz = reader.ReadUInt32LE();

            long dataSize = totalSz - chunkHdrSz;
            long posBefore = input.Position;

            switch (chunkType)
            {
                case ChunkTypeRaw:
                    input.CopyExact(output, chunkSz * (long)blkSz);
                    outputBytes += chunkSz * (long)blkSz;
                    break;

                case ChunkTypeFill:
                    {
                        if (input.Read(fillBuf, 0, 4) != 4) throw new EndOfStreamException();
                        // 将 4 字节填充值扩展到整个块
                        for (int b = 4; b < blkSz; b++) fillBuf[b] = fillBuf[b & 3];
                        for (uint b = 0; b < chunkSz; b++)
                        {
                            output.Write(fillBuf, 0, (int)blkSz);
                        }
                        outputBytes += chunkSz * (long)blkSz;
                    }
                    break;

                case ChunkTypeDontCare:
                    // 写零
                    output.Write(new byte[chunkSz * (long)blkSz]);
                    outputBytes += chunkSz * (long)blkSz;
                    break;

                case ChunkTypeCrc32:
                    // 跳过 CRC32 chunk
                    break;

                default:
                    // 未知 chunk 类型，跳过数据部分
                    break;
            }

            // 确保流位置正确（有些 chunk 可能有对齐填充）
            input.Position = posBefore + dataSize;

            if (progress != null && totalBytes > 0)
            {
                int pct = (int)Math.Min(100, outputBytes * 100 / totalBytes);
                progress.Report(new RomTaskProgress(pct, "展开 sparse 镜像"));
            }
        }
    }

    /// <summary>
    /// 将原始镜像转换为 sparse 格式写入目标流。
    /// 使用简单策略：连续相同 4 字节的区域转为 fill chunk，零区域转为 dontcare，其余为 raw。
    /// </summary>
    public static void Sparseify(Stream input, Stream output, uint blockSize = 4096, IProgress<RomTaskProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        long totalBytes = input.Length;
        uint totalBlks = (uint)((totalBytes + blockSize - 1) / blockSize);

        // 先写一个占位 header，最后回来更新
        long headerPos = output.Position;
        using (var writer = new BinaryWriter(output, System.Text.Encoding.Default, leaveOpen: true))
        {
            writer.WriteUInt32LE(SparseMagic);
            writer.WriteUInt16LE(1); // major
            writer.WriteUInt16LE(0); // minor
            writer.WriteUInt16LE(28); // file_hdr_sz
            writer.WriteUInt16LE(12); // chunk_hdr_sz
            writer.WriteUInt32LE(blockSize);
            writer.WriteUInt32LE(totalBlks);
            writer.WriteUInt32LE(0); // total_chunks 占位
            writer.WriteUInt32LE(0); // checksum
        }

        uint totalChunks = 0;
        var blockBuf = new byte[blockSize];
        long processed = 0;

        while (processed < totalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long remaining = totalBytes - processed;
            int toRead = (int)Math.Min(blockSize, remaining);

            int n = input.Read(blockBuf, 0, toRead);
            if (n <= 0) break;
            if (n < blockSize) Array.Clear(blockBuf, n, (int)blockSize - n);

            // 判断是否全零
            bool allZero = true;
            for (int i = 0; i < blockSize; i++)
            {
                if (blockBuf[i] != 0) { allZero = false; break; }
            }

            using var writer = new BinaryWriter(output, System.Text.Encoding.Default, leaveOpen: true);

            if (allZero)
            {
                writer.WriteUInt16LE(ChunkTypeDontCare);
                writer.WriteUInt16LE(0); // reserved
                writer.WriteUInt32LE(1); // chunk_sz (blocks)
                writer.WriteUInt32LE(12); // total_sz
                totalChunks++;
            }
            else
            {
                // 判断是否为 fill（4 字节重复）
                bool isFill = true;
                for (int i = 4; i < blockSize; i++)
                {
                    if (blockBuf[i] != blockBuf[i & 3]) { isFill = false; break; }
                }

                if (isFill)
                {
                    writer.WriteUInt16LE(ChunkTypeFill);
                    writer.WriteUInt16LE(0);
                    writer.WriteUInt32LE(1);
                    writer.WriteUInt32LE(16); // 12 + 4
                    writer.Write(blockBuf, 0, 4);
                    totalChunks++;
                }
                else
                {
                    writer.WriteUInt16LE(ChunkTypeRaw);
                    writer.WriteUInt16LE(0);
                    writer.WriteUInt32LE(1);
                    writer.WriteUInt32LE(12 + blockSize);
                    writer.Write(blockBuf, 0, (int)blockSize);
                    totalChunks++;
                }
            }

            processed += blockSize;

            if (progress != null && totalBytes > 0)
            {
                int pct = (int)Math.Min(100, processed * 100 / totalBytes);
                progress.Report(new RomTaskProgress(pct, "生成 sparse 镜像"));
            }
        }

        // 回填 total_chunks
        long endPos = output.Position;
        output.Position = headerPos + 20; // total_chunks 偏移
        using (var writer = new BinaryWriter(output, System.Text.Encoding.Default, leaveOpen: true))
        {
            writer.WriteUInt32LE(totalChunks);
        }
        output.Position = endPos;
    }
}
