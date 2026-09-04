using System.IO.Compression;

namespace RomPackageMaker.Services;

/// <summary>
/// ROM 解包/打包核心服务。
/// 支持刷机包 zip 以及 system.img / vendor.img / boot.img / super.img 等分区镜像。
/// </summary>
public sealed class RomPackService : IRomPackService
{
    public Task UnpackAsync(string sourcePath, string workspaceDir, IProgress<RomTaskProgress> progress, CancellationToken cancellationToken)
    {
        return Task.Run(() => Unpack(sourcePath, workspaceDir, progress, cancellationToken), cancellationToken);
    }

    public Task PackAsync(string workspaceDir, string outputPath, IProgress<RomTaskProgress> progress, CancellationToken cancellationToken, bool sign = true)
    {
        return Task.Run(() => Pack(workspaceDir, outputPath, sign, progress, cancellationToken), cancellationToken);
    }

    private void Unpack(string sourcePath, string workspaceDir, IProgress<RomTaskProgress> progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("源文件不存在。", sourcePath);
        Directory.CreateDirectory(workspaceDir);

        var manifest = new RomManifest
        {
            SourceFile = sourcePath,
        };

        string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ext == ".zip")
        {
            manifest.SourceType = "zip";
            UnpackZip(sourcePath, workspaceDir, manifest, progress, cancellationToken);
        }
        else
        {
            manifest.SourceType = "image";
            UnpackSingleImage(sourcePath, workspaceDir, manifest, progress, cancellationToken);
        }

        manifest.Save(workspaceDir);
        progress.Report(new RomTaskProgress(100, "解包完成", $"工作目录：{workspaceDir}"));
    }

    private void UnpackZip(string zipPath, string workspaceDir, RomManifest manifest, IProgress<RomTaskProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new RomTaskProgress(5, "解压刷机包", zipPath));

        string extractDir = Path.Combine(workspaceDir, "_zip");
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
        Directory.CreateDirectory(extractDir);

        // 先解压整个 zip
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            int total = archive.Entries.Count;
            int done = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string dest = Path.Combine(extractDir, entry.FullName);
                string? dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (!entry.FullName.EndsWith('/'))
                {
                    entry.ExtractToFile(dest, overwrite: true);
                }
                done++;
                int pct = 5 + (int)(done * 30.0 / total);
                progress.Report(new RomTaskProgress(pct, "解压刷机包", $"  {entry.FullName}"));
            }
        }

        // 查找并解包其中的 .img 文件
        var imgFiles = Directory.GetFiles(extractDir, "*.img", SearchOption.AllDirectories);
        // 按路径长度排序，优先处理顶层
        Array.Sort(imgFiles, (a, b) => a.Length.CompareTo(b.Length));

        for (int i = 0; i < imgFiles.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string imgPath = imgFiles[i];
            string rel = Path.GetRelativePath(extractDir, imgPath);
            string partName = SanitizePartitionName(Path.GetFileNameWithoutExtension(imgPath));

            progress.Report(new RomTaskProgress(35 + i * 60 / Math.Max(1, imgFiles.Length), $"解包镜像：{partName}", rel));

            string partDir = Path.Combine(workspaceDir, partName);
            Directory.CreateDirectory(partDir);

            try
            {
                var partManifest = UnpackImageFile(imgPath, partDir, partName, progress, cancellationToken);
                partManifest.OriginalImagePath = rel;
                manifest.Partitions.Add(partManifest);
            }
            catch (Exception ex)
            {
                progress.Report(new RomTaskProgress(35 + i * 60 / Math.Max(1, imgFiles.Length), $"跳过 {partName}", $"  {ex.Message}"));
                // 复制原始镜像作为占位
                File.Copy(imgPath, Path.Combine(partDir, Path.GetFileName(imgPath)), true);
                manifest.Partitions.Add(new PartitionManifest
                {
                    Name = partName,
                    ImageType = "raw",
                    OriginalImagePath = rel,
                    ExtractedDir = partName,
                    OriginalSize = new FileInfo(imgPath).Length,
                });
            }
        }
    }

    private void UnpackSingleImage(string imgPath, string workspaceDir, RomManifest manifest, IProgress<RomTaskProgress> progress, CancellationToken cancellationToken)
    {
        string partName = SanitizePartitionName(Path.GetFileNameWithoutExtension(imgPath));
        string partDir = Path.Combine(workspaceDir, partName);
        Directory.CreateDirectory(partDir);

        progress.Report(new RomTaskProgress(10, $"解包镜像：{partName}", imgPath));

        var partManifest = UnpackImageFile(imgPath, partDir, partName, progress, cancellationToken);
        partManifest.OriginalImagePath = Path.GetFileName(imgPath);
        manifest.Partitions.Add(partManifest);
    }

    private PartitionManifest UnpackImageFile(string imgPath, string outputDir, string partName, IProgress<RomTaskProgress> progress, CancellationToken cancellationToken)
    {
        var pm = new PartitionManifest
        {
            Name = partName,
            ExtractedDir = partName,
            OriginalSize = new FileInfo(imgPath).Length,
        };

        using var fs = File.OpenRead(imgPath);

        // 检测镜像类型
        ImageType type = DetectImageType(fs);
        Stream workStream = fs;
        bool ownStream = false;

        // sparse 镜像：先展开，再判断真实类型（可能是 ext4 或 super）
        if (type == ImageType.Sparse)
        {
            progress.Report(new RomTaskProgress(15, "展开 sparse 镜像"));
            var rawMs = new MemoryStream();
            SparseImage.Unsparse(fs, rawMs, progress, cancellationToken);
            rawMs.Position = 0;
            ImageType innerType = DetectImageType(rawMs);
            if (innerType == ImageType.Super)
            {
                type = ImageType.Super;
            }
            else
            {
                type = ImageType.Ext4;
            }
            pm.WasSparse = true;
            workStream = rawMs;
            ownStream = true;
        }

        try
        {
            switch (type)
            {
                case ImageType.Boot:
                    pm.ImageType = "boot";
                    UnpackBootImage(workStream, outputDir, pm, progress, cancellationToken);
                    break;

                case ImageType.Ext4:
                    pm.ImageType = "ext4";
                    UnpackExt4Image(workStream, outputDir, wasSparse: false, progress, cancellationToken);
                    break;

                case ImageType.Super:
                    pm.ImageType = "super";
                    UnpackSuperImage(workStream, outputDir, pm, progress, cancellationToken);
                    break;

                default:
                    pm.ImageType = "raw";
                    File.Copy(imgPath, Path.Combine(outputDir, Path.GetFileName(imgPath)), true);
                    progress.Report(new RomTaskProgress(100, "复制原始镜像"));
                    break;
            }
        }
        finally
        {
            if (ownStream) workStream.Dispose();
        }

        return pm;
    }

    private enum ImageType { Unknown, Sparse, Ext4, Boot, Super }

    private static ImageType DetectImageType(Stream stream)
    {
        long pos = stream.Position;
        try
        {
            Span<byte> head = stackalloc byte[16];
            if (stream.Read(head) < 4) return ImageType.Unknown;

            // Sparse magic
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(head) == SparseImage.SparseMagic)
                return ImageType.Sparse;

            // Boot magic
            if (head.Slice(0, 8).SequenceEqual(System.Text.Encoding.ASCII.GetBytes("ANDROID!")) ||
                head.Slice(0, 8).SequenceEqual(System.Text.Encoding.ASCII.GetBytes("VNDRBOOT")))
                return ImageType.Boot;

            // Super partition magic: 0x56454C41 ("ALEV" LE)
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(head) == 0x56454C41)
                return ImageType.Super;

            // ext4: superblock magic at offset 1024
            stream.Position = 1024 + 0x38;
            Span<byte> sbMagic = stackalloc byte[2];
            if (stream.Read(sbMagic) == 2)
            {
                if (System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(sbMagic) == 0xEF53)
                    return ImageType.Ext4;
            }

            return ImageType.Unknown;
        }
        finally
        {
            stream.Position = pos;
        }
    }

    private void UnpackBootImage(Stream stream, string outputDir, PartitionManifest pm, IProgress<RomTaskProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new RomTaskProgress(20, "解析 boot 镜像头"));
        var info = BootImage.Parse(stream);
        pm.Metadata = new Dictionary<string, string>
        {
            ["page_size"] = info.PageSize.ToString(),
            ["header_version"] = info.HeaderVersion.ToString(),
            ["name"] = info.Name,
            ["cmdline"] = info.Cmdline,
            ["os_version"] = info.OsVersion.ToString("X8"),
        };

        // 保存原始参数文件
        File.WriteAllText(Path.Combine(outputDir, "boot_params.json"),
            System.Text.Json.JsonSerializer.Serialize(info with
            {
                Kernel = Array.Empty<byte>(),
                Ramdisk = Array.Empty<byte>(),
                Second = Array.Empty<byte>(),
                Dt = Array.Empty<byte>(),
                VendorRamdisk = Array.Empty<byte>(),
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        progress.Report(new RomTaskProgress(40, "保存 kernel"));
        File.WriteAllBytes(Path.Combine(outputDir, "kernel"), info.Kernel);

        progress.Report(new RomTaskProgress(60, "解压 ramdisk"));
        byte[] ramdiskRaw = info.Ramdisk;
        File.WriteAllBytes(Path.Combine(outputDir, "ramdisk.cpio.gz"), ramdiskRaw);
        try
        {
            byte[] decompressed = BootImage.DecompressRamdisk(ramdiskRaw);
            File.WriteAllBytes(Path.Combine(outputDir, "ramdisk.cpio"), decompressed);
            // 尝试解压 cpio 到 ramdisk/ 目录
            string ramdiskDir = Path.Combine(outputDir, "ramdisk");
            Directory.CreateDirectory(ramdiskDir);
            ExtractCpio(decompressed, ramdiskDir);
        }
        catch (Exception ex)
        {
            progress.Report(new RomTaskProgress(60, "ramdisk 解压跳过", $"  {ex.Message}"));
        }

        if (info.Second.Length > 0)
            File.WriteAllBytes(Path.Combine(outputDir, "second"), info.Second);
        if (info.Dt.Length > 0)
            File.WriteAllBytes(Path.Combine(outputDir, "dt"), info.Dt);
        if (info.VendorRamdisk.Length > 0)
            File.WriteAllBytes(Path.Combine(outputDir, "vendor_ramdisk"), info.VendorRamdisk);

        progress.Report(new RomTaskProgress(100, "boot 镜像解包完成"));
    }

    private void UnpackExt4Image(Stream stream, string outputDir, bool wasSparse, IProgress<RomTaskProgress> progress, CancellationToken cancellationToken)
    {
        Stream ext4Stream;
        if (wasSparse)
        {
            progress.Report(new RomTaskProgress(15, "展开 sparse 镜像"));
            var rawMs = new MemoryStream();
            SparseImage.Unsparse(stream, rawMs, progress, cancellationToken);
            rawMs.Position = 0;
            ext4Stream = rawMs;
        }
        else
        {
            ext4Stream = stream;
        }

        progress.Report(new RomTaskProgress(30, "解析 ext4 文件系统"));
        using var reader = new Ext4Reader(ext4Stream, leaveOpen: !wasSparse);
        reader.ExtractTo(outputDir, progress, cancellationToken);

        if (wasSparse) ext4Stream.Dispose();
        progress.Report(new RomTaskProgress(100, "ext4 解包完成"));
    }

    private void UnpackSuperImage(Stream stream, string outputDir, PartitionManifest pm, IProgress<RomTaskProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new RomTaskProgress(10, "解析 super 分区表"));
        var partitions = SuperImage.Parse(stream);
        progress.Report(new RomTaskProgress(30, $"发现 {partitions.Count} 个逻辑分区"));

        for (int i = 0; i < partitions.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var p = partitions[i];
            progress.Report(new RomTaskProgress(30 + i * 70 / Math.Max(1, partitions.Count), $"提取 {p.Name}"));

            string subImgPath = Path.Combine(outputDir, p.Name + ".img");
            using (var outFs = File.Create(subImgPath))
            {
                SuperImage.ExtractPartition(stream, p, outFs);
            }

            // 递归解包子镜像
            string subDir = Path.Combine(outputDir, p.Name);
            Directory.CreateDirectory(subDir);
            try
            {
                using var subFs = File.OpenRead(subImgPath);
                var subType = DetectImageType(subFs);
                var subPm = new PartitionManifest { Name = p.Name, ExtractedDir = p.Name, OriginalSize = p.TotalSize };
                if (subType == ImageType.Ext4 || subType == ImageType.Sparse)
                {
                    subPm.ImageType = "ext4";
                    subPm.WasSparse = subType == ImageType.Sparse;
                    UnpackExt4Image(subFs, subDir, subType == ImageType.Sparse, progress, cancellationToken);
                }
                else
                {
                    subPm.ImageType = "raw";
                }
            }
            catch (Exception ex)
            {
                progress.Report(new RomTaskProgress(50, $"{p.Name} 子镜像解包跳过", $"  {ex.Message}"));
            }
        }
        progress.Report(new RomTaskProgress(100, "super 解包完成"));
    }

    // ==================== 打包 ====================

    private void Pack(string workspaceDir, string outputPath, bool sign, IProgress<RomTaskProgress> progress, CancellationToken cancellationToken)
    {
        var manifest = RomManifest.Load(workspaceDir);
        if (manifest.Partitions.Count == 0)
        {
            throw new InvalidOperationException("工作目录中未找到 ROM 清单（.rom_manifest.json），无法打包。请先解包。");
        }

        // 临时打包目录
        string stageDir = Path.Combine(Path.GetTempPath(), "rommaker_pack_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(stageDir);

            // 重建 zip 结构
            string zipExtractDir = Path.Combine(workspaceDir, "_zip");
            if (Directory.Exists(zipExtractDir))
            {
                progress.Report(new RomTaskProgress(5, "复制刷机包结构"));
                CopyDirectory(zipExtractDir, stageDir);
            }

            // 为每个分区重建镜像
            for (int i = 0; i < manifest.Partitions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pm = manifest.Partitions[i];
                progress.Report(new RomTaskProgress(10 + i * 60 / manifest.Partitions.Count, $"打包分区：{pm.Name}"));

                string partDir = Path.Combine(workspaceDir, pm.ExtractedDir);
                if (!Directory.Exists(partDir)) continue;

                string outputImg = Path.Combine(stageDir, pm.OriginalImagePath);
                string? outDir = Path.GetDirectoryName(outputImg);
                if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

                switch (pm.ImageType)
                {
                    case "ext4":
                        PackExt4Image(partDir, outputImg, pm.WasSparse, progress, cancellationToken);
                        break;
                    case "boot":
                        PackBootImage(partDir, outputImg, pm, progress, cancellationToken);
                        break;
                    default:
                        // raw：直接复制目录中的 .img 文件
                        string? imgInDir = Directory.GetFiles(partDir, "*.img").FirstOrDefault() ??
                                           Directory.GetFiles(partDir).FirstOrDefault();
                        if (!string.IsNullOrEmpty(imgInDir))
                            File.Copy(imgInDir, outputImg, true);
                        break;
                }
            }

            // 生成 zip
            progress.Report(new RomTaskProgress(80, "生成刷机包 zip"));
            string unsignedZip = outputPath + ".unsigned";
            if (File.Exists(unsignedZip)) File.Delete(unsignedZip);
            ZipFile.CreateFromDirectory(stageDir, unsignedZip, CompressionLevel.Optimal, includeBaseDirectory: false);

            // 签名
            if (sign)
            {
                progress.Report(new RomTaskProgress(90, "签名刷机包"));
                try
                {
                    ZipSigner.SignZip(unsignedZip, outputPath);
                    File.Delete(unsignedZip);
                }
                catch (Exception ex)
                {
                    progress.Report(new RomTaskProgress(95, "签名失败，使用未签名包", $"  {ex.Message}"));
                    if (File.Exists(unsignedZip))
                    {
                        if (File.Exists(outputPath)) File.Delete(outputPath);
                        File.Move(unsignedZip, outputPath);
                    }
                }
            }
            else
            {
                progress.Report(new RomTaskProgress(90, "跳过签名"));
                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Move(unsignedZip, outputPath);
            }

            progress.Report(new RomTaskProgress(100, "打包完成", outputPath));
        }
        finally
        {
            if (Directory.Exists(stageDir))
            {
                try { Directory.Delete(stageDir, true); } catch { }
            }
        }
    }

    private void PackExt4Image(string partDir, string outputImg, bool toSparse, IProgress<RomTaskProgress> progress, CancellationToken cancellationToken)
    {
        // 先构建原始 ext4 镜像
        string rawImg = outputImg + ".raw";
        using (var fs = File.Create(rawImg))
        {
            var writer = new Ext4Writer();
            writer.Build(partDir, fs, progress, cancellationToken);
        }

        if (toSparse)
        {
            using var inFs = File.OpenRead(rawImg);
            using var outFs = File.Create(outputImg);
            SparseImage.Sparseify(inFs, outFs, 4096, progress, cancellationToken);
            File.Delete(rawImg);
        }
        else
        {
            if (File.Exists(outputImg)) File.Delete(outputImg);
            File.Move(rawImg, outputImg);
        }
    }

    private void PackBootImage(string partDir, string outputImg, PartitionManifest pm, IProgress<RomTaskProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new RomTaskProgress(40, "读取 boot 参数"));
        string paramsPath = Path.Combine(partDir, "boot_params.json");
        var info = new BootImage.BootInfo();
        if (File.Exists(paramsPath))
        {
            var json = File.ReadAllText(paramsPath);
            info = System.Text.Json.JsonSerializer.Deserialize<BootImage.BootInfo>(json) ?? info;
        }

        // 读取 kernel
        string kernelPath = Path.Combine(partDir, "kernel");
        if (File.Exists(kernelPath)) info.Kernel = File.ReadAllBytes(kernelPath);

        // 读取 ramdisk：优先 ramdisk/ 目录重新打包为 cpio.gz，否则用 ramdisk.cpio.gz
        string ramdiskDir = Path.Combine(partDir, "ramdisk");
        string ramdiskCpioGz = Path.Combine(partDir, "ramdisk.cpio.gz");
        if (Directory.Exists(ramdiskDir))
        {
            progress.Report(new RomTaskProgress(60, "重新打包 ramdisk"));
            byte[] cpio = CreateCpio(ramdiskDir);
            info.Ramdisk = BootImage.CompressRamdiskGzip(cpio);
            info.RamdiskSize = (uint)info.Ramdisk.Length;
        }
        else if (File.Exists(ramdiskCpioGz))
        {
            info.Ramdisk = File.ReadAllBytes(ramdiskCpioGz);
            info.RamdiskSize = (uint)info.Ramdisk.Length;
        }

        string secondPath = Path.Combine(partDir, "second");
        if (File.Exists(secondPath))
        {
            info.Second = File.ReadAllBytes(secondPath);
            info.SecondSize = (uint)info.Second.Length;
        }
        string dtPath = Path.Combine(partDir, "dt");
        if (File.Exists(dtPath))
        {
            info.Dt = File.ReadAllBytes(dtPath);
            info.DtSize = (uint)info.Dt.Length;
        }

        using var outFs = File.Create(outputImg);
        BootImage.Write(info, outFs);
        progress.Report(new RomTaskProgress(100, "boot 镜像重打包完成"));
    }

    // ==================== 辅助方法 ====================

    private static string SanitizePartitionName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
        {
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), true);
        }
        foreach (var dir in Directory.GetDirectories(src))
        {
            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
        }
    }

    /// <summary>解压 cpio(newc) 归档到目录。</summary>
    private static void ExtractCpio(byte[] data, string outputDir)
    {
        int offset = 0;
        while (offset + 110 <= data.Length)
        {
            // newc header: 110 bytes, all fields are 8-char hex ASCII
            string header = System.Text.Encoding.ASCII.GetString(data, offset, 110);
            string magic = header.Substring(0, 6);
            if (magic != "070701") break; // newc magic

            uint filesize = Convert.ToUInt32(header.Substring(54, 8), 16);
            uint namesize = Convert.ToUInt32(header.Substring(94, 8), 16);
            uint mode = Convert.ToUInt32(header.Substring(14, 8), 16);

            int nameStart = offset + 110;
            int nameLen = (int)namesize - 1; // 去掉 null 终止符
            if (nameStart + nameLen > data.Length) break;
            string name = System.Text.Encoding.ASCII.GetString(data, nameStart, nameLen);

            if (name == "TRAILER!!!") break;

            // 数据起始：header(110) + namesize，按 4 字节对齐
            int dataStart = Align4(nameStart + (int)namesize);
            int dataEnd = Align4(dataStart + (int)filesize);

            if (name != "." && name != "..")
            {
                string outPath = Path.Combine(outputDir, name);
                if ((mode & 0xF000) == 0x4000) // 目录
                {
                    Directory.CreateDirectory(outPath);
                }
                else
                {
                    string? dir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    if (dataEnd <= data.Length)
                    {
                        var fileData = new byte[filesize];
                        Array.Copy(data, dataStart, fileData, 0, filesize);
                        File.WriteAllBytes(outPath, fileData);
                    }
                }
            }

            offset = dataEnd;
        }
    }

    /// <summary>将目录打包为 cpio(newc) 归档。</summary>
    private static byte[] CreateCpio(string rootDir)
    {
        using var ms = new MemoryStream();
        var files = new List<(string RelPath, string FullPath, bool IsDir)>();

        foreach (var path in Directory.GetFileSystemEntries(rootDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(rootDir, path).Replace('\\', '/');
            files.Add((rel, path, Directory.Exists(path)));
        }
        files.Insert(0, (".", rootDir, true));

        uint inode = 1;
        foreach (var (rel, full, isDir) in files)
        {
            byte[] fileData = Array.Empty<byte>();
            uint mode = isDir ? 0x41EDu : 0x81A4u;
            uint fileSize = 0;
            if (!isDir)
            {
                fileData = File.ReadAllBytes(full);
                fileSize = (uint)fileData.Length;
            }
            uint nameSize = (uint)rel.Length + 1;

            // 构造 header（newc 格式，110 字节）
            string header = string.Format(
                "070701" +
                "{0:X8}{1:X8}{2:X8}{3:X8}{4:X8}{5:X8}{6:X8}" +
                "{7:X8}{8:X8}{9:X8}{10:X8}{11:X8}{12:X8}{13:X8}",
                inode++, mode, 0u, 0u, 0u, 0u,
                fileSize, 0u, 0u, 0u, nameSize, 0u, 0u, 0u);
            // 注意：newc 实际字段顺序为：
            // c_magic[6] c_ino[8] c_mode[8] c_uid[8] c_gid[8] c_nlink[8]
            // c_mtime[8] c_filesize[8] c_devmajor[8] c_devminor[8] c_rdevmajor[8] c_rdevminor[8]
            // c_namesize[8] c_check[8]
            var sb = new System.Text.StringBuilder(110);
            sb.Append("070701");
            sb.AppendFormat("{0:X8}", inode - 1);
            sb.AppendFormat("{0:X8}", mode);
            sb.Append("00000000"); // uid
            sb.Append("00000000"); // gid
            sb.Append("00000001"); // nlink
            sb.Append("00000000"); // mtime
            sb.AppendFormat("{0:X8}", fileSize);
            sb.Append("00000000"); // devmajor
            sb.Append("00000000"); // devminor
            sb.Append("00000000"); // rdevmajor
            sb.Append("00000000"); // rdevminor
            sb.AppendFormat("{0:X8}", nameSize);
            sb.Append("00000000"); // check

            var headerBytes = System.Text.Encoding.ASCII.GetBytes(sb.ToString());
            ms.Write(headerBytes, 0, headerBytes.Length);
            var nameBytes = System.Text.Encoding.ASCII.GetBytes(rel + "\0");
            ms.Write(nameBytes, 0, nameBytes.Length);
            PadTo(ms, 4);
            if (fileData.Length > 0)
            {
                ms.Write(fileData, 0, fileData.Length);
                PadTo(ms, 4);
            }
        }

        // TRAILER
        var trailer = System.Text.Encoding.ASCII.GetBytes(
            "070701" +
            "00000000" + // ino
            "00000000" + // mode
            "00000000" + // uid
            "00000000" + // gid
            "00000000" + // nlink
            "00000000" + // mtime
            "00000000" + // filesize
            "00000000" + // devmajor
            "00000000" + // devminor
            "00000000" + // rdevmajor
            "00000000" + // rdevminor
            "0000000B" + // namesize = 11
            "00000000");  // check
        ms.Write(trailer, 0, trailer.Length);
        ms.Write(System.Text.Encoding.ASCII.GetBytes("TRAILER!!!\0"), 0, 11);
        PadTo(ms, 4);
        // 末尾补齐到 512 字节倍数
        while (ms.Length % 512 != 0) ms.WriteByte(0);

        return ms.ToArray();
    }

    private static int Align4(int v) => (v + 3) & ~3;
    private static void PadTo(Stream s, int alignment)
    {
        while (s.Position % alignment != 0) s.WriteByte(0);
    }
}
