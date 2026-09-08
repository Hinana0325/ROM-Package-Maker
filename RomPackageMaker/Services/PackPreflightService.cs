namespace RomPackageMaker.Services;

/// <summary>预检问题级别。</summary>
public enum PreflightLevel
{
    Info = 0,
    Warn = 1,
    Error = 2,
}

/// <summary>单条预检结果。</summary>
public sealed class PreflightIssue
{
    public PreflightLevel Level { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>打包前防呆检查与 fastboot 刷机脚本生成。</summary>
public static class PackPreflightService
{
    /// <summary>运行打包前检查。返回问题列表（空 = 一切正常）。不抛异常。</summary>
    public static List<PreflightIssue> RunChecks(string workspaceDir, string outputPath)
    {
        var issues = new List<PreflightIssue>();
        if (string.IsNullOrWhiteSpace(workspaceDir) || !Directory.Exists(workspaceDir))
        {
            issues.Add(new PreflightIssue { Level = PreflightLevel.Error, Message = "工作目录不存在。" });
            return issues;
        }

        var manifest = RomManifest.Load(workspaceDir);
        if (manifest.Partitions.Count == 0)
        {
            issues.Add(new PreflightIssue
            {
                Level = PreflightLevel.Error,
                Message = "工作目录缺少解包清单（.rom_manifest.json）或没有分区条目——产物只包含 _zip 原始文件，不会重建任何镜像。",
            });
        }

        // 分区目录完整性
        foreach (var pm in manifest.Partitions)
        {
            if (pm.ExtractedDir.Length > 0 && !Directory.Exists(Path.Combine(workspaceDir, pm.ExtractedDir)))
            {
                issues.Add(new PreflightIssue
                {
                    Level = PreflightLevel.Error,
                    Message = $"分区 {pm.Name} 的解包目录丢失（{pm.ExtractedDir}），该分区将不会进入 ROM。",
                });
            }
            if (pm.ImageType == "boot" && !File.Exists(Path.Combine(workspaceDir, pm.ExtractedDir, "boot_params.json")))
            {
                issues.Add(new PreflightIssue
                {
                    Level = PreflightLevel.Warn,
                    Message = $"boot 分区缺少 boot_params.json，将用默认参数重打包（可能丢失原 cmdline / 页大小）。",
                });
            }
            // 容量预估：ext4 分区按目录字节和 + 12% 元数据开销，与原始镜像大小对比
            if (pm.ImageType == "ext4" && pm.OriginalSize > 0
                && !string.IsNullOrEmpty(pm.ExtractedDir))
            {
                string partDir = Path.Combine(workspaceDir, pm.ExtractedDir);
                if (Directory.Exists(partDir))
                {
                    long content = 0;
                    foreach (var f in Directory.EnumerateFiles(partDir, "*", SearchOption.AllDirectories))
                    {
                        try { content += new FileInfo(f).Length; } catch { }
                    }
                    long est = (long)(content * 1.12);
                    double ratio = (double)est / pm.OriginalSize;
                    if (ratio > 1.0)
                    {
                        issues.Add(new PreflightIssue
                        {
                            Level = PreflightLevel.Error,
                            Message = $"分区 {pm.Name} 预估 {est / (1 << 20)} MiB > 原镜像 {pm.OriginalSize / (1 << 20)} MiB，打包后很可能装不进 super 槽位。请先精简或扩容 super。",
                        });
                    }
                    else if (ratio > 0.85)
                    {
                        issues.Add(new PreflightIssue
                        {
                            Level = PreflightLevel.Warn,
                            Message = $"分区 {pm.Name} 预估 {est / (1 << 20)} MiB / 原 {pm.OriginalSize / (1 << 20)} MiB（{ratio:P0}），已接近上限。",
                        });
                    }
                }
            }
        }

        // AVB 状态：存在 vbmeta 且未禁用验证 → 修改过的 ROM 大概率校验失败
        try
        {
            var vbm = AvbService.ScanWorkspace(workspaceDir).Where(v => v.IsVbmeta).ToList();
            if (vbm.Count > 0 && vbm.Any(v => !v.VerificationDisabled))
            {
                issues.Add(new PreflightIssue
                {
                    Level = PreflightLevel.Warn,
                    Message = $"检测到 {vbm.Count} 个 vbmeta 镜像未禁用验证：定制过的分区哈希与 vbmeta 记录不匹配，设备可能无法开机。建议先在「AVB / dm-verity」页一键禁用。",
                });
            }
        }
        catch
        {
            // AVB 扫描失败不阻塞打包
        }

        // 磁盘剩余空间（工作区大小的 1.2 倍估算）
        try
        {
            long workspaceSize = DirectorySize(workspaceDir);
            string? outDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(outDir) && Directory.Exists(outDir))
            {
                long free = new DriveInfo(Path.GetFullPath(outDir).Substring(0, 1)).AvailableFreeSpace;
                if (free < workspaceSize * 12 / 10)
                {
                    issues.Add(new PreflightIssue
                    {
                        Level = PreflightLevel.Warn,
                        Message = $"输出磁盘剩余空间不足（工作区 {workspaceSize / (1 << 20)} MB，可用 {free / (1 << 20)} MB），打包可能中途失败。",
                    });
                }
            }
        }
        catch
        {
            // 忽略磁盘检查异常
        }

        return issues;
    }

    /// <summary>按解包清单生成 fastboot 刷机脚本（flash_all.bat），写到输出 zip 同目录。返回脚本路径。</summary>
    public static string? GenerateFastbootScript(string workspaceDir, string outputPath)
    {
        var manifest = RomManifest.Load(workspaceDir);
        if (manifest.Partitions.Count == 0) return null;

        string scriptDir = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".";
        string scriptPath = Path.Combine(scriptDir, "flash_all.bat");

        var lines = new List<string>
        {
            "@echo off",
            "REM Generated by ROM Package Maker",
            "REM 将 ROM zip 解压到本脚本同目录后以管理员身份运行；设备需进入 fastboot 模式。",
            "fastboot getvar product 2>nul",
        };
        foreach (var pm in manifest.Partitions)
        {
            // 镜像文件名保持 zip 内相对路径（多数为根目录 <name>.img）
            string img = pm.OriginalImagePath.Replace('/', '\\');
            lines.Add($"fastboot flash {pm.Name} \"{img}\"");
        }
        lines.Add("fastboot reboot");
        lines.Add("echo Done.");
        lines.Add("pause");

        File.WriteAllLines(scriptPath, lines);
        return scriptPath;
    }

    private static long DirectorySize(string dir)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch (IOException) { /* 忽略 */ }
        }
        return total;
    }
}
