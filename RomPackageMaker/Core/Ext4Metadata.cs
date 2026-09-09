using System.Text.Json;

namespace RomPackageMaker.Core;

/// <summary>ext4 单个文件的元数据（解包时导出，打包时还原）。</summary>
public sealed class Ext4FileMeta
{
    /// <summary>相对分区根的 POSIX 路径（'/' 分隔，已做 Windows 文件名消毒）。</summary>
    public string Path { get; set; } = string.Empty;
    /// <summary>完整 mode（含文件类型位）。</summary>
    public uint Mode { get; set; }
    public uint Uid { get; set; }
    public uint Gid { get; set; }
    public uint Atime { get; set; }
    public uint Ctime { get; set; }
    public uint Mtime { get; set; }
    /// <summary>符号链接目标（仅符号链接非 null）。</summary>
    public string? SymlinkTarget { get; set; }
    /// <summary>设备号原始 32 位值（仅字符/块设备）。</summary>
    public uint Rdev { get; set; }
    /// <summary>扩展属性（SELinux 标签、capabilities、POSIX ACL 等）。</summary>
    public List<Ext4Xattr> Xattrs { get; set; } = new();
}

public sealed class Ext4Xattr
{
    /// <summary>完整名称（含前缀，如 security.selinux）。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>值的 Base64（原样保留，不解释语义）。</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// ext4 元数据清单：解包时写入 .rom_metadata.json，打包时回读以还原
/// 权限 / 属主 / 时间戳 / 符号链接 / 设备节点 / SELinux xattr。
/// </summary>
public static class Ext4Metadata
{
    private const string FileName = ".rom_metadata.json";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static List<Ext4FileMeta> Load(string dir)
    {
        string path = Path.Combine(dir, FileName);
        if (!File.Exists(path)) return new List<Ext4FileMeta>();
        try
        {
            return JsonSerializer.Deserialize<List<Ext4FileMeta>>(File.ReadAllText(path), Options) ?? new();
        }
        catch (JsonException)
        {
            return new List<Ext4FileMeta>();
        }
    }

    public static void Save(string dir, List<Ext4FileMeta> metadata) =>
        File.WriteAllText(Path.Combine(dir, FileName), JsonSerializer.Serialize(metadata, Options));
}
