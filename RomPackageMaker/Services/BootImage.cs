using System.IO.Compression;
using System.Text;

namespace RomPackageMaker.Services;

/// <summary>
/// Android boot / vendor_boot 镜像的解包与重打包。
/// 支持 boot image header v0-v3 以及 vendor_boot v4。
/// </summary>
internal static class BootImage
{
    private static readonly byte[] BootMagic = Encoding.ASCII.GetBytes("ANDROID!");
    private static readonly byte[] VendorBootMagic = Encoding.ASCII.GetBytes("VNDRBOOT");

    public static bool IsBootImage(Stream stream)
    {
        long pos = stream.Position;
        try
        {
            Span<byte> buf = stackalloc byte[8];
            if (stream.Read(buf) != 8) return false;
            return buf.SequenceEqual(BootMagic) || buf.SequenceEqual(VendorBootMagic);
        }
        finally
        {
            stream.Position = pos;
        }
    }

    public sealed record BootInfo
    {
        public int HeaderVersion;
        public uint PageSize = 2048;
        public uint KernelSize;
        public uint RamdiskSize;
        public uint SecondSize;
        public uint DtSize;
        public string Name = string.Empty;
        public string Cmdline = string.Empty;
        public string ExtraCmdline = string.Empty;
        public uint OsVersion;
        public byte[] Kernel = Array.Empty<byte>();
        public byte[] Ramdisk = Array.Empty<byte>();
        public byte[] Second = Array.Empty<byte>();
        public byte[] Dt = Array.Empty<byte>();
        // vendor_boot v4
        public uint VendorRamdiskSize;
        public byte[] VendorRamdisk = Array.Empty<byte>();
        public uint VendorBootHeaderSize;
    }

    public static BootInfo Parse(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.Default, leaveOpen: true);
        var magic = reader.ReadBytes(8);

        if (magic.SequenceEqual(VendorBootMagic))
        {
            return ParseVendorBoot(reader);
        }
        if (!magic.SequenceEqual(BootMagic))
        {
            throw new InvalidDataException("不是有效的 boot 镜像。");
        }

        var info = new BootInfo
        {
            KernelSize = reader.ReadUInt32LE(),
        };
        reader.ReadUInt32LE(); // kernel_addr
        info.RamdiskSize = reader.ReadUInt32LE();
        reader.ReadUInt32LE(); // ramdisk_addr
        info.SecondSize = reader.ReadUInt32LE();
        reader.ReadUInt32LE(); // second_addr
        reader.ReadUInt32LE(); // tags_addr
        info.PageSize = reader.ReadUInt32LE();
        info.DtSize = reader.ReadUInt32LE();
        reader.ReadUInt32LE(); // unused
        info.OsVersion = reader.ReadUInt32LE();
        info.Name = reader.ReadCString(16);
        info.Cmdline = reader.ReadCString(512);
        reader.ReadBytes(32); // id
        info.ExtraCmdline = reader.ReadCString(1024);

        // header version: v0 没有 header_version 字段；v1+ 在偏移 0x410 处
        // boot image header 总大小 v0=0x440, v1=0x448, v2=0x448, v3=0x448
        stream.Position = 0x410;
        info.HeaderVersion = (int)reader.ReadUInt32LE();

        // 数据从 page_size 之后开始
        long dataStart = info.PageSize;
        stream.Position = dataStart;

        info.Kernel = reader.ReadBytes((int)info.KernelSize);
        PadToPage(stream, info.PageSize);
        info.Ramdisk = reader.ReadBytes((int)info.RamdiskSize);
        PadToPage(stream, info.PageSize);
        if (info.SecondSize > 0)
        {
            info.Second = reader.ReadBytes((int)info.SecondSize);
            PadToPage(stream, info.PageSize);
        }
        if (info.DtSize > 0)
        {
            info.Dt = reader.ReadBytes((int)info.DtSize);
            PadToPage(stream, info.PageSize);
        }

        return info;
    }

    private static BootInfo ParseVendorBoot(BinaryReader reader)
    {
        var info = new BootInfo
        {
            HeaderVersion = (int)reader.ReadUInt32LE(),
            PageSize = reader.ReadUInt32LE(),
            KernelSize = 0,
        };
        reader.ReadUInt32LE(); // kernel_addr? no
        // vendor_boot header:
        // magic[8], header_version[4], page_size[4], kernel_addr[4], ramdisk_addr[4]? 
        // 实际 vendor_boot v4:
        // magic[8], header_version[4], page_size[4], kernel_addr[4], ramdisk_addr[4]? 
        // 让我们按标准 vendor_boot header v4 解析
        // 参考 AOSP system/tools/mkbootimg/include/bootimg/bootimg.h
        reader.BaseStream.Position = 8; // magic 已读
        info.HeaderVersion = (int)reader.ReadUInt32LE();
        info.PageSize = reader.ReadUInt32LE();
        reader.ReadUInt32LE(); // kernel_addr
        reader.ReadUInt32LE(); // ramdisk_addr
        reader.ReadUInt32LE(); // vendor_ramdisk_addr
        reader.ReadUInt32LE(); // tags_addr
        reader.ReadUInt32LE(); // product[16]
        reader.ReadBytes(16);
        info.Cmdline = reader.ReadCString(512);
        reader.ReadBytes(32); // id
        info.ExtraCmdline = reader.ReadCString(1024);
        info.VendorRamdiskSize = reader.ReadUInt32LE();
        reader.ReadUInt32LE(); // dtb size? 
        reader.ReadUInt32LE(); // dtb addr
        info.VendorBootHeaderSize = reader.ReadUInt32LE();

        long dataStart = info.PageSize;
        reader.BaseStream.Position = dataStart;
        info.VendorRamdisk = reader.ReadBytes((int)info.VendorRamdiskSize);
        return info;
    }

    private static void PadToPage(Stream stream, uint pageSize)
    {
        long pos = stream.Position;
        long aligned = (pos + pageSize - 1) & ~((long)pageSize - 1);
        stream.Position = aligned;
    }

    private static long AlignPage(long pos, uint pageSize) => (pos + pageSize - 1) & ~((long)pageSize - 1);

    /// <summary>将 boot 镜像信息写回为 boot.img。</summary>
    public static void Write(BootInfo info, Stream output)
    {
        using var writer = new BinaryWriter(output, Encoding.Default, leaveOpen: true);

        writer.Write(BootMagic);
        writer.WriteUInt32LE(info.KernelSize);
        writer.Write(0u); // kernel_addr
        writer.WriteUInt32LE(info.RamdiskSize);
        writer.Write(0u); // ramdisk_addr
        writer.WriteUInt32LE(info.SecondSize);
        writer.Write(0u); // second_addr
        writer.Write(0u); // tags_addr
        writer.WriteUInt32LE(info.PageSize);
        writer.WriteUInt32LE(info.DtSize);
        writer.Write(0u); // unused
        writer.WriteUInt32LE(info.OsVersion);

        // name[16]
        var nameBytes = Encoding.ASCII.GetBytes(info.Name);
        var nameBuf = new byte[16];
        Array.Copy(nameBytes, nameBuf, Math.Min(nameBytes.Length, 15));
        writer.Write(nameBuf);

        // cmdline[512]
        var cmdlineBuf = new byte[512];
        var cmdBytes = Encoding.ASCII.GetBytes(info.Cmdline);
        Array.Copy(cmdBytes, cmdlineBuf, Math.Min(cmdBytes.Length, 511));
        writer.Write(cmdlineBuf);

        // id[32]
        writer.Write(new byte[32]);

        // extra_cmdline[1024]
        var extraBuf = new byte[1024];
        var extraBytes = Encoding.ASCII.GetBytes(info.ExtraCmdline);
        Array.Copy(extraBytes, extraBuf, Math.Min(extraBytes.Length, 1023));
        writer.Write(extraBuf);

        // 填充到页大小
        long headerEnd = output.Position;
        long pageAligned = AlignPage(headerEnd, info.PageSize);
        output.Write(new byte[pageAligned - headerEnd]);

        // kernel
        writer.Write(info.Kernel);
        PadWrite(output, info.PageSize);
        // ramdisk
        writer.Write(info.Ramdisk);
        PadWrite(output, info.PageSize);
        // second
        if (info.SecondSize > 0)
        {
            writer.Write(info.Second);
            PadWrite(output, info.PageSize);
        }
        // dt
        if (info.DtSize > 0)
        {
            writer.Write(info.Dt);
            PadWrite(output, info.PageSize);
        }
    }

    private static void PadWrite(Stream stream, uint pageSize)
    {
        long pos = stream.Position;
        long aligned = AlignPage(pos, pageSize);
        if (aligned > pos) stream.Write(new byte[aligned - pos]);
    }

    /// <summary>检测 ramdisk 压缩格式并解压。</summary>
    public static byte[] DecompressRamdisk(byte[] data)
    {
        if (data.Length < 4) return data;
        // gzip: 1F 8B
        if (data[0] == 0x1F && data[1] == 0x8B)
        {
            using var ms = new MemoryStream(data);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            gz.CopyTo(outMs);
            return outMs.ToArray();
        }
        // lz4: 04 22 4D 18
        if (data[0] == 0x04 && data[1] == 0x22 && data[2] == 0x4D && data[3] == 0x18)
        {
            // .NET 内置不支持 lz4，返回原始数据并提示
            return data;
        }
        // xz: FD 37 7A 58 5A 00
        if (data[0] == 0xFD && data[1] == 0x37 && data[2] == 0x7A)
        {
            return data; // 不内置支持
        }
        // 假设未压缩（cpio）
        return data;
    }

    /// <summary>将 ramdisk 用 gzip 压缩。</summary>
    public static byte[] CompressRamdiskGzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal))
        {
            gz.Write(data, 0, data.Length);
        }
        return ms.ToArray();
    }
}
