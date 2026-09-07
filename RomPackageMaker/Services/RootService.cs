using System.IO;

namespace RomPackageMaker.Services;

/// <summary>ROOT 集成相关的应用模式。</summary>
public static class RootModes
{
    public const string None = "none";
    /// <summary>将 Magisk 管理器 APK 内置为系统应用。</summary>
    public const string MagiskSystem = "magisk-system";
    /// <summary>用已修补（含 Magisk/KernelSU）的 boot.img 替换原镜像。</summary>
    public const string ReplaceBoot = "replace-boot";
}

/// <summary>向工作目录集成 ROOT（Magisk / KernelSU）。</summary>
public static class RootService
{
    /// <summary>获取工作目录中 boot 类分区名清单（boot / vendor_boot / init_boot 等）。</summary>
    public static List<string> GetBootPartitions(string workspaceDir)
    {
        var manifest = RomManifest.Load(workspaceDir);
        return manifest.Partitions
            .Where(p => p.ImageType == "boot")
            .Select(p => p.Name)
            .ToList();
    }

    /// <summary>检测可用的 system 分区目录（工作区根或 super 嵌套下）。</summary>
    public static string? FindSystemDir(string workspaceDir)
    {
        foreach (var candidate in new[] { Path.Combine(workspaceDir, "system"), Path.Combine(workspaceDir, "super", "system") })
        {
            if (Directory.Exists(candidate) && Directory.EnumerateFileSystemEntries(candidate).Any())
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// 将 Magisk 管理器内置为系统应用（system/priv-app/Magisk/Magisk.apk）。
    /// 注意：仅内置管理器，root 本身仍需配合已修补的 boot.img（ReplaceBootImage）。
    /// </summary>
    public static void InstallMagiskManager(string workspaceDir, string magiskApkPath)
    {
        if (!File.Exists(magiskApkPath))
            throw new FileNotFoundException("Magisk APK 不存在。", magiskApkPath);

        string? systemDir = FindSystemDir(workspaceDir)
            ?? throw new InvalidOperationException("未找到已解包的 system 分区目录，无法内置 Magisk 管理器。");

        string destDir = Path.Combine(systemDir, "priv-app", "Magisk");
        Directory.CreateDirectory(destDir);
        File.Copy(magiskApkPath, Path.Combine(destDir, "Magisk.apk"), overwrite: true);
    }

    /// <summary>
    /// 用已修补的 boot.img（Magisk / KernelSU 修补产物）替换工作区中的 boot 类分区。
    /// 实现方式：清空分区目录中的 .img，放入修补镜像，并把分区标记为 raw 直通，
    /// 使打包阶段直接复制该镜像而不重建。
    /// </summary>
    /// <param name="partitionName">目标分区名（boot / vendor_boot / init_boot…）；null 表示第一个 boot 类分区；不存在时新建。</param>
    /// <returns>实际使用的分区名。</returns>
    public static string ReplaceBootImage(string workspaceDir, string patchedBootImg, string? partitionName)
    {
        if (!File.Exists(patchedBootImg))
            throw new FileNotFoundException("已修补的 boot.img 不存在。", patchedBootImg);

        var manifest = RomManifest.Load(workspaceDir);
        var bootParts = manifest.Partitions.Where(p => p.ImageType == "boot").ToList();

        PartitionManifest? target = null;
        if (bootParts.Count > 0)
        {
            target = partitionName is null
                ? bootParts[0]
                : bootParts.FirstOrDefault(p => string.Equals(p.Name, partitionName, StringComparison.OrdinalIgnoreCase));
        }

        bool createNew = false;
        if (target is null)
        {
            // 指定分区不存在或工作区没有 boot 类分区：新建分区条目
            string name = string.IsNullOrWhiteSpace(partitionName) ? "boot" : partitionName;
            target = new PartitionManifest { Name = name, ExtractedDir = name, OriginalImagePath = name + ".img" };
            manifest.Partitions.Add(target);
            createNew = true;
        }

        string partDir = Path.Combine(workspaceDir, target.ExtractedDir);
        Directory.CreateDirectory(partDir);

        // 清除已有 .img，确保打包时 raw 直通取到的是修补镜像
        foreach (var old in Directory.GetFiles(partDir, "*.img"))
        {
            File.Delete(old);
        }

        string destImg = Path.Combine(partDir, Path.GetFileName(patchedBootImg));
        if (!destImg.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
        {
            destImg += ".img";
        }
        File.Copy(patchedBootImg, destImg, overwrite: true);

        target.ImageType = "raw";
        target.WasSparse = false;
        target.OriginalSize = new FileInfo(destImg).Length;
        if (createNew)
        {
            // 新建分区无解析历史，记录来源
            target.Metadata = new Dictionary<string, string> { ["patched_from"] = Path.GetFileName(patchedBootImg) };
        }

        manifest.Save(workspaceDir);
        return target.Name;
    }
}
