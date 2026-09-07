using System.IO.Compression;
using System.Text;

namespace RomPackageMaker.Services;

/// <summary>APK AndroidManifest 解析结果。</summary>
public sealed class ApkInfo
{
    /// <summary>manifest package 属性。</summary>
    public string PackageName { get; set; } = string.Empty;

    /// <summary>manifest versionName（原始字符串）。</summary>
    public string VersionName { get; set; } = string.Empty;

    /// <summary>manifest versionCode。</summary>
    public long VersionCode { get; set; }

    /// <summary>uses-sdk minSdkVersion；未声明为 -1。</summary>
    public int MinSdk { get; set; } = -1;

    /// <summary>uses-sdk targetSdkVersion；未声明为 -1。</summary>
    public int TargetSdk { get; set; } = -1;

    /// <summary>uses-permission 列表（android:name）。</summary>
    public List<string> Permissions { get; } = new();

    /// <summary>application debuggable。</summary>
    public bool Debuggable { get; set; }

    /// <summary>application extractNativeLibs；未声明为 null。</summary>
    public bool? ExtractNativeLibs { get; set; }

    /// <summary>application hasCode；未声明为 null。</summary>
    public bool? HasCode { get; set; }

    /// <summary>显示用摘要：包名 · 版本。</summary>
    public string Summary => string.IsNullOrEmpty(VersionName)
        ? PackageName
        : $"{PackageName} · v{VersionName}";
}

/// <summary>
/// APK（zip + 二进制 AndroidManifest.xml，即 AXML）解析。
/// AXML 布局：RES_XML_TYPE 总块 → 字符串池（RES_STRING_POOL_TYPE）→
/// 命名空间 / 开始 / 结束元素块；元素属性为 (ns, name, rawValue, typedValue) 四元组。
/// </summary>
public static class ApkParser
{
    // ResChunk_header.type
    private const ushort ResStringPoolType = 0x0001;
    private const ushort ResXmlStartElementType = 0x0102;

    // Res_value.dataType
    private const byte TypeString = 0x03;
    private const byte TypeIntDec = 0x10;

    private const uint NoEntry = 0xFFFFFFFF;
    private const int Utf8Flag = 0x100;

    /// <summary>解析 APK 的 AndroidManifest。失败抛异常（zip 损坏 / 无 manifest / 格式非法）。</summary>
    public static ApkInfo Parse(string apkPath)
    {
        using var archive = ZipFile.OpenRead(apkPath);
        var entry = archive.GetEntry("AndroidManifest.xml")
            ?? throw new InvalidDataException($"APK 中未找到 AndroidManifest.xml：{apkPath}");

        using var ms = new MemoryStream();
        using (var es = entry.Open())
        {
            es.CopyTo(ms);
        }
        return ParseManifest(ms.ToArray());
    }

    // ==================== AXML 解析 ====================

    private static ApkInfo ParseManifest(byte[] data)
    {
        if (data.Length < 8) throw new InvalidDataException("AXML 过小。");

        var info = new ApkInfo();
        string[]? strings = null;

        int pos = 8; // 跳过 RES_XML_TYPE 总块头
        while (pos + 8 <= data.Length)
        {
            int chunkStart = pos;
            ushort type = ReadU16(data, chunkStart);
            ushort headerSize = ReadU16(data, chunkStart + 2);
            uint size = ReadU32(data, chunkStart + 4);
            if (size < 8 || chunkStart + size > data.Length)
                throw new InvalidDataException($"AXML 块长度非法（type=0x{type:X4}）。");

            switch (type)
            {
                case ResStringPoolType:
                    strings = ParseStringPool(data, chunkStart);
                    break;
                case ResXmlStartElementType:
                    if (strings is null)
                        throw new InvalidDataException("元素块先于字符串池出现。");
                    ReadElement(data, chunkStart + headerSize, strings, info);
                    break;
                // 命名空间 / 结束元素 / CDATA / 资源映射等对 manifest 提取无意义，跳过
            }

            pos = chunkStart + (int)size;
        }

        if (string.IsNullOrEmpty(info.PackageName))
            throw new InvalidDataException("AXML 中未解析到 manifest package 属性。");
        return info;
    }

    /// <summary>解析字符串池块（UTF-8 / UTF-16 两种编码）。</summary>
    private static string[] ParseStringPool(byte[] data, int chunkStart)
    {
        uint stringCount = ReadU32(data, chunkStart + 8);
        uint flags = ReadU32(data, chunkStart + 16);
        uint stringsStart = ReadU32(data, chunkStart + 20);
        bool utf8 = (flags & Utf8Flag) != 0;

        var result = new string[stringCount];
        int offsets = chunkStart + 28; // chunk 头 8 + 池头 5×4
        for (int i = 0; i < (int)stringCount; i++)
        {
            uint offset = ReadU32(data, offsets + i * 4);
            int p = chunkStart + (int)stringsStart + (int)offset;
            result[i] = utf8 ? ReadUtf8String(data, ref p) : ReadUtf16String(data, ref p);
        }
        return result;
    }

    /// <summary>读取开始元素：按元素名分发提取 manifest 关注的属性。</summary>
    private static void ReadElement(byte[] data, int p, string[] strings, ApkInfo info)
    {
        uint nameIdx = ReadU32(data, p + 4);
        ushort attrStart = ReadU16(data, p + 8);
        ushort attrSize = ReadU16(data, p + 10);
        ushort attrCount = ReadU16(data, p + 12);

        string element = At(strings, nameIdx);
        int attrsBase = p + attrStart;

        for (int i = 0; i < attrCount; i++)
        {
            int a = attrsBase + i * attrSize;
            uint nsIdx = ReadU32(data, a);
            uint attrNameIdx = ReadU32(data, a + 4);
            uint rawIdx = ReadU32(data, a + 8);
            byte dataType = data[a + 15];
            uint dataValue = ReadU32(data, a + 16);

            string attrName = At(strings, attrNameIdx);

            switch (element)
            {
                case "manifest":
                    if (attrName == "package") info.PackageName = Str(strings, rawIdx, dataType, dataValue);
                    else if (attrName == "versionName") info.VersionName = Str(strings, rawIdx, dataType, dataValue);
                    else if (attrName == "versionCode") info.VersionCode = Int(strings, rawIdx, dataType, dataValue);
                    break;
                case "uses-sdk":
                    if (attrName == "minSdkVersion") info.MinSdk = (int)Int(strings, rawIdx, dataType, dataValue);
                    else if (attrName == "targetSdkVersion") info.TargetSdk = (int)Int(strings, rawIdx, dataType, dataValue);
                    break;
                case "uses-permission":
                case "uses-permission-sdk-23":
                case "uses-permission-sdk-m":
                    if (attrName == "name")
                    {
                        string perm = Str(strings, rawIdx, dataType, dataValue);
                        if (!string.IsNullOrEmpty(perm) && !info.Permissions.Contains(perm))
                            info.Permissions.Add(perm);
                    }
                    break;
                case "application":
                    if (attrName == "debuggable") info.Debuggable = Bool(dataType, dataValue);
                    else if (attrName == "extractNativeLibs") info.ExtractNativeLibs = Bool(dataType, dataValue);
                    else if (attrName == "hasCode") info.HasCode = Bool(dataType, dataValue);
                    break;
            }
        }
    }

    // ==================== 属性值解析 ====================

    private static string Str(string[] strings, uint rawIdx, byte type, uint value)
    {
        if (rawIdx != NoEntry) return At(strings, rawIdx);
        return type == TypeString ? At(strings, value) : string.Empty;
    }

    private static long Int(string[] strings, uint rawIdx, byte type, uint value)
    {
        if (type == TypeIntDec) return value;
        // 早期工具链会把 minSdk 等写成字符串
        string s = Str(strings, rawIdx, type, value);
        return long.TryParse(s, out long v) ? v : 0;
    }

    private static bool Bool(byte type, uint value) => value != 0;

    private static string At(string[] strings, uint index) =>
        index != NoEntry && index < (uint)strings.Length ? strings[index] : string.Empty;

    // ==================== 底层读取 ====================

    private static ushort ReadU16(byte[] d, int p) => (ushort)(d[p] | (d[p + 1] << 8));
    private static uint ReadU32(byte[] d, int p) =>
        (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24));

    /// <summary>UTF-8 池字符串：变长字符数 + 变长字节数 + 数据 + 0x00。</summary>
    private static string ReadUtf8String(byte[] d, ref int p)
    {
        int _ = ReadVar8(d, ref p); // 字符数（解码后长度，忽略）
        int byteLen = ReadVar8(d, ref p);
        string s = Encoding.UTF8.GetString(d, p, byteLen);
        p += byteLen + 1; // 跳过数据与终止符
        return s;
    }

    private static int ReadVar8(byte[] d, ref int p)
    {
        int b = d[p++];
        return (b & 0x80) != 0 ? ((b & 0x7F) << 8) | d[p++] : b;
    }

    /// <summary>UTF-16 池字符串：u16 长度（超高位置位时跨双 u16）+ 数据 + 0x0000。</summary>
    private static string ReadUtf16String(byte[] d, ref int p)
    {
        int len = ReadU16(d, p);
        p += 2;
        if ((len & 0x8000) != 0)
        {
            len = ((len & 0x7FFF) << 16) | ReadU16(d, p);
            p += 2;
        }
        string s = Encoding.Unicode.GetString(d, p, len * 2);
        p += len * 2 + 2; // 跳过数据与终止符
        return s;
    }
}
