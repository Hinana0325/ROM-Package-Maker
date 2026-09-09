using RomPackageMaker.Application;
using RomPackageMaker.Engine;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
namespace RomBenchmark;

/// <summary>
/// 轻量基准工具：测量关键路径的峰值内存 / GC Gen2 次数 / 耗时。
///
/// 用法（在解决方案根目录执行）：
///   dotnet run --project _benchmark -- prepare          # 预生成样本（一次性）
///   dotnet run --project _benchmark -- measure before   # 旧版基线（git archive 导出后运行）
///   dotnet run --project _benchmark -- measure after    # 当前版本
///
/// 设计要点：
///   - 样本预生成到 Benchmarks/data/（.gitignore 排除），测量进程只做被测路径，
///     基线在进程启动时取，避免样本生成的内存分配污染峰值。
///   - 同一份代码同时兼容流式（WriteCpio）与旧版（CreateCpio）实现，
///     便于在 git 旧版本上运行得到 Before 数据。
/// </summary>
internal static class Program
{
    private const long MB = 1024 * 1024;
    private static readonly string DataDir = Path.Combine("Benchmarks", "data");
    private static readonly string Ext4Img = Path.Combine(DataDir, "ext4_500mb.img");

    private static long _baselinePrivate;
    private static long _peakPrivate;
    private static long _peakManaged;
    private static CancellationTokenSource? _samplerCts;
    private static Task? _samplerTask;

    private static void Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0] : "measure";

        if (cmd == "prepare")
        {
            PrepareSamples();
            return;
        }
        if (cmd == "prepare-write")
        {
            PrepareWriteSamples();
            return;
        }
        if (cmd == "bench-write")
        {
            string writeMode = args.Length > 1 ? args[1] : "after";
            _baselinePrivate = Process.GetCurrentProcess().PrivateMemorySize64;
            Console.WriteLine($"=== ROM Benchmark write ({writeMode}) ===");
            Console.WriteLine();
            BenchmarkExt4Write(writeMode);
            Console.WriteLine();
            Console.WriteLine("Done.");
            return;
        }
        if (cmd == "analyze-selftest")
        {
            AnalyzeMoveSelfTest();
            return;
        }
        if (cmd == "analyze-payload")
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: dotnet run --project _benchmark -- analyze-payload <payload.bin|ota.zip>");
                return;
            }
            AnalyzePayloadFile(args[1]);
            return;
        }

        string mode = args.Length > 1 ? args[1] : "after";
        // 进程启动基线：此时只加载了运行时，尚无业务分配
        _baselinePrivate = Process.GetCurrentProcess().PrivateMemorySize64;

        Console.WriteLine($"=== ROM Benchmark ({mode}) ===");
        Console.WriteLine();

        try { BenchmarkExt4Extract(mode); }
        catch (Exception ex) { Console.WriteLine($"  ERROR: {ex.Message}"); }
        Console.WriteLine();

        try { BenchmarkRamdiskPack(mode); }
        catch (Exception ex) { Console.WriteLine($"  ERROR: {ex.Message}"); }

        Console.WriteLine();
        Console.WriteLine("Done.");
    }

    // ==================== MOVE 重叠分析 ====================

    private static void AnalyzeMoveSelfTest()
    {
        Console.WriteLine("=== PayloadMoveAnalyzer selftest (synthetic MOVE scenarios) ===");
        int pass = 0, fail = 0;

        void Check(string name, PayloadOperation op, MoveOverlapKind expected)
        {
            var kind = PayloadMoveAnalyzer.Classify(op, 1);
            bool ok = kind == expected;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}: expected={expected}, got={kind}");
            if (ok) pass++; else fail++;
        }

        // 场景 1：无重叠 src=[0,4) dst=[5,9)
        Check("no-overlap", MoveOp(0, 4, 5, 9), MoveOverlapKind.NoOverlap);

        // 场景 2：后向重叠 src=[0,6) dst=[3,9)：块3,4,5 写序<读序 → 不安全
        Check("overlap-backward-unsafe", MoveOp(0, 6, 3, 9), MoveOverlapKind.OverlapForwardUnsafe);

        // 场景 3：前向安全 src=[3,9) dst=[0,6)：块3,4,5 先读后写 → 安全
        Check("overlap-forward-safe", MoveOp(3, 9, 0, 6), MoveOverlapKind.OverlapForwardSafe);

        // 场景 4：包含重叠 src=[0,10) dst=[2,4) → 不安全
        Check("overlap-contain-unsafe", MoveOp(0, 10, 2, 4), MoveOverlapKind.OverlapForwardUnsafe);

        // 场景 5：多 extent 无重叠
        {
            var op = new PayloadOperation { Type = PayloadOpType.Move };
            op.SrcExtents.Add(new PayloadExtent { StartBlock = 0, NumBlocks = 2 });
            op.SrcExtents.Add(new PayloadExtent { StartBlock = 5, NumBlocks = 2 });
            op.DstExtents.Add(new PayloadExtent { StartBlock = 8, NumBlocks = 2 });
            op.DstExtents.Add(new PayloadExtent { StartBlock = 12, NumBlocks = 2 });
            Check("multi-extent-no-overlap", op, MoveOverlapKind.NoOverlap);
        }

        // 场景 6：多 extent 混合重叠 → 不安全
        {
            var op = new PayloadOperation { Type = PayloadOpType.Move };
            op.SrcExtents.Add(new PayloadExtent { StartBlock = 0, NumBlocks = 4 });
            op.SrcExtents.Add(new PayloadExtent { StartBlock = 8, NumBlocks = 4 });
            op.DstExtents.Add(new PayloadExtent { StartBlock = 2, NumBlocks = 4 });
            op.DstExtents.Add(new PayloadExtent { StartBlock = 10, NumBlocks = 4 });
            Check("multi-extent-overlap-unsafe", op, MoveOverlapKind.OverlapForwardUnsafe);
        }

        // 场景 7：相等区间平移（相邻，无重叠）：src=[0,4) dst=[4,8)
        Check("adjacent-no-overlap", MoveOp(0, 4, 4, 8), MoveOverlapKind.NoOverlap);

        Console.WriteLine($"  -> {pass} passed, {fail} failed");
    }

    private static PayloadOperation MoveOp(ulong srcStart, ulong srcEnd, ulong dstStart, ulong dstEnd)
    {
        var op = new PayloadOperation { Type = PayloadOpType.Move };
        op.SrcExtents.Add(new PayloadExtent { StartBlock = srcStart, NumBlocks = srcEnd - srcStart });
        op.DstExtents.Add(new PayloadExtent { StartBlock = dstStart, NumBlocks = dstEnd - dstStart });
        return op;
    }

    private static void AnalyzePayloadFile(string path)
    {
        string work = path;
        string? temp = null;
        try
        {
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Extracting payload.bin from {path} ...");
                temp = PayloadBinService.ExtractFromZip(path);
                work = temp!; // 未找到条目时 ExtractFromZip 抛异常，不会返回 null
            }

            Console.WriteLine($"Parsing {work} ...");
            var info = PayloadBinService.Parse(work);
            Console.WriteLine($"payload v{info.Version} · block size {info.BlockSize}");
            Console.WriteLine();
            Console.WriteLine("Partition | MOVE | TotalBytes | MaxSrc | MaxDst | NoOverlap | FwdSafe | FwdUnsafe |");
            Console.WriteLine("----------|------|------------|--------|--------|-----------|---------|-----------|");

            var all = PayloadMoveAnalyzer.Analyze(info);
            foreach (var s in all)
            {
                Console.WriteLine($"{s.PartitionName} | {s.MoveCount} | {s.TotalMoveBytes / 1024 / 1024}MB | " +
                    $"{s.MaxSrcBytes / 1024 / 1024}MB | {s.MaxDstBytes / 1024 / 1024}MB | {s.NoOverlapCount} | " +
                    $"{s.OverlapSafeCount} | {s.OverlapUnsafeCount} |");
            }

            int totalUnsafe = all.Sum(s => s.OverlapUnsafeCount);
            Console.WriteLine();
            Console.WriteLine(totalUnsafe == 0
                ? "=> 所有 MOVE 均可安全使用 chunk 前向复制（内存 O(chunk)）"
                : $"=> {totalUnsafe} 个 MOVE 存在前向不安全重叠，需要反向拷贝/分段缓冲/整读");
        }
        finally
        {
            if (temp != null && File.Exists(temp)) { try { File.Delete(temp); } catch { } }
        }
    }

    // ==================== 样本准备 ====================

    private static void PrepareSamples()
    {
        Directory.CreateDirectory(DataDir);

        // Ext4 镜像：含 500MB 大文件的镜像
        if (!File.Exists(Ext4Img))
        {
            Console.WriteLine("Generating ext4_500mb.img ...");
            string srcDir = Path.Combine(Path.GetTempPath(), "rom_bench_prep_src");
            if (Directory.Exists(srcDir)) Directory.Delete(srcDir, true);
            Directory.CreateDirectory(srcDir);
            string big = Path.Combine(srcDir, "big.apk");
            using (var fs = File.Create(big))
            {
                var pattern = new byte[1 << 20];
                new Random(42).NextBytes(pattern);
                long written = 0;
                while (written < 500L * MB)
                {
                    int n = (int)Math.Min(pattern.Length, 500L * MB - written);
                    fs.Write(pattern, 0, n);
                    written += n;
                }
            }
            using var imgFs = File.Create(Ext4Img);
            var w = new Ext4Writer();
            w.Build(srcDir, imgFs, null, CancellationToken.None);
            Directory.Delete(srcDir, true);
            Console.WriteLine($"  -> {Ext4Img} ({new FileInfo(Ext4Img).Length / MB} MB)");
        }

        // Ramdisk 目录：50 / 100 / 200 MB
        foreach (long size in new[] { 50L * MB, 100L * MB, 200L * MB })
        {
            string dir = Path.Combine(DataDir, $"ramdisk_{size / MB}mb");
            if (Directory.Exists(dir)) { Console.WriteLine($"  (exists) {dir}"); continue; }
            Console.WriteLine($"Generating {dir} ...");
            CreateRamdiskDir(dir, size);
            Console.WriteLine($"  -> {dir}");
        }
        Console.WriteLine("Samples ready.");
    }

    /// <summary>写入基准样本：3.8GB 单文件 + 1000 小文件（Build 出 ~3.9GB / 32 组镜像）。</summary>
    private static void PrepareWriteSamples()
    {
        Directory.CreateDirectory(DataDir);
        string srcDir = Path.Combine(DataDir, "ext4_write_src");
        string big = Path.Combine(srcDir, "big.bin");

        if (Directory.Exists(srcDir) && new FileInfo(big).Length >= 3800L * MB)
        {
            Console.WriteLine($"  (exists) {srcDir}");
            return;
        }

        if (Directory.Exists(srcDir)) Directory.Delete(srcDir, true);
        Directory.CreateDirectory(srcDir);

        Console.WriteLine("Generating ext4_write_src/big.bin (3.8GB) ...");
        var pattern = new byte[1 << 20];
        new Random(7).NextBytes(pattern);
        long target = 3800L * MB;
        using (var fs = File.Create(big))
        {
            long written = 0;
            while (written < target)
            {
                int n = (int)Math.Min(pattern.Length, target - written);
                fs.Write(pattern, 0, n);
                written += n;
            }
        }

        Console.WriteLine("Generating 1000 small files ...");
        string smallDir = Path.Combine(srcDir, "small");
        Directory.CreateDirectory(smallDir);
        for (int i = 0; i < 1000; i++)
        {
            File.WriteAllBytes(Path.Combine(smallDir, $"f{i:D4}.bin"), new byte[4096]);
        }
        Console.WriteLine($"  -> {srcDir}");
    }

    /// <summary>Ext4Writer.Build 峰值内存：inode 表驻留随组数线性增长（32 组 = 64MB Before）。</summary>
    private static void BenchmarkExt4Write(string mode)
    {
        string src = Path.Combine(DataDir, "ext4_write_src");
        if (!Directory.Exists(src))
        {
            Console.WriteLine("  (run prepare-write first)");
            return;
        }
        string outImg = Path.Combine(DataDir, "ext4_write_out.img");
        if (File.Exists(outImg)) File.Delete(outImg);

        long peakManaged = 0;
        var sw = Stopwatch.StartNew();
        using (var sink = new FileStream(outImg, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            using var sampler = new System.Threading.Timer(_ =>
            {
                long m = GC.GetTotalMemory(false);
                if (m > peakManaged) peakManaged = m;
            }, null, 0, 25);
            new Ext4Writer().Build(src, sink, null, CancellationToken.None);
        }
        sw.Stop();
        int gen2 = GC.CollectionCount(2);
        long size = new FileInfo(outImg).Length;

        Console.WriteLine($"  (image size: {size / MB} MB)");
        Console.WriteLine($"[{mode}] Ext4 Write 3.8GB dir (inode tables)");
        Console.WriteLine($"  Private Peak Delta: {(Process.GetCurrentProcess().PrivateMemorySize64 - _baselinePrivate) / MB} MB");
        Console.WriteLine($"  Managed Heap Peak:  {peakManaged / MB} MB");
        Console.WriteLine($"  Gen2 GC:            {gen2}");
        Console.WriteLine($"  Time:               {sw.Elapsed.TotalSeconds:0.#} s");
    }

    // ==================== 内存采样 ====================

    private static void StartSampler()
    {
        _peakPrivate = _baselinePrivate;
        _peakManaged = GC.GetTotalMemory(false);
        _samplerCts = new CancellationTokenSource();
        _samplerTask = Task.Run(() =>
        {
            var proc = Process.GetCurrentProcess();
            while (!_samplerCts.IsCancellationRequested)
            {
                long p = proc.PrivateMemorySize64;
                if (p > _peakPrivate) _peakPrivate = p;
                long m = GC.GetTotalMemory(false);
                if (m > _peakManaged) _peakManaged = m;
                Thread.Sleep(25);
            }
        });
    }

    private static void Report(string mode, string name, TimeSpan elapsed, long gen2Before)
    {
        _samplerCts?.Cancel();
        _samplerTask?.Wait(2000);
        long privateDelta = (_peakPrivate - _baselinePrivate) / MB;
        long managedPeak = _peakManaged / MB;
        long gen2Delta = GC.CollectionCount(2) - gen2Before;
        Console.WriteLine($"[{mode}] {name}");
        Console.WriteLine($"  Private Peak Delta: {privateDelta} MB");
        Console.WriteLine($"  Managed Heap Peak:  {managedPeak} MB");
        Console.WriteLine($"  Gen2 GC:            {gen2Delta}");
        Console.WriteLine($"  Time:               {elapsed.TotalSeconds:F1} s");
    }

    // ==================== 基准 1：Ext4 大文件提取 ====================

    private static void BenchmarkExt4Extract(string mode)
    {
        if (!File.Exists(Ext4Img))
        {
            Console.WriteLine("  (sample missing: run 'dotnet run --project _benchmark -- prepare' first)");
            return;
        }

        string outDir = Path.Combine(Path.GetTempPath(), "rom_bench_ext4_out");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);

        long gen2Before = GC.CollectionCount(2);
        var sw = Stopwatch.StartNew();
        StartSampler();
        using (var imgFs = File.OpenRead(Ext4Img))
        {
            var reader = new Ext4Reader(imgFs);
            reader.ExtractTo(outDir);
        }
        sw.Stop();

        string extracted = Path.Combine(outDir, "big.apk");
        Console.WriteLine($"  (verified output: {new FileInfo(extracted).Length:N0} bytes)");
        Report(mode, "Ext4 Extract 500MB APK", sw.Elapsed, gen2Before);

        Directory.Delete(outDir, true);
    }

    // ==================== 基准 2：Ramdisk 打包 ====================

    private static void BenchmarkRamdiskPack(string mode)
    {
        foreach (long size in new[] { 50L * MB, 100L * MB, 200L * MB })
        {
            string dir = Path.Combine(DataDir, $"ramdisk_{size / MB}mb");
            if (!Directory.Exists(dir))
            {
                Console.WriteLine($"  (sample missing: {dir})");
                continue;
            }

            long gen2Before = GC.CollectionCount(2);
            var sw = Stopwatch.StartNew();
            StartSampler();

            byte[] gzBytes;
            var writeCpio = typeof(RomPackService).GetMethod("WriteCpio", BindingFlags.NonPublic | BindingFlags.Static);
            if (writeCpio != null)
            {
                // 新实现：WriteCpio 边读边写进 GZipStream
                using var ms = new MemoryStream();
                using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                {
                    writeCpio.Invoke(null, new object[] { dir, gz });
                }
                gzBytes = ms.ToArray();
            }
            else
            {
                // 旧实现：CreateCpio 全量 byte[] 后压缩
                var createCpio = typeof(RomPackService).GetMethod("CreateCpio", BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new InvalidOperationException("WriteCpio/CreateCpio 均未找到");
                byte[] cpio = (byte[])createCpio.Invoke(null, new object[] { dir })!;
                using var ms = new MemoryStream();
                using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                {
                    gz.Write(cpio, 0, cpio.Length);
                }
                gzBytes = ms.ToArray();
            }

            sw.Stop();
            Console.WriteLine($"  (gzip size: {gzBytes.Length:N0} bytes)");
            Report(mode, $"Ramdisk Pack {size / MB}MB", sw.Elapsed, gen2Before);
        }
    }

    // ==================== 样本生成辅助 ====================

    /// <summary>构造真实感 ramdisk 目录：大量小文件 + 大文件填充到目标大小。</summary>
    private static void CreateRamdiskDir(string dir, long targetBytes)
    {
        Directory.CreateDirectory(dir);
        var rng = new Random(7);

        for (int i = 0; i < 600; i++)
        {
            int len = 256 + rng.Next(4096);
            string sub = i % 3 == 0 ? "etc" : i % 3 == 1 ? "lib/modules" : "bin";
            string path = Path.Combine(dir, sub, $"f{i}.cfg");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, RandomBytes(rng, len));
        }

        long remaining = targetBytes - GetDirSize(dir);
        int idx = 0;
        while (remaining > 8 * MB)
        {
            string path = Path.Combine(dir, "lib", $"blob{idx++}.so");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            int chunk = (int)Math.Min(8 * MB, remaining);
            File.WriteAllBytes(path, RandomBytes(rng, chunk));
            remaining -= chunk;
        }
        if (remaining > 0)
        {
            File.WriteAllBytes(Path.Combine(dir, "lib", "tail.bin"), RandomBytes(rng, (int)remaining));
        }
    }

    private static byte[] RandomBytes(Random rng, int len)
    {
        var buf = new byte[len];
        var pattern = new byte[64 * 1024];
        rng.NextBytes(pattern);
        for (int off = 0; off < len; off += pattern.Length)
        {
            int n = Math.Min(pattern.Length, len - off);
            Array.Copy(pattern, 0, buf, off, n);
        }
        return buf;
    }

    private static long GetDirSize(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
}
