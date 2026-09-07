using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using RomPackageMaker.Services;

int fail = 0;
void Check(string name, bool cond)
{
    Console.WriteLine((cond ? "PASS" : "FAIL") + "  " + name);
    if (!cond) fail++;
}

string root = Path.Combine(Path.GetTempPath(), "rpm_e2e_" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
var progress = new Progress<RomTaskProgress>(_ => { });
var ct = CancellationToken.None;

// ==================== 1. 构造 ROM 材料 ====================

string sysSrc = Path.Combine(root, "sys_src");
string MakeDir(string rel) { string p = Path.Combine(sysSrc, rel.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(p); return p; }
MakeDir("app/Browser/lib/arm64-v8a");
MakeDir("priv-app/Shell");
File.WriteAllText(Path.Combine(sysSrc, "build.prop"),
    "# begin build properties\nro.product.model=OldModel\nro.product.device=TESTDEV\nro.debuggable=0\n# end\n");
File.WriteAllBytes(Path.Combine(sysSrc, "app/Browser/Browser.apk"), new byte[2048]);
File.WriteAllBytes(Path.Combine(sysSrc, "app/Browser/lib/arm64-v8a/libcrom.so"), new byte[512]);
File.WriteAllBytes(Path.Combine(sysSrc, "priv-app/Shell/Shell.apk"), new byte[1024]);

string systemImg = Path.Combine(root, "system.img");
using (var fs = File.Create(systemImg))
{
    new Ext4Writer().Build(sysSrc, fs, progress, ct);
}
Check("make: system.img non-empty", new FileInfo(systemImg).Length > 8192);

byte[] MakeTrailerCpio()
{
    var sb = new StringBuilder();
    sb.Append("070701");
    for (int i = 0; i < 12; i++) sb.Append("00000000");
    sb.Append("0000000B");
    sb.Append("00000000");
    byte[] header = Encoding.ASCII.GetBytes(sb.ToString());
    byte[] name = Encoding.ASCII.GetBytes("TRAILER!!!\0");
    using var ms = new MemoryStream();
    ms.Write(header);
    ms.Write(name);
    while (ms.Length % 4 != 0) ms.WriteByte(0);
    while (ms.Length % 512 != 0) ms.WriteByte(0);
    return ms.ToArray();
}
byte[] kernel = Encoding.ASCII.GetBytes("FAKEKERNEL" + new string('K', 2048));
byte[] ramdisk;
using (var ms = new MemoryStream())
{
    using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
    {
        gz.Write(MakeTrailerCpio());
    }
    ramdisk = ms.ToArray();
}
string bootImg = Path.Combine(root, "boot.img");
var bootInfo = new BootImage.BootInfo
{
    PageSize = 2048,
    Kernel = kernel,
    KernelSize = (uint)kernel.Length,
    Ramdisk = ramdisk,
    RamdiskSize = (uint)ramdisk.Length,
    Name = "test_boot",
    Cmdline = "console=ttyMSM0",
};
using (var fs = File.Create(bootImg))
{
    BootImage.Write(bootInfo, fs);
}

string vbmetaImg = Path.Combine(root, "vbmeta.img");
{
    var hdr = new byte[256];
    Encoding.ASCII.GetBytes("AVB0").CopyTo(hdr, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(28), 1);
    File.WriteAllBytes(vbmetaImg, hdr.Concat(new byte[64]).ToArray());
}

string romZip = Path.Combine(root, "rom.zip");
using (var za = ZipFile.Open(romZip, ZipArchiveMode.Create))
{
    za.CreateEntryFromFile(systemImg, "system.img");
    za.CreateEntryFromFile(bootImg, "boot.img");
    za.CreateEntryFromFile(vbmetaImg, "vbmeta.img");
}

// ==================== 2. 解包（第一轮） ====================

string ws1 = Path.Combine(root, "ws1");
var svc = new RomPackService();
await svc.UnpackAsync(romZip, ws1, progress, ct);

Check("unpack: system dir", Directory.Exists(Path.Combine(ws1, "system")));
Check("unpack: system/build.prop", File.Exists(Path.Combine(ws1, "system/build.prop")));
Check("unpack: boot dir", Directory.Exists(Path.Combine(ws1, "boot")));
Check("unpack: vbmeta raw copy", File.Exists(Path.Combine(ws1, "vbmeta/vbmeta.img")));
string prop1 = File.ReadAllText(Path.Combine(ws1, "system/build.prop"));
Check("unpack: prop roundtrip", prop1.Contains("ro.product.model=OldModel") && prop1.Contains("# begin build properties"));

// ==================== 3. 定制：build.prop + 精简 + AVB ====================

var propPath = Path.Combine(ws1, "system/build.prop");
var lines = BuildPropService.Parse(File.ReadAllText(propPath));
lines.First(l => l.IsProperty && l.Key == "ro.product.model").Value = "NewModel";
lines.Add(new BuildPropLine { IsProperty = true, Key = "ro.setupwizard.mode", Value = "OPTIONAL", IsNew = true });
File.WriteAllText(propPath, BuildPropService.Serialize(lines), new UTF8Encoding(false));

var apps = WorkspaceScanner.ScanApps(ws1);
Check("debloat: 2 apps found", apps.Count == 2 && apps.Any(a => a.Name == "Browser") && apps.Any(a => a.Name == "Shell"));
WorkspaceScanner.RemoveApp(ws1, apps.First(a => a.Name == "Browser"));
Check("debloat: Browser removed", !Directory.Exists(Path.Combine(ws1, "system/app/Browser")));
Check("debloat: Shell kept", File.Exists(Path.Combine(ws1, "system/priv-app/Shell/Shell.apk")));

var avbLog = AvbService.DisableAllVerification(ws1);
Check("avb: vbmeta patched (all)", avbLog.Count >= 1 && avbLog.All(l => l.Contains("禁用")));

// ==================== 4. 打包（签名） ====================

string outZip = Path.Combine(root, "out.zip");
await svc.PackAsync(ws1, outZip, progress, ct, sign: true);
Check("pack: zip created", File.Exists(outZip) && new FileInfo(outZip).Length > 0);

using (var za = ZipFile.OpenRead(outZip))
{
    var names = za.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
    Check("pack: has system/boot/vbmeta", names.Contains("system.img") && names.Contains("boot.img") && names.Contains("vbmeta.img"));
}

// ==================== 5. 再解包验证闭环 ====================

string ws2 = Path.Combine(root, "ws2");
await svc.UnpackAsync(outZip, ws2, progress, ct);

string prop2 = File.ReadAllText(Path.Combine(ws2, "system/build.prop"));
Check("e2e: prop modified", prop2.Contains("ro.product.model=NewModel"));
Check("e2e: prop added", prop2.Contains("ro.setupwizard.mode=OPTIONAL"));
Check("e2e: comment survives", prop2.Contains("# begin build properties") && prop2.Contains("# end"));
Check("e2e: untouched prop survives", prop2.Contains("ro.debuggable=0"));
Check("e2e: Browser gone", !Directory.Exists(Path.Combine(ws2, "system/app/Browser")));
Check("e2e: Shell survives", File.Exists(Path.Combine(ws2, "system/priv-app/Shell/Shell.apk")));

var vbEntry = AvbService.ReadEntry(Path.Combine(ws2, "vbmeta/vbmeta.img"));
Check("e2e: vbmeta flags=3", vbEntry.IsVbmeta && vbEntry.Flags == 3);

// boot kernel 往返一致（解包→重打包→再解包）
byte[] kernel1 = File.ReadAllBytes(Path.Combine(ws1, "boot/kernel"));
byte[] kernel2 = File.ReadAllBytes(Path.Combine(ws2, "boot/kernel"));
Console.WriteLine($"  [diag] orig={kernel.Length} ws1={kernel1.Length} ws2={kernel2.Length}");
Console.WriteLine($"  [diag] ws1==orig: {kernel1.AsSpan().SequenceEqual(kernel)}");
if (!kernel2.AsSpan().SequenceEqual(kernel))
{
    int fd = 0;
    while (fd < Math.Min(kernel2.Length, kernel.Length) && kernel2[fd] == kernel[fd]) fd++;
    Console.WriteLine($"  [diag] ws2 first diff @{fd}, ws2len={kernel2.Length}");
    Console.WriteLine($"  [diag] ws2 head: {Convert.ToHexString(kernel2, 0, Math.Min(16, kernel2.Length))}");
    Console.WriteLine($"  [diag] orig head: {Convert.ToHexString(kernel, 0, Math.Min(16, kernel.Length))}");
    string params1 = File.ReadAllText(Path.Combine(ws1, "boot/boot_params.json"));
    Console.WriteLine($"  [diag] boot_params.json(ws1): {params1[..Math.Min(200, params1.Length)]}");
}
Check("e2e: boot kernel roundtrip", kernel2.AsSpan().SequenceEqual(kernel));

Check("e2e: shell apk size", new FileInfo(Path.Combine(ws2, "system/priv-app/Shell/Shell.apk")).Length == 1024);

// ==================== 6. payload.bin 解析与提取 ====================

// --- protobuf 编码辅助（构造合成 manifest 用） ---
void WVarint(List<byte> b, ulong v) { do { byte x = (byte)(v & 0x7F); v >>= 7; if (v > 0) x |= 0x80; b.Add(x); } while (v > 0); }
void WVarintField(List<byte> b, int field, ulong v) { WVarint(b, (ulong)((field << 3) | 0)); WVarint(b, v); }
void WLenField(List<byte> b, int field, ReadOnlySpan<byte> payload) { WVarint(b, (ulong)((field << 3) | 2)); WVarint(b, (ulong)payload.Length); b.AddRange(payload.ToArray()); }
void WExtentField(List<byte> b, int field, ulong start, ulong num)
{ var e = new List<byte>(); WVarintField(e, 1, start); WVarintField(e, 2, num); WLenField(b, field, e.ToArray()); }
List<byte> Op(PayloadOpType type, ulong offset, ulong length, (ulong Start, ulong Num)? src, (ulong Start, ulong Num) dst)
{
    var m = new List<byte>();
    WVarintField(m, 1, (ulong)type);
    if (length > 0) { WVarintField(m, 2, offset); WVarintField(m, 3, length); }
    if (src is { } s) WExtentField(m, 4, s.Start, s.Num);
    WExtentField(m, 6, dst.Start, dst.Num);
    return m;
}

// --- 合成材料（XZ/BZ2 blob 由 python lzma/bz2 预生成，解压后各 4096B） ---
byte[] xzBlob = Convert.FromHexString("fd377a585a0000016922de360200210116000000742fe5a3e00fff01015d00000052500a84f99bb28021a969d627e03e065a5f048d53d404ba39570509c15524de9db871593160a19ff96f4973f2c8ea8cba1a8b29692180fe338366af466dec9e898a0b83f03c0e898e3fed5fe79e90d91cff32f4b2e03951b2d21415b4c571badb06e3799a9fbb38c1b000ac930baa0619031208155b9bc848f0322efe2da087c8f0a4e0d251eb8d675692b24d84c5f18631df6a625bc2792dd9f73c73ba747407d83ca9562224a166f85a845f3067d2f64b492e7f20ebdbf8100e947877c73f6befb4cd95e26ff6446e06cf0b821acbdb7af0578d98ff90c03ee6c1124175ee032896eb13fba728ccaf32bba40e25f258b0ded8561c66f0e21b355d8e9f5c00000000822091a20001990280200000ed30e8563e300d8b020000000001595a");
byte[] bzBlob = Convert.FromHexString("425a68393141592653598e5bdcd700001fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffd0035e00000024c00130001300000000000000000000000000000000000000000000000000000000126000980009800000000000000000000000000000000000000000000000000000000930004c0004c0000000000000000000000000000000000000000000000000000000049800260002600000000000000000000000000000000000000000000000000000000055554002600230004c0000000004c0000980000000000004c0000000000000000980000000c40800810820fb87e03f21fa086843821e1100fe0222112089845022a11608b846023211a08d87f21fd047023a11e08f8480242122091849024a12609384a03fb09484a825612c09684b825e13009884c826613409a84d826e13809c84e827613c09e84f827e1400a085082861440a2851828e1480a4852829614c0a6853829e1fe05402a21520a985502aa1560ab85602b215a0ad85702ba15e0af85802c21fe85882c61640b285982ce1680b485a82d616c0b685b82de1700b885c82e61740ba85d82ee1780bc85e82f617c0be85f82fe1800c086083061840c2861830e1880c4862831618c0c6863831e1900c886483261940ca87fc1960cb866033219a0cd867033a19e0cf86803421a20d1869034a1a60d386a03521aa0d586b035a1ae0d786c03621b20d986d036a1b60db86e03721ba0dd86f037a1be0df87003821c20e1871038a1c60e387203921ca0e5873039a1ce0e787403a21d20e987503aa1d60eb87fe1d80ec87683b61dc0ee87783be1e00f087883c61e40f287983ce1e80f487a83d61ec0f687b83de1f00f887c83e61f40fa87d82002042083ee1f80fc87e83f62ee48a70a1211cb7b9ae");
byte[] xzPlain = Enumerable.Range(0, 4096).Select(i => (byte)(i % 256)).ToArray();
byte[] bzPlain = Enumerable.Range(0, 4096).Select(i => (byte)((i * 7 + 3) % 256)).ToArray();
byte[] blockA = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
byte[] tinyData = Encoding.ASCII.GetBytes("TINYPARTITION!" + new string('t', 4081));

// 期望 testsys 镜像：A | xz | bz | 0 | A(MOVE自块0) | 0,0,0(DISCARD)
byte[] expectedSys = new byte[8 * 4096];
blockA.CopyTo(expectedSys, 0);
xzPlain.CopyTo(expectedSys, 4096);
bzPlain.CopyTo(expectedSys, 2 * 4096);
blockA.CopyTo(expectedSys, 4 * 4096);
byte[] sysHash = SHA256.HashData(expectedSys);

// manifest（proto2）
List<byte> BuildManifest(byte[]? hashOverride)
{
    var m = new List<byte>();
    WVarintField(m, 3, 4096); // block_size
    // partitions[0] testsys
    {
        var p = new List<byte>();
        WLenField(p, 1, Encoding.UTF8.GetBytes("testsys"));
        var pi = new List<byte>();
        WVarintField(pi, 1, (ulong)expectedSys.Length);
        WLenField(pi, 2, hashOverride ?? sysHash);
        WLenField(p, 7, pi.ToArray()); // new_partition_info
        WLenField(p, 8, Op(PayloadOpType.Replace, 0, 4096, null, (0, 1)).ToArray());
        WLenField(p, 8, Op(PayloadOpType.ReplaceXz, 4096, (ulong)xzBlob.Length, null, (1, 1)).ToArray());
        WLenField(p, 8, Op(PayloadOpType.ReplaceBz, 4096 + (uint)xzBlob.Length, (ulong)bzBlob.Length, null, (2, 1)).ToArray());
        WLenField(p, 8, Op(PayloadOpType.Zero, 0, 0, null, (3, 1)).ToArray());
        WLenField(p, 8, Op(PayloadOpType.Move, 0, 0, (0, 1), (4, 1)).ToArray());
        WLenField(p, 8, Op(PayloadOpType.Discard, 0, 0, null, (5, 3)).ToArray());
        WLenField(m, 13, p.ToArray());
    }
    // partitions[1] tiny（无 new_partition_info）
    {
        var p = new List<byte>();
        WLenField(p, 1, Encoding.UTF8.GetBytes("tiny"));
        WLenField(p, 8, Op(PayloadOpType.Replace, 4096 + (uint)xzBlob.Length + (uint)bzBlob.Length, (ulong)tinyData.Length, null, (0, 1)).ToArray());
        WLenField(m, 13, p.ToArray());
    }
    return m;
}

string BuildPayload(byte[]? hashOverride)
{
    var manifest = BuildManifest(hashOverride);
    string path = Path.Combine(root, "payload.bin");
    using (var fs = File.Create(path))
    {
        Span<byte> head = stackalloc byte[24];
        Encoding.ASCII.GetBytes("CrAU").CopyTo(head);
        BinaryPrimitives.WriteUInt64LittleEndian(head.Slice(4), 2); // version
        BinaryPrimitives.WriteUInt64LittleEndian(head.Slice(12), (ulong)manifest.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(head.Slice(20), 0); // metadata_sig_size
        fs.Write(head);
        fs.Write(manifest.ToArray());
        fs.Write(blockA);
        fs.Write(xzBlob);
        fs.Write(bzBlob);
        fs.Write(tinyData);
    }
    return path;
}

string payloadPath = BuildPayload(null);
var pinfo = PayloadBinService.Parse(payloadPath);
Check("payload: 2 partitions", pinfo.Partitions.Count == 2 && pinfo.Partitions[0].Name == "testsys" && pinfo.Partitions[1].Name == "tiny");
Check("payload: block size", pinfo.BlockSize == 4096);
var psys = pinfo.Partitions[0];
Check("payload: new_partition_info size", psys.NewPartitionSize == (ulong)expectedSys.Length);
Check("payload: hash parsed", psys.HasHash && psys.Sha256Hash.AsSpan().SequenceEqual(sysHash));
Check("payload: ops parsed", psys.Operations.Count == 6 && psys.Operations[2].Type == PayloadOpType.ReplaceBz);
Check("payload: bz supported now", psys.IsSupported && PayloadPartitionInfo.IsSupportedOp(PayloadOpType.ReplaceBz));
Check("payload: tiny size estimated", pinfo.Partitions[1].ImageSize == 4096);

string outDir = Path.Combine(root, "payload_out");
bool v1 = PayloadBinService.ExtractPartition(payloadPath, psys, outDir, progress, ct);
byte[] actualSys = File.ReadAllBytes(Path.Combine(outDir, "testsys.img"));
Check("payload: extract verified", v1);
Check("payload: image size", actualSys.Length == expectedSys.Length);
Check("payload: image content (REPLACE/XZ/BZ/ZERO/MOVE/DISCARD)", actualSys.AsSpan().SequenceEqual(expectedSys));

bool v2 = PayloadBinService.ExtractPartition(payloadPath, pinfo.Partitions[1], outDir, progress, ct);
byte[] actualTiny = File.ReadAllBytes(Path.Combine(outDir, "tiny.img"));
// tiny 数据 4095B → 1 块（4096B），末尾 1 字节零填充（REPLACE 语义：zero padding to block size）
byte[] expectedTiny = new byte[4096];
tinyData.CopyTo(expectedTiny, 0);
Check("payload: no-hash partition unverified", !v2);
Check("payload: tiny content + zero padding", actualTiny.AsSpan().SequenceEqual(expectedTiny));

// 坏哈希 → 提取必须失败
var badHash = (byte[])sysHash.Clone();
badHash[0] ^= 0xFF;
string badPayload = BuildPayload(badHash);
try
{
    PayloadBinService.ExtractPartition(badPayload, PayloadBinService.Parse(badPayload).Partitions[0], outDir, progress, ct);
    Check("payload: bad hash rejected", false);
}
catch (InvalidDataException)
{
    Check("payload: bad hash rejected", true);
}

// zip 直选 payload.bin
string otaZip = Path.Combine(root, "ota.zip");
using (var za = ZipFile.Open(otaZip, ZipArchiveMode.Create)) za.CreateEntryFromFile(payloadPath, "payload.bin");
string? tmpPayload = PayloadBinService.ExtractFromZip(otaZip);
Check("payload: extract from zip", tmpPayload is not null && File.ReadAllBytes(tmpPayload).AsSpan().SequenceEqual(File.ReadAllBytes(payloadPath)));

// ==================== 7. APK 解析（AXML AndroidManifest） ====================

// --- AXML 编码辅助（构造合成 manifest 用，字段号对照 AOSP ResourceTypes.h） ---
byte[] U16v(ushort v) => BitConverter.GetBytes(v);
byte[] U32v(uint v) => BitConverter.GetBytes(v);
var axStrings = new List<string>();
int AS(string s) { int i = axStrings.IndexOf(s); if (i < 0) { axStrings.Add(s); i = axStrings.Count - 1; } return i; }
void AddVar8(List<byte> b, int v) { if (v >= 0x80) { b.Add((byte)((v >> 8) | 0x80)); b.Add((byte)(v & 0xFF)); } else b.Add((byte)v); }

List<byte> StringPoolChunk(bool utf8)
{
    var data = new List<byte>();
    var offsets = new List<int>();
    foreach (var s in axStrings)
    {
        while (data.Count % 4 != 0) data.Add(0);
        offsets.Add(data.Count);
        if (utf8)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            AddVar8(data, s.Length);
            AddVar8(data, bytes.Length);
            data.AddRange(bytes);
            data.Add(0);
        }
        else
        {
            data.AddRange(U16v((ushort)s.Length));
            data.AddRange(Encoding.Unicode.GetBytes(s));
            data.AddRange(U16v(0));
        }
    }
    while (data.Count % 4 != 0) data.Add(0);

    var b = new List<byte>();
    b.AddRange(U16v(0x0001)); // RES_STRING_POOL_TYPE
    b.AddRange(U16v(28)); // headerSize
    b.AddRange(U32v((uint)(28 + 4 * axStrings.Count + data.Count)));
    b.AddRange(U32v((uint)axStrings.Count)); // stringCount
    b.AddRange(U32v(0)); // styleCount
    b.AddRange(U32v(utf8 ? 0x100u : 0u)); // flags
    b.AddRange(U32v((uint)(28 + 4 * axStrings.Count))); // stringsStart
    b.AddRange(U32v(0)); // stylesStart
    foreach (var o in offsets) b.AddRange(U32v((uint)o));
    b.AddRange(data);
    return b;
}

List<byte> Attr(int ns, int name, int raw, byte type, uint data)
{
    var b = new List<byte>();
    b.AddRange(U32v(ns < 0 ? 0xFFFFFFFFu : (uint)ns));
    b.AddRange(U32v((uint)name));
    b.AddRange(U32v(raw < 0 ? 0xFFFFFFFFu : (uint)raw));
    b.AddRange(U16v(8)); // Res_value size
    b.Add(0); b.Add(type);
    b.AddRange(U32v(data));
    return b;
}

List<byte> StartElem(int name, int ns, params List<byte>[] attrs)
{
    var payload = new List<byte>();
    payload.AddRange(U32v(ns < 0 ? 0xFFFFFFFFu : (uint)ns));
    payload.AddRange(U32v((uint)name));
    payload.AddRange(U16v(20)); // attributeStart（相对 attrExt 起点）
    payload.AddRange(U16v(20)); // attributeSize
    payload.AddRange(U16v((ushort)attrs.Length));
    payload.AddRange(U16v(0)); payload.AddRange(U16v(0)); payload.AddRange(U16v(0)); // id/class/style index
    foreach (var a in attrs) payload.AddRange(a);

    var b = new List<byte>();
    b.AddRange(U16v(0x0102)); // RES_XML_START_ELEMENT_TYPE
    b.AddRange(U16v(16)); // headerSize（chunk 头 8 + lineNumber 4 + comment 4）
    b.AddRange(U32v((uint)(16 + payload.Count)));
    b.AddRange(U32v(1)); // lineNumber
    b.AddRange(U32v(0xFFFFFFFF)); // comment
    b.AddRange(payload);
    return b;
}

List<byte> EndElem(int name, int ns)
{
    var b = new List<byte>();
    b.AddRange(U16v(0x0103)); // RES_XML_END_ELEMENT_TYPE
    b.AddRange(U16v(16));
    b.AddRange(U32v(24));
    b.AddRange(U32v(1));
    b.AddRange(U32v(0xFFFFFFFF));
    b.AddRange(U32v(ns < 0 ? 0xFFFFFFFFu : (uint)ns));
    b.AddRange(U32v((uint)name));
    return b;
}

List<byte> NsChunk(ushort type, int prefix, int uri)
{
    var b = new List<byte>();
    b.AddRange(U16v(type));
    b.AddRange(U16v(16));
    b.AddRange(U32v(24));
    b.AddRange(U32v(1));
    b.AddRange(U32v(0xFFFFFFFF));
    b.AddRange(U32v((uint)prefix));
    b.AddRange(U32v((uint)uri));
    return b;
}

List<byte> BuildAxml(bool utf8)
{
    int sManifest = AS("manifest"), sPackage = AS("package"), sVerCode = AS("versionCode"), sVerName = AS("versionName");
    int sUsesSdk = AS("uses-sdk"), sMinSdk = AS("minSdkVersion"), sTargetSdk = AS("targetSdkVersion");
    int sUsesPerm = AS("uses-permission"), sName = AS("name");
    int sApplication = AS("application"), sDebug = AS("debuggable"), sExtract = AS("extractNativeLibs");
    int sAndroid = AS("android"), sUri = AS("http://schemas.android.com/apk/res/android");
    int sPkg = AS("com.example.testapp"), sVerVal = AS("1.2.3-test");
    int sPermNet = AS("android.permission.INTERNET"), sPermCam = AS("android.permission.CAMERA");

    var body = new List<byte>();
    body.AddRange(StringPoolChunk(utf8));
    body.AddRange(NsChunk(0x0100, sAndroid, sUri)); // start namespace

    // <manifest package="..." android:versionCode="123" android:versionName="1.2.3-test">
    body.AddRange(StartElem(sManifest, -1,
        Attr(-1, sPackage, sPkg, 0x03, (uint)sPkg),
        Attr(sUri, sVerCode, -1, 0x10, 123),
        Attr(sUri, sVerName, sVerVal, 0x03, (uint)sVerVal)));
    // <uses-sdk android:minSdkVersion="29" android:targetSdkVersion="34"/>
    body.AddRange(StartElem(sUsesSdk, -1,
        Attr(sUri, sMinSdk, -1, 0x10, 29),
        Attr(sUri, sTargetSdk, -1, 0x10, 34)));
    body.AddRange(EndElem(sUsesSdk, -1));
    // <uses-permission android:name="android.permission.INTERNET"/> ×2
    body.AddRange(StartElem(sUsesPerm, -1, Attr(sUri, sName, sPermNet, 0x03, (uint)sPermNet)));
    body.AddRange(EndElem(sUsesPerm, -1));
    body.AddRange(StartElem(sUsesPerm, -1, Attr(sUri, sName, sPermCam, 0x03, (uint)sPermCam)));
    body.AddRange(EndElem(sUsesPerm, -1));
    // <application android:debuggable="true" android:extractNativeLibs="false"/>
    body.AddRange(StartElem(sApplication, -1,
        Attr(sUri, sDebug, -1, 0x12, 0xFFFFFFFF),
        Attr(sUri, sExtract, -1, 0x12, 0)));
    body.AddRange(EndElem(sApplication, -1));

    body.AddRange(EndElem(sManifest, -1));
    body.AddRange(NsChunk(0x0101, sAndroid, sUri)); // end namespace

    var axml = new List<byte>();
    axml.AddRange(U16v(0x0003)); // RES_XML_TYPE
    axml.AddRange(U16v(8));
    axml.AddRange(U32v((uint)(8 + body.Count)));
    axml.AddRange(body);
    return axml;
}

string MakeApk(bool utf8)
{
    string path = Path.Combine(root, utf8 ? "test_utf8.apk" : "test_utf16.apk");
    using (var za = ZipFile.Open(path, ZipArchiveMode.Create))
    {
        var entry = za.CreateEntry("AndroidManifest.xml");
        using var es = entry.Open();
        es.Write(BuildAxml(utf8).ToArray());
    }
    return path;
}

var apkInfo = ApkParser.Parse(MakeApk(utf8: true));
Check("apk: package name", apkInfo.PackageName == "com.example.testapp");
Check("apk: version name", apkInfo.VersionName == "1.2.3-test");
Check("apk: version code", apkInfo.VersionCode == 123);
Check("apk: min/target sdk", apkInfo.MinSdk == 29 && apkInfo.TargetSdk == 34);
Check("apk: permissions", apkInfo.Permissions.Count == 2 &&
    apkInfo.Permissions[0] == "android.permission.INTERNET" && apkInfo.Permissions[1] == "android.permission.CAMERA");
Check("apk: application flags", apkInfo.Debuggable && apkInfo.ExtractNativeLibs == false);

// UTF-16 字符串池（旧工具链布局）同样可解析
var apkInfo16 = ApkParser.Parse(MakeApk(utf8: false));
Check("apk: utf-16 pool", apkInfo16.PackageName == "com.example.testapp" && apkInfo16.VersionCode == 123 && apkInfo16.Permissions.Count == 2);

// 缺 manifest 的 zip → 报错
string badApk = Path.Combine(root, "bad.apk");
using (ZipFile.Open(badApk, ZipArchiveMode.Create)) { }
try { ApkParser.Parse(badApk); Check("apk: missing manifest rejected", false); }
catch (InvalidDataException) { Check("apk: missing manifest rejected", true); }

// 工作区扫描记录主 APK 路径（目录型取最大 APK）
string apkWs = Path.Combine(root, "apk_ws");
Directory.CreateDirectory(Path.Combine(apkWs, "system", "app", "TestApp"));
File.Copy(Path.Combine(root, "test_utf8.apk"), Path.Combine(apkWs, "system", "app", "TestApp", "TestApp.apk"));
File.WriteAllBytes(Path.Combine(apkWs, "system", "app", "TestApp", "config.pk"), new byte[8]);
var scanned = WorkspaceScanner.ScanApps(apkWs);
Check("apk: scanner records main apk", scanned.Count == 1 && scanned[0].MainApkPath!.EndsWith("TestApp.apk"));
scanned[0].ApkInfo = ApkParser.Parse(scanned[0].MainApkPath!);
Check("apk: workspace app enrich", scanned[0].PackageText == "com.example.testapp · v1.2.3-test");

// ==================== 8. 精简安全知识库 ====================

Check("safety: keep (AOSP core)", DebloatSafetyService.GetRisk("com.android.systemui") == DebloatRisk.Keep &&
    DebloatSafetyService.GetRisk("com.android.providers.media") == DebloatRisk.Keep);
Check("safety: keep (GMS core)", DebloatSafetyService.GetRisk("com.google.android.gms") == DebloatRisk.Keep);
Check("safety: caution", DebloatSafetyService.GetRisk("com.google.android.tts") == DebloatRisk.Caution &&
    DebloatSafetyService.GetRisk("com.android.stk") == DebloatRisk.Caution);
Check("safety: safe (Google apps)", DebloatSafetyService.GetRisk("com.google.android.youtube") == DebloatRisk.Safe);
Check("safety: oem (MIUI/Samsung/Huawei)", DebloatSafetyService.GetRisk("com.miui.player") == DebloatRisk.Safe &&
    DebloatSafetyService.GetRisk("com.miui.securitycore") == DebloatRisk.Keep &&
    DebloatSafetyService.GetRisk("com.sec.android.daemonapp") == DebloatRisk.Safe &&
    DebloatSafetyService.GetRisk("com.huawei.systemmanager") == DebloatRisk.Keep);
Check("safety: unknown package", DebloatSafetyService.GetRisk("com.example.nonexistent") == DebloatRisk.Unknown);
Check("safety: reason text", DebloatSafetyService.GetReason("com.android.systemui")?.Contains("bootloop") == true);

// ==================== 9. 应用预装与资源替换 ====================

// ws1 是 e2e 解包的工作区（system 分区已展开），正好作为预装目标
string extraApk = Path.Combine(root, "extra.apk");
File.Copy(Path.Combine(root, "test_utf8.apk"), extraApk);
string destApp = PreinstallService.AddApk(ws1, extraApk, privileged: false);
Check("preinstall: app layout", destApp.EndsWith(Path.Combine("system", "app", "extra", "extra.apk")) && File.Exists(destApp));
string destPriv = PreinstallService.AddApk(ws1, extraApk, privileged: true);
Check("preinstall: priv-app layout", destPriv.EndsWith(Path.Combine("system", "priv-app", "extra", "extra.apk")));
try { PreinstallService.AddApk(ws1, extraApk, privileged: false); Check("preinstall: duplicate rejected", false); }
catch (InvalidOperationException) { Check("preinstall: duplicate rejected", true); }
// 预装产物可被扫描器识别
var appsAfter = WorkspaceScanner.ScanApps(ws1);
Check("preinstall: visible to scanner", appsAfter.Any(a => a.Name == "extra" && a.Partition == "system"));

// bootanimation：合法包（含 desc.txt）
string ba = Path.Combine(root, "bootanimation.zip");
using (var za = ZipFile.Open(ba, ZipArchiveMode.Create)) { za.CreateEntry("desc.txt"); za.CreateEntry("part0/0001.png"); }
PreinstallService.ReplaceResource(ws1, ba, ResourceKind.Bootanimation);
Check("preinstall: bootanimation", File.Exists(Path.Combine(ws1, "system", "media", "bootanimation.zip")));
// 非法 bootanimation（无 desc.txt）必须拒绝
string badBa = Path.Combine(root, "bad_ba.zip");
using (var za = ZipFile.Open(badBa, ZipArchiveMode.Create)) { za.CreateEntry("foo.txt"); }
try { PreinstallService.ReplaceResource(ws1, badBa, ResourceKind.Bootanimation); Check("preinstall: bad bootanimation rejected", false); }
catch (InvalidDataException) { Check("preinstall: bad bootanimation rejected", true); }

// 字体 / 铃声 / 通知音
string ttf = Path.Combine(root, "custom.ttf");
File.WriteAllBytes(ttf, new byte[64]);
string fontDest = PreinstallService.ReplaceResource(ws1, ttf, ResourceKind.Font);
Check("preinstall: font", fontDest.EndsWith(Path.Combine("system", "fonts", "custom.ttf")));
string ogg = Path.Combine(root, "ring.ogg");
File.WriteAllBytes(ogg, new byte[64]);
PreinstallService.ReplaceResource(ws1, ogg, ResourceKind.Ringtone);
PreinstallService.ReplaceResource(ws1, ogg, ResourceKind.Notification);
Check("preinstall: audio kinds", File.Exists(Path.Combine(ws1, "system", "media", "audio", "ringtones", "ring.ogg")) &&
    File.Exists(Path.Combine(ws1, "system", "media", "audio", "notifications", "ring.ogg")));
// 无 system 分区的工作区必须报错
string noSystemWs = Path.Combine(root, "no_sys_ws");
Directory.CreateDirectory(noSystemWs);
try { PreinstallService.AddApk(noSystemWs, extraApk, false); Check("preinstall: no-system rejected", false); }
catch (InvalidOperationException) { Check("preinstall: no-system rejected", true); }

// ==================== 10. 打包前防呆检查 + fastboot 脚本 ====================

// ws1：解包产物完整 + vbmeta 已禁用（e2e 中 AVB 步骤处理过）→ 应无任何问题
var checksClean = PackPreflightService.RunChecks(ws1, Path.Combine(root, "out2.zip"));
Check("preflight: clean workspace", checksClean.Count == 0);

// 非解包目录 → 必须报 error
var checksBad = PackPreflightService.RunChecks(noSystemWs, Path.Combine(root, "x.zip"));
Check("preflight: non-workspace error", checksBad.Any(c => c.Level == PreflightLevel.Error));

// fastboot 脚本生成（按 ws1 的 manifest 分区）
string? script = PackPreflightService.GenerateFastbootScript(ws1, Path.Combine(root, "out2.zip"));
Check("preflight: script generated", script is not null && File.Exists(script));
string[] scriptLines = File.ReadAllLines(script!);
Check("preflight: script content", scriptLines.Any(l => l.Contains("fastboot flash system")) &&
    scriptLines.Any(l => l.Contains("fastboot flash boot")) &&
    scriptLines.Any(l => l.Contains("fastboot flash vbmeta")) &&
    scriptLines[^1].Contains("pause", StringComparison.OrdinalIgnoreCase));

// ==================== 11. build.prop 预设 ====================

// ws1 的 build.prop 含 ro.product.model（=NewModel）与 ro.product.device（=TESTDEV），其余 3 个键不存在
var propLines = BuildPropService.Parse(File.ReadAllText(Path.Combine(ws1, "system/build.prop")));
var spoof = BuildPropPresets.All.First(p => p.Name.Contains("机型伪装"));
var (mod1, add1) = BuildPropPresets.Apply(propLines, spoof);
Check("preset: modified/added", mod1 == 2 && add1 == 3);
Check("preset: model spoofed", propLines.First(l => l.IsProperty && l.Key == "ro.product.model").Value == "Pixel 8");
Check("preset: device spoofed", propLines.First(l => l.IsProperty && l.Key == "ro.product.device").Value == "husky");
// 幂等：再次应用不产生改动
var (mod2, add2) = BuildPropPresets.Apply(propLines, spoof);
Check("preset: idempotent", mod2 == 0 && add2 == 0);
// 序列化保留
string propSer = BuildPropService.Serialize(propLines);
Check("preset: serialize roundtrip", propSer.Contains("ro.product.model=Pixel 8") && propSer.Contains("ro.product.brand=google"));

// ==================== 12. ROM 信息总览 ====================

// 补充系统属性，验证汇总提取
File.AppendAllText(Path.Combine(ws1, "system/build.prop"),
    "ro.build.version.release=15\nro.build.version.sdk=35\nro.build.version.security_patch=2026-08-05\nro.build.fingerprint=test/TESTDEV/TESTDEV:15/root\n");
var info = RomInfoService.Collect(ws1);
Check("info: model & version", info.Props.First(p => p.Key == "ro.product.model").Value == "NewModel" &&
    info.Props.First(p => p.Key == "ro.build.version.release").Value == "15");
Check("info: security patch", info.Props.First(p => p.Key == "ro.build.version.security_patch").Value == "2026-08-05");
Check("info: featured order first", info.Props[0].Key == "ro.product.model" && info.Props.Take(5).All(p =>
    p.Key.StartsWith("ro.product.") || p.Key.StartsWith("ro.build.version.")) &&
    info.Props.FindIndex(p => p.Key == "ro.debuggable") > info.Props.FindIndex(p => p.Key == "ro.build.fingerprint"));
Check("info: app count", info.AppCount == 3); // Shell + extra(app) + extra(priv-app)
Check("info: partitions", info.Partitions.Count >= 3 && info.Partitions.Any(p => p.Name == "system"));
Check("info: gms absent", !info.GmsPresent);
Check("info: avb disabled", info.AvbPresent && info.AvbAllDisabled);
Check("info: no root", !info.RootSuPresent);
Check("info: total size", info.TotalImageSize > 0 && info.TotalSizeText.Length > 0);
// 不存在的目录安全返回空结果
var emptyInfo = RomInfoService.Collect(Path.Combine(root, "nonexistent_ws"));
Check("info: empty safe", emptyInfo.AppCount == 0 && emptyInfo.Partitions.Count == 0 && emptyInfo.Props.Count == 0);

// ==================== 13. hosts 广告过滤 ====================

string hostsPath = HostsService.GetHostsPath(ws1);
int blocked = HostsService.Apply(ws1, HostsService.BuiltInDomains);
string hostsText1 = File.ReadAllText(hostsPath);
Check("hosts: apply builtin", blocked == HostsService.BuiltInDomains.Length &&
    hostsText1.Contains("0.0.0.0 ad.doubleclick.net"));
Check("hosts: markers", hostsText1.Contains(HostsService.BeginMarker) && hostsText1.Contains(HostsService.EndMarker));
Check("hosts: parse blocked", HostsService.GetBlockedDomains(hostsText1).Count == HostsService.BuiltInDomains.Length);
// 幂等：重复应用同列表不翻倍（并集语义）
int blocked2 = HostsService.Apply(ws1, new[] { HostsService.BuiltInDomains[0], "ads.example.com" });
string hostsText2 = File.ReadAllText(hostsPath);
Check("hosts: idempotent union", blocked2 == HostsService.BuiltInDomains.Length + 1 &&
    HostsService.GetBlockedDomains(hostsText2).Count == blocked2 &&
    hostsText2.Split(HostsService.BeginMarker, StringSplitOptions.None).Length == 2); // 只有一个块
Check("hosts: custom append", hostsText2.Contains("0.0.0.0 ads.example.com"));
// 自定义域名解析：合法保留，非法过滤
var custom = HostsService.ParseCustomDomains("a.com, b.com\nbad domain\n-c.com\ngood.example.org");
Check("hosts: custom parse", custom.Count == 3 && custom.Contains("a.com") && custom.Contains("b.com") && custom.Contains("good.example.org"));
Check("hosts: invalid domain", !HostsService.IsValidDomain("-c.com") && !HostsService.IsValidDomain("localhost") &&
    HostsService.IsValidDomain("ads.example.com"));
// 保留块外内容：原始 hosts 行不受影响
File.WriteAllText(hostsPath, "127.0.0.1 localhost\n");
HostsService.Apply(ws1, new[] { "ads.example.com" });
string hostsText3 = File.ReadAllText(hostsPath);
Check("hosts: preserve original", hostsText3.StartsWith("127.0.0.1 localhost\n") && hostsText3.Contains("0.0.0.0 ads.example.com"));
// 移除
Check("hosts: remove", HostsService.Remove(ws1) && !File.ReadAllText(hostsPath).Contains(HostsService.BeginMarker) &&
    File.ReadAllText(hostsPath).TrimEnd() == "127.0.0.1 localhost");
// 无 system 分区必须拒绝
try { HostsService.Apply(noSystemWs, new[] { "a.com" }); Check("hosts: no-system rejected", false); }
catch (InvalidOperationException) { Check("hosts: no-system rejected", true); }

// ==================== 14. 工作区版本对比 ====================

void CopyDir(string src, string dst)
{
    Directory.CreateDirectory(dst);
    foreach (var file in Directory.EnumerateFiles(src))
    {
        File.Copy(file, Path.Combine(dst, Path.GetFileName(file)));
    }
    foreach (var dir in Directory.EnumerateDirectories(src))
    {
        CopyDir(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }
}

string wsOld = Path.Combine(root, "ws_old");
CopyDir(ws1, wsOld);

// 在 ws1 上做变更：改属性 + 新增属性 + 加应用 + 删应用
var diffPropPath = Path.Combine(ws1, "system/build.prop");
var diffLines = BuildPropService.Parse(File.ReadAllText(diffPropPath));
diffLines.First(l => l.IsProperty && l.Key == "ro.product.model").Value = "DiffModel";
diffLines.Add(new BuildPropLine { IsProperty = true, Key = "ro.diff.test", Value = "1", IsNew = true });
File.WriteAllText(diffPropPath, BuildPropService.Serialize(diffLines), new UTF8Encoding(false));

string diffApk = Path.Combine(root, "test_diff.apk");
File.Copy(extraApk, diffApk);
PreinstallService.AddApk(ws1, diffApk, privileged: false);
var shellApp = WorkspaceScanner.ScanApps(ws1).First(a => a.Name == "Shell");
WorkspaceScanner.RemoveApp(ws1, shellApp);

var diff = WorkspaceDiffService.Compare(wsOld, ws1);
Check("diff: added app", diff.AddedApps.Count == 1 && diff.AddedApps[0].Name == "test_diff");
Check("diff: removed app", diff.RemovedApps.Count == 1 && diff.RemovedApps[0].Name == "Shell");
var modelDiff = diff.ModifiedProps.FirstOrDefault(p => p.Key == "ro.product.model");
Check("diff: modified prop", modelDiff is not null && modelDiff.OldValue == "NewModel" && modelDiff.NewValue == "DiffModel");
Check("diff: added prop", diff.AddedProps.Count == 1 && diff.AddedProps[0].Key == "ro.diff.test");
Check("diff: removed prop none", diff.RemovedProps.Count == 0);
var sysPart = diff.Partitions.FirstOrDefault(p => p.Name == "system");
Check("diff: partition size changed", sysPart is not null && sysPart.Changed && sysPart.NewSize > 0);
Check("diff: summary text", diff.SummaryText.Contains("应用") && diff.SummaryText.Contains("属性"));
// 相同目录对比：无差异
var sameDiff = WorkspaceDiffService.Compare(ws1, ws1);
Check("diff: same dir no changes", !sameDiff.HasChanges && sameDiff.AddedApps.Count == 0 && sameDiff.ModifiedProps.Count == 0);
// 不存在的目录：安全空对比
var voidDiff = WorkspaceDiffService.Compare(Path.Combine(root, "no_x"), Path.Combine(root, "no_y"));
Check("diff: missing dirs safe", !voidDiff.HasChanges);

// ==================== 规范符合性测试 ====================

// --- BootImage v0 头部偏移正确性 ---
// BootImage v0 写入布局（AOSP bootimg.h v0 + MSM 地址扩展）：
//   0=magic, 8=kernel_size, 12=kernel_addr, 16=ramdisk_size, 20=ramdisk_addr
//   24=second_size, 28=second_addr, 32=tags_addr, 36=page_size
//   40=dt_size(v0)/header_version(v1+), 44=os_version, 48=name[16], 64=cmdline[512]
string bootV0 = Path.Combine(root, "boot_v0.img");
var bootV0Info = new BootImage.BootInfo
{
    HeaderVersion = 0,
    PageSize = 2048,
    Kernel = Encoding.ASCII.GetBytes("KERNELDATA" + new string('X', 2048)),
    Ramdisk = Encoding.ASCII.GetBytes("RAMDISKDATA" + new string('Y', 1024)),
    Name = "specboot",
    Cmdline = "console=ttyMSM0 androidboot.selinux=permissive",
    OsVersion = 0x07030002, // Android 13.0.0 2
};
bootV0Info.KernelSize = (uint)bootV0Info.Kernel.Length;
bootV0Info.RamdiskSize = (uint)bootV0Info.Ramdisk.Length;
using (var fs = File.Create(bootV0)) BootImage.Write(bootV0Info, fs);

byte[] bootBytes = File.ReadAllBytes(bootV0);
Check("boot v0: magic", Encoding.ASCII.GetString(bootBytes, 0, 8) == "ANDROID!");
Check("boot v0: os_version@44", BinaryPrimitives.ReadUInt32LittleEndian(bootBytes.AsSpan(44)) == bootV0Info.OsVersion);
Check("boot v0: name@48", Encoding.ASCII.GetString(bootBytes, 48, 9) == "specboot\0"[..9]);
Check("boot v0: cmdline@64", Encoding.ASCII.GetString(bootBytes, 64, 20).StartsWith("console=ttyMSM0"));
Check("boot v0: page_size@36", BinaryPrimitives.ReadUInt32LittleEndian(bootBytes.AsSpan(36)) == 2048);

// 往返解析
using (var sr = File.OpenRead(bootV0))
{
    var roundtrip = BootImage.Parse(sr);
    Check("boot v0 roundtrip: header_version", roundtrip.HeaderVersion == 0);
    Check("boot v0 roundtrip: page_size", roundtrip.PageSize == bootV0Info.PageSize);
    Check("boot v0 roundtrip: kernel_size", roundtrip.KernelSize == bootV0Info.KernelSize);
    Check("boot v0 roundtrip: ramdisk_size", roundtrip.RamdiskSize == bootV0Info.RamdiskSize);
    Check("boot v0 roundtrip: os_version", roundtrip.OsVersion == bootV0Info.OsVersion);
    Check("boot v0 roundtrip: name", roundtrip.Name == bootV0Info.Name);
    Check("boot v0 roundtrip: cmdline", roundtrip.Cmdline == bootV0Info.Cmdline);
}

// --- BootImage v1 往返（带 header_version @40） ---
string bootV1 = Path.Combine(root, "boot_v1.img");
var bootV1Info = new BootImage.BootInfo
{
    HeaderVersion = 1,
    PageSize = 2048,
    Kernel = Encoding.ASCII.GetBytes("V1KERNEL" + new string('X', 2048)),
    Ramdisk = Encoding.ASCII.GetBytes("V1RAMDISK" + new string('Y', 1024)),
    Name = "v1boot",
    Cmdline = "bootv1 cmdline",
    OsVersion = 0x07000000,
};
bootV1Info.KernelSize = (uint)bootV1Info.Kernel.Length;
bootV1Info.RamdiskSize = (uint)bootV1Info.Ramdisk.Length;
using (var fs = File.Create(bootV1)) BootImage.Write(bootV1Info, fs);
using (var sr = File.OpenRead(bootV1))
{
    var rt = BootImage.Parse(sr);
    Check("boot v1: header_version@40", rt.HeaderVersion == 1);
    Check("boot v1 roundtrip: page_size", rt.PageSize == 2048);
    Check("boot v1 roundtrip: os_version", rt.OsVersion == bootV1Info.OsVersion);
    Check("boot v1 roundtrip: name", rt.Name == bootV1Info.Name);
    Check("boot v1 roundtrip: cmdline", rt.Cmdline == bootV1Info.Cmdline);
}

// --- BootImage v3 往返（紧凑头，page_size 不在头内，默认 4096） ---
string bootV3 = Path.Combine(root, "boot_v3.img");
var bootV3Info = new BootImage.BootInfo
{
    HeaderVersion = 3,
    PageSize = 4096,
    Kernel = Encoding.ASCII.GetBytes("V3KERNEL" + new string('X', 2048)),
    Ramdisk = Encoding.ASCII.GetBytes("V3RAMDISK" + new string('Y', 100)),
    Cmdline = "bootv3 cmdline",
};
bootV3Info.KernelSize = (uint)bootV3Info.Kernel.Length;
bootV3Info.RamdiskSize = (uint)bootV3Info.Ramdisk.Length;
using (var fs = File.Create(bootV3)) BootImage.Write(bootV3Info, fs);
using (var sr = File.OpenRead(bootV3))
{
    var rt = BootImage.Parse(sr);
    Check("boot v3 roundtrip: header_version", rt.HeaderVersion == 3);
    Check("boot v3 roundtrip: kernel_size", rt.KernelSize == bootV3Info.KernelSize);
    Check("boot v3 roundtrip: cmdline", rt.Cmdline == bootV3Info.Cmdline);
}

// --- Ext4 元数据 roundtrip（符号链接 + xattr + 权限） ---
string sysMetaSrc = Path.Combine(root, "sys_meta_src");
Directory.CreateDirectory(sysMetaSrc);
// 创建常规文件
File.WriteAllText(Path.Combine(sysMetaSrc, "regular.txt"), "hello");
// 创建 .rom_metadata.json 模拟解包导出的元数据
var meta = new List<Ext4FileMeta>
{
    new() { Path = ".", Mode = 0x41ED, Uid = 0, Gid = 0, Atime = 100, Ctime = 100, Mtime = 100 },
    new() { Path = "regular.txt", Mode = 0x81A4, Uid = 1000, Gid = 1000, Atime = 200, Ctime = 200, Mtime = 200,
        Xattrs = new List<Ext4Xattr> { new() { Name = "security.selinux", Value = Convert.ToBase64String(Encoding.ASCII.GetBytes("u:object_r:system_file:s0")) } } },
};
Ext4Metadata.Save(sysMetaSrc, meta);

string sysMetaImg = Path.Combine(root, "sys_meta.img");
using (var fs = File.Create(sysMetaImg)) new Ext4Writer().Build(sysMetaSrc, fs, progress, ct);

// 解包到新目录核对
string sysMetaDst = Path.Combine(root, "sys_meta_dst");
using (var fs = File.OpenRead(sysMetaImg))
{
    var reader = new Ext4Reader(fs);
    reader.ExtractTo(sysMetaDst, progress, ct);
}
var readMeta = Ext4Metadata.Load(sysMetaDst);
var regularMeta = readMeta.FirstOrDefault(m => m.Path == "regular.txt");
Check("ext4 meta roundtrip: regular.txt exists", regularMeta is not null);
Check("ext4 meta roundtrip: uid preserved", regularMeta!.Uid == 1000);
Check("ext4 meta roundtrip: gid preserved", regularMeta.Gid == 1000);
Check("ext4 meta roundtrip: xattr count", regularMeta.Xattrs.Count == 1);
Check("ext4 meta roundtrip: selinux label", regularMeta.Xattrs[0].Name == "security.selinux" &&
    Encoding.ASCII.GetString(Convert.FromBase64String(regularMeta.Xattrs[0].Value)) == "u:object_r:system_file:s0");

// --- Ext4 深度 extent 树（构造 >4 个 extent 的大文件） ---
string bigSrc = Path.Combine(root, "big_src");
Directory.CreateDirectory(bigSrc);
// 构造一个 500KB 的文件，每 8KB 写一段不同数据 → 超过 4 个 extent
int blockSize = 4096;
int segments = 10; // 10 段 → 10 个 extent，超过根节点 4 的限制
using (var fs = File.Create(Path.Combine(bigSrc, "big.bin")))
{
    for (int i = 0; i < segments; i++)
    {
        byte[] seg = new byte[blockSize];
        Array.Fill(seg, (byte)(i + 1));
        fs.Write(seg);
    }
}
string bigImg = Path.Combine(root, "big.img");
using (var fs = File.Create(bigImg)) new Ext4Writer().Build(bigSrc, fs, progress, ct);

// 解包核对数据完整
string bigDst = Path.Combine(root, "big_dst");
using (var fs = File.OpenRead(bigImg))
{
    var reader = new Ext4Reader(fs);
    reader.ExtractTo(bigDst, progress, ct);
}
byte[] extractedBig = File.ReadAllBytes(Path.Combine(bigDst, "big.bin"));
Check("ext4 deep extent: size match", extractedBig.Length == blockSize * segments);
Check("ext4 deep extent: segment 0 data", extractedBig[0] == 1);
Check("ext4 deep extent: segment 9 data", extractedBig[blockSize * 9] == 10);

// --- SuperImage parse/rebuild roundtrip ---
// 先构造两个小分区镜像
string subSys = Path.Combine(root, "sub_system.img");
using (var fs = File.Create(subSys))
{
    var w = new Ext4Writer();
    // 简化：直接写几个文件
    string subDir = Path.Combine(root, "sub_sys_src");
    Directory.CreateDirectory(subDir);
    File.WriteAllText(Path.Combine(subDir, "hello.txt"), "super test");
    w.Build(subDir, fs, progress, ct);
}
string subVendor = Path.Combine(root, "sub_vendor.img");
using (var fs = File.Create(subVendor)) new Ext4Writer().Build(Path.Combine(root, "sys_src"), fs, progress, ct);

// 构造 super
var prms = new SuperImage.SuperParams
{
    MajorVersion = 10,
    MinorVersion = 0,
    MetadataMaxSize = 65536,
    MetadataSlotCount = 2,
    LogicalBlockSize = 4096,
    Partitions =
    {
        new() { Name = "system_a", GroupIndex = 0 },
        new() { Name = "vendor_a", GroupIndex = 0 },
    },
    Groups = { new() { Name = "default" } },
    BlockDevices = { new() { Size = 4096UL * 1024UL * 1024UL } },
};
var entries = new List<SuperImage.SuperPackEntry>
{
    new() { Name = "system_a", ImagePath = subSys },
    new() { Name = "vendor_a", ImagePath = subVendor },
};
string superImg = Path.Combine(root, "super.img");
using (var fs = File.Create(superImg)) SuperImage.Pack(prms, entries, fs, progress, ct);
Check("super pack: non-empty", new FileInfo(superImg).Length > 1_000_000);

// 解析 super 核对分区
using (var fs = File.OpenRead(superImg))
{
    var superMeta = SuperImage.Parse(fs);
    Check("super parse: partitions count", superMeta.Partitions.Count == 2);
    Check("super parse: system_a found", superMeta.Partitions.Any(p => p.Name == "system_a"));
    Check("super parse: vendor_a found", superMeta.Partitions.Any(p => p.Name == "vendor_a"));
    Check("super parse: magic", SuperImage.IsSuperImage(fs));
}

Directory.Delete(root, true);

Console.WriteLine(fail == 0 ? "ALL PASS" : $"{fail} FAILED");
Environment.Exit(fail);
