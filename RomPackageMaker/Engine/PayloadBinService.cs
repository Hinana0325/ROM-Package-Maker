using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;

using RomPackageMaker.Core;
namespace RomPackageMaker.Engine;

/// <summary>InstallOperation.Type（update_metadata.proto）。</summary>
public enum PayloadOpType
{
    Replace = 0,
    ReplaceBz = 1,
    Move = 2,
    Bsdiff = 3,
    SourceCopy = 4,
    SourceBsdiff = 5,
    Zero = 6,
    Discard = 7,
    ReplaceXz = 8,
    Puffdiff = 9,
    BrotliBsdiff = 10,
    Zucchini = 11,
    Lz4diffBsdiff = 12,
    Lz4diffPuffdiff = 13,
    Zstd = 14,
}

/// <summary>payload.bin 中的一个分区更新。</summary>
public sealed class PayloadPartitionInfo : INotifyPropertyChanged
{
    private bool _isSelected;

    // 注意：属性一律使用 set（init 会破坏 XAML 类型生成）
    public string Name { get; set; } = string.Empty;
    public List<PayloadOperation> Operations { get; set; } = new();
    public uint BlockSize { get; set; }
    public long DataOffsetBase { get; set; }

    /// <summary>new_partition_info.size：目标分区精确大小（字节）；无该信息时为 0。</summary>
    public ulong NewPartitionSize { get; set; }

    /// <summary>new_partition_info.hash：目标分区 SHA-256；无该信息时为空。</summary>
    public byte[] Sha256Hash { get; set; } = Array.Empty<byte>();

    /// <summary>manifest 是否携带该分区的新版本哈希（可用于提取后校验）。</summary>
    public bool HasHash => Sha256Hash.Length > 0;

    /// <summary>哈希缩写（前 8 字节 hex），供 UI 显示。</summary>
    public string HashShortText => HasHash ? Convert.ToHexString(Sha256Hash, 0, 4) : string.Empty;

    /// <summary>是否勾选（用于批量提取）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    /// <summary>全部操作均为可提取类型（REPLACE 家族 + ZERO/DISCARD/MOVE；无需旧分区镜像）。</summary>
    public bool IsSupported =>
        Operations.All(op => IsSupportedOp(op.Type));

    /// <summary>输出镜像大小：优先 manifest 给出的精确值，否则按最大目标 extent 估算。</summary>
    public long ImageSize
    {
        get
        {
            if (NewPartitionSize > 0) return (long)NewPartitionSize;
            long maxEnd = 0;
            foreach (var op in Operations)
            {
                foreach (var ext in op.DstExtents)
                {
                    maxEnd = Math.Max(maxEnd, (long)ext.EndBlock);
                }
            }
            return maxEnd * BlockSize;
        }
    }

    public string SizeText => ImageSize >= 1 << 20 ? $"{ImageSize / (double)(1 << 20):0.#} MB" : $"{ImageSize / (double)(1 << 10):0.#} KB";

    /// <summary>列表状态行：操作数 + 可提取性 + 哈希信息。</summary>
    public string StatusLine => $"{Operations.Count} 个操作 · {UnsupportedText}{(HasHash ? " · 含 SHA-256" : "")}";

    /// <summary>不支持时的原因摘要（供 UI 显示）。</summary>
    public string UnsupportedText
    {
        get
        {
            var bad = Operations
                .Where(op => !IsSupportedOp(op.Type))
                .Select(op => op.Type.ToString())
                .Distinct()
                .ToList();
            return bad.Count == 0
                ? "可提取"
                : $"需要源镜像（{string.Join("、", bad)}）";
        }
    }

    internal static bool IsSupportedOp(PayloadOpType t) => t is PayloadOpType.Replace or PayloadOpType.ReplaceBz
        or PayloadOpType.ReplaceXz
        or PayloadOpType.Zero or PayloadOpType.Discard or PayloadOpType.Move;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>InstallOperation。</summary>
public sealed class PayloadOperation
{
    public PayloadOpType Type { get; set; }
    public uint DataOffset { get; set; }
    public long DataLength { get; set; }
    public List<PayloadExtent> SrcExtents { get; } = new();
    public List<PayloadExtent> DstExtents { get; } = new();
}

/// <summary>Extent（块区间 [start, start+num)）。</summary>
public sealed class PayloadExtent
{
    public ulong StartBlock { get; set; }
    public ulong NumBlocks { get; set; }
    public ulong EndBlock => StartBlock + NumBlocks;
}

/// <summary>payload.bin 解析结果。Version 即头部的 payload 主版本号。</summary>
public sealed class PayloadInfo
{
    public ulong Version { get; set; }
    public uint BlockSize { get; set; } = 4096;
    public uint MinorVersion { get; set; }
    public List<PayloadPartitionInfo> Partitions { get; } = new();
    public long DataOffsetBase { get; set; }
}

/// <summary>
/// payload.bin（Android A/B OTA 有效载荷）解析与分区提取。
/// 布局：头（"CrAU" 魔数 4B + version 8B + manifest_size 8B [+ metadata_sig_size 4B，仅 v2]）
/// + manifest(protobuf) + 元数据签名 + 数据区。
/// 提取支持全量 OTA 的操作类型：REPLACE / REPLACE_BZ / REPLACE_XZ / ZERO / DISCARD / MOVE；
/// 增量类型（SOURCE_* / BSDIFF / PUFFDIFF 等）需要旧分区镜像，标记为不可提取。
/// </summary>
public static class PayloadBinService
{
    /// <summary>payload 头魔数 "CrAU"。</summary>
    public static ReadOnlySpan<byte> Magic => "CrAU"u8;

    /// <summary>解析 payload.bin。也可传 OTA zip（自动定位其中的 payload.bin 条目）。</summary>
    public static PayloadInfo Parse(string payloadPath)
    {
        using var fs = File.OpenRead(payloadPath);

        Span<byte> head = stackalloc byte[20];
        if (fs.Read(head) < 20)
            throw new InvalidDataException("文件过小，不是有效的 payload.bin。");

        if (!head.Slice(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("payload 魔数不匹配（非 CrAU），可能不是 A/B OTA 刷机包。");

        ulong version = BinaryPrimitives.ReadUInt64LittleEndian(head.Slice(4));
        ulong manifestSize = BinaryPrimitives.ReadUInt64LittleEndian(head.Slice(12));

        uint metadataSigSize = 0;
        if (version >= 2)
        {
            Span<byte> sig = stackalloc byte[4];
            if (fs.Read(sig) < 4)
                throw new InvalidDataException("payload 头读取不完整。");
            metadataSigSize = BinaryPrimitives.ReadUInt32LittleEndian(sig);
        }

        var manifest = new byte[manifestSize];
        if (fs.Read(manifest) < (int)manifestSize)
            throw new InvalidDataException("manifest 读取不完整。");

        long dataBase = (version >= 2 ? 24 : 20) + (long)manifestSize + metadataSigSize;
        var info = ParseManifest(manifest, dataBase);
        info.Version = version;
        return info;
    }

    /// <summary>从 OTA zip 中提取 payload.bin 到临时文件，返回临时路径；未找到返回 null。</summary>
    public static string? ExtractFromZip(string zipPath, string? destPath = null)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.FirstOrDefault(e =>
            string.Equals(e.Name, "payload.bin", StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("zip 中未找到 payload.bin（非 A/B OTA 刷机包？）。");

        destPath ??= Path.Combine(Path.GetTempPath(), "rpm_payload_" + Guid.NewGuid().ToString("N")[..8] + ".bin");
        entry.ExtractToFile(destPath, overwrite: true);
        return destPath;
    }

    // ==================== manifest（最小 protobuf 解码） ====================

    private static PayloadInfo ParseManifest(byte[] data, long dataBase)
    {
        var info = new PayloadInfo { DataOffsetBase = dataBase };
        var reader = new ProtoReader(data);

        while (reader.HasMore)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 3: // block_size
                    info.BlockSize = (uint)reader.ReadVarint(wire);
                    break;
                case 12: // minor_version
                    info.MinorVersion = (uint)reader.ReadVarint(wire);
                    break;
                case 13: // partitions
                    info.Partitions.Add(ParsePartition(reader.ReadMessage(wire), info));
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        if (info.BlockSize == 0) info.BlockSize = 4096;
        return info;
    }

    private static PayloadPartitionInfo ParsePartition(byte[] data, PayloadInfo info)
    {
        var part = new PayloadPartitionInfo { BlockSize = info.BlockSize, DataOffsetBase = info.DataOffsetBase };
        var reader = new ProtoReader(data);

        while (reader.HasMore)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 1: // partition_name
                    part.Name = Encoding.UTF8.GetString(reader.ReadMessage(wire));
                    break;
                case 7: // new_partition_info
                    ParsePartitionInfo(reader.ReadMessage(wire), part);
                    break;
                case 8: // operations
                    part.Operations.Add(ParseOperation(reader.ReadMessage(wire)));
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }
        return part;
    }

    /// <summary>PartitionInfo：size=1（uint64）、hash=2（bytes，SHA-256）。</summary>
    private static void ParsePartitionInfo(byte[] data, PayloadPartitionInfo part)
    {
        var reader = new ProtoReader(data);
        while (reader.HasMore)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 1: part.NewPartitionSize = reader.ReadVarint(wire); break;
                case 2: part.Sha256Hash = reader.ReadMessage(wire); break;
                default: reader.Skip(wire); break;
            }
        }
    }

    private static PayloadOperation ParseOperation(byte[] data)
    {
        var op = new PayloadOperation();
        var reader = new ProtoReader(data);

        while (reader.HasMore)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 1: op.Type = (PayloadOpType)reader.ReadVarint(wire); break;
                case 2: op.DataOffset = (uint)reader.ReadVarint(wire); break;
                case 3: op.DataLength = (long)reader.ReadVarint(wire); break;
                case 4: op.SrcExtents.Add(ParseExtent(reader.ReadMessage(wire))); break;
                case 6: op.DstExtents.Add(ParseExtent(reader.ReadMessage(wire))); break;
                default: reader.Skip(wire); break;
            }
        }
        return op;
    }

    private static PayloadExtent ParseExtent(byte[] data)
    {
        var ext = new PayloadExtent();
        var reader = new ProtoReader(data);
        while (reader.HasMore)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 1: ext.StartBlock = reader.ReadVarint(wire); break;
                case 2: ext.NumBlocks = reader.ReadVarint(wire); break;
                default: reader.Skip(wire); break;
            }
        }
        return ext;
    }

    // ==================== 分区提取 ====================

    /// <summary>提取单个分区为 &lt;outputDir&gt;/&lt;name&gt;.img。仅支持全量操作类型，含增量操作时抛异常。
    /// 返回值：是否执行并通过了 manifest 哈希校验（无哈希信息时返回 false，失败抛异常）。</summary>
    public static bool ExtractPartition(string payloadPath, PayloadPartitionInfo part, string outputDir,
        IProgress<RomTaskProgress> progress, CancellationToken cancellationToken)
    {
        if (!part.IsSupported)
            throw new InvalidOperationException($"分区 {part.Name} 含有需要源镜像的增量操作，无法从全量 payload 提取。");

        Directory.CreateDirectory(outputDir);
        string outPath = Path.Combine(outputDir, part.Name + ".img");
        long blockSize = part.BlockSize;

        using var payload = File.OpenRead(payloadPath);
        using var output = new FileStream(outPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, bufferSize: 1 << 16);
        output.SetLength(part.ImageSize);

        int total = part.Operations.Count;
        for (int i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var op = part.Operations[i];
            progress.Report(new RomTaskProgress(5 + i * 85 / Math.Max(1, total), part.Name,
                $"  操作 {i + 1}/{total}（{op.Type}）"));

            switch (op.Type)
            {
                case PayloadOpType.Zero:
                case PayloadOpType.Discard:
                    // 输出文件已预置零，跳过实际写入
                    break;

                case PayloadOpType.Replace:
                case PayloadOpType.ReplaceBz:
                case PayloadOpType.ReplaceXz:
                {
                    payload.Position = part.DataOffsetBase + op.DataOffset;
                    byte[] data;
                    if (op.Type == PayloadOpType.Replace)
                    {
                        data = new byte[op.DataLength];
                        ReadExactly(payload, data);
                    }
                    else if (op.Type == PayloadOpType.ReplaceBz)
                    {
                        var slice = new StreamSlice(payload, part.DataOffsetBase + op.DataOffset, op.DataLength);
                        using var bz = BZip2Stream.Create(slice, SharpCompress.Compressors.CompressionMode.Decompress,
                            decompressConcatenated: false, leaveOpen: true);
                        data = ReadAll(bz);
                    }
                    else
                    {
                        var slice = new StreamSlice(payload, part.DataOffsetBase + op.DataOffset, op.DataLength);
                        using var decompressed = new XZStream(slice);
                        data = ReadAll(decompressed);
                    }
                    WriteToExtents(output, data, op.DstExtents, blockSize);
                    break;
                }

                case PayloadOpType.Move:
                {
                    // MOVE：在同输出镜像内从 src extents 拷贝到 dst extents
                    byte[] data = new byte[op.SrcExtents.Sum(e => (long)e.NumBlocks * blockSize)];
                    long readPos = 0;
                    foreach (var ext in op.SrcExtents)
                    {
                        output.Position = (long)ext.StartBlock * blockSize;
                        int toRead = (int)(ext.NumBlocks * (ulong)blockSize);
                        ReadExactly(output, data.AsSpan((int)readPos, toRead));
                        readPos += toRead;
                    }
                    WriteToExtents(output, data, op.DstExtents, blockSize);
                    break;
                }

                default:
                    throw new InvalidOperationException($"未支持的操作类型：{op.Type}");
            }
        }

        // manifest 携带新版本哈希时校验提取结果（对整个目标分区镜像）
        bool verified = false;
        if (part.HasHash)
        {
            progress.Report(new RomTaskProgress(92, part.Name, "  SHA-256 校验中..."));
            output.Position = 0;
            byte[] actual;
            using (var sha = SHA256.Create())
            {
                actual = sha.ComputeHash(output);
            }
            if (!actual.AsSpan().SequenceEqual(part.Sha256Hash))
            {
                string expected = Convert.ToHexString(part.Sha256Hash);
                string actualHex = Convert.ToHexString(actual);
                throw new InvalidDataException(
                    $"分区 {part.Name} 提取结果哈希校验失败（期望 {expected[..Math.Min(16, expected.Length)]}…，实际 {actualHex[..Math.Min(16, actualHex.Length)]}…）。");
            }
            verified = true;
        }

        progress.Report(new RomTaskProgress(100, part.Name, $"  完成 → {outPath}"));
        return verified;
    }

    /// <summary>按目标 extents 写入操作数据。REPLACE 家族语义：数据不足块时以零填充到块边界
    /// （输出文件已预置零，只需跳过）；数据超出 extents 总长则为格式错误。</summary>
    private static void WriteToExtents(FileStream output, byte[] data, List<PayloadExtent> extents, long blockSize)
    {
        long pos = 0;
        foreach (var ext in extents)
        {
            if (pos >= data.Length) break; // 剩余块保持预置零
            long len = (long)ext.NumBlocks * blockSize;
            int toWrite = (int)Math.Min(len, data.Length - pos);
            output.Position = (long)ext.StartBlock * blockSize;
            output.Write(data, (int)pos, toWrite);
            pos += len;
        }
        if (pos < data.Length)
            throw new InvalidDataException($"操作数据长度（{data.Length}）超出目标 extents 总长（{pos}）。");
    }

    private static void ReadExactly(Stream s, Span<byte> buffer)
    {
        int filled = 0;
        while (filled < buffer.Length)
        {
            int n = s.Read(buffer.Slice(filled));
            if (n <= 0) throw new EndOfStreamException();
            filled += n;
        }
    }

    private static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>限定长度的流切片（供 XZ/BZip2 包装，禁止越界读取）。</summary>
    private sealed class StreamSlice : Stream
    {
        private readonly Stream _inner;
        private readonly long _length;
        private long _pos;

        public StreamSlice(Stream inner, long offset, long length)
        {
            _inner = inner;
            _inner.Position = offset;
            _length = length;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = _inner.Read(buffer, offset, (int)Math.Min(count, _length - _pos));
            _pos += n;
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            // 不关闭底层流（后续操作还要用）
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>最小 protobuf wire 解码器。</summary>
    private sealed class ProtoReader
    {
        private readonly byte[] _data;
        private int _pos;

        public ProtoReader(byte[] data) => _data = data;

        public bool HasMore => _pos < _data.Length;

        /// <summary>读取字段标签，返回 (fieldNumber, wireType)。</summary>
        public (int Field, int Wire) ReadTag()
        {
            ulong tag = ReadVarintRaw();
            return ((int)(tag >> 3), (int)(tag & 7));
        }

        public ulong ReadVarint(int wireType)
        {
            if (wireType != 0) throw new InvalidDataException($"期望 varint 字段，实际 wire type {wireType}。");
            return ReadVarintRaw();
        }

        public byte[] ReadMessage(int wireType)
        {
            if (wireType != 2) throw new InvalidDataException($"期望 length-delimited 字段，实际 wire type {wireType}。");
            ulong len = ReadVarintRaw();
            var buf = new byte[len];
            Array.Copy(_data, _pos, buf, 0, (int)len);
            _pos += (int)len;
            return buf;
        }

        public void Skip(int wireType)
        {
            switch (wireType)
            {
                case 0: ReadVarintRaw(); break;
                case 1: _pos += 8; break;
                case 2:
                    ulong len = ReadVarintRaw();
                    _pos += (int)len;
                    break;
                case 5: _pos += 4; break;
                default: throw new InvalidDataException($"未知 wire type {wireType}。");
            }
        }

        private ulong ReadVarintRaw()
        {
            ulong result = 0;
            int shift = 0;
            while (_pos < _data.Length)
            {
                byte b = _data[_pos++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
                if (shift > 63) throw new InvalidDataException("varint 过长。");
            }
            throw new InvalidDataException("varint 不完整。");
        }
    }
}
