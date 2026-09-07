namespace RomPackageMaker.Services;

/// <summary>build.prop 预设条目。</summary>
public sealed class PropPresetItem
{
    public string Key { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string Comment { get; init; } = string.Empty;
}

/// <summary>build.prop 预设组。</summary>
public sealed class PropPreset
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public List<PropPresetItem> Items { get; init; } = new();
}

/// <summary>常用 build.prop 参数预设。所有条目均为社区通用做法，效果因设备 / ROM 而异。</summary>
public static class BuildPropPresets
{
    public static List<PropPreset> All { get; } = new()
    {
        new PropPreset
        {
            Name = "开发者调试",
            Description = "开启 root 调试与无线 ADB，方便 ROM 开发调试（注意安全风险）",
            Items =
            {
                new PropPresetItem { Key = "ro.debuggable", Value = "1", Comment = "允许 adb root（userdebug 特性）" },
                new PropPresetItem { Key = "ro.adb.secure", Value = "0", Comment = "ADB 不鉴权（免弹窗授权，注意安全）" },
                new PropPresetItem { Key = "persist.sys.usb.config", Value = "mtp,adb", Comment = "USB 默认 MTP + ADB" },
                new PropPresetItem { Key = "service.adb.tcp.port", Value = "5555", Comment = "无线 ADB 端口" },
            },
        },
        new PropPreset
        {
            Name = "机型伪装（Pixel 8 示例）",
            Description = "将设备标识改为 Pixel 8，常用于 Google Play 认证 / 应用兼容性测试；改后注意与 GMS 一致性",
            Items =
            {
                new PropPresetItem { Key = "ro.product.model", Value = "Pixel 8", Comment = "设备型号" },
                new PropPresetItem { Key = "ro.product.device", Value = "husky", Comment = "设备代号（Pixel 8）" },
                new PropPresetItem { Key = "ro.product.brand", Value = "google", Comment = "品牌" },
                new PropPresetItem { Key = "ro.product.manufacturer", Value = "Google", Comment = "制造商" },
                new PropPresetItem { Key = "ro.product.name", Value = "husky", Comment = "产品名" },
            },
        },
        new PropPreset
        {
            Name = "流畅度优化",
            Description = "渲染缓存与过渡动画相关参数（社区常用，效果因设备而异）",
            Items =
            {
                new PropPresetItem { Key = "ro.hwui.texture_cache_size", Value = "72", Comment = "硬件渲染纹理缓存（MB）" },
                new PropPresetItem { Key = "ro.hwui.layer_cache_size", Value = "48", Comment = "图层缓存（MB）" },
                new PropPresetItem { Key = "ro.hwui.path_cache_size", Value = "32", Comment = "路径缓存（MB）" },
                new PropPresetItem { Key = "debug.sf.hw", Value = "1", Comment = "SurfaceFlinger GPU 合成" },
                new PropPresetItem { Key = "persist.sys.ui.hw", Value = "1", Comment = "UI 硬件加速" },
            },
        },
        new PropPreset
        {
            Name = "网络优化",
            Description = "TCP 缓冲区调优（社区常用，效果因运营商 / ROM 而异）",
            Items =
            {
                new PropPresetItem { Key = "net.tcp.buffersize.default", Value = "4096,87380,256960,4096,16384,256960", Comment = "默认 TCP 缓冲" },
                new PropPresetItem { Key = "net.tcp.buffersize.wifi", Value = "524288,1048576,2097152,262144,524288,1048576", Comment = "WiFi TCP 缓冲" },
                new PropPresetItem { Key = "net.tcp.buffersize.lte", Value = "524288,1048576,2097152,262144,524288,1048576", Comment = "LTE TCP 缓冲" },
            },
        },
        new PropPreset
        {
            Name = "省电流畅均衡",
            Description = "降低动画干扰与后台刷新频率（保守设置）",
            Items =
            {
                new PropPresetItem { Key = "ro.config.max_starting_bg_procs", Value = "8", Comment = "后台并发进程上限" },
                new PropPresetItem { Key = "persist.sys.shutdown.mode", Value = "hibernate", Comment = "关机模式" },
                new PropPresetItem { Key = "pm.sleep_mode", Value = "1", Comment = "睡眠模式电源管理" },
            },
        },
    };

    /// <summary>应用预设到已解析的行列表：已存在的键改值，不存在的键追加。返回 (修改数, 新增数)。</summary>
    public static (int Modified, int Added) Apply(List<BuildPropLine> lines, PropPreset preset)
    {
        int modified = 0, added = 0;
        foreach (var item in preset.Items)
        {
            var existing = lines.FirstOrDefault(l => l.IsProperty && l.Key == item.Key);
            if (existing is not null)
            {
                if (existing.Value != item.Value)
                {
                    existing.Value = item.Value;
                    modified++;
                }
            }
            else
            {
                lines.Add(new BuildPropLine
                {
                    IsProperty = true,
                    Key = item.Key,
                    Value = item.Value,
                    IsNew = true,
                });
                added++;
            }
        }
        return (modified, added);
    }
}
