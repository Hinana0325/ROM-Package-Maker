using RomPackageMaker.Engine;
using System.Text;
// 真机样本验证台：直接用 RomPackageMaker 服务层解析真实 ROM 镜像，
// 重点验证规范符合性（boot 头字段 / AVB / super 元数据）与往返字节一致性。
// 用法: RealTest <boot|avb|super|all> <路径>

if (args.Length < 2)
{
    Console.WriteLine("用法: RealTest <boot|avb|super|all> <文件或目录路径>");
    return 1;
}

string cmd = args[0];
string path = args[1];

return cmd switch
{
    "boot" => BootTest(path),
    "avb" => AvbTest(path),
    "super" => SuperTest(path),
    "ext4" => Ext4Test(path),
    "all" => AllTest(path),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("未知命令。可用: boot / avb / super / all");
    return 1;
}

static int BootTest(string path)
{
    byte[] orig = File.ReadAllBytes(path);
    Console.WriteLine($"── boot 解析: {Path.GetFileName(path)} ({orig.Length:N0} 字节)");

    using var ms = new MemoryStream(orig);
    var info = BootImage.Parse(ms);

    Console.WriteLine($"  魔数类型     : {(info.IsVendorBoot ? "vendor_boot" : "boot")}");
    Console.WriteLine($"  头部版本     : v{info.HeaderVersion}");
    Console.WriteLine($"  页大小       : {info.PageSize}");
    Console.WriteLine($"  os_version   : 0x{info.OsVersion:X8}");
    Console.WriteLine($"  name         : '{info.Name}'");
    Console.WriteLine($"  cmdline      : '{info.Cmdline}'");
    if (info.ExtraCmdline.Length > 0)
        Console.WriteLine($"  extra_cmdline: '{info.ExtraCmdline}'");
    Console.WriteLine($"  kernel       : {info.KernelSize:N0} 字节 (实际 {info.Kernel.Length:N0})");
    Console.WriteLine($"  ramdisk      : {info.RamdiskSize:N0} 字节 (实际 {info.Ramdisk.Length:N0})");
    if (info.DtSize > 0) Console.WriteLine($"  dt (v0)      : {info.DtSize:N0}");
    if (info.RecoveryDtboSize > 0) Console.WriteLine($"  recovery_dtbo: {info.RecoveryDtboSize:N0}");
    if (info.DtbSize > 0) Console.WriteLine($"  dtb (v2)     : {info.DtbSize:N0}");
    if (info.VendorRamdiskSize > 0) Console.WriteLine($"  vendor_ramdisk: {info.VendorRamdiskSize:N0}");
    Console.WriteLine($"  Trailer      : {info.Trailer.Length:N0} 字节");

    // 往返：解析 → 重写 → 与原文件逐字节比较
    using var outMs = new MemoryStream();
    BootImage.Write(info, outMs);
    byte[] rebuilt = outMs.ToArray();

    bool identical = rebuilt.Length == orig.Length && rebuilt.AsSpan().SequenceEqual(orig);
    Console.WriteLine(identical
        ? "  ✔ 往返字节完全一致"
        : $"  ✘ 往返不一致（原 {orig.Length:N0} → 新 {rebuilt.Length:N0}）");

    if (!identical)
    {
        int n = Math.Min(orig.Length, rebuilt.Length);
        int off = 0;
        while (off < n && orig[off] == rebuilt[off]) off++;
        Console.WriteLine($"    首个差异 @0x{off:X} ({off})：原 0x{(off < orig.Length ? orig[off] : 0):X2} → 新 0x{(off < rebuilt.Length ? rebuilt[off] : 0):X2}");
        int diffCount = 0;
        var spots = new List<int>();
        for (int i = 0; i < n; i++)
            if (orig[i] != rebuilt[i]) { diffCount++; if (spots.Count < 24) spots.Add(i); }
        diffCount += Math.Abs(orig.Length - rebuilt.Length);
        Console.WriteLine($"    差异字节总数: {diffCount:N0}");
        Console.WriteLine("    差异位置: " + string.Join(" ", spots.Select(i =>
            $"@{i}(0x{orig[i]:X2}→0x{rebuilt[i]:X2})")));
    }

    // 字段级再解析校验
    using var reMs = new MemoryStream(rebuilt);
    var re = BootImage.Parse(reMs);
    bool fieldsOk = re.HeaderVersion == info.HeaderVersion && re.PageSize == info.PageSize
        && re.OsVersion == info.OsVersion && re.Name == info.Name
        && re.Cmdline == info.Cmdline && re.ExtraCmdline == info.ExtraCmdline
        && re.KernelSize == info.KernelSize && re.RamdiskSize == info.RamdiskSize;
    Console.WriteLine(fieldsOk ? "  ✔ 重解析字段一致" : "  ✘ 重解析字段不一致");

    return identical && fieldsOk ? 0 : 1;
}

static int AvbTest(string path)
{
    var e = AvbService.ReadEntry(path);
    Console.WriteLine($"── AVB: {Path.GetFileName(path)} ({e.FileSize:N0} 字节)");
    Console.WriteLine($"  vbmeta      : {e.IsVbmeta}");
    Console.WriteLine($"  sparse      : {e.IsSparse}");
    Console.WriteLine($"  含校验页脚  : {e.HasFooter}" + (e.HasFooter ? $"（原像 {e.OriginalImageSize:N0}）" : ""));
    if (e.IsVbmeta)
    {
        Console.WriteLine($"  flags       : 0x{e.Flags:X8}（verification_disabled={e.VerificationDisabled}, hashtree_disabled={e.HashtreeDisabled}）");
        Console.WriteLine($"  release     : '{e.ReleaseString}'");
    }
    return 0;
}

static int Ext4Test(string path)
{
    Console.WriteLine($"── ext4: {Path.GetFileName(path)} ({new FileInfo(path).Length:N0} 字节)");
    Stream target = File.OpenRead(path);
    if (SparseImage.IsSparse(target))
    {
        var capped = new CappedMemoryStream(1024 * 1024 * 1024); // 1GB 内存上限
        Console.WriteLine("  （sparse 容器，先在内存中解 sparse…）");
        try { SparseImage.Unsparse(target, capped); }
        catch (OverflowException ex) { Console.WriteLine($"  ✘ Unsparse 抛异常: {ex.Message}"); return 1; }
        target.Dispose();
        target = capped;
        Console.WriteLine($"  解 sparse 后 {capped.Length:N0} 字节");
    }

    target.Position = 0;
    using var reader = new Ext4Reader(target);
    Console.WriteLine($"  块大小={reader.BlockSize} inode数={reader.InodeCount:N0} 块数={reader.BlockCount:N0} inode大小={reader.InodeSize}");
    Console.WriteLine($"  每组inode={reader.InodesPerGroup} 每组块={reader.BlocksPerGroup} 首数据块={reader.FirstDataBlock} desc大小={reader.DescSize}");
    Console.WriteLine($"  feature_incompat=0x{reader.FeatureIncompat:X8}");

    string outDir = Path.Combine(Path.GetTempPath(), "romrealtest_ext4", Path.GetFileNameWithoutExtension(path));
    if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
    reader.ExtractTo(outDir);
    int files = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Count();
    int dirs = Directory.GetDirectories(outDir, "*", SearchOption.AllDirectories).Count();
    long bytes = new DirectoryInfo(outDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
    Console.WriteLine($"  ✔ 提取完成: {dirs} 个目录, {files} 个文件, 共 {bytes:N0} 字节");
    foreach (var f in new DirectoryInfo(outDir).EnumerateFiles("*", SearchOption.AllDirectories).OrderByDescending(f => f.Length).Take(8))
        Console.WriteLine($"      {f.FullName[outDir.Length..],-60} {f.Length,12:N0}");
    return files > 0 ? 0 : 1;
}

static int SuperTest(string path)
{
    Console.WriteLine($"── super 解析: {Path.GetFileName(path)} ({new FileInfo(path).Length:N0} 字节)");
    using var fs = File.OpenRead(path);

    // super.img 常以 sparse 形式分发，需先解 sparse（元数据只在前 1MB，故内存截断即可）
    Stream target = fs;
    var capped = new CappedMemoryStream(4 * 1024 * 1024);
    if (SparseImage.IsSparse(fs))
    {
        Console.WriteLine("  （检测到 sparse 容器，先解 sparse…）");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        SparseImage.Unsparse(fs, capped);
        Console.WriteLine($"  解 sparse 完成，保留前 {capped.Length:N0} 字节，耗时 {sw.Elapsed.TotalSeconds:0.0}s");
        target = capped;
    }

    target.Position = 0;
    SuperImage.SuperMetadata md = SuperImage.Parse(target);
    Console.WriteLine($"  主/次版本     : {md.MajorVersion}.{md.MinorVersion}");
    Console.WriteLine($"  逻辑块大小    : {md.LogicalBlockSize}");
    Console.WriteLine($"  元数据槽数    : {md.MetadataSlotCount}");
    Console.WriteLine($"  分区组        : {string.Join(", ", md.Groups.Select(g => g.Name))}");
    Console.WriteLine($"  逻辑分区数    : {md.Partitions.Count}");
    foreach (var p in md.Partitions)
    {
        Console.WriteLine($"    {p.Name,-20} {p.TotalSize / 1048576.0,9:0.##} MB  extents={p.Extents.Count}  group={p.GroupIndex}");
    }
    return md.Partitions.Count > 0 ? 0 : 1;
}

static int AllTest(string dir)
{
    var files = Directory.GetFiles(dir, "*.img").OrderBy(f => f).ToList();
    Console.WriteLine($"── 目录扫描: {dir}（{files.Count} 个 .img）\n");
    int fail = 0;

    foreach (var f in files)
    {
        var fi = new FileInfo(f);
        byte[] head = new byte[Math.Min(8, fi.Length)];
        using (var fs = File.OpenRead(f)) { fs.ReadExactly(head, 0, head.Length); }

        string kind = "其他";
        if (Encoding.ASCII.GetString(head, 0, Math.Min(8, head.Length)).StartsWith("ANDROID!")) kind = "boot";
        else if (Encoding.ASCII.GetString(head, 0, Math.Min(8, head.Length)).StartsWith("VNDRBOOT")) kind = "vendor_boot";
        else if (Encoding.ASCII.GetString(head, 0, Math.Min(4, head.Length)) == "AVB0") kind = "vbmeta";
        else if (BitConverter.ToUInt32(head, 0) == 0xED26FF3A) kind = "sparse";

        Console.WriteLine($"[{kind,-11}] {Path.GetFileName(f),-28} {fi.Length,14:N0}");
        try
        {
            if (kind is "boot" or "vendor_boot") fail += BootTest(f) == 0 ? 0 : 1;
            else if (kind == "vbmeta") AvbTest(f);
            else
            {
                var e = AvbService.ReadEntry(f);
                if (e.HasFooter) Console.WriteLine($"                含 AVB 校验页脚（原像 {e.OriginalImageSize:N0}）");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"                ! 处理失败: {ex.Message}");
            fail++;
        }
        Console.WriteLine();
    }

    Console.WriteLine(fail == 0 ? "全部通过" : $"{fail} 项失败");
    return fail == 0 ? 0 : 1;
}

/// <summary>只保留前 N 字节、其余丢弃的只写流（用于大镜像解 sparse 的抽样验证）。</summary>
internal sealed class CappedMemoryStream : Stream
{
    private readonly MemoryStream _buf;
    private readonly int _cap;
    private long _written;
    public CappedMemoryStream(int cap) { _cap = cap; _buf = new MemoryStream(cap); }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => Math.Min(_written, _cap);
    public override long Position
    {
        get => Math.Min(_buf.Position, Length);
        set => _buf.Position = Math.Min(value, _cap);
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        long room = _cap - _buf.Position;
        if (room > 0) _buf.Write(buffer, offset, (int)Math.Min(count, room));
        _written += count;
    }
    public override int Read(byte[] buffer, int offset, int count) => _buf.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin)
    {
        long p = origin switch
        {
            SeekOrigin.End => Length + offset,
            SeekOrigin.Current => _buf.Position + offset,
            _ => offset,
        };
        _buf.Position = Math.Min(p, _cap);
        return _buf.Position;
    }
    public override void Flush() { }
    public override void SetLength(long value) { }
}
