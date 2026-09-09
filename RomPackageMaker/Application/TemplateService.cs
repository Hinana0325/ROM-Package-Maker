using System.IO;
using System.Text.Json;

using RomPackageMaker.Engine;
namespace RomPackageMaker.Application;

/// <summary>ROM 定制模板：一次应用即可完成 build.prop 覆盖、预装精简与 ROOT 集成。</summary>
public sealed class RomTemplate
{
    /// <summary>模板名（同时作为文件名）。</summary>
    public string Name { get; set; } = "新模板";

    public string? Description { get; set; }

    /// <summary>build.prop 属性覆盖：key → value（存在则改，不存在则追加）。</summary>
    public Dictionary<string, string> BuildPropOverrides { get; set; } = new();

    /// <summary>要删除的预装应用（应用目录名或 APK 文件名，忽略大小写）。</summary>
    public List<string> RemoveApps { get; set; } = new();

    /// <summary>ROOT 集成模式：none / magisk-system / replace-boot。</summary>
    public string RootMode { get; set; } = RootModes.None;

    /// <summary>ROOT 源文件：Magisk APK 或已修补的 boot.img 路径。</summary>
    public string? RootSourcePath { get; set; }

    /// <summary>replace-boot 模式的目标分区（boot / vendor_boot / init_boot…）。</summary>
    public string? RootBootPartition { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>模板编辑器的键值对行模型。</summary>
public sealed class TemplatePropOverride
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

/// <summary>模板编辑器的文本行模型。</summary>
public sealed class TemplateTextItem
{
    public string Text { get; set; } = string.Empty;
}

/// <summary>模板的存储与应用。</summary>
public static class TemplateService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string TemplatesDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RomPackageMaker", "templates");

    /// <summary>加载全部模板（按更新时间倒序）。</summary>
    public static List<RomTemplate> LoadAll()
    {
        var list = new List<RomTemplate>();
        if (!Directory.Exists(TemplatesDir)) return list;
        foreach (var file in Directory.EnumerateFiles(TemplatesDir, "*.json"))
        {
            try
            {
                var t = JsonSerializer.Deserialize<RomTemplate>(File.ReadAllText(file));
                if (t is not null) list.Add(t);
            }
            catch (JsonException)
            {
                // 跳过损坏的模板文件
            }
        }
        return list.OrderByDescending(t => t.UpdatedAt).ToList();
    }

    /// <summary>保存模板（按模板名生成安全文件名）。</summary>
    public static void Save(RomTemplate template)
    {
        template.UpdatedAt = DateTime.Now;
        Directory.CreateDirectory(TemplatesDir);
        string path = GetTemplatePath(template.Name);
        File.WriteAllText(path, JsonSerializer.Serialize(template, JsonOpts));
    }

    /// <summary>删除模板文件。</summary>
    public static void Delete(string templateName)
    {
        string path = GetTemplatePath(templateName);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>将模板应用到工作目录，返回执行日志。</summary>
    public static List<string> Apply(RomTemplate template, string workspaceDir)
    {
        if (!Directory.Exists(workspaceDir))
            throw new DirectoryNotFoundException($"工作目录不存在：{workspaceDir}");

        var log = new List<string>();

        // 1. build.prop 覆盖
        if (template.BuildPropOverrides.Count > 0)
        {
            var candidates = BuildPropService.FindCandidates(workspaceDir);
            // 优先 system 分区下的 build.prop（含 super/system 嵌套）
            var targets = candidates
                .Where(p => IsUnderSystem(workspaceDir, p))
                .ToList();
            if (targets.Count == 0) targets = candidates;

            if (targets.Count == 0)
            {
                log.Add("⚠ 未找到任何 build.prop，属性覆盖未执行。");
            }
            else
            {
                foreach (var path in targets)
                {
                    int changed = ApplyPropOverrides(path, template.BuildPropOverrides);
                    log.Add($"build.prop：{Path.GetRelativePath(workspaceDir, path)} 应用 {changed} 项属性。");
                }
            }
        }

        // 2. 预装精简
        if (template.RemoveApps.Count > 0)
        {
            var apps = WorkspaceScanner.ScanApps(workspaceDir);
            int removed = 0, missing = 0;
            foreach (var name in template.RemoveApps)
            {
                var match = apps.FirstOrDefault(a =>
                    string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a.RelPath, name, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    missing++;
                    continue;
                }
                WorkspaceScanner.RemoveApp(workspaceDir, match);
                removed++;
                log.Add($"精简：已删除 {match.RelPath}");
            }
            if (missing > 0)
                log.Add($"⚠ {missing} 个应用在工作目录中未找到（可能已删除或未解包）。");
            log.Add($"精简完成：删除 {removed} 个应用。");
        }

        // 3. ROOT 集成
        switch (template.RootMode)
        {
            case RootModes.MagiskSystem:
                try
                {
                    RootService.InstallMagiskManager(workspaceDir, template.RootSourcePath ?? string.Empty);
                    log.Add("ROOT：已内置 Magisk 管理器到 system/priv-app/Magisk/。");
                }
                catch (Exception ex)
                {
                    log.Add($"⚠ Magisk 管理器内置失败：{ex.Message}");
                }
                break;

            case RootModes.ReplaceBoot:
                try
                {
                    string part = RootService.ReplaceBootImage(workspaceDir, template.RootSourcePath ?? string.Empty, template.RootBootPartition);
                    log.Add($"ROOT：已用修补镜像替换分区 {part}（打包时直通复制）。");
                }
                catch (Exception ex)
                {
                    log.Add($"⚠ boot.img 替换失败：{ex.Message}");
                }
                break;
        }

        if (log.Count == 0)
        {
            log.Add("模板未包含任何可执行的动作（无属性覆盖、无精简列表、无 ROOT 配置）。");
        }
        return log;
    }

    /// <summary>对单个 build.prop 应用属性覆盖：存在则修改，不存在则追加；返回修改数。</summary>
    private static int ApplyPropOverrides(string propPath, Dictionary<string, string> overrides)
    {
        var lines = BuildPropService.Parse(File.ReadAllText(propPath));

        // 首次修改前备份
        string bak = propPath + ".bak";
        if (!File.Exists(bak)) File.Copy(propPath, bak, overwrite: false);

        int changed = 0;
        foreach (var (key, value) in overrides)
        {
            var existing = lines.FirstOrDefault(l => l.IsProperty && l.Key == key);
            if (existing is not null)
            {
                if (existing.Value != value)
                {
                    existing.Value = value;
                    changed++;
                }
            }
            else
            {
                lines.Add(new BuildPropLine { IsProperty = true, Key = key, Value = value, IsNew = true });
                changed++;
            }
        }

        if (changed > 0)
        {
            File.WriteAllText(propPath, BuildPropService.Serialize(lines), new System.Text.UTF8Encoding(false));
        }
        return changed;
    }

    private static bool IsUnderSystem(string workspaceDir, string propPath)
    {
        string rel = Path.GetRelativePath(workspaceDir, propPath);
        return rel.StartsWith("system", StringComparison.OrdinalIgnoreCase)
            && (rel.Length == 6 || rel[6] == Path.DirectorySeparatorChar);
    }

    private static string GetTemplatePath(string name)
    {
        // 文件名安全化
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (safe.Length == 0) safe = "template";
        return Path.Combine(TemplatesDir, safe + ".json");
    }
}
