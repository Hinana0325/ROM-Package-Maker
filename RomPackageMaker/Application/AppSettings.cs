using System.Text.Json;

namespace RomPackageMaker.Application;

/// <summary>
/// 应用设置持久化。将设置存储在 %APPDATA%\RomPackageMaker\appsettings.json 中，
/// 以便在应用重启后保留用户偏好。
/// 注意：非打包应用不可使用 Windows.Storage.ApplicationData（会 fail-fast 导致进程终止）。
/// </summary>
public sealed class AppSettings
{
    private static readonly string FileName = "appsettings.json";
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RomPackageMaker");

    private static AppSettings? _instance;
    public static AppSettings Current => _instance ??= Load();

    /// <summary>主题：system / light / dark</summary>
    public string Theme { get; set; } = "system";

    /// <summary>默认工作目录（可选）</summary>
    public string DefaultWorkspace { get; set; } = string.Empty;

    /// <summary>默认输出目录（可选）</summary>
    public string DefaultOutputDir { get; set; } = string.Empty;

    /// <summary>签名私钥路径（.pk8 或 .pfx），为空则使用内置测试证书</summary>
    public string SignKeyPath { get; set; } = string.Empty;

    /// <summary>签名证书路径（.x509.pem），pk8 模式下使用</summary>
    public string SignCertPath { get; set; } = string.Empty;

    /// <summary>sparse 镜像默认块大小</summary>
    public uint SparseBlockSize { get; set; } = 4096;

    /// <summary>打包完成后自动打开输出目录</summary>
    public bool OpenOutputAfterPack { get; set; } = false;

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            string path = Path.Combine(SettingsDir, FileName);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // 持久化失败不影响使用
        }
    }

    private static AppSettings Load()
    {
        try
        {
            string path = Path.Combine(SettingsDir, FileName);
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (s is not null) return s;
            }
        }
        catch
        {
            // 忽略读取失败，使用默认值
        }
        return new AppSettings();
    }
}
