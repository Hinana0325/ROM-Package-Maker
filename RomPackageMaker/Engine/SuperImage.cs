using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using RomPackageMaker.Core;
namespace RomPackageMaker.Engine;

/// <summary>
/// Android super 动态分区镜像（liblp 格式）的解析与重建。
/// 布局（AOSP system/core/fs_mgr/liblp/metadata_format.h）：
///   0x0000  保留 4096 字节（LP_PARTITION_RESERVED_BYTES）
///   0x1000  primary geometry（4096 字节，含填充）
///   0x2000  backup geometry
///   0x3000  metadata slot 0 primary（大小 = geometry.metadata_max_size）
///           slot N primary   = 0x3000 + N * metadata_max_size
///           slot N backup    = 0x3000 + (slot_count + N) * metadata_max_size
///   数据区  按 1MiB 对齐依次存放各逻辑分区（linear extent）
/// 头部（LpMetadataHeader）：magic@0、major@4、minor@6、header_size@8、
///   header_checksum[32]@12、tables_size@44、tables_checksum[32]@48、
///   partitions 描述符@80 / extents@92 / groups@104 / block_devices@116（各 12 字节：
///   offset/num_entries/entry_size，offset 相对 header 末尾）、可选 flags@128。
/// 表项：partition 52 字节（name[36]/attributes/first_extent_index/num_extents/group_index）、
///   extent 24 字节（num_sectors(8)/target_type(4)/target_data(8)/target_source(4)）、
///   group 48 字节、block device 64 字节。
/// </summary>
internal static class SuperImage
{
    public const uint GeometryMagic = 0x616C4467;   // 磁盘字节 "gDla"
    public const uint HeaderMagic = 0x414C5030;     // 磁盘字节 "0PLA"
    public const uint SectorSize = 512;

    private const int ReservedBytes = 4096;         // LP_PARTITION_RESERVED_BYTES
    private const int GeometrySlotSize = 4096;      // LP_METADATA_GEOMETRY_SIZE
    private const int GeometryStructSize = 52;      // LpMetadataGeometry 实际尺寸
    private const int HeaderBaseSize = 128;         // 不含 flags 字段
    private const int PartitionEntrySize = 52;
    private const int ExtentEntrySize = 24;
    private const int GroupEntrySize = 48;
    private const int BlockDeviceEntrySize = 64;
    private const long DefaultAlignment = 1024 * 1024; // lpmake 默认分区对齐

    public const uint TargetTypeLinear = 0;
    public const uint TargetTypeZero = 1;

    // ==================== 数据模型 ====================

    public sealed class Extent
    {
        /// <summary>长度（512 字节扇区数）。</summary>
        public long NumSectors;
        /// <summary>0 = linear，1 = zero。</summary>
        public uint TargetType;
        /// <summary>linear：super 镜像内的起始扇区。</summary>
        public long TargetData;
        /// <summary>linear：块设备索引。</summary>
        public uint TargetSource;
    }

    public sealed class SuperPartition
    {
        public string Name = string.Empty;
        public uint Attributes;
        public uint GroupIndex;
        public long TotalSize;
        public List<Extent> Extents = new();
    }

    public sealed class GroupEntry
    {
        public string Name = string.Empty;
        public uint Flags;
        public ulong MaximumSize;
    }

    public sealed class BlockDeviceEntry
    {
        public ulong FirstBlock;
        public uint Alignment;
        public uint AlignmentOffset;
        public ulong Size;
        public uint Flags;
        public string ProductName = string.Empty;
    }

    /// <summary>geometry + header 汇总信息。</summary>
    public sealed class SuperMetadata
    {
        public ushort MajorVersion = 10;
        public ushort MinorVersion;
        public uint HeaderFlags;
        public uint MetadataMaxSize = 65536;
        public uint MetadataSlotCount = 2;
        public uint LogicalBlockSize = 4096;
        public List<SuperPartition> Partitions = new();
        public List<GroupEntry> Groups = new();
        public List<BlockDeviceEntry> BlockDevices = new();
    }

    /// <summary>重建 super 镜像所需的持久化参数（super_params.json）。</summary>
    public sealed class SuperParams
    {
        public ushort MajorVersion { get; set; } = 10;
        public ushort MinorVersion { get; set; }
        public uint HeaderFlags { get; set; }
        public uint MetadataMaxSize { get; set; } = 65536;
        public uint MetadataSlotCount { get; set; } = 2;
        public uint LogicalBlockSize { get; set; } = 4096;
        public List<PartitionParam> Partitions { get; set; } = new();
        public List<GroupParam> Groups { get; set; } = new();
        public List<BlockDeviceParam> BlockDevices { get; set; } = new();

        public sealed class PartitionParam
        {
            public string Name { get; set; } = string.Empty;
            public uint Attributes { get; set; }
            public uint GroupIndex { get; set; }
        }

        public sealed class GroupParam
        {
            public string Name { get; set; } = string.Empty;
            public uint Flags { get; set; }
            public ulong MaximumSize { get; set; }
        }

        public sealed class BlockDeviceParam
        {
            public ulong FirstBlock { get; set; }
            public uint Alignment { get; set; } = (uint)DefaultAlignment;
            public uint AlignmentOffset { get; set; }
            public ulong Size { get; set; }
            public uint Flags { get; set; }
            public string ProductName { get; set; } = "super";
        }

        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        public static SuperParams Load(string partDir)
        {
            string path = Path.Combine(partDir, "super_params.json");
            if (!File.Exists(path)) throw new FileNotFoundException("缺少 super_params.json，无法重建 super 镜像。请重新解包。", path);
            return JsonSerializer.Deserialize<SuperParams>(File.ReadAllText(path), JsonOpts)
                   ?? throw new InvalidDataException("super_params.json 解析失败。");
        }

        public void Save(string partDir) =>
            File.WriteAllText(Path.Combine(partDir, "super_params.json"),
                JsonSerializer.Serialize(this, JsonOpts));

        public static SuperParams FromMetadata(SuperMetadata meta)
        {
            var p = new SuperParams
            {
                MajorVersion = meta.MajorVersion,
                MinorVersion = meta.MinorVersion,
                HeaderFlags = meta.HeaderFlags,
                MetadataMaxSize = meta.MetadataMaxSize,
                MetadataSlotCount = meta.MetadataSlotCount,
                LogicalBlockSize = meta.LogicalBlockSize,
            };
            foreach (var part in meta.Partitions)
                p.Partitions.Add(new PartitionParam { Name = part.Name, Attributes = part.Attributes, GroupIndex = part.GroupIndex });
            foreach (var g in meta.Groups)
                p.Groups.Add(new GroupParam { Name = g.Name, Flags = g.Flags, MaximumSize = g.MaximumSize });
            foreach (var d in meta.BlockDevices)
                p.BlockDevices.Add(new BlockDeviceParam { FirstBlock = d.FirstBlock, Alignment = d.Alignment, AlignmentOffset = d.AlignmentOffset, Size = d.Size, Flags = d.Flags, ProductName = d.ProductName });
            return p;
        }
    }

    /// <summary>重建时的单个子分区。</summary>
    public sealed class SuperPackEntry
    {
        public string Name = string.Empty;
        public uint Attributes;
        public uint GroupIndex;
        /// <summary>子镜像文件路径；null 表示零大小分区。</summary>
        public string? ImagePath;
    }

    // ==================== 解析 ====================

    /// <summary>判断流是否为 super 镜像（不消费数据）。</summary>
    public static bool IsSuperImage(Stream stream)
    {
        long pos = stream.Position;
        try
        {
            if (stream.Length < ReservedBytes + GeometrySlotSize) return false;
            Span<byte> buf = stackalloc byte[4];
            stream.Position = ReservedBytes;                       // primary geometry
            if (stream.Read(buf) == 4 && BinaryPrimitives.ReadUInt32LittleEndian(buf) == GeometryMagic)
                return true;
            stream.Position = ReservedBytes + GeometrySlotSize * 2; // metadata slot 0 primary
            if (stream.Read(buf) == 4 && BinaryPrimitives.ReadUInt32LittleEndian(buf) == HeaderMagic)
                return true;
            return false;
        }
        finally
        {
            stream.Position = pos;
        }
    }

    /// <summary>解析 geometry 与 slot 0 metadata（primary 失败时回退 backup）。</summary>
    public static SuperMetadata Parse(Stream stream)
    {
        long pos = stream.Position;
        try
        {
            // 1. geometry
            (uint maxSize, uint slotCount, uint blockSize) = ReadGeometry(stream, ReservedBytes)
                ?? ReadGeometry(stream, ReservedBytes + GeometrySlotSize)
                ?? throw new InvalidDataException("不是有效的 super 镜像（geometry 魔数不匹配）。");

            // 2. metadata header（slot 0 primary → backup）
            long primaryOffset = ReservedBytes + GeometrySlotSize * 2;
            long backupOffset = primaryOffset + slotCount * maxSize;
            var meta = ReadMetadata(stream, primaryOffset, maxSize)
                       ?? ReadMetadata(stream, backupOffset, maxSize)
                       ?? throw new InvalidDataException("super 镜像的 metadata 解析失败。");

            meta.MetadataMaxSize = maxSize;
            meta.MetadataSlotCount = slotCount;
            meta.LogicalBlockSize = blockSize;
            return meta;
        }
        finally
        {
            stream.Position = pos;
        }
    }

    private static (uint MaxSize, uint SlotCount, uint BlockSize)? ReadGeometry(Stream stream, long offset)
    {
        try
        {
            stream.Position = offset;
            using var reader = new BinaryReader(stream, Encoding.Default, leaveOpen: true);
            if (reader.ReadUInt32LE() != GeometryMagic) return null;
            uint structSize = reader.ReadUInt32LE();
            if (structSize != GeometryStructSize) return null;
            reader.ReadBytes(32); // checksum
            uint maxSize = reader.ReadUInt32LE();
            uint slotCount = reader.ReadUInt32LE();
            uint blockSize = reader.ReadUInt32LE();
            if (maxSize == 0 || slotCount == 0 || blockSize == 0) return null;
            return (maxSize, slotCount, blockSize);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static SuperMetadata? ReadMetadata(Stream stream, long offset, uint maxSize)
    {
        try
        {
            stream.Position = offset;
            using var reader = new BinaryReader(stream, Encoding.Default, leaveOpen: true);
            if (reader.ReadUInt32LE() != HeaderMagic) return null;
            var meta = new SuperMetadata
            {
                MajorVersion = reader.ReadUInt16LE(),
                MinorVersion = reader.ReadUInt16LE(),
            };
            uint headerSize = reader.ReadUInt32LE();
            reader.ReadBytes(32); // header_checksum
            uint tablesSize = reader.ReadUInt32LE();
            reader.ReadBytes(32); // tables_checksum

            if (headerSize < HeaderBaseSize) return null;
            if (tablesSize > maxSize) return null;

            // 4 个表描述符（各 12 字节：offset/num_entries/entry_size）
            var descriptors = new (uint Offset, uint Count, uint EntrySize)[4];
            for (int i = 0; i < 4; i++)
            {
                descriptors[i] = (reader.ReadUInt32LE(), reader.ReadUInt32LE(), reader.ReadUInt32LE());
            }
            if (headerSize >= HeaderBaseSize + 4)
                meta.HeaderFlags = reader.ReadUInt32LE(); // 可选 flags@128

            // 表数据紧随 header 之后
            stream.Position = offset + headerSize;
            byte[] tables = reader.ReadBytes((int)tablesSize);

            // 分区表
            var (partOff, partCount, partEntrySize) = descriptors[0];
            partEntrySize = partEntrySize == 0 ? PartitionEntrySize : partEntrySize;
            for (uint i = 0; i < partCount; i++)
            {
                int base_ = (int)(partOff + i * partEntrySize);
                if (base_ + PartitionEntrySize > tables.Length) break;
                var sp = new SuperPartition
                {
                    Name = ReadFixedString(tables.AsSpan(base_, 36)),
                    Attributes = BinaryPrimitives.ReadUInt32LittleEndian(tables.AsSpan(base_ + 36)),
                    GroupIndex = BinaryPrimitives.ReadUInt32LittleEndian(tables.AsSpan(base_ + 48)),
                };
                uint firstExtent = BinaryPrimitives.ReadUInt32LittleEndian(tables.AsSpan(base_ + 40));
                uint numExtents = BinaryPrimitives.ReadUInt32LittleEndian(tables.AsSpan(base_ + 44));
                sp.Extents.Capacity = (int)Math.Min(numExtents, 1024);
                meta.Partitions.Add(sp);

                // extent 表
                var (extOff, extCount, extEntrySize) = descriptors[1];
                extEntrySize = extEntrySize == 0 ? ExtentEntrySize : extEntrySize;
                for (uint e = 0; e < numExtents; e++)
                {
                    uint idx = firstExtent + e;
                    if (idx >= extCount) break;
                    int eb = (int)(extOff + idx * extEntrySize);
                    if (eb + ExtentEntrySize > tables.Length) break;
                    var ext = new Extent
                    {
                        NumSectors = BinaryPrimitives.ReadInt64LittleEndian(tables.AsSpan(eb)),
                        TargetType = BinaryPrimitives.ReadUInt32LittleEndian(tables.AsSpan(eb + 8)),
                        TargetData = BinaryPrimitives.ReadInt64LittleEndian(tables.AsSpan(eb + 12)),
                        TargetSource = BinaryPrimitives.ReadUInt32LittleEndian(tables.AsSpan(eb + 20)),
                    };
                    sp.Extents.Add(ext);
                    sp.TotalSize += ext.NumSectors * SectorSize;
                }
            }

            // 组表
            var (grpOff, grpCount, grpEntrySize) = descriptors[2];
            grpEntrySize = grpEntrySize == 0 ? GroupEntrySize : grpEntrySize;
            for (uint i = 0; i < grpCount; i++)
            {
                int gb = (int)(grpOff + i * grpEntrySize);
                if (gb + GroupEntrySize > tables.Length) break;
                meta.Groups.Add(new GroupEntry
                {
                    Name = ReadFixedString(tables.AsSpan(gb, 36)),
                    Flags = BinaryPrimitives.ReadUInt32LittleEndian(tables.AsSpan(gb + 36)),
                    MaximumSize = BinaryPrimitives.ReadUInt64LittleEndian(tables.AsSpan(gb + 40)),
                });
            }

            // 块设备表
            var (devOff, devCount, devEntrySize) = descriptors[3];
            devEntrySize = devEntrySize == 0 ? BlockDeviceEntrySize : devEntrySize;
            for (uint i = 0; i < devCount; i++)
            {
                int db = (int)(devOff + i * devEntrySize);
                if (db + BlockDeviceEntrySize > tables.Length) break;
                meta.BlockDevices.Add(new BlockDeviceEntry
                {
                    FirstBlock = BinaryPrimitives.ReadUInt64LittleEndian(tables.AsSpan(db)),
                    Alignment = BinaryPrimitives.ReadUInt32LittleEndian(tables.AsSpan(db + 8)),
                    AlignmentOffset = BinaryPrimitives.ReadUInt32LittleEndian(tables.AsSpan(db + 12)),
                    Size = BinaryPrimitives.ReadUInt64LittleEndian(tables.AsSpan(db + 16)),
                    Flags = BinaryPrimitives.ReadUInt32LittleEndian(tables.AsSpan(db + 24)),
                    ProductName = ReadFixedString(tables.AsSpan(db + 28, 36)),
                });
            }

            return meta;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string ReadFixedString(ReadOnlySpan<byte> bytes)
    {
        int len = bytes.IndexOf((byte)0);
        if (len < 0) len = bytes.Length;
        return Encoding.UTF8.GetString(bytes[..len]);
    }

    /// <summary>将指定逻辑分区的原始数据提取到输出流。</summary>
    public static void ExtractPartition(Stream superStream, SuperPartition partition, Stream output)
    {
        foreach (var ext in partition.Extents)
        {
            long length = ext.NumSectors * SectorSize;
            if (length <= 0) continue;
            if (ext.TargetType == TargetTypeZero)
            {
                output.Write(new byte[length]);
            }
            else if (ext.TargetType == TargetTypeLinear)
            {
                superStream.Position = ext.TargetData * SectorSize;
                superStream.CopyExact(output, length);
            }
            else
            {
                throw new InvalidDataException($"不支持的 extent 类型：{ext.TargetType}");
            }
        }
    }

    // ==================== 重建 ====================

    /// <summary>
    /// 重建 super 镜像：按子分区实际大小重新生成 geometry / metadata（写入全部槽位的 primary+backup），
    /// 数据区按 1MiB 对齐依次写入各子镜像。
    /// </summary>
    public static void Pack(SuperParams prms, IReadOnlyList<SuperPackEntry> entries, Stream output,
        IProgress<RomTaskProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0) throw new InvalidDataException("没有任何子分区，无法重建 super 镜像。");
        if (prms.Groups.Count == 0)
            prms.Groups.Add(new SuperParams.GroupParam { Name = "default" });

        // 1. 规划数据区：跳过全部 metadata 槽位后按 1MiB 对齐
        uint maxSize = Math.Max(prms.MetadataMaxSize, 4096);
        uint slotCount = Math.Max(prms.MetadataSlotCount, 1);
        long dataStart = ReservedBytes + GeometrySlotSize * 2 + (long)maxSize * slotCount * 2;
        dataStart = AlignUp(dataStart, DefaultAlignment);

        long cursor = dataStart;
        var extentList = new List<(SuperPackEntry Entry, long StartSector, long NumSectors)>();
        var tablePartitions = new List<(SuperPackEntry Entry, uint FirstExtent, uint NumExtents)>();

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint numExtents = 0;
            uint firstExtent = (uint)extentList.Count;
            long size = entry.ImagePath is null ? 0 : new FileInfo(entry.ImagePath).Length;
            if (size > 0)
            {
                cursor = AlignUp(cursor, DefaultAlignment);
                long numSectors = (size + SectorSize - 1) / SectorSize;
                extentList.Add((entry, cursor / SectorSize, numSectors));
                numExtents = 1;
                cursor += numSectors * SectorSize;
            }
            tablePartitions.Add((entry, firstExtent, numExtents));
        }

        long imageSize = AlignUp(Math.Max(cursor, dataStart + DefaultAlignment), DefaultAlignment);

        // 2. 构造 metadata（header + tables）
        int partTableSize = tablePartitions.Count * PartitionEntrySize;
        int extTableSize = extentList.Count * ExtentEntrySize;
        int grpTableSize = prms.Groups.Count * GroupEntrySize;
        int devTableSize = Math.Max(1, prms.BlockDevices.Count) * BlockDeviceEntrySize;
        uint tablesSize = (uint)(partTableSize + extTableSize + grpTableSize + devTableSize);
        bool hasFlags = prms.HeaderFlags != 0 || prms.MinorVersion >= 1;
        int headerSize = hasFlags ? HeaderBaseSize + 4 : HeaderBaseSize;

        if ((uint)(headerSize + tablesSize) > maxSize)
            throw new InvalidDataException($"metadata（header+tables，{headerSize + tablesSize} 字节）超过槽位上限（{maxSize} 字节）。");

        int partOff = 0;
        int extOff = partTableSize;
        int grpOff = extOff + extTableSize;
        int devOff = grpOff + grpTableSize;

        var header = new byte[headerSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), HeaderMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), prms.MajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), prms.MinorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)headerSize);
        // header_checksum[32]@12（稍后计算）
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), tablesSize);
        // tables_checksum[32]@48（稍后计算）
        WriteDescriptor(header.AsSpan(80), (uint)partOff, (uint)tablePartitions.Count, PartitionEntrySize);
        WriteDescriptor(header.AsSpan(92), (uint)extOff, (uint)extentList.Count, ExtentEntrySize);
        WriteDescriptor(header.AsSpan(104), (uint)grpOff, (uint)prms.Groups.Count, GroupEntrySize);
        WriteDescriptor(header.AsSpan(116), (uint)devOff, (uint)(prms.BlockDevices.Count == 0 ? 1 : prms.BlockDevices.Count), BlockDeviceEntrySize);
        if (hasFlags)
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(128), prms.HeaderFlags);

        var tables = new byte[tablesSize];
        for (int i = 0; i < tablePartitions.Count; i++)
        {
            var (entry, firstExtent, numExtents) = tablePartitions[i];
            int b = partOff + i * PartitionEntrySize;
            WriteFixedBytes(tables.AsSpan(b, 36), entry.Name);
            uint attributes = entry.Attributes;
            BinaryPrimitives.WriteUInt32LittleEndian(tables.AsSpan(b + 36), attributes);
            BinaryPrimitives.WriteUInt32LittleEndian(tables.AsSpan(b + 40), firstExtent);
            BinaryPrimitives.WriteUInt32LittleEndian(tables.AsSpan(b + 44), numExtents);
            BinaryPrimitives.WriteUInt32LittleEndian(tables.AsSpan(b + 48), entry.GroupIndex);
        }
        for (int i = 0; i < extentList.Count; i++)
        {
            var (entry, startSector, numSectors) = extentList[i];
            int b = extOff + i * ExtentEntrySize;
            BinaryPrimitives.WriteInt64LittleEndian(tables.AsSpan(b), numSectors);
            BinaryPrimitives.WriteUInt32LittleEndian(tables.AsSpan(b + 8), TargetTypeLinear);
            BinaryPrimitives.WriteInt64LittleEndian(tables.AsSpan(b + 12), startSector);
            BinaryPrimitives.WriteUInt32LittleEndian(tables.AsSpan(b + 20), 0); // target_source = 块设备 0
        }
        for (int i = 0; i < prms.Groups.Count; i++)
        {
            var g = prms.Groups[i];
            int b = grpOff + i * GroupEntrySize;
            WriteFixedBytes(tables.AsSpan(b, 36), g.Name);
            BinaryPrimitives.WriteUInt32LittleEndian(tables.AsSpan(b + 36), g.Flags);
            BinaryPrimitives.WriteUInt64LittleEndian(tables.AsSpan(b + 40), g.MaximumSize);
        }
        if (prms.BlockDevices.Count == 0)
        {
            prms.BlockDevices.Add(new SuperParams.BlockDeviceParam());
        }
        for (int i = 0; i < prms.BlockDevices.Count; i++)
        {
            var d = prms.BlockDevices[i];
            int b = devOff + i * BlockDeviceEntrySize;
            BinaryPrimitives.WriteUInt64LittleEndian(tables.AsSpan(b), (ulong)(dataStart / SectorSize));
            BinaryPrimitives.WriteUInt32LittleEndian(tables.AsSpan(b + 8), d.Alignment == 0 ? (uint)DefaultAlignment : d.Alignment);
            BinaryPrimitives.WriteUInt32LittleEndian(tables.AsSpan(b + 12), d.AlignmentOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(tables.AsSpan(b + 16), (ulong)imageSize);
            BinaryPrimitives.WriteUInt32LittleEndian(tables.AsSpan(b + 24), d.Flags);
            WriteFixedBytes(tables.AsSpan(b + 28, 36), string.IsNullOrEmpty(d.ProductName) ? "super" : d.ProductName);
        }

        // 3. 计算校验和（对应字段清零后对整个结构取 SHA-256）
        byte[] hashBuf = (byte[])header.Clone();
        Array.Clear(hashBuf, 12, 32); // header_checksum 清零后计算
        SHA256.HashData(hashBuf).CopyTo(header, 12);
        SHA256.HashData(tables).CopyTo(header, 48);

        // 4. 写 geometry
        byte[] geometry = new byte[GeometryStructSize];
        BinaryPrimitives.WriteUInt32LittleEndian(geometry, GeometryMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(geometry.AsSpan(4), GeometryStructSize);
        // checksum[32]@8 稍后计算
        BinaryPrimitives.WriteUInt32LittleEndian(geometry.AsSpan(40), maxSize);
        BinaryPrimitives.WriteUInt32LittleEndian(geometry.AsSpan(44), slotCount);
        BinaryPrimitives.WriteUInt32LittleEndian(geometry.AsSpan(48), prms.LogicalBlockSize == 0 ? 4096 : prms.LogicalBlockSize);
        byte[] geoHash = SHA256.HashData(geometry); // checksum 字段当前为 0
        geoHash.CopyTo(geometry, 8);

        // 5. 写镜像
        output.SetLength(imageSize);
        output.Position = ReservedBytes;
        output.Write(geometry);
        output.Write(new byte[GeometrySlotSize - GeometryStructSize]); // 填充到 4096
        output.Position = ReservedBytes + GeometrySlotSize;
        output.Write(geometry);
        output.Write(new byte[GeometrySlotSize - GeometryStructSize]);

        // metadata 写入全部槽位的 primary + backup
        byte[] metadataBlob = header.Concat(tables).ToArray();
        for (uint slot = 0; slot < slotCount; slot++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Position = ReservedBytes + GeometrySlotSize * 2 + slot * maxSize;
            output.Write(metadataBlob);
            output.Position = ReservedBytes + GeometrySlotSize * 2 + (slotCount + slot) * maxSize;
            output.Write(metadataBlob);
        }

        // 数据区
        int done = 0;
        foreach (var (entry, startSector, numSectors) in extentList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new RomTaskProgress(50 + done * 40 / Math.Max(1, extentList.Count), "写入子分区", entry.Name));
            output.Position = startSector * SectorSize;
            using var inFs = File.OpenRead(entry.ImagePath!);
            inFs.CopyTo(output);
            // 分区内不足一个扇区的尾部补零
            long written = numSectors * SectorSize;
            long pad = written - inFs.Length;
            if (pad > 0) output.Write(new byte[pad]);
            done++;
        }

        progress?.Report(new RomTaskProgress(95, "super 镜像重建完成"));
    }

    private static void WriteDescriptor(Span<byte> span, uint offset, uint count, uint entrySize)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(span, offset);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], count);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], entrySize);
    }

    private static void WriteFixedBytes(Span<byte> span, string value)
    {
        span.Clear();
        var bytes = Encoding.UTF8.GetBytes(value);
        bytes.AsSpan(0, Math.Min(bytes.Length, span.Length)).CopyTo(span);
    }

    private static long AlignUp(long value, long alignment) => (value + alignment - 1) / alignment * alignment;
}
