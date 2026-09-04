using System.Text.Json;
using System.Text.Json.Serialization;

namespace RomPackageMaker.Services;

/// <summary>
/// 工作区清单，记录解包内容与原始结构，供打包阶段重建镜像。
/// </summary>
internal sealed class RomManifest
{
    public string SourceFile { get; set; } = string.Empty;
    public string SourceType { get; set; } = "zip"; // zip | image
    public List<PartitionManifest> Partitions { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static RomManifest Load(string workspaceDir)
    {
        string path = Path.Combine(workspaceDir, ".rom_manifest.json");
        if (!File.Exists(path)) return new RomManifest();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<RomManifest>(json, Options) ?? new RomManifest();
    }

    public void Save(string workspaceDir)
    {
        string path = Path.Combine(workspaceDir, ".rom_manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }
}

internal sealed class PartitionManifest
{
    /// <summary>分区名，如 system / vendor / boot。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>镜像类型：ext4 | boot | super | raw。</summary>
    public string ImageType { get; set; } = "raw";
    /// <summary>原始镜像是否为 sparse 格式。</summary>
    public bool WasSparse { get; set; }
    /// <summary>原始镜像路径（相对工作区）。</summary>
    public string OriginalImagePath { get; set; } = string.Empty;
    /// <summary>解包输出目录（相对工作区）。</summary>
    public string ExtractedDir { get; set; } = string.Empty;
    /// <summary>原始镜像总大小（字节）。</summary>
    public long OriginalSize { get; set; }
    /// <summary>boot 镜像的页大小等附加参数。</summary>
    public Dictionary<string, string>? Metadata { get; set; }
}
