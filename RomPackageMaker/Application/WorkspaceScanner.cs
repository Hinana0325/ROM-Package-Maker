using System.ComponentModel;
using System.IO;

using RomPackageMaker.Engine;
namespace RomPackageMaker.Application;

/// <summary>工作目录中的预装应用（精简管理条目）。</summary>
public sealed class WorkspaceApp : INotifyPropertyChanged
{
    private bool _isSelected;

    /// <summary>是否勾选（用于批量删除）。</summary>
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

    /// <summary>应用名（目录名或 APK 文件名去扩展名）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>所属分区：system / vendor / product / system_ext / odm。</summary>
    public string Partition { get; set; } = string.Empty;

    /// <summary>是否位于 priv-app（系统特权应用）。</summary>
    public bool IsPrivApp { get; set; }

    /// <summary>相对工作目录路径，如 system/app/Browser。</summary>
    public string RelPath { get; set; } = string.Empty;

    /// <summary>是否为单 APK 文件（而非独立目录）。</summary>
    public bool IsSingleFile { get; set; }

    /// <summary>占用大小（字节）。</summary>
    public long SizeBytes { get; set; }

    /// <summary>预格式化大小文本，如 "12.3 MB"。</summary>
    public string SizeText { get; set; } = string.Empty;

    /// <summary>分区位置显示文本，如 "system · priv-app"。</summary>
    public string LocationText { get; set; } = string.Empty;

    /// <summary>主 APK 路径（解析 AndroidManifest 用）；目录型应用取其中最大的 APK。</summary>
    public string? MainApkPath { get; set; }

    /// <summary>解析出的 AndroidManifest 信息；尚未解析为 null，解析失败为 null 且 ParseFailed 为 true。</summary>
    public ApkInfo? ApkInfo
    {
        get => _apkInfo;
        set
        {
            _apkInfo = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ApkInfo)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PackageText)));
        }
    }
    private ApkInfo? _apkInfo;

    /// <summary>APK 解析是否失败（用于列表显示占位）。</summary>
    public bool ParseFailed { get; set; }

    /// <summary>删除风险等级（依据 ApkInfo 包名查询知识库）。</summary>
    public DebloatRisk Risk
    {
        get => _risk;
        set
        {
            if (_risk == value) return;
            _risk = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Risk)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RiskText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PackageText)));
        }
    }
    private DebloatRisk _risk;

    /// <summary>风险徽标文本（可删 / 谨慎 / 禁删 / 空）。</summary>
    public string RiskText => DebloatSafetyService.RiskText(Risk);

    /// <summary>风险原因（悬停提示）；未知为空串。</summary>
    public string RiskReason { get; set; } = string.Empty;

    /// <summary>列表副行文本：包名 · 版本；未解析 / 失败时为占位文本。</summary>
    public string PackageText
    {
        get
        {
            if (ApkInfo is { } a) return a.Summary;
            if (ParseFailed) return "APK 信息解析失败";
            return MainApkPath is null ? "" : "APK 解析中...";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>扫描与删除工作目录中的预装应用。</summary>
public static class WorkspaceScanner
{
    private static readonly string[] Partitions = { "system", "vendor", "product", "system_ext", "odm" };
    private static readonly string[] AppSubdirs = { "app", "priv-app" };

    /// <summary>分区目录候选：直接位于工作区根，或嵌套在 super 分区下。</summary>
    private static IEnumerable<string> EnumeratePartitionRoots(string workspaceDir, string partition)
    {
        string direct = Path.Combine(workspaceDir, partition);
        if (Directory.Exists(direct)) yield return direct;
        string inSuper = Path.Combine(workspaceDir, "super", partition);
        if (Directory.Exists(inSuper)) yield return inSuper;
    }

    /// <summary>扫描所有分区的预装应用（目录型与单 APK 文件型）。</summary>
    public static List<WorkspaceApp> ScanApps(string workspaceDir)
    {
        var apps = new List<WorkspaceApp>();
        if (string.IsNullOrWhiteSpace(workspaceDir) || !Directory.Exists(workspaceDir)) return apps;

        foreach (var partition in Partitions)
        {
            foreach (var root in EnumeratePartitionRoots(workspaceDir, partition))
            {
                foreach (var sub in AppSubdirs)
                {
                    string appDir = Path.Combine(root, sub);
                    if (!Directory.Exists(appDir)) continue;
                    bool priv = sub == "priv-app";

                    // 目录型应用：system/app/<Name>/<Name>.apk
                    foreach (var dir in Directory.EnumerateDirectories(appDir))
                    {
                        var apks = Directory.EnumerateFiles(dir, "*.apk").ToList();
                        if (apks.Count == 0) continue;
                        long size = DirectorySize(dir);
                        apps.Add(new WorkspaceApp
                        {
                            Name = Path.GetFileName(dir),
                            Partition = partition,
                            IsPrivApp = priv,
                            RelPath = Path.GetRelativePath(workspaceDir, dir),
                            IsSingleFile = false,
                            SizeBytes = size,
                            SizeText = FormatSize(size),
                            LocationText = partition + (priv ? " · priv-app" : " · app"),
                            // 目录内多个 APK（split）时取最大的作为主 APK
                            MainApkPath = apks.OrderByDescending(f => new FileInfo(f).Length).First(),
                        });
                    }

                    // 文件型应用（老式布局）：system/app/<Name>.apk
                    foreach (var apk in Directory.EnumerateFiles(appDir, "*.apk"))
                    {
                        long size = new FileInfo(apk).Length;
                        apps.Add(new WorkspaceApp
                        {
                            Name = Path.GetFileNameWithoutExtension(apk),
                            Partition = partition,
                            IsPrivApp = priv,
                            RelPath = Path.GetRelativePath(workspaceDir, apk),
                            IsSingleFile = true,
                            SizeBytes = size,
                            SizeText = FormatSize(size),
                            LocationText = partition + (priv ? " · priv-app" : " · app"),
                            MainApkPath = apk,
                        });
                    }
                }
            }
        }

        return apps
            .OrderBy(a => a.Partition, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>从工作目录删除一个预装应用（目录整体删除；单文件删除 APK 及同名 odex/vdex/art 变体）。</summary>
    public static void RemoveApp(string workspaceDir, WorkspaceApp app)
    {
        string fullPath = Path.Combine(workspaceDir, app.RelPath);
        if (app.IsSingleFile)
        {
            if (File.Exists(fullPath)) File.Delete(fullPath);
            // 同名优化/校验产物一并清理
            string baseNoExt = Path.Combine(
                Path.GetDirectoryName(fullPath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(fullPath));
            foreach (var ext in new[] { ".odex", ".vdex", ".art" })
            {
                string sibling = baseNoExt + ext;
                if (File.Exists(sibling)) File.Delete(sibling);
            }
        }
        else if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private static long DirectorySize(string dir)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch (IOException) { /* 文件被占用等，忽略 */ }
        }
        return total;
    }

    /// <summary>格式化字节大小为可读文本。</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1 << 30 => $"{bytes / (double)(1 << 30):0.##} GB",
        >= 1 << 20 => $"{bytes / (double)(1 << 20):0.##} MB",
        >= 1 << 10 => $"{bytes / (double)(1 << 10):0.##} KB",
        _ => $"{bytes} B",
    };
}
