using System.Buffers.Binary;
using System.Text;

namespace RomPackageMaker.Services;

/// <summary>
/// 从目录树构建 ext4 分区镜像。生成的镜像兼容 ext4（extent 布局），
/// 可被 Android 工具（如 simg2img / make_ext4fs 生成的同类镜像）识别。
/// 适用于 system / vendor / product 等只读分区的重打包。
/// 若源目录中存在解包时导出的 .rom_metadata.json，则还原
/// 权限 / 属主 / 时间戳 / 符号链接 / 设备节点 / SELinux xattr。
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

    // i_flags: EXT4_EXTENTS_FL —— extent 树文件必须置位，否则内核按间接块解释 i_block
    private const uint ExtentsFlag = 0x80000;

    // xattr 外部块
    private const uint XattrMagic = 0xEA020000;
    private const int XattrBlockHeaderSize = 32;

    // extent 树容量：根在 i_block 内（60-12）/12=4；普通块节点 (4096-12)/12=340
    private const int RootMaxExtentEntries = 4;
    private const int BlockMaxExtentEntries = (int)((BlockSize - 12) / 12);

    private sealed class InodeEntry
    {
        public uint InodeNo;
        public ushort Mode;
        public uint Uid;
        public uint Gid;
        public uint Atime;
        public uint Ctime;
        public uint Mtime;
        public ulong Size;
        public byte FileType; // 1=regular 2=dir 3=chr 4=blk 5=fifo 6=sock 7=symlink
        public string SymlinkTarget = string.Empty;
        public byte[]? SlowSymlinkData;   // 目标 ≥60 字节的慢速符号链接内容
        public uint Rdev;
        public uint ParentIno;
        public List<ulong> DataBlocks = new();
        public List<ulong> ExtentNodeBlocks = new();   // 深度 extent 树的节点块
        public byte[]? RootExtentNode;                  // i_block 内的根节点（60 字节）
        public ulong? XattrBlock;
        public Dictionary<string, uint>? DirEntries; // name -> inode
    }

    /// <summary>将目录树构建为 ext4 镜像并写入目标流。</summary>
    public void Build(string sourceDir, Stream output, IProgress<RomTaskProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(sourceDir)) throw new DirectoryNotFoundException(sourceDir);
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 0. 加载解包时导出的元数据（无则用默认值）
        var metaByPath = new Dictionary<string, Ext4FileMeta>();
        foreach (var m in Ext4Metadata.Load(sourceDir))
        {
            metaByPath[m.Path] = m; // 同名以后者覆盖（更晚遍历的优先）
        }
        Ext4FileMeta? MetaOf(string relPath) => metaByPath.TryGetValue(relPath, out var m) ? m : null;

        // 收集所有路径
        var fileList = new List<(string Path, bool IsDir)>();
        CollectPaths(sourceDir, fileList);

        // 分配 inode（metaForInode：inode 号 → 元数据，供 xattr 块构建阶段使用）
        var inodes = new Dictionary<uint, InodeEntry>();
        var metaForInode = new Dictionary<uint, Ext4FileMeta>();
        uint nextInode = 2; // 1 保留，2 = 根目录

        var rootMeta = MetaOf(".");
        var root = new InodeEntry
        {
            InodeNo = nextInode++,
            Mode = (ushort)(rootMeta?.Mode ?? 0x41ED),
            Uid = rootMeta?.Uid ?? 0,
            Gid = rootMeta?.Gid ?? 0,
            Atime = rootMeta?.Atime ?? now,
            Ctime = rootMeta?.Ctime ?? now,
            Mtime = rootMeta?.Mtime ?? now,
            FileType = 2,
            DirEntries = new(),
            ParentIno = 2,
        };
        inodes[root.InodeNo] = root;
        if (rootMeta is not null) metaForInode[root.InodeNo] = rootMeta;

        string Rel(string path) => Path.GetRelativePath(sourceDir, path).Replace('\\', '/');

        // 先分配目录 inode，再处理文件（保证目录先存在）
        var dirMap = new Dictionary<string, uint> { [sourceDir] = root.InodeNo };
        foreach (var (path, isDir) in fileList)
        {
            if (!isDir) continue;
            var meta = MetaOf(Rel(path));
            var entry = new InodeEntry
            {
                InodeNo = nextInode++,
                Mode = (ushort)(meta?.Mode ?? 0x41ED),
                Uid = meta?.Uid ?? 0,
                Gid = meta?.Gid ?? 0,
                Atime = meta?.Atime ?? now,
                Ctime = meta?.Ctime ?? now,
                Mtime = meta?.Mtime ?? now,
                FileType = 2,
                DirEntries = new(),
            };
            inodes[entry.InodeNo] = entry;
            dirMap[path] = entry.InodeNo;
            if (meta is not null) metaForInode[entry.InodeNo] = meta;
        }

        // 分配文件 inode
        var fileMap = new Dictionary<string, uint>();
        foreach (var (path, isDir) in fileList)
        {
            if (isDir) continue;
            var meta = MetaOf(Rel(path));
            var entry = new InodeEntry
            {
                InodeNo = nextInode++,
                Mode = (ushort)(meta?.Mode ?? 0x81A4),
                Uid = meta?.Uid ?? 0,
                Gid = meta?.Gid ?? 0,
                Atime = meta?.Atime ?? now,
                Ctime = meta?.Ctime ?? now,
                Mtime = meta?.Mtime ?? now,
                FileType = 1,
            };
            inodes[entry.InodeNo] = entry;
            fileMap[path] = entry.InodeNo;
            if (meta is not null) metaForInode[entry.InodeNo] = meta;
        }

        // 补充元数据中的虚拟条目（符号链接 / 设备节点等在 Windows 上无法落盘的项）
        var virtualInodes = new List<(Ext4FileMeta Meta, InodeEntry Entry)>();
        foreach (var m in metaByPath.Values)
        {
            if (m.Path == ".") continue;
            ushort type = (ushort)(m.Mode & 0xF000);
            if (type is not (0xA000 or 0x2000 or 0x6000 or 0x1000 or 0xC000)) continue; // symlink/dev/fifo/socket
            // 若磁盘上存在同名实体文件则跳过（以磁盘为准）
            if (fileMap.Keys.Any(p => Rel(p) == m.Path)) continue;

            var entry = new InodeEntry
            {
                InodeNo = nextInode++,
                Mode = (ushort)m.Mode,
                Uid = m.Uid,
                Gid = m.Gid,
                Atime = m.Atime,
                Ctime = m.Ctime,
                Mtime = m.Mtime,
                FileType = type switch
                {
                    0xA000 => 7,
                    0x2000 => 3,
                    0x6000 => 4,
                    0x1000 => 5,
                    0xC000 => 6,
                    _ => (byte)1,
                },
                SymlinkTarget = m.SymlinkTarget ?? string.Empty,
                Rdev = m.Rdev,
            };
            if (entry.FileType == 7)
            {
                byte[] target = Encoding.UTF8.GetBytes(entry.SymlinkTarget);
                entry.Size = (ulong)target.Length;
                if (target.Length >= 60) entry.SlowSymlinkData = target; // 慢速符号链接走数据块
            }
            inodes[entry.InodeNo] = entry;
            metaForInode[entry.InodeNo] = m;
            virtualInodes.Add((m, entry));
        }

        uint totalInodes = nextInode - 1;

        // 建立目录条目映射（磁盘文件 + 虚拟条目）
        foreach (var (path, isDir) in fileList)
        {
            string parentDir = Directory.GetParent(path)!.FullName;
            uint parentIno = dirMap.TryGetValue(parentDir, out var p) ? p : root.InodeNo;
            var parent = inodes[parentIno];
            string name = Path.GetFileName(path);
            uint childIno = isDir ? dirMap[path] : fileMap[path];
            parent.DirEntries![name] = childIno;
            inodes[childIno].ParentIno = parentIno;
        }
        foreach (var (m, entry) in virtualInodes)
        {
            string parentRel = m.Path.Contains('/') ? m.Path[..m.Path.LastIndexOf('/')] : string.Empty;
            uint parentIno = root.InodeNo;
            if (parentRel.Length > 0)
            {
                string parentPath = Path.Combine(sourceDir, parentRel.Replace('/', Path.DirectorySeparatorChar));
                if (dirMap.TryGetValue(parentPath, out var p)) parentIno = p;
            }
            inodes[parentIno].DirEntries![Path.GetFileName(m.Path)] = entry.InodeNo;
            entry.ParentIno = parentIno;
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
        // 慢速符号链接的数据块
        foreach (var ino in inodes.Values)
        {
            if (ino.SlowSymlinkData is null) continue;
            ulong blocks = ((ulong)ino.SlowSymlinkData.Length + BlockSize - 1) / BlockSize;
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

        // 构建 extent 深度树（>4 个 extent 时溢出到独立节点块）与 xattr 外部块
        var nodeContents = new Dictionary<ulong, byte[]>();
        var xattrContents = new Dictionary<ulong, byte[]>();
        foreach (var ino in inodes.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ino.DataBlocks.Count > 0)
            {
                var extents = CoalesceExtents(ino.DataBlocks);
                if (extents.Count <= RootMaxExtentEntries)
                {
                    var rootNode = new byte[60];
                    WriteLeafNode(rootNode, extents, RootMaxExtentEntries, childDepth: 0);
                    ino.RootExtentNode = rootNode;
                }
                else
                {
                    BuildDeepExtentTree(ino, extents, AllocateBlock, nodeContents);
                }
            }

            if (metaForInode.TryGetValue(ino.InodeNo, out var meta) && meta.Xattrs.Count > 0)
            {
                byte[]? block = BuildXattrBlock(meta.Xattrs);
                if (block is not null)
                {
                    ulong xb = AllocateBlock();
                    ino.XattrBlock = xb;
                    xattrContents[xb] = block;
                }
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
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(off + 0x02, 2), (ushort)(ino.Uid & 0xFFFF)); // uid_lo
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(off + 0x04, 4), (uint)(ino.Size & 0xFFFFFFFF)); // size_lo
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(off + 0x08, 4), ino.Atime); // atime
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(off + 0x0C, 4), ino.Ctime); // ctime
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(off + 0x10, 4), ino.Mtime); // mtime
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(off + 0x14, 4), 0); // dtime
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(off + 0x18, 2), (ushort)(ino.Gid & 0xFFFF)); // gid_lo
            // links_count：目录 = 2 + 子目录数；其他 = 1
            ushort links = ino.DirEntries != null
                ? (ushort)(2 + ino.DirEntries.Values.Count(child => inodes.TryGetValue(child, out var c) && c.FileType == 2))
                : (ushort)1;
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(off + 0x1A, 2), links); // links_count
            // i_blocks: 512-byte 扇区数（数据块 + extent 树节点块 + xattr 块）
            ulong blocks512 = (ulong)(ino.DataBlocks.Count + ino.ExtentNodeBlocks.Count) * (BlockSize / 512);
            if (ino.XattrBlock is not null) blocks512 += BlockSize / 512;
            if (blocks512 == 0 && ino.SlowSymlinkData is null && ino.FileType is not (3 or 4 or 5 or 6 or 7)) blocks512 = (ino.Size + 511) / 512;
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(off + 0x1C, 4), (uint)(blocks512 & 0xFFFFFFFF)); // blocks_lo
            // flags：extent 树文件必须置 EXT4_EXTENTS_FL
            uint flags = ino.RootExtentNode is null ? 0 : ExtentsFlag;
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(off + 0x20, 4), flags);
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(off + 0x64, 4), 0); // generation
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(off + 0x68, 4), ino.XattrBlock is { } xb ? (uint)xb : 0); // i_file_acl_lo
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(off + 0x6C, 4), (uint)(ino.Size >> 32)); // size_high
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(off + 0x70, 4), 0); // obso_faddr
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(off + 0x74, 2), (ushort)(blocks512 >> 32)); // blocks_high
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(off + 0x76, 2), 0); // file_acl_high
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(off + 0x78, 2), (ushort)(ino.Uid >> 16)); // uid_high
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(off + 0x7A, 2), (ushort)(ino.Gid >> 16)); // gid_high
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(off + 0x80, 4), 32); // i_extra_isize（无内联 xattr）

            // i_block 区域
            Span<byte> iblock = table.AsSpan(off + 0x28, 60);
            if (ino.RootExtentNode is not null)
            {
                ino.RootExtentNode.CopyTo(iblock);
            }
            else if (ino.FileType == 7 && ino.SlowSymlinkData is null)
            {
                // 快速符号链接：目标存于 i_block
                var target = Encoding.UTF8.GetBytes(ino.SymlinkTarget);
                if (target.Length <= 60) target.CopyTo(iblock);
            }
            else if (ino.FileType is 3 or 4)
            {
                // 设备节点：i_block[0] = rdev
                BinaryPrimitives.WriteUInt32LittleEndian(iblock, ino.Rdev);
            }
        }

        // 写 inode 表到对应块组
        for (uint g = 0; g < groups; g++)
        {
            ulong inodeTableStart = g == 0 ? 4u : (ulong)g * BlocksPerGroup + 2u;
            output.Position = (long)inodeTableStart * BlockSize;
            output.Write(inodeTables[g]);
        }

        // 写数据块 —— 先写根目录（根目录不在 fileList 中）
        {
            var dirData = BuildDirData(root, inodes);
            for (int i = 0; i < root.DataBlocks.Count; i++)
            {
                output.Position = (long)root.DataBlocks[i] * BlockSize;
                int copyLen = (int)Math.Min(BlockSize, dirData.Length - i * (int)BlockSize);
                if (copyLen > 0) output.Write(dirData, i * (int)BlockSize, copyLen);
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
                var dirData = BuildDirData(ino, inodes);
                for (int i = 0; i < ino.DataBlocks.Count; i++)
                {
                    output.Position = (long)ino.DataBlocks[i] * BlockSize;
                    int copyLen = (int)Math.Min(BlockSize, dirData.Length - i * BlockSize);
                    if (copyLen > 0) output.Write(dirData, i * (int)BlockSize, copyLen);
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

        // 慢速符号链接数据
        foreach (var ino in inodes.Values)
        {
            if (ino.SlowSymlinkData is null) continue;
            for (int i = 0; i < ino.DataBlocks.Count; i++)
            {
                output.Position = (long)ino.DataBlocks[i] * BlockSize;
                int copyLen = (int)Math.Min(BlockSize, ino.SlowSymlinkData.Length - i * (int)BlockSize);
                if (copyLen > 0) output.Write(ino.SlowSymlinkData, i * (int)BlockSize, copyLen);
            }
        }

        // extent 树节点块与 xattr 外部块
        foreach (var (block, content) in nodeContents.Concat(xattrContents))
        {
            output.Position = (long)block * BlockSize;
            output.Write(content);
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

            // 标记数据块 / extent 树节点块 / xattr 块
            foreach (var ino in inodes.Values)
            {
                foreach (var db in ino.DataBlocks)
                {
                    if (db >= groupStartBlock && db < (ulong)(g + 1) * BlocksPerGroup)
                    {
                        SetBit(blockBmp, (uint)(db - groupStartBlock));
                    }
                }
                foreach (var nb in ino.ExtentNodeBlocks)
                {
                    if (nb >= groupStartBlock && nb < (ulong)(g + 1) * BlocksPerGroup)
                    {
                        SetBit(blockBmp, (uint)(nb - groupStartBlock));
                    }
                }
                if (ino.XattrBlock is { } xb && xb >= groupStartBlock && xb < (ulong)(g + 1) * BlocksPerGroup)
                {
                    SetBit(blockBmp, (uint)(xb - groupStartBlock));
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

    // ==================== extent 树 ====================

    private sealed record ExtentEntry(uint Logical, ushort Len, ulong Phys);

    /// <summary>把物理块序列合并为连续 extent 列表。</summary>
    private static List<ExtentEntry> CoalesceExtents(List<ulong> blocks)
    {
        var extents = new List<ExtentEntry>();
        if (blocks.Count == 0) return extents;
        uint curLogical = 0;
        ulong curPhys = blocks[0];
        uint curLen = 1;
        for (int i = 1; i < blocks.Count; i++)
        {
            if (blocks[i] == curPhys + curLen && curLen < 0x7FFF) // ee_len 15 位
            {
                curLen++;
            }
            else
            {
                extents.Add(new ExtentEntry(curLogical, (ushort)curLen, curPhys));
                curLogical += curLen;
                curPhys = blocks[i];
                curLen = 1;
            }
        }
        extents.Add(new ExtentEntry(curLogical, (ushort)curLen, curPhys));
        return extents;
    }

    /// <summary>
    /// 构建深度 extent 树：叶子/索引节点占独立块（每块 340 项），根索引写入 i_block。
    /// </summary>
    private void BuildDeepExtentTree(InodeEntry ino, List<ExtentEntry> extents, Func<ulong> allocate, Dictionary<ulong, byte[]> nodeContents)
    {
        // 1. 叶子层：每 BlockMaxExtentEntries 个 extent 一个叶子块
        var level = new List<(uint First, byte[] Content)>();
        for (int i = 0; i < extents.Count; i += BlockMaxExtentEntries)
        {
            int cnt = Math.Min(BlockMaxExtentEntries, extents.Count - i);
            var leaf = new byte[BlockSize];
            WriteLeafNode(leaf, extents.GetRange(i, cnt), BlockMaxExtentEntries, childDepth: 0);
            level.Add((extents[i].Logical, leaf));
        }

        // 2. 自底向上合并索引层：除根的直接子节点外全部占块
        ushort levelDepth = 0; // 当前 level 中节点的 eh_depth
        while (level.Count > RootMaxExtentEntries)
        {
            var next = new List<(uint First, byte[] Content)>();
            for (int i = 0; i < level.Count; i += BlockMaxExtentEntries)
            {
                int cnt = Math.Min(BlockMaxExtentEntries, level.Count - i);
                var children = new List<(uint Logical, ulong Block)>();
                for (int j = 0; j < cnt; j++)
                {
                    ulong blk = allocate();
                    nodeContents[blk] = level[i + j].Content;
                    ino.ExtentNodeBlocks.Add(blk);
                    children.Add((level[i + j].First, blk));
                }
                var idx = new byte[BlockSize];
                WriteIndexNode(idx, children, BlockMaxExtentEntries, childDepth: levelDepth);
                next.Add((children[0].Logical, idx));
            }
            levelDepth++;
            level = next;
        }

        // 3. 根的直接子节点占块，根索引写入 i_block
        var rootChildren = new List<(uint Logical, ulong Block)>();
        foreach (var (first, content) in level)
        {
            ulong blk = allocate();
            nodeContents[blk] = content;
            ino.ExtentNodeBlocks.Add(blk);
            rootChildren.Add((first, blk));
        }
        var rootNode = new byte[60];
        WriteIndexNode(rootNode, rootChildren, RootMaxExtentEntries, childDepth: levelDepth);
        ino.RootExtentNode = rootNode;
    }

    /// <summary>写叶子节点（depth = childDepth + 1 的持有者调用时 childDepth 传节点自身深度）。</summary>
    private static void WriteLeafNode(byte[] node, List<ExtentEntry> extents, int maxEntries, int childDepth)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(0, 2), (ushort)ExtentMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(2, 2), (ushort)extents.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(4, 2), (ushort)maxEntries);
        BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(6, 2), (ushort)childDepth);
        BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(8, 4), 0); // generation

        int entryOff = 12;
        foreach (var e in extents)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(entryOff, 4), e.Logical);
            BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(entryOff + 4, 2), e.Len);
            BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(entryOff + 6, 2), (ushort)(e.Phys >> 32));
            BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(entryOff + 8, 4), (uint)(e.Phys & 0xFFFFFFFF));
            entryOff += 12;
        }
    }

    private static void WriteIndexNode(byte[] node, List<(uint Logical, ulong Block)> children, int maxEntries, int childDepth)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(0, 2), (ushort)ExtentMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(2, 2), (ushort)children.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(4, 2), (ushort)maxEntries);
        BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(6, 2), (ushort)(childDepth + 1));
        BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(8, 4), 0); // generation

        int entryOff = 12;
        foreach (var (logical, block) in children)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(entryOff, 4), logical); // ei_block
            BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(entryOff + 4, 4), (uint)(block & 0xFFFFFFFF)); // ei_leaf_lo
            BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(entryOff + 8, 2), (ushort)(block >> 32)); // ei_leaf_hi
            BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(entryOff + 10, 2), 0); // ei_unused
            entryOff += 12;
        }
    }

    // ==================== xattr 外部块 ====================

    /// <summary>构建单个 xattr 外部块。放不下的条目截断（SELinux 标签等常规条目远小于一个块）。</summary>
    private static byte[]? BuildXattrBlock(List<Ext4Xattr> xattrs)
    {
        var items = new List<(byte Index, byte[] Name, byte[] Value)>();
        foreach (var xa in xattrs)
        {
            byte[] value;
            try { value = Convert.FromBase64String(xa.Value); }
            catch (FormatException) { continue; }
            (byte index, string suffix) = XattrNameToIndex(xa.Name);
            if (suffix.Length > 255) continue;
            items.Add((index, Encoding.UTF8.GetBytes(suffix), value));
        }
        if (items.Count == 0) return null;

        // 可容纳的条目数：entries 从 32 向后，values 从块尾向前
        int entriesSize = XattrBlockHeaderSize;
        int valuesSize = 0;
        int fit = 0;
        foreach (var item in items)
        {
            int entrySize = (16 + item.Name.Length + 3) & ~3;
            int valueSize = (item.Value.Length + 3) & ~3;
            if (entriesSize + entrySize + valuesSize + valueSize + 4 > BlockSize) break;
            entriesSize += entrySize;
            valuesSize += valueSize;
            fit++;
        }
        if (fit == 0) return null;

        var block = new byte[BlockSize];
        // 头（32 字节）：magic / refcount=1 / blocks=1 / hash=0 / checksum=0（未启用 metadata_csum）
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0, 4), XattrMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12, 4), 0);

        // values：从块尾向前，4 字节对齐
        int valueCursor = (int)BlockSize;
        var valueOffsets = new int[fit];
        for (int i = 0; i < fit; i++)
        {
            int padded = (items[i].Value.Length + 3) & ~3;
            valueCursor -= padded;
            valueOffsets[i] = valueCursor;
            items[i].Value.CopyTo(block.AsSpan(valueCursor));
        }

        // entries：从头（32）向后
        int entryCursor = XattrBlockHeaderSize;
        for (int i = 0; i < fit; i++)
        {
            var (index, name, value) = items[i];
            block[entryCursor] = (byte)name.Length;
            block[entryCursor + 1] = index;
            BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(entryCursor + 2, 2), (ushort)valueOffsets[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(entryCursor + 4, 4), 0); // e_value_inum
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(entryCursor + 8, 4), (uint)value.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(entryCursor + 12, 4), 0); // e_hash
            name.CopyTo(block.AsSpan(entryCursor + 16));
            entryCursor += (16 + name.Length + 3) & ~3;
        }
        return block;
    }

    /// <summary>带前缀的 xattr 名称 → (命名空间索引, 去前缀名称)。</summary>
    private static (byte Index, string Suffix) XattrNameToIndex(string name)
    {
        if (name == "system.posix_acl_access") return (2, "");
        if (name == "system.posix_acl_default") return (3, "");
        if (name == "system.richacl") return (8, "");
        if (name == "c") return (9, "");
        if (name.StartsWith("c.", StringComparison.Ordinal)) return (9, name[2..]);
        if (name.StartsWith("user.", StringComparison.Ordinal)) return (1, name[5..]);
        if (name.StartsWith("trusted.", StringComparison.Ordinal)) return (4, name[8..]);
        if (name.StartsWith("lustre.", StringComparison.Ordinal)) return (5, name[7..]);
        if (name.StartsWith("security.", StringComparison.Ordinal)) return (6, name[9..]);
        if (name.StartsWith("system.", StringComparison.Ordinal)) return (7, name[7..]);
        if (name.StartsWith("gnu.", StringComparison.Ordinal)) return (10, name[4..]);
        return (0, name); // 未知前缀：原样存储
    }

    // ==================== 目录 / 辅助 ====================

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

    private byte[] BuildDirData(InodeEntry dir, Dictionary<uint, InodeEntry> inodes)
    {
        // 收集所有条目（文件类型取自子 inode）
        var entries = new List<(uint Inode, string Name, byte FileType)>
        {
            (dir.InodeNo, ".", (byte)2),
            (dir.ParentIno == 0 ? dir.InodeNo : dir.ParentIno, "..", (byte)2),
        };

        foreach (var (name, childIno) in dir.DirEntries!)
        {
            byte fileType = inodes.TryGetValue(childIno, out var child) ? child.FileType : (byte)1;
            entries.Add((childIno, name, fileType));
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
            int writtenLen = 8 + nameBytes.Length;
            while (writtenLen < recLens[i]) { w.Write((byte)0); writtenLen++; }
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
            string name = Path.GetFileName(f);
            if (name.EndsWith(".symlink", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".romdev", StringComparison.OrdinalIgnoreCase) ||
                name == ".rom_extract_errors.log" ||
                name == ".rom_metadata.json")
                continue;
            list.Add((f, false));
        }
    }
}
