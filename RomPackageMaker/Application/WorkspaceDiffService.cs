using System.IO;

using RomPackageMaker.Engine;
namespace RomPackageMaker.Application;

/// <summary>一条属性差异。注意：属性不能使用 init（会破坏 XAML 类型信息生成）。</summary>
public sealed class PropDiff
{
    public string Key { get; set; } = string.Empty;
    public string OldValue { get; set; } = string.Empty;
    public string NewValue { get; set; } = string.Empty;
    /// <summary>变化类型：modified / added / removed。</summary>
    public string Kind { get; set; } = "modified";

    /// <summary>旧值显示文本（新增属性显示"（无）"）。</summary>
    public string OldText => Kind == "added" ? "（无）" : OldValue;
    /// <summary>新值显示文本（移除属性显示"（无）"）。</summary>
    public string NewText => Kind == "removed" ? "（无）" : NewValue;
}

/// <summary>一条应用差异（新增或移除的预装应用）。</summary>
public sealed class AppDiff
{
    public string Name { get; set; } = string.Empty;
    public string RelPath { get; set; } = string.Empty;
    public string Partition { get; set; } = string.Empty;
    public string LocationText { get; set; } = string.Empty;
    public string SizeText { get; set; } = string.Empty;
}

/// <summary>一条分区大小差异（比较两边解包目录的实际占用）。</summary>
public sealed class PartitionDiff
{
    public string Name { get; set; } = string.Empty;
    public long OldSize { get; set; }
    public long NewSize { get; set; }
    public bool Changed => OldSize != NewSize;
    public string OldSizeText => OldSize > 0 ? WorkspaceScanner.FormatSize(OldSize) : "（无）";
    public string NewSizeText => NewSize > 0 ? WorkspaceScanner.FormatSize(NewSize) : "（无）";
    /// <summary>变化方向：grow / shrink / added / removed / same。</summary>
    public string ChangeText => OldSize == NewSize ? "无变化" : OldSize == 0 ? "新增分区" : NewSize == 0 ? "移除分区" : NewSize > OldSize ? "↑ 增大" : "↓ 减小";
}

/// <summary>两个工作区（新旧版本）的对比结果。</summary>
public sealed class WorkspaceDiff
{
    public List<AppDiff> AddedApps { get; } = new();
    public List<AppDiff> RemovedApps { get; } = new();
    public List<PropDiff> ModifiedProps { get; } = new();
    public List<PropDiff> AddedProps { get; } = new();
    public List<PropDiff> RemovedProps { get; } = new();
    public List<PartitionDiff> Partitions { get; } = new();

    public bool HasChanges => AddedApps.Count > 0 || RemovedApps.Count > 0 ||
        ModifiedProps.Count > 0 || AddedProps.Count > 0 || RemovedProps.Count > 0 ||
        Partitions.Any(p => p.Changed);

    /// <summary>摘要文本。</summary>
    public string SummaryText =>
        $"应用：新增 {AddedApps.Count}，移除 {RemovedApps.Count}；" +
        $"属性：修改 {ModifiedProps.Count}，新增 {AddedProps.Count}，移除 {RemovedProps.Count}；" +
        $"分区大小变化 {Partitions.Count(p => p.Changed)} 处";
}

/// <summary>对比两个解包工作区（如新旧两个版本的 ROM），汇总应用/属性/分区差异。</summary>
public static class WorkspaceDiffService
{
    /// <summary>对比两个工作区。目录不存在时按空工作区处理，不抛异常。</summary>
    public static WorkspaceDiff Compare(string oldDir, string newDir)
    {
        var result = new WorkspaceDiff();
        CompareApps(oldDir, newDir, result);
        CompareProps(oldDir, newDir, result);
        ComparePartitions(oldDir, newDir, result);
        return result;
    }

    private static void CompareApps(string oldDir, string newDir, WorkspaceDiff result)
    {
        var oldApps = SafeScan(oldDir);
        var newApps = SafeScan(newDir);
        var oldPaths = oldApps.Select(a => a.RelPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newPaths = newApps.Select(a => a.RelPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var app in newApps)
        {
            if (!oldPaths.Contains(app.RelPath))
            {
                result.AddedApps.Add(ToDiff(app));
            }
        }
        foreach (var app in oldApps)
        {
            if (!newPaths.Contains(app.RelPath))
            {
                result.RemovedApps.Add(ToDiff(app));
            }
        }
    }

    private static List<WorkspaceApp> SafeScan(string dir)
    {
        try { return WorkspaceScanner.ScanApps(dir); }
        catch (IOException) { return new List<WorkspaceApp>(); }
    }

    private static AppDiff ToDiff(WorkspaceApp app) => new()
    {
        Name = app.Name,
        RelPath = app.RelPath,
        Partition = app.Partition,
        LocationText = app.LocationText,
        SizeText = app.SizeText,
    };

    private static void CompareProps(string oldDir, string newDir, WorkspaceDiff result)
    {
        var oldProps = BuildPropService.CollectProps(oldDir);
        var newProps = BuildPropService.CollectProps(newDir);

        // 按旧侧原序遍历，保证输出稳定
        foreach (var (key, oldValue) in oldProps)
        {
            if (newProps.TryGetValue(key, out var newValue))
            {
                if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
                {
                    result.ModifiedProps.Add(new PropDiff { Key = key, OldValue = oldValue, NewValue = newValue, Kind = "modified" });
                }
            }
            else
            {
                result.RemovedProps.Add(new PropDiff { Key = key, OldValue = oldValue, NewValue = string.Empty, Kind = "removed" });
            }
        }
        foreach (var (key, newValue) in newProps)
        {
            if (!oldProps.ContainsKey(key))
            {
                result.AddedProps.Add(new PropDiff { Key = key, OldValue = string.Empty, NewValue = newValue, Kind = "added" });
            }
        }
    }

    private static void ComparePartitions(string oldDir, string newDir, WorkspaceDiff result)
    {
        var oldManifest = RomManifest.Load(oldDir);
        var newManifest = RomManifest.Load(newDir);

        var names = oldManifest.Partitions.Select(p => p.Name)
            .Concat(newManifest.Partitions.Select(p => p.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            var oldPart = oldManifest.Partitions.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var newPart = newManifest.Partitions.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            result.Partitions.Add(new PartitionDiff
            {
                Name = name,
                OldSize = ExtractedDirSize(oldDir, oldPart),
                NewSize = ExtractedDirSize(newDir, newPart),
            });
        }
    }

    private static long ExtractedDirSize(string workspaceDir, PartitionManifest? part)
    {
        if (part is null || part.ExtractedDir.Length == 0) return 0;
        string dir = Path.Combine(workspaceDir, part.ExtractedDir);
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch (IOException) { /* 文件被占用等，忽略 */ }
        }
        return total;
    }
}
