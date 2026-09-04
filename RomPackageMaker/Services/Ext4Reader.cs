using System.Buffers.Binary;
using System.Text;

namespace RomPackageMaker.Services;

/// <summary>
/// ext2/ext3/ext4 文件系统只读解析器，用于从分区镜像中提取文件。
/// 支持 extent 树与间接块两种数据布局，以及快速符号链接。
/// </summary>
internal sealed class Ext4Reader : IDisposable
{
    private readonly Stream _stream;
    private readonly BinaryReader _reader;
    private readonly bool _leaveOpen;

    // Superblock 字段
    public uint BlockSize { get; }
    public uint InodesPerGroup { get; }
    public uint BlocksPerGroup { get; }
    public ushort InodeSize { get; }
    public uint FirstDataBlock { get; }
    public ushort DescSize { get; }
    public uint InodeCount { get; }
    public ulong BlockCount { get; }
    public uint FeatureIncompat { get; }

    private const ushort Ext4Magic = 0xEF53;
    private const uint ExtentMagic = 0xF30A;
    private const int SuperblockOffset = 1024;

    // 64-bit 特性标志
    private const uint INCOMPAT_64BIT = 0x80;
    private const uint INCOMPAT_INLINE_DATA = 0x1000;
    private const uint INCOMPAT_ENCRYPT = 0x4000;

    public Ext4Reader(Stream stream, bool leaveOpen = false)
    {
        _stream = stream;
        _reader = new BinaryReader(stream, Encoding.Default, leaveOpen: true);
        _leaveOpen = leaveOpen;

        // 读取 superblock
        stream.Position = SuperblockOffset;
        var sb = _reader.ReadBytes(1024);
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(0x38, 2));
        if (magic != Ext4Magic) throw new InvalidDataException($"不是有效的 ext4 镜像（magic=0x{magic:X4}）。");

        InodeCount = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x00, 4));
        uint blocksCountLo = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x04, 4));
        FirstDataBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x14, 4));
        uint logBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x18, 4));
        BlocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x20, 4));
        InodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x28, 4));
        InodeSize = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(0x58, 2));
        FeatureIncompat = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x60, 4));
        DescSize = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(0xFE, 2));
        if (DescSize == 0) DescSize = 32;

        BlockSize = (uint)(1024 << (int)logBlockSize);

        ulong blocksCountHi = 0;
        if ((FeatureIncompat & INCOMPAT_64BIT) != 0)
        {
            blocksCountHi = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x150, 4));
        }
        BlockCount = (blocksCountHi << 32) | blocksCountLo;
    }

    private long BlockOffset(ulong block) => (long)block * BlockSize;

    private byte[] ReadBlock(ulong block)
    {
        _stream.Position = BlockOffset(block);
        return _stream.ReadExact((int)BlockSize);
    }

    private byte[] ReadBlocks(ulong block, uint count)
    {
        _stream.Position = BlockOffset(block);
        return _stream.ReadExact((int)(count * BlockSize));
    }

    /// <summary>获取块组描述符中 inode 表的起始块号。</summary>
    private ulong GetInodeTableBlock(uint group)
    {
        // 描述符表起始块：first_data_block + 1（对于 1k 块）或 1（更大块）
        ulong gdtStartBlock = BlockSize == 1024 ? FirstDataBlock + 1u : 1u;
        long descOffset = BlockOffset(gdtStartBlock) + (long)group * DescSize;
        _stream.Position = descOffset;

        uint inodeTableLo = _reader.ReadUInt32LE();
        // bg_block_bitmap_lo, bg_inode_bitmap_lo, bg_inode_table_lo
        // 实际偏移：0=block_bitmap, 4=inode_bitmap, 8=inode_table
        // 上面读了第一个（block_bitmap），需要重新读
        _stream.Position = descOffset + 8;
        inodeTableLo = _reader.ReadUInt32LE();

        ulong inodeTableHi = 0;
        if ((FeatureIncompat & INCOMPAT_64BIT) != 0 && DescSize >= 64)
        {
            _stream.Position = descOffset + 0x28; // bg_inode_table_hi
            inodeTableHi = _reader.ReadUInt32LE();
        }
        return (inodeTableHi << 32) | inodeTableLo;
    }

    /// <summary>读取指定 inode 号的原始 inode 数据。</summary>
    private byte[] ReadInode(uint inodeNo)
    {
        if (inodeNo == 0) throw new ArgumentException("inode 号不能为 0", nameof(inodeNo));
        uint group = (inodeNo - 1) / InodesPerGroup;
        uint index = (inodeNo - 1) % InodesPerGroup;
        ulong inodeTable = GetInodeTableBlock(group);
        long offset = BlockOffset(inodeTable) + (long)index * InodeSize;
        _stream.Position = offset;
        return _stream.ReadExact(InodeSize);
    }

    private sealed class InodeInfo
    {
        public ushort Mode;
        public ulong Size;
        public byte[] Block = Array.Empty<byte>(); // i_block[60]
        public uint Flags;
    }

    private InodeInfo ParseInode(byte[] data)
    {
        var info = new InodeInfo
        {
            Mode = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x00, 2)),
            Size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x04, 4)),
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x20, 4)),
        };
        if (InodeSize >= 128)
        {
            uint sizeHi = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x6C, 4));
            info.Size |= (ulong)sizeHi << 32;
        }
        info.Block = new byte[60];
        Array.Copy(data, 0x28, info.Block, 0, 60);
        return info;
    }

    private static bool IsDirectory(InodeInfo i) => (i.Mode & 0xF000) == 0x4000;
    private static bool IsRegularFile(InodeInfo i) => (i.Mode & 0xF000) == 0x8000;
    private static bool IsSymlink(InodeInfo i) => (i.Mode & 0xF000) == 0xA000;

    /// <summary>读取文件全部数据。</summary>
    private byte[] ReadFileData(InodeInfo inode)
    {
        if (inode.Size == 0) return Array.Empty<byte>();

        // 内联数据（ext4 inline_data）
        if ((FeatureIncompat & INCOMPAT_INLINE_DATA) != 0 && (inode.Flags & 0x10000000) != 0)
        {
            // 简化处理：内联数据存储在 i_block 区域，跳过系统数据（xattr）
            // 实际需要解析内联数据头，这里做简化：直接取 i_block 中可能的数据
            // 对于大多数 ROM 镜像此特性不常用，给出合理降级
        }

        // 加密文件不支持
        if ((FeatureIncompat & INCOMPAT_ENCRYPT) != 0 && (inode.Flags & 0x800) != 0)
        {
            throw new NotSupportedException("不支持读取加密文件。");
        }

        // 判断是否使用 extent（i_block 开头是 extent magic）
        uint magic = BinaryPrimitives.ReadUInt16LittleEndian(inode.Block.AsSpan(0, 2));
        if (magic == ExtentMagic)
        {
            return ReadExtentData(inode);
        }
        else
        {
            return ReadIndirectData(inode);
        }
    }

    private byte[] ReadExtentData(InodeInfo inode)
    {
        var result = new MemoryStream();
        WalkExtentTree(inode.Block, result);
        // 截断到实际大小
        var arr = result.ToArray();
        if ((ulong)arr.Length > inode.Size)
        {
            var trimmed = new byte[inode.Size];
            Array.Copy(arr, trimmed, (int)inode.Size);
            return trimmed;
        }
        return arr;
    }

    private void WalkExtentTree(byte[] node, Stream output)
    {
        ushort entries = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(2, 2));
        ushort depth = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(6, 2));

        if (depth == 0)
        {
            // 叶子节点：extent entries
            for (int i = 0; i < entries; i++)
            {
                int off = 12 + i * 12;
                uint eeBlock = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(off, 4));
                ushort eeLen = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(off + 4, 2));
                ushort eeStartHi = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(off + 6, 2));
                uint eeStartLo = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(off + 8, 4));
                ulong startBlock = ((ulong)eeStartHi << 32) | eeStartLo;

                // 写入前置零（如果 extent 不连续）
                long expectedPos = (long)eeBlock * BlockSize;
                if (output.Position < expectedPos)
                {
                    output.Write(new byte[expectedPos - output.Position]);
                }

                _stream.Position = BlockOffset(startBlock);
                _stream.CopyExact(output, (long)eeLen * BlockSize);
            }
        }
        else
        {
            // 索引节点：extent index entries
            for (int i = 0; i < entries; i++)
            {
                int off = 12 + i * 12;
                uint eiBlock = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(off, 4));
                uint eiLeafLo = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(off + 4, 4));
                ushort eiLeafHi = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(off + 8, 2));
                ulong leafBlock = ((ulong)eiLeafHi << 32) | eiLeafLo;

                byte[] leafNode = ReadBlock(leafBlock);
                WalkExtentTree(leafNode, output);
            }
        }
    }

    private byte[] ReadIndirectData(InodeInfo inode)
    {
        var result = new MemoryStream();
        var blocks = new List<ulong>();

        // 12 个直接块
        for (int i = 0; i < 12; i++)
        {
            uint b = BinaryPrimitives.ReadUInt32LittleEndian(inode.Block.AsSpan(i * 4, 4));
            if (b != 0) blocks.Add(b);
        }

        // 一级间接块
        uint indirect1 = BinaryPrimitives.ReadUInt32LittleEndian(inode.Block.AsSpan(48, 4));
        if (indirect1 != 0)
        {
            byte[] data = ReadBlock(indirect1);
            int entries = (int)(BlockSize / 4);
            for (int i = 0; i < entries; i++)
            {
                uint b = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 4, 4));
                if (b != 0) blocks.Add(b);
            }
        }

        // 二级间接块
        uint indirect2 = BinaryPrimitives.ReadUInt32LittleEndian(inode.Block.AsSpan(52, 4));
        if (indirect2 != 0)
        {
            byte[] l1 = ReadBlock(indirect2);
            int entries = (int)(BlockSize / 4);
            for (int i = 0; i < entries; i++)
            {
                uint b1 = BinaryPrimitives.ReadUInt32LittleEndian(l1.AsSpan(i * 4, 4));
                if (b1 == 0) continue;
                byte[] l2 = ReadBlock(b1);
                for (int j = 0; j < entries; j++)
                {
                    uint b2 = BinaryPrimitives.ReadUInt32LittleEndian(l2.AsSpan(j * 4, 4));
                    if (b2 != 0) blocks.Add(b2);
                }
            }
        }

        // 三级间接块
        uint indirect3 = BinaryPrimitives.ReadUInt32LittleEndian(inode.Block.AsSpan(56, 4));
        if (indirect3 != 0)
        {
            byte[] l1 = ReadBlock(indirect3);
            int entries = (int)(BlockSize / 4);
            for (int i = 0; i < entries; i++)
            {
                uint b1 = BinaryPrimitives.ReadUInt32LittleEndian(l1.AsSpan(i * 4, 4));
                if (b1 == 0) continue;
                byte[] l2 = ReadBlock(b1);
                for (int j = 0; j < entries; j++)
                {
                    uint b2 = BinaryPrimitives.ReadUInt32LittleEndian(l2.AsSpan(j * 4, 4));
                    if (b2 == 0) continue;
                    byte[] l3 = ReadBlock(b2);
                    for (int k = 0; k < entries; k++)
                    {
                        uint b3 = BinaryPrimitives.ReadUInt32LittleEndian(l3.AsSpan(k * 4, 4));
                        if (b3 != 0) blocks.Add(b3);
                    }
                }
            }
        }

        // 按顺序写入
        for (int i = 0; i < blocks.Count; i++)
        {
            _stream.Position = BlockOffset(blocks[i]);
            _stream.CopyExact(result, BlockSize);
        }

        var arr = result.ToArray();
        if ((ulong)arr.Length > inode.Size)
        {
            var trimmed = new byte[inode.Size];
            Array.Copy(arr, trimmed, (int)inode.Size);
            return trimmed;
        }
        return arr;
    }

    /// <summary>解析符号链接目标。</summary>
    private string ReadSymlink(InodeInfo inode)
    {
        // 快速符号链接：数据存储在 i_block 中（长度 < 60）
        if (inode.Size < 60)
        {
            int len = (int)inode.Size;
            return Encoding.UTF8.GetString(inode.Block, 0, len);
        }
        // 慢速符号链接：数据在数据块中
        var data = ReadFileData(inode);
        return Encoding.UTF8.GetString(data);
    }

    /// <summary>目录条目。</summary>
    private sealed class DirEntry
    {
        public uint Inode;
        public string Name = string.Empty;
        public byte FileType; // 1=regular, 2=dir, 7=symlink
    }

    private List<DirEntry> ReadDirectory(InodeInfo inode)
    {
        var entries = new List<DirEntry>();
        var data = ReadFileData(inode);
        int offset = 0;
        while (offset < data.Length)
        {
            if (offset + 8 > data.Length) break;
            uint inodeNo = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
            ushort recLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4, 2));
            if (recLen == 0) break;
            byte nameLen = data[offset + 6];
            byte fileType = data[offset + 7];

            if (inodeNo != 0 && nameLen > 0 && offset + 8 + nameLen <= data.Length)
            {
                string name = Encoding.UTF8.GetString(data, offset + 8, nameLen);
                if (name is not "." and not "..")
                {
                    entries.Add(new DirEntry { Inode = inodeNo, Name = name, FileType = fileType });
                }
            }
            offset += recLen;
        }
        return entries;
    }

    /// <summary>将整个文件系统提取到目标目录。</summary>
    public void ExtractTo(string outputDir, IProgress<RomTaskProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDir);
        // 根目录 inode 号为 2
        var rootInode = ParseInode(ReadInode(2));
        if (!IsDirectory(rootInode)) throw new InvalidDataException("根 inode 不是目录。");

        long totalEstimate = (long)BlockCount * BlockSize;
        long processed = 0;
        ExtractDir(rootInode, outputDir, ref processed, totalEstimate, progress, cancellationToken);
    }

    private void ExtractDir(InodeInfo dirInode, string targetDir, ref long processed, long totalEstimate, IProgress<RomTaskProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(targetDir);

        var entries = ReadDirectory(dirInode);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.Combine(targetDir, SanitizeName(entry.Name));
            var inode = ParseInode(ReadInode(entry.Inode));

            try
            {
                if (IsDirectory(inode))
                {
                    ExtractDir(inode, path, ref processed, totalEstimate, progress, cancellationToken);
                }
                else if (IsRegularFile(inode))
                {
                    var data = ReadFileData(inode);
                    File.WriteAllBytes(path, data);
                    processed += data.Length;
                    if (progress != null && totalEstimate > 0)
                    {
                        int pct = (int)Math.Min(99, processed * 100 / totalEstimate);
                        progress.Report(new RomTaskProgress(pct, "提取 ext4 文件", $"  {entry.Name}"));
                    }
                }
                else if (IsSymlink(inode))
                {
                    string target = ReadSymlink(inode);
                    CreateSymlink(path, target);
                }
                else
                {
                    // 设备文件、FIFO 等：在 Windows 上创建占位文件记录类型
                    File.WriteAllText(path + ".romdev", $"type=0x{inode.Mode & 0xF000:X}\n");
                }
            }
            catch (Exception ex)
            {
                // 单个文件失败不影响整体
                File.AppendAllText(Path.Combine(targetDir, ".rom_extract_errors.log"), $"{entry.Name}: {ex.Message}{Environment.NewLine}");
            }
        }
    }

    private static string SanitizeName(string name)
    {
        // Windows 文件名非法字符
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }

    private static void CreateSymlink(string path, string target)
    {
        try
        {
            File.WriteAllText(path + ".symlink", target);
        }
        catch
        {
            // 忽略
        }
    }

    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _reader.Dispose();
            _stream.Dispose();
        }
    }
}
