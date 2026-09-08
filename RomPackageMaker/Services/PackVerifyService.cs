using System.IO.Compression;

namespace RomPackageMaker.Services;

/// <summary>打包自检的单项结果。</summary>
internal sealed record PackCheck(string Name, bool Ok, string Detail);

/// <summary>
/// 打包结果自检：打包完成后回读产物，逐项验证「打得出来」且「内容与工作区一致」。
///
/// 存在的理由：此前打包完成即结束，没有任何产物验证——镜像头写错、ext4 丢文件、
/// sparse 展开异常等问题都要等刷机才发现。自检把这类问题前移到打包阶段。
/// </summary>
internal static class PackVerifyService
{
    /// <summary>
    /// 校验打包产物。outputPath 为 zip 或单个 .img。
    /// 返回逐项检查结果（Ok=false 表示该项未通过）。
    /// </summary>
    public static List<PackCheck> Verify(string workspaceDir, string outputPath,
        IProgress<RomTaskProgress>? progress = null)
    {
        var results = new List<PackCheck>();

        if (!File.Exists(outputPath))
        {
            results.Add(new PackCheck("产物存在", false, $"未找到 {outputPath}"));
            return results;
        }

        var fi = new FileInfo(outputPath);
        results.Add(new PackCheck("产物非空", fi.Length > 0, $"{fi.Length / 1024.0 / 1024.0:F1} MiB"));

        var manifest = RomManifest.Load(workspaceDir);
        if (manifest.Partitions.Count == 0)
        {
            results.Add(new PackCheck("工作区清单", false, ".rom_manifest.json 缺失或无分区记录，跳过逐分区校验"));
            return results;
        }

        string ext = Path.GetExtension(outputPath).ToLowerInvariant();
        if (ext == ".zip")
        {
            VerifyZip(workspaceDir, outputPath, manifest, results, progress);
        }
        else
        {
            // 单镜像产物：直接校验该镜像，分区名取清单中同名项（找不到就只做格式校验）
            var pm = manifest.Partitions.FirstOrDefault(p =>
                p.Name.Equals(Path.GetFileNameWithoutExtension(outputPath), StringComparison.OrdinalIgnoreCase));
            using var fs = File.OpenRead(outputPath);
            VerifyImage(fs, pm?.Name ?? Path.GetFileNameWithoutExtension(outputPath),
                pm?.ImageType ?? "raw", workspaceDir, pm, results);
        }

        return results;
    }

    private static void VerifyZip(string workspaceDir, string zipPath, RomManifest manifest,
        List<PackCheck> results, IProgress<RomTaskProgress>? progress)
    {
        List<string> entries;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            entries = zip.Entries.Select(e => e.FullName).ToList();
        }
        catch (Exception ex)
        {
            results.Add(new PackCheck("zip 可打开", false, ex.Message));
            return;
        }

        results.Add(new PackCheck("zip 可打开", true, $"{entries.Count} 个条目"));

        // 逐分区镜像校验：从 zip 中解压到临时文件再解析（避免为 zip 内条目写一套流式解析）
        string tempDir = Path.Combine(Path.GetTempPath(), "rommaker_verify_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            int i = 0;
            foreach (var pm in manifest.Partitions)
            {
                i++;
                progress?.Report(new RomTaskProgress(90 + (int)(10.0 * i / manifest.Partitions.Count), "自检", pm.Name));

                string? entryName = entries.FirstOrDefault(e =>
                    e.EndsWith(pm.Name + ".img", StringComparison.OrdinalIgnoreCase));
                if (entryName is null)
                {
                    results.Add(new PackCheck($"分区 {pm.Name}", false, "产物中未找到对应镜像"));
                    continue;
                }

                string tempImg = Path.Combine(tempDir, pm.Name + ".img");
                try
                {
                    using (var zip = ZipFile.OpenRead(zipPath))
                    {
                        var entry = zip.GetEntry(entryName)!;
                        entry.ExtractToFile(tempImg, overwrite: true);
                    }
                    using var fs = File.OpenRead(tempImg);
                    VerifyImage(fs, pm.Name, pm.ImageType, workspaceDir, pm, results);
                }
                finally
                {
                    try { File.Delete(tempImg); } catch { }
                }
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>按镜像类型校验流内容；stream 需可 Seek。</summary>
    private static void VerifyImage(Stream stream, string name, string imageType, string workspaceDir,
        PartitionManifest? pm, List<PackCheck> results)
    {
        try
        {
            // sparse 先展开到内存/临时文件（分区通常很大，用临时文件）
            Stream target = stream;
            FileStream? tempRaw = null;
            try
            {
                if (SparseImage.IsSparse(stream))
                {
                    stream.Position = 0;
                    string tmp = Path.Combine(Path.GetTempPath(), "rommaker_verify_" + Guid.NewGuid().ToString("N")[..8] + ".raw");
                    tempRaw = File.Create(tmp);
                    SparseImage.Unsparse(stream, tempRaw);
                    tempRaw.Flush();
                    tempRaw.Position = 0;
                    target = tempRaw;
                    results.Add(new PackCheck($"{name} sparse 展开", true, $"{tempRaw.Length / 1024.0 / 1024.0:F1} MiB"));
                }

                switch (imageType)
                {
                    case "ext4":
                        VerifyExt4(target, name, workspaceDir, pm, results);
                        break;
                    case "boot":
                        VerifyBoot(target, name, results);
                        break;
                    case "super":
                        VerifySuper(target, name, pm, results);
                        break;
                    default:
                        results.Add(new PackCheck($"{name} 大小", target.Length > 0, $"{target.Length / 1024.0 / 1024.0:F1} MiB"));
                        break;
                }
            }
            finally
            {
                if (tempRaw is not null)
                {
                    string p = tempRaw.Name;
                    tempRaw.Dispose();
                    try { File.Delete(p); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            results.Add(new PackCheck($"{name} 解析", false, ex.Message));
        }
    }

    private static void VerifyBoot(Stream stream, string name, List<PackCheck> results)
    {
        stream.Position = 0;
        var info = BootImage.Parse(stream);
        long kernelSize = info.KernelSize;
        results.Add(new PackCheck($"{name} boot 头", true,
            $"v{info.HeaderVersion} kernel={kernelSize / 1024.0 / 1024.0:F1} MiB ramdisk={(info.Ramdisk?.Length ?? 0) / 1024.0 / 1024.0:F1} MiB"));
    }

    private static void VerifyExt4(Stream stream, string name, string workspaceDir,
        PartitionManifest? pm, List<PackCheck> results)
    {
        stream.Position = 0;
        using var reader = new Ext4Reader(stream, leaveOpen: true);
        results.Add(new PackCheck($"{name} ext4 superblock", true,
            $"块大小 {reader.BlockSize}、共 {reader.BlockCount * reader.BlockSize / 1024.0 / 1024.0:F0} MiB"));

        int count = reader.CountEntries();
        if (count < 0)
        {
            results.Add(new PackCheck($"{name} ext4 目录树", false, "读取目录树失败"));
            return;
        }

        // 与工作区解包目录对比（仅统计到 3 层、上限 2 万条目，够发现"整包丢失"与"大面积漏文件"）
        if (pm is not null && !string.IsNullOrEmpty(pm.ExtractedDir))
        {
            string dir = Path.Combine(workspaceDir, pm.ExtractedDir);
            if (Directory.Exists(dir))
            {
                int expected = CountDirEntries(dir, 3, 20000);
                bool ok = expected == 0 ? count >= 0 : count >= expected;
                results.Add(new PackCheck($"{name} 条目数一致", ok,
                    $"镜像 {count} 条 vs 工作区 {expected} 条（统计上限 3 层 / 20000）"));
                return;
            }
        }

        results.Add(new PackCheck($"{name} ext4 目录树", count > 0, $"{count} 个条目"));
    }

    private static void VerifySuper(Stream stream, string name, PartitionManifest? pm, List<PackCheck> results)
    {
        stream.Position = 0;
        var md = SuperImage.Parse(stream);
        int actual = md.Partitions.Count;
        int expected = pm?.SubPartitions?.Count ?? 0;
        bool ok = expected == 0 || actual == expected;
        results.Add(new PackCheck($"{name} 子分区数", ok,
            expected == 0 ? $"{actual} 个子分区" : $"镜像 {actual} 个 vs 清单 {expected} 个"));
    }

    /// <summary>统计目录树条目数（与 Ext4Reader.CountEntries 同口径）。</summary>
    public static int CountDirEntries(string dir, int maxDepth = 3, int cap = 20000)
    {
        int count = 0;
        try
        {
            void Walk(string d, int depth)
            {
                if (count >= cap || depth > maxDepth) return;
                foreach (var f in Directory.EnumerateFileSystemEntries(d))
                {
                    // 排除 Ext4Reader.ExtractTo 写入但镜像本身不包含的元数据文件
                    if (Path.GetFileName(f) == ".rom_metadata.json") continue;
                    count++;
                    if (count >= cap) return;
                    if (Directory.Exists(f)) Walk(f, depth + 1);
                }
            }
            Walk(dir, 0);
        }
        catch { }
        return count;
    }
}
