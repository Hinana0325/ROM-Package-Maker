using System.Text;
using System.Text.RegularExpressions;

namespace RomPackageMaker.Engine;

/// <summary>build.prop 中的一行：属性、注释或空行。</summary>
public sealed class BuildPropLine
{
    /// <summary>是否为 key=value 属性行。</summary>
    public bool IsProperty { get; set; }

    /// <summary>原始行文本。未修改的行保存时沿用，避免破坏原格式。</summary>
    public string Raw { get; set; } = string.Empty;

    /// <summary>解析瞬间的标准 key=value 形式，作为修改判断基线（独立于原始行格式）。</summary>
    public string Original { get; set; } = string.Empty;

    /// <summary>属性键（仅属性行有效）。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>属性值（仅属性行有效，可编辑）。</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>是否为本次会话新增的行。</summary>
    public bool IsNew { get; set; }

    /// <summary>该属性键的中文说明（无则为空串），供列表显示。</summary>
    public string Description => IsProperty ? BuildPropService.Describe(Key) ?? string.Empty : string.Empty;

    /// <summary>值是否有未保存的修改（与解析基线不一致；不依赖原始行格式）。</summary>
    public bool IsDirty => IsProperty && (IsNew || Original != $"{Key}={Value}");
}

/// <summary>build.prop 解析、序列化与常见属性说明。</summary>
public static class BuildPropService
{
    // 匹配 "key = value"：= 前不允许出现 # 或空白开头；键内不允许空行
    private static readonly Regex PropRegex = new(@"^[ \t]*([^#=\s][^=]*?)[ \t]*=[ \t]*(.*)$", RegexOptions.Compiled);

    /// <summary>常见属性中文说明，用于编辑器提示。</summary>
    private static readonly Dictionary<string, string> KnownProps = new()
    {
        ["ro.product.model"] = "设备型号（部分系统界面显示）",
        ["ro.product.brand"] = "设备品牌",
        ["ro.product.device"] = "设备代号（影响 OTA 与机型的核心标识）",
        ["ro.product.name"] = "产品名",
        ["ro.product.manufacturer"] = "制造商",
        ["ro.build.id"] = "构建 ID",
        ["ro.build.display.id"] = "版本号（设置中显示的 ROM 版本）",
        ["ro.build.version.incremental"] = "版本增量号（OTA 校验用，改 ROM 常需同步修改）",
        ["ro.build.version.release"] = "Android 大版本号",
        ["ro.build.version.sdk"] = "Android SDK 版本号（API 级别）",
        ["ro.build.version.security_patch"] = "安全补丁级别（如 2024-05-05）",
        ["ro.build.fingerprint"] = "构建指纹（OTA 校验用）",
        ["ro.build.date"] = "构建日期",
        ["ro.build.date.utc"] = "构建日期（Unix 时间戳）",
        ["ro.build.tags"] = "构建标签（release-keys 表示正式签名）",
        ["ro.build.type"] = "构建类型（user / userdebug / eng）",
        ["ro.build.host"] = "构建主机名",
        ["ro.build.user"] = "构建用户",
        ["ro.product.cpu.abi"] = "CPU ABI（64 位设备第一位为 arm64-v8a）",
        ["ro.product.cpu.abilist"] = "支持的 ABI 列表",
        ["ro.debuggable"] = "是否可调试（1 开启 root adb 调试，注意安全风险）",
        ["ro.secure"] = "是否安全模式（0 允许 root shell，注意安全风险）",
        ["ro.adb.secure"] = "adb 是否要求 RSA 授权",
        ["ro.treble.enabled"] = "是否启用 Project Treble",
        ["ro.sf.lcd_density"] = "屏幕像素密度（dpi），影响 UI 缩放",
        ["ro.hardware"] = "硬件平台名",
        ["ro.bootmode"] = "启动模式",
        ["persist.sys.timezone"] = "系统默认时区（如 Asia/Shanghai）",
        ["persist.sys.language"] = "默认语言（已弃用，多由系统设置管理）",
        ["persist.sys.usb.config"] = "默认 USB 模式（如 mtp,adb）",
        ["persist.vendor.usb.config"] = "vendor 侧默认 USB 模式",
        ["ro.config.ringtone"] = "默认铃声",
        ["ro.config.notification_sound"] = "默认通知音",
        ["ro.config.alarm_alert"] = "默认闹钟音",
        ["ro.opa.eligible_device"] = "是否启用 Google Assistant",
        ["dalvik.vm.heapsize"] = "Dalvik/ART 堆大小",
        ["dalvik.vm.heapstartsize"] = "ART 初始堆大小",
        ["ro.zygote"] = "zygote 模式（zygote64_32 表示 64+32 位）",
        ["keyguard.no_require_sim"] = "锁屏是否跳过 SIM 卡校验",
        ["ro.storage_layout_type"] = "存储布局类型",
    };

    /// <summary>查询属性键的中文说明，无则返回 null。</summary>
    public static string? Describe(string key) =>
        KnownProps.TryGetValue(key, out var desc) ? desc : null;

    /// <summary>解析 build.prop 文本。保留注释、空行与原始顺序。</summary>
    public static List<BuildPropLine> Parse(string text)
    {
        var result = new List<BuildPropLine>();
        string normalized = text.Replace("\r\n", "\n");
        var parts = normalized.Split('\n');
        // 结尾换行符产生的末尾空元素不单独成行，序列化时会自动补回
        if (parts.Length > 0 && parts[^1].Length == 0 && normalized.EndsWith('\n'))
        {
            parts = parts[..^1];
        }
        foreach (var raw in parts)
        {
            if (raw.Length == 0)
            {
                result.Add(new BuildPropLine());
                continue;
            }
            var m = PropRegex.Match(raw);
            if (m.Success)
            {
                string key = m.Groups[1].Value;
                string value = m.Groups[2].Value;
                result.Add(new BuildPropLine
                {
                    IsProperty = true,
                    Raw = raw,
                    Key = key,
                    Value = value,
                    Original = key + "=" + value,
                });
            }
            else
            {
                result.Add(new BuildPropLine { Raw = raw });
            }
        }
        return result;
    }

    /// <summary>序列化回 build.prop 文本。未修改的行沿用原文，修改/新增行输出标准 key=value。换行符固定为 \n（Android 要求）。</summary>
    public static string Serialize(IReadOnlyList<BuildPropLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.IsDirty)
                sb.Append(line.Key).Append('=').Append(line.Value).Append('\n');
            else
                sb.Append(line.Raw).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>统计未保存的修改数。</summary>
    public static int CountDirty(IReadOnlyList<BuildPropLine> lines) =>
        lines.Count(l => l.IsDirty);

    /// <summary>合并工作区内所有 build.prop 候选为键值字典。system 分区最后读（同键覆盖），与 ROM 实际加载语义一致。</summary>
    public static Dictionary<string, string> CollectProps(string rootDir)
    {
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidate in FindCandidates(rootDir).OrderBy(IsSystemCandidate))
        {
            List<BuildPropLine> lines;
            try { lines = Parse(File.ReadAllText(candidate)); }
            catch (IOException) { continue; }
            foreach (var line in lines)
            {
                if (line.IsProperty) props[line.Key] = line.Value;
            }
        }
        return props;
    }

    private static int IsSystemCandidate(string path) =>
        string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "system", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

    /// <summary>在目录中查找 build.prop 候选：分区解包目录（&lt;dir&gt;/&lt;分区&gt;/build.prop）与 zip 解包目录（&lt;dir&gt;/_zip/**/build.prop）。</summary>
    public static List<string> FindCandidates(string rootDir)
    {
        var found = new List<string>();
        try
        {
            // 分区镜像（ext4 等）直接解到 <root>/<分区>/ 根下
            foreach (var dir in Directory.EnumerateDirectories(rootDir))
            {
                var p = Path.Combine(dir, "build.prop");
                if (File.Exists(p)) found.Add(p);
            }
            // zip 刷机包解到 _zip/，保留原目录层级
            var zipDir = Path.Combine(rootDir, "_zip");
            if (Directory.Exists(zipDir))
            {
                found.AddRange(Directory.EnumerateFiles(zipDir, "build.prop", SearchOption.AllDirectories));
            }
        }
        catch (Exception)
        {
            // 目录不可访问等：返回已找到的部分
        }
        return found;
    }
}
