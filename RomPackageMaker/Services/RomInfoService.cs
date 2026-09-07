using System.IO;

namespace RomPackageMaker.Services;

/// <summary>信息总览中的一条系统属性。注意：属性不能使用 init（会破坏 XAML 类型信息生成）。</summary>
public sealed class RomInfoProp
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    /// <summary>来源 build.prop 相对路径（system 优先覆盖）。</summary>
    public string Source { get; set; } = string.Empty;
    /// <summary>属性中文说明（无则为空串）。</summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>信息总览中的分区摘要。</summary>
public sealed class RomInfoPartition
{
    public string Name { get; set; } = string.Empty;
    public string ImageType { get; set; } = string.Empty;
    /// <summary>原始镜像大小文本。</summary>
    public string SizeText { get; set; } = string.Empty;
    /// <summary>原镜像是否为 sparse 格式（"sparse" / ""）。</summary>
    public string SparseText { get; set; } = string.Empty;
}

/// <summary>工作区 ROM 信息汇总。</summary>
public sealed class RomInfo
{
    public string SourceFile { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string SourceName => Path.GetFileName(SourceFile);
    public List<RomInfoProp> Props { get; set; } = new();
    public List<RomInfoPartition> Partitions { get; set; } = new();
    public int AppCount { get; set; }
    public bool GmsPresent { get; set; }
    public bool AvbPresent { get; set; }
    public bool AvbAllDisabled { get; set; }
    public bool RootSuPresent { get; set; }
    public long TotalImageSize { get; set; }
    public string TotalSizeText { get; set; } = string.Empty;
}

/// <summary>扫描工作区，汇总 ROM 的关键信息（属性 / 分区 / 应用 / GMS / AVB / ROOT）。</summary>
public static class RomInfoService
{
    /// <summary>关键属性展示顺序（命中的排在最前，其余按字母序）。</summary>
    private static readonly string[] FeaturedKeys =
    {
        "ro.product.model", "ro.product.brand", "ro.product.device", "ro.product.name",
        "ro.product.manufacturer", "ro.build.version.release", "ro.build.version.sdk",
        "ro.build.version.security_patch", "ro.build.display.id", "ro.build.id",
        "ro.build.type", "ro.build.tags", "ro.build.fingerprint", "ro.build.date",
        "ro.product.cpu.abilist", "ro.zygote", "ro.treble.enabled",
    };

    private static readonly string[] Partitions = { "system", "vendor", "product", "system_ext", "odm" };
    private static readonly string[] AppSubdirs = { "app", "priv-app" };
    private static readonly string[] GmsMarkers = { "com.google.android.gms", "PrebuiltGmsCore", "GmsCore" };

    /// <summary>采集工作区信息。目录不存在时返回空结果，不抛异常。</summary>
    public static RomInfo Collect(string workspaceDir)
    {
        var info = new RomInfo();
        if (string.IsNullOrWhiteSpace(workspaceDir) || !Directory.Exists(workspaceDir)) return info;

        var manifest = RomManifest.Load(workspaceDir);
        info.SourceFile = manifest.SourceFile;
        info.SourceType = manifest.SourceType;

        info.Props = CollectPropsDetailed(workspaceDir);

        foreach (var p in manifest.Partitions)
        {
            info.Partitions.Add(new RomInfoPartition
            {
                Name = p.Name,
                ImageType = p.ImageType,
                SizeText = p.OriginalSize > 0 ? WorkspaceScanner.FormatSize(p.OriginalSize) : "—",
                SparseText = p.WasSparse ? "sparse" : string.Empty,
            });
        }

        info.AppCount = CountApps(workspaceDir);
        info.GmsPresent = DetectGms(workspaceDir);

        var vbm = AvbService.ScanWorkspace(workspaceDir).Where(e => e.IsVbmeta).ToList();
        info.AvbPresent = vbm.Count > 0;
        info.AvbAllDisabled = vbm.Count > 0 && vbm.All(v => v.VerificationDisabled && v.HashtreeDisabled);

        info.RootSuPresent = DetectSu(workspaceDir);

        info.TotalImageSize = manifest.Partitions.Sum(p => p.OriginalSize);
        info.TotalSizeText = info.TotalImageSize > 0 ? WorkspaceScanner.FormatSize(info.TotalImageSize) : "—";
        return info;
    }

    /// <summary>合并工作区内全部 build.prop 候选（system 最后读、同键覆盖），返回带来源的属性列表。</summary>
    private static List<RomInfoProp> CollectPropsDetailed(string workspaceDir)
    {
        var merged = new Dictionary<string, RomInfoProp>(StringComparer.Ordinal);
        foreach (var candidate in BuildPropService.FindCandidates(workspaceDir).OrderBy(IsSystemCandidate))
        {
            List<BuildPropLine> lines;
            try { lines = BuildPropService.Parse(File.ReadAllText(candidate)); }
            catch (IOException) { continue; }
            string source = Path.GetRelativePath(workspaceDir, candidate);
            foreach (var line in lines)
            {
                if (!line.IsProperty) continue;
                merged[line.Key] = new RomInfoProp
                {
                    Key = line.Key,
                    Value = line.Value,
                    Source = source,
                    Description = BuildPropService.Describe(line.Key) ?? string.Empty,
                };
            }
        }

        // 关键属性按预定义顺序排前，其余按字母序
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < FeaturedKeys.Length; i++) index[FeaturedKeys[i]] = i;
        return merged.Values
            .OrderBy(p => index.TryGetValue(p.Key, out int i) ? i : int.MaxValue)
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .ToList();
    }

    private static int IsSystemCandidate(string path) =>
        string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "system", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

    /// <summary>轻量统计预装应用数（只枚举 app/priv-app 一层，不递归算大小）。</summary>
    private static int CountApps(string workspaceDir)
    {
        int count = 0;
        foreach (var partition in Partitions)
        {
            foreach (var root in EnumeratePartitionRoots(workspaceDir, partition))
            {
                foreach (var sub in AppSubdirs)
                {
                    string appDir = Path.Combine(root, sub);
                    if (!Directory.Exists(appDir)) continue;
                    // 目录型：至少含一个 APK 才计入
                    foreach (var dir in Directory.EnumerateDirectories(appDir))
                    {
                        if (Directory.EnumerateFiles(dir, "*.apk").Any()) count++;
                    }
                    // 文件型（老式布局）
                    count += Directory.EnumerateFiles(appDir, "*.apk").Count();
                }
            }
        }
        return count;
    }

    private static IEnumerable<string> EnumeratePartitionRoots(string workspaceDir, string partition)
    {
        string direct = Path.Combine(workspaceDir, partition);
        if (Directory.Exists(direct)) yield return direct;
        string inSuper = Path.Combine(workspaceDir, "super", partition);
        if (Directory.Exists(inSuper)) yield return inSuper;
    }

    /// <summary>检测 GMS：app/priv-app 下的特征目录名，或 etc/permissions 中的 GMS 特征文件。</summary>
    private static bool DetectGms(string workspaceDir)
    {
        foreach (var partition in Partitions)
        {
            foreach (var root in EnumeratePartitionRoots(workspaceDir, partition))
            {
                foreach (var sub in AppSubdirs)
                {
                    string appDir = Path.Combine(root, sub);
                    if (!Directory.Exists(appDir)) continue;
                    foreach (var dir in Directory.EnumerateDirectories(appDir))
                    {
                        string name = Path.GetFileName(dir);
                        if (GmsMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase))) return true;
                    }
                    foreach (var apk in Directory.EnumerateFiles(appDir, "*.apk"))
                    {
                        string name = Path.GetFileNameWithoutExtension(apk);
                        if (GmsMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase))) return true;
                    }
                }

                string permDir = Path.Combine(root, "etc", "permissions");
                if (Directory.Exists(permDir))
                {
                    foreach (var xml in Directory.EnumerateFiles(permDir, "*.xml"))
                    {
                        string name = Path.GetFileName(xml);
                        if (name.Contains("com.google.android.gms", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("com.google.android.feature.gms", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
        }
        return false;
    }

    /// <summary>检测 ROOT 痕迹：su 二进制、Superuser 管理器、Magisk 管理器、init.d 脚本目录。</summary>
    private static bool DetectSu(string workspaceDir)
    {
        string? systemDir = RootService.FindSystemDir(workspaceDir);
        if (systemDir is null) return false;

        foreach (var rel in new[] { Path.Combine("bin", "su"), Path.Combine("xbin", "su"), Path.Combine("sbin", "su") })
        {
            if (File.Exists(Path.Combine(systemDir, rel))) return true;
        }
        if (File.Exists(Path.Combine(systemDir, "app", "Superuser.apk"))) return true;
        if (Directory.Exists(Path.Combine(systemDir, "priv-app", "Magisk"))) return true;
        if (Directory.Exists(Path.Combine(systemDir, "etc", "init.d"))) return true;
        return false;
    }
}
