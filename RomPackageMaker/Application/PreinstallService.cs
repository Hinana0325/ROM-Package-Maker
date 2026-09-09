using System.IO.Compression;

namespace RomPackageMaker.Application;

/// <summary>资源类型与目标路径映射。</summary>
public enum ResourceKind
{
    /// <summary>开机动画 → system/media/bootanimation.zip。</summary>
    Bootanimation,
    /// <summary>字体 → system/fonts/*.ttf。</summary>
    Font,
    /// <summary>铃声 → system/media/audio/ringtones/。</summary>
    Ringtone,
    /// <summary>通知音 → system/media/audio/notifications/。</summary>
    Notification,
    /// <summary>闹钟音 → system/media/audio/alarms/。</summary>
    Alarm,
}

/// <summary>应用预装与系统资源替换。</summary>
public static class PreinstallService
{
    /// <summary>预装一个 APK 到 system/app 或 system/priv-app（标准目录布局 &lt;Name&gt;/&lt;Name&gt;.apk）。返回目标路径。</summary>
    public static string AddApk(string workspaceDir, string apkPath, bool privileged)
    {
        string systemDir = RequireSystem(workspaceDir);
        string name = Path.GetFileNameWithoutExtension(apkPath);

        string appDir = Path.Combine(systemDir, privileged ? "priv-app" : "app", name);
        string dest = Path.Combine(appDir, name + ".apk");
        if (File.Exists(dest))
            throw new InvalidOperationException($"目标已存在：{Path.GetRelativePath(workspaceDir, dest)}（如需覆盖请先删除）");

        Directory.CreateDirectory(appDir);
        File.Copy(apkPath, dest);
        return dest;
    }

    /// <summary>替换系统资源（开机动画 / 字体 / 音频）。bootanimation 要求目标为合法 zip（含 desc.txt）。返回目标路径。</summary>
    public static string ReplaceResource(string workspaceDir, string sourcePath, ResourceKind kind)
    {
        string systemDir = RequireSystem(workspaceDir);
        string fileName = kind switch
        {
            ResourceKind.Bootanimation => "bootanimation.zip",
            _ => Path.GetFileName(sourcePath),
        };

        string dest = kind switch
        {
            ResourceKind.Bootanimation => Path.Combine(systemDir, "media", "bootanimation.zip"),
            ResourceKind.Font => Path.Combine(systemDir, "fonts", fileName),
            ResourceKind.Ringtone => Path.Combine(systemDir, "media", "audio", "ringtones", fileName),
            ResourceKind.Notification => Path.Combine(systemDir, "media", "audio", "notifications", fileName),
            ResourceKind.Alarm => Path.Combine(systemDir, "media", "audio", "alarms", fileName),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        if (kind == ResourceKind.Bootanimation) ValidateBootanimation(sourcePath);

        string? dir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.Copy(sourcePath, dest, overwrite: true);
        return dest;
    }

    /// <summary>校验 bootanimation.zip：必须是 zip 且包含 desc.txt（开机动画描述文件）。</summary>
    public static void ValidateBootanimation(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.All(e => e.Name != "desc.txt"))
                throw new InvalidDataException("bootanimation.zip 中未找到 desc.txt（不是有效的开机动画包）。");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"无法读取 bootanimation.zip：{ex.Message}", ex);
        }
    }

    private static string RequireSystem(string workspaceDir)
    {
        string systemDir = Path.Combine(workspaceDir, "system");
        if (!Directory.Exists(systemDir))
            throw new InvalidOperationException("工作目录中没有 system 分区目录（需先解包 system.img 或刷机包）。");
        return systemDir;
    }
}
