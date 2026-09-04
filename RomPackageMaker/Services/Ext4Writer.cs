using System.Buffers.Binary;
using System.Text;

namespace RomPackageMaker.Services;

/// <summary>
/// 从目录树构建 ext4 分区镜像。生成的镜像兼容 ext4（extent 布局），
/// 可被 Android 工具（如 simg2img / make_ext4fs 生成的同类镜像）识别。
/// 适用于 system / vendor / product 等只读分区的重打包。
/// </summary>
internal sealed class Ext4Writer
{
    private const uint BlockSize = 4096;
    private const ushort InodeSize = 256;
    private const uint BlocksPerGroup = BlockSize * 8; // 32768 -> 128MB/group
    private const uint InodesPerGroup = 8192; // inode_ratio=16384
    private const uint InodeBlocksPerGroup = (InodesPerGroup * InodeSize + BlockSize - 1) / BlockSize; // 512 blocks
    private const ushort Ext4Magic = 0xEF53;
    private const uint ExtentMagic = 0xF30A;
    private const uint IncompatFeatures = 0x2 | 0x80; // INCOMPAT_FILETYPE | INCOMPAT_64BIT

    private sealed class InodeEntry
    {
        public uint InodeNo;
        public ushort Mode;
        public ulong Size;
        public List<ulong> DataBlocks = new();
        public Dictionary<string, uint>? DirEntries; // name -> inode
    }

    /// <summary>将目录树构建为 ext4 镜像并写入目标流。</summary>
    public void Build(string sourceDir, Stream output, IProgress<RomTaskProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(sourceDir)) throw new DirectoryNotFoundException(sourceDir);
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 收集所有路径
        var fileList = new List<(string Path, bool IsDir)>();
        CollectPaths(sourceDir, fileList);

        // 分配 inode
        var inodes = new Dictionary<uint, InodeEntry>();
        uint nextInode = 2; // 1 保留，2 = 根目录

        // 根 inode
        var root = new InodeEntry { InodeNo = nextInode++, Mode = 0x41ED, DirEntries = new() };
        inodes[root.InodeNo] = root;

        // 先分配目录 inode，再处理文件（保证目录先存在）
        var dirMap = new Dictionary<string, uint> { [sourceDir] = root.InodeNo };
        foreach (var (path, isDir) in fileList)
        {
            if (!isDir) continue;
            var entry = new InodeEntry { InodeNo = nextInode++, Mode = 0x41ED, DirEntries = new() };
            inodes[entry.InodeNo] = entry;
            dirMap[path] = entry.InodeNo;
        }

        // 分配文件 inode
        var fileMap = new Dictionary<string, uint>();
        foreach (var (path, isDir) in fileList)
        {
            if (isDir) continue;
            var entry = new InodeEntry { InodeNo = nextInode++, Mode = 0x81A4 };
            inodes[entry.InodeNo] = entry;
            fileMap[path] = entry.InodeNo;
        }

        uint totalInodes = nextInode - 1;

        // 建立目录条目映射
        foreach (var (path, isDir) in fileList)
        {
            string parentDir = Directory.GetParent(path)!.FullName;
            uint parentIno = dirMap.TryGetValue(parentDir, out var p) ? p : root.InodeNo;
            var parent = inodes[parentIno];
            string name = Path.GetFileName(path);
            uint childIno = isDir ? dirMap[path] : fileMap[path];
            parent.DirEntries![name] = childIno;
        }

        // 计算每个文件需要的数据块
        ulong totalDataBlocks = 0;
        foreach (var (path, isDir) in fileList)
        {
            if (isDir) continue;
            var ino = inodes[fileMap[path]];
            var fi = new FileInfo(path);
            ino.Size = (ulong)fi.Length;
            // 所有常规文件都使用数据块（不使用 inline data，简化读取逻辑）
            ulong blocks = ((ulong)fi.Length + BlockSize - 1) / BlockSize;
            if (blocks == 0) blocks = 1;
            ino.DataBlocks = new List<ulong>();
            for (ulong i = 0; i < blocks; i++) ino.DataBlocks.Add(0);
            totalDataBlocks += blocks;
        }

        // 目录条目占用的块
        foreach (var ino in inodes.Values)
        {
            if (ino.DirEntries == null) continue;
            // 每个条目大小（4 字节对齐），最后一个条目延伸到块末尾
            int rawSize = 12 + 12; // "." + ".."
            foreach (var name in ino.DirEntries.Keys)
            {
                int nameLen = Encoding.UTF8.GetByteCount(name);
                rawSize += (8 + nameLen + 3) & ~3;
            }
            ulong blocks = ((ulong)rawSize + BlockSize - 1) / BlockSize;
            if (blocks == 0) blocks = 1;
            // 目录 i_size 为实际占用的块大小（最后一个条目填满到块末尾）
            ino.Size = blocks * BlockSize;
            ino.DataBlocks = new List<ulong>();
            for (ulong i = 0; i < blocks; i++) ino.DataBlocks.Add(0);
            totalDataBlocks += blocks;
        }

        // 计算块组数量
        uint groups = (uint)Math.Ceiling((double)totalInodes / InodesPerGroup);
        if (groups == 0) groups = 1;

        // 每个块组的固定开销：block bitmap(1) + inode bitmap(1) + inode table(InodeBlocksPerGroup)
        uint overheadPerGroup = 1 + 1 + InodeBlocksPerGroup;
        ulong totalBlocks = (ulong)groups * overheadPerGroup + totalDataBlocks + 1 /* superblock block */ + (ulong)groups; // GDT 块
        // 向上取整到块组边界
        totalBlocks = (ulong)Math.Ceiling((double)totalBlocks / BlocksPerGroup) * BlocksPerGroup;
        groups = (uint)((totalBlocks + BlocksPerGroup - 1) / BlocksPerGroup);

        // 重新确保块组足够容纳数据
        ulong maxDataBlocks = (ulong)groups * (BlocksPerGroup - overheadPerGroup);
        while (maxDataBlocks < totalDataBlocks)
        {
            groups++;
            totalBlocks = (ulong)groups * BlocksPerGroup;
            maxDataBlocks = (ulong)groups * (BlocksPerGroup - overheadPerGroup);
        }
        totalBlocks = (ulong)groups * BlocksPerGroup;

        // 分配数据块：从每个组的数据区开始分配
        // 组 g 的数据区起始块 = g * BlocksPerGroup + overheadPerGroup (+ 1 for superblock if g==0)
        // 实际上 superblock 在 block 0，GDT 在 block 1
        // 组 0 的布局：block 0 = superblock, block 1 = GDT, block 2 = block bitmap, block 3 = inode bitmap, block 4.. = inode table, then data
        // 对于组 g>0：没有 superblock/GDT 副本（简化），所以 block g*BPG = block bitmap, +1 inode bitmap, +2.. inode table, then data

        ulong dataBlockCursor = 0;
        uint currentGroup = 0;
        ulong groupDataStart = overheadPerGroup + 2; // group 0: superblock(1) + GDT(1) + overhead

        ulong AllocateBlock()
        {
            if (dataBlockCursor == 0) dataBlockCursor = groupDataStart;
            if (dataBlockCursor >= (ulong)(currentGroup + 1) * BlocksPerGroup)
            {
                currentGroup++;
                dataBlockCursor = (ulong)currentGroup * BlocksPerGroup + overheadPerGroup;
            }
            return dataBlockCursor++;
        }

        // 为所有数据块分配物理块号
        foreach (var ino in inodes.Values)
        {
            if (ino.DataBlocks.Count == 0) continue;
            for (int i = 0; i < ino.DataBlocks.Count; i++)
            {
                ino.DataBlocks[i] = AllocateBlock();
            }
        }

        // 现在写入镜像
        long imageSize = (long)totalBlocks * BlockSize;
        output.SetLength(imageSize);
        output.Position = 0;
        // 清零（sparse 流可能不需要，但内存流需要）
        output.Write(new byte[BlockSize]); // block 0 先占位，后面写 superblock

        // 构建 inode 表内容
        var inodeTables = new byte[groups][];
        for (uint g = 0; g < groups; g++)
        {
            inodeTables[g] = new byte[InodeBlocksPerGroup * BlockSize];
        }

        foreach (var ino in inodes.Values)
        {
            uint group = (ino.InodeNo - 1) / InodesPerGroup;
            uint idx = (ino.InodeNo - 1) % InodesPerGroup;
            if (group >= groups) continue;
            int off = (int)(idx * InodeSize);
            var table = inodeTables[group];

            // 写 inode
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(off + 0x00, 2), ino.Mode); // mode
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(off + 0x02, 2), 0); // uid
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(off + 0x04, 4), (uint)(ino.Size & 0xFFFFFFFF)); // size_lo
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(off + 0x08, 4), now); // atime
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(off + 0x0C, 4), now); // ctime
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(off + 0x10, 4), now); // mtime
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(off + 0x14, 4), 0); // dtime
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(off + 0x18, 2), 0); // gid
            ushort links = ino.DirEntries != null ? (ushort)(2 + ino.DirEntries.Count) : (ushort)1;
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(off + 0x1A, 2), links); // links_count
            // i_blocks: 512-byte 扇区数
            ulong blocks512 = (ulong)ino.DataBlocks.Count * (BlockSize / 512);
            if (blocks512 == 0) blocks512 = (ino.Size + 511) / 512;
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(off + 0x1C, 4), (uint)(blocks512 & 0xFFFFFFFF)); // blocks_lo
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(off + 0x20, 4), 0); // flags
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(off + 0x6C, 4), (uint)(ino.Size >> 32)); // size_high
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(off + 0x74, 2), (ushort)(blocks512 >> 32)); // blocks_high

            // i_block 区域：写 extent 树
            if (ino.DataBlocks.Count > 0)
            {
                WriteExtentTree(table, off + 0x28, ino.DataBlocks);
            }
        }

        // 写 inode 表到对应块组
        for (uint g = 0; g < groups; g++)
        {
            ulong inodeTableStart = (ulong)g * BlocksPerGroup + (g == 0 ? 4u : 2u);
            // block bitmap(g*BPG + 2 for g0, g*BPG for g>0), inode bitmap(+1), inode table(+2 for g0, +0 for g>0)
            // group 0: block 0=sb, 1=gdt, 2=block bitmap, 3=inode bitmap, 4..=inode table
            // group g>0: g*BPG+0=block bitmap, +1=inode bitmap, +2..=inode table
            inodeTableStart = g == 0 ? 4u : (ulong)g * BlocksPerGroup + 2u;
            output.Position = (long)inodeTableStart * BlockSize;
            output.Write(inodeTables[g]);
        }

        // 写数据块 —— 先写根目录（根目录不在 fileList 中）
        {
            var dirData = BuildDirData(root, dirMap, sourceDir);
            for (int i = 0; i < root.DataBlocks.Count; i++)
            {
                output.Position = (long)root.DataBlocks[i] * BlockSize;
                int copyLen = (int)Math.Min(BlockSize, dirData.Length - i * (int)BlockSize);
                if (copyLen > 0)
                    output.Write(dirData, i * (int)BlockSize, copyLen);
            }
        }

        long written = 0;
        long totalBytes = fileList.Count > 0 ? fileList.Count : 1;
        foreach (var (path, isDir) in fileList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (isDir)
            {
                var ino = inodes[dirMap[path]];
                var dirData = BuildDirData(ino, dirMap, sourceDir);
                for (int i = 0; i < ino.DataBlocks.Count; i++)
                {
                    output.Position = (long)ino.DataBlocks[i] * BlockSize;
                    int copyLen = (int)Math.Min(BlockSize, dirData.Length - i * BlockSize);
                    if (copyLen > 0)
                        output.Write(dirData, i * (int)BlockSize, copyLen);
                }
            }
            else
            {
                var ino = inodes[fileMap[path]];
                using var fs = File.OpenRead(path);
                for (int i = 0; i < ino.DataBlocks.Count; i++)
                {
                    output.Position = (long)ino.DataBlocks[i] * BlockSize;
                    var buf = new byte[BlockSize];
                    int n = fs.Read(buf, 0, (int)BlockSize);
                    output.Write(buf, 0, (int)BlockSize);
                }
            }
            written++;
            if (progress != null)
            {
                int pct = (int)(written * 100 / totalBytes);
                progress.Report(new RomTaskProgress(Math.Min(90, pct), "构建 ext4 镜像"));
            }
        }

        // 写块位图和 inode 位图
        for (uint g = 0; g < groups; g++)
        {
            ulong blockBmpBlock = g == 0 ? 2u : (ulong)g * BlocksPerGroup;
            ulong inodeBmpBlock = g == 0 ? 3u : (ulong)g * BlocksPerGroup + 1;

            var blockBmp = new byte[BlockSize];
            var inodeBmp = new byte[BlockSize];

            // 标记已用块
            uint groupStartBlock = g * BlocksPerGroup;
            for (uint b = 0; b < BlocksPerGroup; b++)
            {
                ulong abs = groupStartBlock + b;
                // superblock/GDT/bitmap/inode table 都是已用
                bool used;
                if (g == 0)
                    used = abs < 4 + InodeBlocksPerGroup;
                else
                    used = abs < groupStartBlock + 2 + InodeBlocksPerGroup;
                if (used) SetBit(blockBmp, b);
            }

            // 标记数据块
            foreach (var ino in inodes.Values)
            {
                foreach (var db in ino.DataBlocks)
                {
                    if (db >= groupStartBlock && db < (ulong)(g + 1) * BlocksPerGroup)
                    {
                        SetBit(blockBmp, (uint)(db - groupStartBlock));
                    }
                }
            }

            // 标记已用 inode
            uint groupStartInode = g * InodesPerGroup + 1;
            foreach (var ino in inodes.Values)
            {
                if (ino.InodeNo >= groupStartInode && ino.InodeNo < groupStartInode + InodesPerGroup)
                {
                    SetBit(inodeBmp, ino.InodeNo - groupStartInode);
                }
            }

            output.Position = (long)blockBmpBlock * BlockSize;
            output.Write(blockBmp);
            output.Position = (long)inodeBmpBlock * BlockSize;
            output.Write(inodeBmp);
        }

        // 写块组描述符表（GDT）在 block 1
        output.Position = BlockSize; // block 1
        var gdt = new byte[(int)Math.Min(BlockSize, (long)groups * 64)];
        for (uint g = 0; g < groups; g++)
        {
            int off = (int)g * 64;
            ulong blockBmp = g == 0 ? 2u : (ulong)g * BlocksPerGroup;
            ulong inodeBmp = g == 0 ? 3u : (ulong)g * BlocksPerGroup + 1;
            ulong inodeTable = g == 0 ? 4u : (ulong)g * BlocksPerGroup + 2u;

            BinaryPrimitives.WriteUInt32LittleEndian(gdt.AsSpan(off + 0, 4), (uint)(blockBmp & 0xFFFFFFFF));
            BinaryPrimitives.WriteUInt32LittleEndian(gdt.AsSpan(off + 4, 4), (uint)(inodeBmp & 0xFFFFFFFF));
            BinaryPrimitives.WriteUInt32LittleEndian(gdt.AsSpan(off + 8, 4), (uint)(inodeTable & 0xFFFFFFFF));
            // free_blocks_count, free_inodes_count, used_dirs_count 等简化处理
            BinaryPrimitives.WriteUInt16LittleEndian(gdt.AsSpan(off + 12, 2), 0); // free_blocks_lo
            BinaryPrimitives.WriteUInt16LittleEndian(gdt.AsSpan(off + 14, 2), 0); // free_inodes_lo
            BinaryPrimitives.WriteUInt16LittleEndian(gdt.AsSpan(off + 16, 2), CountDirsInGroup(inodes, g)); // used_dirs_lo
            // 高 32 位字段
            BinaryPrimitives.WriteUInt32LittleEndian(gdt.AsSpan(off + 0x20, 4), (uint)(blockBmp >> 32));
            BinaryPrimitives.WriteUInt32LittleEndian(gdt.AsSpan(off + 0x24, 4), (uint)(inodeBmp >> 32));
            BinaryPrimitives.WriteUInt32LittleEndian(gdt.AsSpan(off + 0x28, 4), (uint)(inodeTable >> 32));
        }
        output.Write(gdt);

        // 写 superblock 在 block 0 偏移 1024
        output.Position = 1024;
        var sb = new byte[1024];
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0x00, 4), totalInodes); // inodes_count
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0x04, 4), (uint)(totalBlocks & 0xFFFFFFFF)); // blocks_count_lo
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0x08, 4), 0); // r_blocks
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0x0C, 4), 0); // free_blocks
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0x10, 4), 0); // free_inodes
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0x14, 4), 0); // first_data_block
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0x18, 4), 2); // log_block_size = 2 -> 4096
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0x1C, 4), 2); // log_cluster_size
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0x20, 4), BlocksPerGroup);
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0x24, 4), BlocksPerGroup); // clusters_per_group
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0x28, 4), InodesPerGroup);
        BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(0x38, 2), Ext4Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(0x3A, 2), 1); // state: clean
        BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(0x3C, 2), 0); // errors
        BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(0x58, 2), InodeSize);
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0x5C, 4), 0x24); // compat: HAS_JOURNAL | EXT_ATTR? use 0
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0x60, 4), IncompatFeatures);
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0x64, 4), 0); // ro_compat
        // UUID (16 bytes) - random
        var uuid = Guid.NewGuid().ToByteArray();
        Array.Copy(uuid, 0, sb, 0x68, 16);
        // volume name
        Encoding.UTF8.GetBytes("rommaker").CopyTo(sb.AsSpan(0x78, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0x98, 4), now); // mtime
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0x9C, 4), now); // wtime
        BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(0xFE, 2), 64); // desc_size
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0x150, 4), (uint)(totalBlocks >> 32)); // blocks_count_hi
        output.Write(sb);

        progress?.Report(new RomTaskProgress(100, "ext4 镜像构建完成"));
    }

    private static ushort CountDirsInGroup(Dictionary<uint, InodeEntry> inodes, uint group)
    {
        uint start = group * InodesPerGroup + 1;
        ushort count = 0;
        foreach (var ino in inodes.Values)
        {
            if (ino.InodeNo >= start && ino.InodeNo < start + InodesPerGroup && ino.DirEntries != null)
                count++;
        }
        return count;
    }

    private static void SetBit(byte[] bitmap, uint bit)
    {
        int byteIdx = (int)(bit / 8);
        int bitIdx = (int)(bit % 8);
        if (byteIdx < bitmap.Length)
            bitmap[byteIdx] |= (byte)(1 << bitIdx);
    }

    /// <summary>将 extent 列表写入 i_block 区域（仅支持叶子节点，单 extent 或多个 extent）。</summary>
    private static void WriteExtentTree(byte[] table, int offset, List<ulong> blocks)
    {
        // 按连续块分组
        var extents = new List<(uint startBlock, ushort len, ulong physBlock)>();
        if (blocks.Count > 0)
        {
            uint curLogical = 0;
            ulong curPhys = blocks[0];
            ushort curLen = 1;
            for (int i = 1; i < blocks.Count; i++)
            {
                if (blocks[i] == curPhys + curLen)
                {
                    curLen++;
                }
                else
                {
                    extents.Add((curLogical, curLen, curPhys));
                    curLogical += curLen;
                    curPhys = blocks[i];
                    curLen = 1;
                }
            }
            extents.Add((curLogical, curLen, curPhys));
        }

        // extent header
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(offset + 0, 2), (ushort)ExtentMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(offset + 2, 2), (ushort)extents.Count); // eh_entries
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(offset + 4, 2), (ushort)Math.Max(4, extents.Count)); // eh_max
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(offset + 6, 2), 0); // eh_depth = 0 (leaf)
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(offset + 8, 4), 0); // eh_generation

        int entryOff = offset + 12;
        foreach (var (startBlock, len, physBlock) in extents)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(entryOff + 0, 4), startBlock); // ee_block
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(entryOff + 4, 2), len); // ee_len
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(entryOff + 6, 2), (ushort)(physBlock >> 32)); // ee_start_hi
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(entryOff + 8, 4), (uint)(physBlock & 0xFFFFFFFF)); // ee_start_lo
            entryOff += 12;
        }
    }

    private byte[] BuildDirData(InodeEntry dir, Dictionary<string, uint> dirMap, string rootDir)
    {
        // 收集所有条目
        var entries = new List<(uint Inode, string Name, byte FileType)>();
        entries.Add((dir.InodeNo, ".", 2));

        uint parentIno = dir.InodeNo;
        string? thisPath = dirMap.FirstOrDefault(kv => kv.Value == dir.InodeNo).Key;
        if (thisPath != null && Directory.GetParent(thisPath) is { } parentDir)
        {
            if (dirMap.TryGetValue(parentDir.FullName, out var p))
                parentIno = p;
        }
        entries.Add((parentIno, "..", 2));

        foreach (var (name, childIno) in dir.DirEntries!)
        {
            bool isDir = false;
            foreach (var kv in dirMap)
            {
                if (kv.Value == childIno) { isDir = true; break; }
            }
            entries.Add((childIno, name, (byte)(isDir ? 2 : 1)));
        }

        // 计算每个条目的 rec_len（4 字节对齐），最后一个条目延伸到块末尾
        var recLens = new int[entries.Count];
        int total = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            int nameLen = Encoding.UTF8.GetByteCount(entries[i].Name);
            int raw = 8 + nameLen;
            recLens[i] = (raw + 3) & ~3;
            total += recLens[i];
        }

        // 调整最后一个条目的 rec_len 以填满到块大小
        int blockSize = (int)BlockSize;
        int totalBlocks = (total + blockSize - 1) / blockSize;
        int totalSize = totalBlocks * blockSize;
        if (entries.Count > 0)
        {
            recLens[^1] = totalSize - (total - recLens[^1]);
        }

        // 写入
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        for (int i = 0; i < entries.Count; i++)
        {
            var (ino, name, ftype) = entries[i];
            var nameBytes = Encoding.UTF8.GetBytes(name);
            w.Write((uint)ino);
            w.Write((ushort)recLens[i]);
            w.Write((byte)nameBytes.Length);
            w.Write(ftype);
            w.Write(nameBytes);
            // 填充到 rec_len
            int written = 8 + nameBytes.Length;
            while (written < recLens[i]) { w.Write((byte)0); written++; }
        }

        var data = ms.ToArray();
        // 填充到块大小的整数倍
        int paddedLen = (data.Length + blockSize - 1) & ~(blockSize - 1);
        var result = new byte[paddedLen];
        Array.Copy(data, result, data.Length);
        return result;
    }

    private static void CollectPaths(string root, List<(string Path, bool IsDir)> list)
    {
        // 先目录后文件，便于建立父子关系
        var dirs = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);
        Array.Sort(dirs, StringComparer.Ordinal);
        foreach (var d in dirs) list.Add((d, true));

        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        foreach (var f in files)
        {
            if (f.EndsWith(".symlink", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".romdev", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".rom_extract_errors.log", StringComparison.OrdinalIgnoreCase))
                continue;
            list.Add((f, false));
        }
    }
}
