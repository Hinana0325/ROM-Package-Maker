using System.Buffers.Binary;
using System.Text;

namespace RomPackageMaker.Services;

/// <summary>工作区内 vbmeta / AVB 页脚的检测结果。注意：属性不能使用 init（会破坏 XAML 类型信息生成）。</summary>
public sealed class AvbEntry
{
    public string Path { get; set; } = string.Empty;
    public bool IsVbmeta { get; set; }
    public bool IsSparse { get; set; }
    public bool HasFooter { get; set; }
    public uint Flags { get; set; }
    public string ReleaseString { get; set; } = string.Empty;
    public long OriginalImageSize { get; set; }
    public long FileSize { get; set; }

    public bool VerificationDisabled => (Flags & AvbService.FlagVerificationDisabled) != 0;
    public bool HashtreeDisabled => (Flags & AvbService.FlagHashtreeDisabled) != 0;
}

/// <summary>
/// AVB（Android Verified Boot）/ dm-verity 处理。
/// 修改 system 等分区后，镜像内容与 vbmeta 记录的哈希不再匹配，设备将无法通过 AVB 校验启动。
/// 处理方式：
///  1. 在 vbmeta 中设置禁用标志（flags |= 3，等价于 fastboot --disable-verity --disable-verification）；
///  2. 截断镜像末尾追加的校验页脚与哈希树数据（AvbFooter，镜像最后 64 字节）。
/// </summary>
public static class AvbService
{
    /// <summary>AVB_VBMETA_IMAGE_FLAGS_HASHTREE_DISABLED：禁用 dm-verity 哈希树。</summary>
    public const uint FlagHashtreeDisabled = 1;

    /// <summary>AVB_VBMETA_IMAGE_FLAGS_VERIFICATION_DISABLED：禁用引导验证。</summary>
    public const uint FlagVerificationDisabled = 2;

    // AvbVBMetaImageHeader（libavb）字段偏移：总长 256 字节
    private const int VbmetaHeaderSize = 256;
    private const int VbmetaFlagsOffset = 120;
    private const int VbmetaReleaseOffset = 128;
    private const int VbmetaReleaseSize = 48;

    // AvbFooter（镜像末尾 64 字节）
    private const int FooterSize = 64;
    private const int FooterOriginalSizeOffset = 12;

    private static readonly byte[] VbmetaMagic = "AVB0"u8.ToArray();
    private static readonly byte[] FooterMagic = "AVBf"u8.ToArray();

    /// <summary>读取单个镜像文件的 AVB 信息（vbmeta / sparse / 校验页脚 / 普通）。</summary>
    public static AvbEntry ReadEntry(string path)
    {
        var fi = new FileInfo(path);
        using var fs = File.OpenRead(path);

        Span<byte> head = stackalloc byte[4];
        if (fs.Read(head) < 4)
        {
            return new AvbEntry { Path = path, FileSize = fi.Length };
        }

        // vbmeta：解析头
        if (head.SequenceEqual(VbmetaMagic))
        {
            var hdr = new byte[VbmetaHeaderSize];
            fs.Position = 0;
            if (fs.Read(hdr) < VbmetaHeaderSize)
            {
                return new AvbEntry { Path = path, FileSize = fi.Length };
            }
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(VbmetaFlagsOffset));
            string release = Encoding.ASCII.GetString(hdr, VbmetaReleaseOffset, VbmetaReleaseSize).TrimEnd('\0');
            return new AvbEntry { Path = path, IsVbmeta = true, Flags = flags, ReleaseString = release, FileSize = fi.Length };
        }

        // sparse 镜像：页脚在稀疏数据内部，不做截断处理
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) == SparseImage.SparseMagic)
        {
            return new AvbEntry { Path = path, IsSparse = true, FileSize = fi.Length };
        }

        // 校验页脚：末尾 64 字节 AVBf
        if (fi.Length >= FooterSize)
        {
            var footer = new byte[FooterSize];
            fs.Seek(-FooterSize, SeekOrigin.End);
            if (fs.Read(footer) == FooterSize && footer.AsSpan(0, 4).SequenceEqual(FooterMagic))
            {
                long original = (long)BinaryPrimitives.ReadUInt64LittleEndian(footer.AsSpan(FooterOriginalSizeOffset));
                return new AvbEntry { Path = path, HasFooter = true, OriginalImageSize = original, FileSize = fi.Length };
            }
        }

        return new AvbEntry { Path = path, FileSize = fi.Length };
    }

    /// <summary>在 vbmeta 中设置禁用验证与哈希树标志（flags |= 3，幂等）。</summary>
    public static void DisableVerification(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);

        Span<byte> magic = stackalloc byte[4];
        if (fs.Read(magic) < 4 || !magic.SequenceEqual(VbmetaMagic))
        {
            throw new InvalidDataException($"不是有效的 vbmeta 镜像：{path}");
        }

        Span<byte> flagsBuf = stackalloc byte[4];
        fs.Position = VbmetaFlagsOffset;
        if (fs.Read(flagsBuf) < 4)
        {
            throw new InvalidDataException("vbmeta 头读取失败。");
        }

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(flagsBuf);
        flags |= FlagHashtreeDisabled | FlagVerificationDisabled;
        BinaryPrimitives.WriteUInt32LittleEndian(flagsBuf, flags);
        fs.Position = VbmetaFlagsOffset;
        fs.Write(flagsBuf);
    }

    /// <summary>截断镜像的 AVB 校验页脚与追加的哈希树（恢复原像大小）。非 vbmeta、sparse 镜像或无页脚返回 false。</summary>
    public static bool StripAvbFooter(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);

        Span<byte> head = stackalloc byte[4];
        fs.Position = 0;
        if (fs.Read(head) == 4 && BinaryPrimitives.ReadUInt32LittleEndian(head) == SparseImage.SparseMagic)
        {
            return false; // sparse 镜像需先展开，跳过
        }

        if (fs.Length < FooterSize) return false;

        var footer = new byte[FooterSize];
        fs.Seek(-FooterSize, SeekOrigin.End);
        if (fs.Read(footer) < FooterSize) return false;
        if (!footer.AsSpan(0, 4).SequenceEqual(FooterMagic)) return false;

        long original = (long)BinaryPrimitives.ReadUInt64LittleEndian(footer.AsSpan(FooterOriginalSizeOffset));
        if (original <= 0 || original > fs.Length - FooterSize) return false;

        fs.SetLength(original);
        return true;
    }

    /// <summary>扫描工作区：所有分区目录中的镜像 + _zip 内的 vbmeta*.img。</summary>
    public static List<AvbEntry> ScanWorkspace(string workspaceDir)
    {
        var entries = new List<AvbEntry>();
        if (!Directory.Exists(workspaceDir)) return entries;

        // 分区目录（含 super 子镜像），跳过 _zip（单独处理）
        foreach (var dir in Directory.EnumerateDirectories(workspaceDir))
        {
            string name = Path.GetFileName(dir);
            if (name.Equals("_zip", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var img in Directory.EnumerateFiles(dir, "*.img"))
            {
                try { entries.Add(ReadEntry(img)); }
                catch (IOException) { /* 文件被占用，跳过 */ }
            }
        }

        // zip 原始结构中的 vbmeta（含 vbmeta_system 等）
        string zipDir = Path.Combine(workspaceDir, "_zip");
        if (Directory.Exists(zipDir))
        {
            foreach (var img in Directory.EnumerateFiles(zipDir, "vbmeta*.img", SearchOption.AllDirectories))
            {
                try { entries.Add(ReadEntry(img)); }
                catch (IOException) { /* 文件被占用，跳过 */ }
            }
        }

        return entries
            .OrderByDescending(e => e.IsVbmeta)
            .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>对工作区中全部 vbmeta 设置禁用标志，返回执行日志。</summary>
    public static List<string> DisableAllVerification(string workspaceDir)
    {
        var log = new List<string>();
        foreach (var e in ScanWorkspace(workspaceDir).Where(e => e.IsVbmeta))
        {
            if (e.VerificationDisabled && e.HashtreeDisabled)
            {
                log.Add($"已禁用，跳过：{e.Path}");
                continue;
            }
            DisableVerification(e.Path);
            log.Add($"已设置禁用标志：{e.Path}");
        }
        if (log.Count == 0) log.Add("未找到 vbmeta 镜像（需先解包含 vbmeta.img 的刷机包）。");
        return log;
    }

    /// <summary>截断工作区中全部带校验页脚的镜像，返回执行日志。</summary>
    public static List<string> StripAllFooters(string workspaceDir)
    {
        var log = new List<string>();
        foreach (var e in ScanWorkspace(workspaceDir).Where(e => e.HasFooter))
        {
            if (StripAvbFooter(e.Path))
            {
                log.Add($"已截断校验页脚（恢复至 {e.OriginalImageSize:N0} 字节）：{e.Path}");
            }
            else
            {
                log.Add($"跳过：{e.Path}");
            }
        }
        if (log.Count == 0) log.Add("未找到带 AVB 校验页脚的镜像。");
        return log;
    }
}
