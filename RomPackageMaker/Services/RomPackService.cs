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

        // A/B OTA 刷机包：payload.bin 中的分区镜像需要专用流程提取
        if (File.Exists(Path.Combine(extractDir, "payload.bin")))
        {
            progress.Report(new RomTaskProgress(33, "检测到 payload.bin",
                "  A/B OTA 有效载荷——请使用「Payload / OTA」页提取分区镜像后再解包。"));
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

        Stream workStream = File.OpenRead(imgPath);
        ImageType type = DetectImageType(workStream);
        string? tempRaw = null;

        try
        {
            // sparse 镜像：展开到临时文件再判断真实类型（可能是 ext4 或 super），避免大镜像全量驻留内存
            if (type == ImageType.Sparse)
            {
                progress.Report(new RomTaskProgress(15, "展开 sparse 镜像"));
                tempRaw = Path.Combine(Path.GetTempPath(), "rommaker_unsparse_" + Guid.NewGuid().ToString("N")[..8] + ".img");
                using (var outFs = File.Create(tempRaw))
                {
                    SparseImage.Unsparse(workStream, outFs, progress, cancellationToken);
                }
                workStream.Dispose();
                workStream = File.OpenRead(tempRaw);
                pm.WasSparse = true;

                ImageType innerType = DetectImageType(workStream);
                type = innerType == ImageType.Super ? ImageType.Super : ImageType.Ext4;
            }

            switch (type)
            {
                case ImageType.Boot:
                    pm.ImageType = "boot";
                    UnpackBootImage(workStream, outputDir, pm, progress, cancellationToken);
                    break;

                case ImageType.Ext4:
                    pm.ImageType = "ext4";
                    UnpackExt4Image(workStream, outputDir, progress, cancellationToken);
                    break;

                case ImageType.Super:
                    pm.ImageType = "super";
                    UnpackSuperImage(workStream, outputDir, pm, progress, cancellationToken);
                    break;

                default:
                    pm.ImageType = "raw";
                    workStream.Position = 0;
                    using (var outFs = File.Create(Path.Combine(outputDir, Path.GetFileName(imgPath))))
                    {
                        workStream.CopyTo(outFs);
                    }
                    progress.Report(new RomTaskProgress(100, "复制原始镜像"));
                    break;
            }
        }
        finally
        {
            workStream.Dispose();
            if (tempRaw is not null)
            {
                try { File.Delete(tempRaw); } catch { }
            }
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

            // Super：geometry 魔数位于 0x1000，metadata 头魔数位于 0x3000
            if (SuperImage.IsSuperImage(stream))
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

        // 保存原始参数文件（BootInfo 为 public 字段的 record，必须 IncludeFields 才能序列化）
        File.WriteAllText(Path.Combine(outputDir, "boot_params.json"),
            System.Text.Json.JsonSerializer.Serialize(info with
            {
                Kernel = Array.Empty<byte>(),
                Ramdisk = Array.Empty<byte>(),
                Second = Array.Empty<byte>(),
                Dt = Array.Empty<byte>(),
                VendorRamdisk = Array.Empty<byte>(),
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));

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

    private void UnpackExt4Image(Stream ext4Stream, string outputDir, IProgress<RomTaskProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new RomTaskProgress(30, "解析 ext4 文件系统"));
        using var reader = new Ext4Reader(ext4Stream, leaveOpen: true);
        reader.ExtractTo(outputDir, progress, cancellationToken);
        progress.Report(new RomTaskProgress(100, "ext4 解包完成"));
    }

    private void UnpackSuperImage(Stream stream, string outputDir, PartitionManifest pm, IProgress<RomTaskProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new RomTaskProgress(10, "解析 super 分区表"));
        var meta = SuperImage.Parse(stream);
        progress.Report(new RomTaskProgress(20, $"发现 {meta.Partitions.Count} 个逻辑分区"));

        // 保存 super 重建参数（geometry / 头版本 / 属性 / 组 / 块设备）
        SuperImage.SuperParams.FromMetadata(meta).Save(outputDir);
        pm.SubPartitions = new List<SubPartitionManifest>();

        for (int i = 0; i < meta.Partitions.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var p = meta.Partitions[i];
            progress.Report(new RomTaskProgress(20 + i * 70 / Math.Max(1, meta.Partitions.Count), $"提取 {p.Name}"));

            string subImgPath = Path.Combine(outputDir, p.Name + ".img");
            using (var outFs = File.Create(subImgPath))
            {
                SuperImage.ExtractPartition(stream, p, outFs);
            }

            // 递归解包子镜像（子分区在 super 内为原始镜像，不会是 sparse）
            var subPm = new SubPartitionManifest
            {
                Name = p.Name,
                ExtractedDir = Path.Combine(pm.ExtractedDir, p.Name).Replace('\\', '/'),
                OriginalSize = p.TotalSize,
                ImageType = "raw",
            };
            string subDir = Path.Combine(outputDir, p.Name);
            try
            {
                using var subFs = File.OpenRead(subImgPath);
                var subType = DetectImageType(subFs);
                if (subType == ImageType.Ext4)
                {
                    subPm.ImageType = "ext4";
                    Directory.CreateDirectory(subDir);
                    UnpackExt4Image(subFs, subDir, progress, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                progress.Report(new RomTaskProgress(50, $"{p.Name} 子镜像解包跳过", $"  {ex.Message}"));
                subPm.ImageType = "raw";
                if (Directory.Exists(subDir))
                {
                    try { Directory.Delete(subDir, true); } catch { }
                }
            }
            pm.SubPartitions.Add(subPm);
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
                        PackExt4Image(partDir, outputImg, pm.WasSparse, pm.OriginalSize, progress, cancellationToken);
                        break;
                    case "boot":
                        PackBootImage(partDir, outputImg, pm, progress, cancellationToken);
                        break;
                    case "super":
                        PackSuperImage(workspaceDir, partDir, outputImg, pm, progress, cancellationToken);
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

            progress.Report(new RomTaskProgress(92, "打包完成", outputPath));

            // 打包自检：回读产物，验证镜像头可解析、ext4 条目数与工作区一致、super 子分区齐全
            progress.Report(new RomTaskProgress(93, "打包自检"));
            try
            {
                var checks = PackVerifyService.Verify(workspaceDir, outputPath, progress);
                foreach (var c in checks)
                {
                    progress.Report(new RomTaskProgress(98, c.Ok ? "自检通过" : "自检未通过", $"  {c.Name}：{c.Detail}"));
                }
                int failed = checks.Count(c => !c.Ok);
                progress.Report(new RomTaskProgress(100,
                    failed == 0 ? $"打包自检通过（{checks.Count} 项）" : $"打包自检发现 {failed} 项异常",
                    failed == 0 ? null : "  请核对上方异常项后再刷机"));
            }
            catch (Exception ex)
            {
                // 自检失败不影响产物，仅提示
                progress.Report(new RomTaskProgress(100, "打包自检异常", $"  {ex.Message}"));
            }
        }
        finally
        {
            if (Directory.Exists(stageDir))
            {
                try { Directory.Delete(stageDir, true); } catch { }
            }
        }
    }

    private void PackExt4Image(string partDir, string outputImg, bool toSparse,
        long minSize, IProgress<RomTaskProgress> progress, CancellationToken cancellationToken)
    {
        // 先构建原始 ext4 镜像
        string rawImg = outputImg + ".raw";
        using (var fs = File.Create(rawImg))
        {
            var writer = new Ext4Writer { MinSize = minSize };
            writer.Build(partDir, fs, progress, cancellationToken);
        }

        long builtSize = new FileInfo(rawImg).Length;
        if (minSize > 0 && builtSize > minSize * 1.05)
        {
            // 超过原大小 5%：明确提示，可能装不进原 super 槽位
            progress.Report(new RomTaskProgress(0,
                "容量告警：分区内容已超出原镜像大小",
                $"  {builtSize / 1024.0 / 1024.0:F0} MiB > {minSize / 1024.0 / 1024.0:F0} MiB（可能需要扩容 super）"));
        }

        if (toSparse)
        {
            using var inFs = File.OpenRead(rawImg);
            using var outFs = File.Create(outputImg);
            SparseImage.Sparseify(inFs, outFs, AppSettings.Current.SparseBlockSize, progress, cancellationToken);
            File.Delete(rawImg);
        }
        else
        {
            if (File.Exists(outputImg)) File.Delete(outputImg);
            File.Move(rawImg, outputImg);
        }
    }

    private void PackSuperImage(string workspaceDir, string partDir, string outputImg, PartitionManifest pm, IProgress<RomTaskProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new RomTaskProgress(30, "读取 super 重建参数"));
        var prms = SuperImage.SuperParams.Load(partDir);
        var attrByName = prms.Partitions.ToDictionary(p => p.Name, p => (p.Attributes, p.GroupIndex));

        // 子分区清单：优先使用 manifest，旧工作区回退到目录下的 .img 文件
        var subs = pm.SubPartitions ?? new List<SubPartitionManifest>();
        if (subs.Count == 0)
        {
            foreach (string img in Directory.GetFiles(partDir, "*.img"))
            {
                subs.Add(new SubPartitionManifest { Name = Path.GetFileNameWithoutExtension(img), ImageType = "raw" });
            }
        }

        // 容量预估：ext4 子分区按目录字节和 + 12% 元数据开销估算，raw 子分区按现有 .img 大小
        // 全部子分区按 group_index 汇总，与该组的 MaximumSize 比较；任一分组超容即告警
        try
        {
            ulong totalMetadata = (ulong)prms.MetadataMaxSize * Math.Max(1, prms.MetadataSlotCount);
            var byGroup = prms.Partitions.GroupBy(p => p.GroupIndex).ToDictionary(g => g.Key, g => g.Select(p => p.Name).ToList());
            for (int gIdx = 0; gIdx < prms.Groups.Count; gIdx++)
            {
                if (!byGroup.TryGetValue((uint)gIdx, out var names)) continue;
                var grp = prms.Groups[gIdx];
                long used = (long)totalMetadata;
                foreach (string name in names)
                {
                    var sub = subs.FirstOrDefault(s => s.Name == name);
                    if (sub is null) continue;
                    string subDir = Path.Combine(workspaceDir, sub.ExtractedDir);
                    used += EstimateSubSize(subDir, sub.ImageType, partDir, name);
                }
                if (used > (long)grp.MaximumSize)
                {
                    progress.Report(new RomTaskProgress(0,
                        "容量告警：super 分组容量不足",
                        $"  分组 {grp.Name} 预估 {used / 1024.0 / 1024.0:F0} MiB > 槽位 {grp.MaximumSize / 1024.0 / 1024.0:F0} MiB"));
                }
            }
        }
        catch { /* 估算失败不阻塞打包 */ }

        var entries = new List<SuperImage.SuperPackEntry>();
        var tempFiles = new List<string>();
        try
        {
            foreach (var sub in subs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = new SuperImage.SuperPackEntry { Name = sub.Name };
                if (attrByName.TryGetValue(sub.Name, out var attr))
                {
                    entry.Attributes = attr.Attributes;
                    entry.GroupIndex = attr.GroupIndex;
                }

                if (sub.ImageType == "ext4")
                {
                    string subDir = Path.Combine(workspaceDir, sub.ExtractedDir);
                    if (Directory.Exists(subDir) && Directory.EnumerateFileSystemEntries(subDir).Any())
                    {
                        progress.Report(new RomTaskProgress(35, "重建子分区", sub.Name));
                        string tempImg = Path.Combine(Path.GetTempPath(), "rommaker_sub_" + Guid.NewGuid().ToString("N")[..8] + ".img");
                        using (var fs = File.Create(tempImg))
                        {
                            var writer = new Ext4Writer();
                            writer.Build(subDir, fs, progress, cancellationToken);
                        }
                        tempFiles.Add(tempImg);
                        entry.ImagePath = tempImg;
                    }
                }

                entry.ImagePath ??= FindSubImage(partDir, sub.Name);
                if (entry.ImagePath is null)
                {
                    progress.Report(new RomTaskProgress(35, "子分区镜像缺失，按零大小处理", sub.Name));
                }
                entries.Add(entry);
            }

            // 重建 super（数据区 + 全槽位 metadata）
            string rawSuper = outputImg + ".raw";
            progress.Report(new RomTaskProgress(50, "重建 super 镜像"));
            using (var outFs = File.Create(rawSuper))
            {
                SuperImage.Pack(prms, entries, outFs, progress, cancellationToken);
            }

            if (pm.WasSparse)
            {
                using var inFs = File.OpenRead(rawSuper);
                using var outFs = File.Create(outputImg);
                SparseImage.Sparseify(inFs, outFs, AppSettings.Current.SparseBlockSize, progress, cancellationToken);
                File.Delete(rawSuper);
            }
            else
            {
                if (File.Exists(outputImg)) File.Delete(outputImg);
                File.Move(rawSuper, outputImg);
            }
        }
        finally
        {
            foreach (string temp in tempFiles)
            {
                try { File.Delete(temp); } catch { }
            }
        }
        progress.Report(new RomTaskProgress(100, "super 镜像重打包完成"));
    }

    /// <summary>在 super 分区目录中查找子镜像文件（system_a.img 等）。</summary>
    private static string? FindSubImage(string partDir, string subName)
    {
        string direct = Path.Combine(partDir, subName + ".img");
        if (File.Exists(direct)) return direct;
        return Directory.GetFiles(partDir, subName + ".*.img").FirstOrDefault()
            ?? Directory.GetFiles(partDir, subName + ".*").FirstOrDefault(f => f.EndsWith(".img", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>估算子分区重建后的字节数（用于 super 容量告警）。</summary>
    private static long EstimateSubSize(string subDir, string imageType, string partDir, string subName)
    {
        if (imageType == "ext4" && Directory.Exists(subDir))
        {
            long content = 0;
            foreach (var f in Directory.EnumerateFiles(subDir, "*", SearchOption.AllDirectories))
            {
                try { content += new FileInfo(f).Length; } catch { }
            }
            // ext4 元数据开销（inode、目录、bitmap、xattr）通常 5–15%，按 12% 估算并向上对齐 4 KiB
            long est = (long)(content * 1.12);
            return (est + 4095) / 4096 * 4096;
        }
        var existing = FindSubImage(partDir, subName);
        if (existing is not null) return new FileInfo(existing).Length;
        return 0;
    }

    private void PackBootImage(string partDir, string outputImg, PartitionManifest pm, IProgress<RomTaskProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new RomTaskProgress(40, "读取 boot 参数"));
        string paramsPath = Path.Combine(partDir, "boot_params.json");
        var info = new BootImage.BootInfo();
        if (File.Exists(paramsPath))
        {
            var json = File.ReadAllText(paramsPath);
            // 与保存侧一致：BootInfo 为 public 字段的 record，必须 IncludeFields 才能读回
            info = System.Text.Json.JsonSerializer.Deserialize<BootImage.BootInfo>(json,
                new System.Text.Json.JsonSerializerOptions { IncludeFields = true }) ?? info;
        }

        // 读取 kernel（头部 kernel_size 依据实际数据重算，避免参数缺失/过期导致往返不一致）
        string kernelPath = Path.Combine(partDir, "kernel");
        if (File.Exists(kernelPath))
        {
            info.Kernel = File.ReadAllBytes(kernelPath);
            info.KernelSize = (uint)info.Kernel.Length;
        }

        // vendor_boot：vendor_ramdisk 单独保存，必须回读
        string vendorRamdiskPath = Path.Combine(partDir, "vendor_ramdisk");
        if (info.IsVendorBoot && File.Exists(vendorRamdiskPath))
        {
            info.VendorRamdisk = File.ReadAllBytes(vendorRamdiskPath);
            info.VendorRamdiskSize = (uint)info.VendorRamdisk.Length;
        }

        // 读取 ramdisk：优先 ramdisk/ 目录重新打包为 cpio.gz，否则用 ramdisk.cpio.gz
        // 仅当目录非空时才重新打包，避免解压失败留下的空目录覆盖原始 ramdisk
        string ramdiskDir = Path.Combine(partDir, "ramdisk");
        string ramdiskCpioGz = Path.Combine(partDir, "ramdisk.cpio.gz");
        if (!info.IsVendorBoot && Directory.Exists(ramdiskDir) && Directory.EnumerateFileSystemEntries(ramdiskDir).Any())
        {
            progress.Report(new RomTaskProgress(60, "重新打包 ramdisk"));
            byte[] cpio = CreateCpio(ramdiskDir);
            info.Ramdisk = BootImage.CompressRamdiskGzip(cpio);
            info.RamdiskSize = (uint)info.Ramdisk.Length;
        }
        else if (!info.IsVendorBoot && File.Exists(ramdiskCpioGz))
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
            // 字段顺序：c_magic[6] c_ino[8] c_mode[8] c_uid[8] c_gid[8] c_nlink[8]
            //   c_mtime[8] c_filesize[8] c_devmajor[8] c_devminor[8] c_rdevmajor[8] c_rdevminor[8]
            //   c_namesize[8] c_check[8]
            var sb = new System.Text.StringBuilder(110);
            sb.Append("070701");
            sb.AppendFormat("{0:X8}", inode++);
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
