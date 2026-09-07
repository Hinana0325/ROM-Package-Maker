using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace RomPackageMaker.Services;

/// <summary>
/// Android boot / vendor_boot 镜像的解包与重打包。
/// 按 AOSP system/tools/mkbootimg/include/bootimg/bootimg.h 实现：
/// - v0: 偏移 40 = dt_size（union header_version），44 = os_version，48 = name，64 = cmdline，576 = id，608 = extra_cmdline
/// - v1: v0 + recovery_dtbo_size@1632、recovery_dtbo_offset@1636、header_size@1644
/// - v2: v1 + dtb_size@1648、dtb_addr@1652
/// - v3: 紧凑头（kernel_size@8、ramdisk_size@12、os_version@16、header_size@20、cmdline@44 共 1536 字节）
/// - vendor_boot v3/v4：VNDRBOOT 魔数，vendor_ramdisk_size@24、dtb_size@3124
/// 最后一个 section 之后的所有字节（页对齐填充 / 签名 / bootconfig）作为 Trailer 原样保留，保证往返一致。
/// </summary>
internal static class BootImage
{
    private static readonly byte[] BootMagic = Encoding.ASCII.GetBytes("ANDROID!");
    private static readonly byte[] VendorBootMagic = Encoding.ASCII.GetBytes("VNDRBOOT");

    // 头部尺寸（不含 padding）
    private const int HeaderV0Size = 1632;   // 0x660
    private const int HeaderV1Size = 1648;
    private const int HeaderV2Size = 1660;
    private const int HeaderV3Size = 1580;   // v3/v4（v4 追加 sig_size 不属于头）
    private const int VendorHeaderV3Size = 3136;
    private const int VendorHeaderV4Size = 3140;

    private const int CmdlineV3Size = 512 + 1024;       // BOOT_ARGS_SIZE + BOOT_EXTRA_ARGS_SIZE
    private const int CmdlineVendorSize = 2048 + 1024;  // vendor_boot cmdline

    public sealed record BootInfo
    {
        public bool IsVendorBoot;
        public int HeaderVersion;
        public uint PageSize = 2048;
        public uint KernelSize;
        public uint RamdiskSize;
        public uint SecondSize;
        public uint DtSize;                 // v0 dt（偏移 40）
        public uint RecoveryDtboSize;       // v1/v2
        public uint DtbSize;               // v2 / vendor_boot
        public string Name = string.Empty;
        public string Cmdline = string.Empty;
        public string ExtraCmdline = string.Empty;
        public uint OsVersion;
        public byte[] Id = new byte[32];
        public ulong DtbAddr;
        public uint VendorRamdiskSize;
        public uint VendorSignatureSize;    // vendor_boot v4
        public byte[] Kernel = Array.Empty<byte>();
        public byte[] Ramdisk = Array.Empty<byte>();
        public byte[] Second = Array.Empty<byte>();
        public byte[] Dt = Array.Empty<byte>();
        public byte[] RecoveryDtbo = Array.Empty<byte>();
        public byte[] Dtb = Array.Empty<byte>();
        public byte[] VendorRamdisk = Array.Empty<byte>();
        /// <summary>最后一个 section 之后的原始字节（padding / 签名 / bootconfig），写回时原样追加。</summary>
        public byte[] Trailer = Array.Empty<byte>();
    }

    public static BootInfo Parse(Stream stream)
    {
        long fileSize = stream.Length;
        using var reader = new BinaryReader(stream, Encoding.Default, leaveOpen: true);
        var magic = reader.ReadBytes(8);

        if (magic.SequenceEqual(VendorBootMagic))
            return ParseVendorBoot(stream, fileSize);
        if (!magic.SequenceEqual(BootMagic))
            throw new InvalidDataException("不是有效的 boot 镜像。");

        // 版本判定：v0-v2 布局中偏移 40 是 header_version（v0 旧镜像此处为 dt_size）
        stream.Position = 40;
        uint field40 = reader.ReadUInt32LE();
        int version = field40 is 1 or 2 or 3 or 4 ? (int)field40 : 0;

        var info = new BootInfo { HeaderVersion = version };
        if (version >= 3)
        {
            // v3/v4：kernel_size@8, ramdisk_size@12, os_version@16, header_size@20,
            // reserved@24[4], header_version@40, cmdline@44[1536]
            stream.Position = 8;
            info.KernelSize = reader.ReadUInt32LE();
            info.RamdiskSize = reader.ReadUInt32LE();
            info.OsVersion = reader.ReadUInt32LE();
            stream.Position = 44;
            string full = reader.ReadCString(CmdlineV3Size);
            info.Cmdline = full.Length > 512 ? full[..512] : full;
            info.ExtraCmdline = full.Length > 512 ? full[512..] : string.Empty;
        }
        else
        {
            // v0/v1/v2 公共布局
            stream.Position = 8;
            info.KernelSize = reader.ReadUInt32LE();
            reader.ReadUInt32LE(); // kernel_addr
            info.RamdiskSize = reader.ReadUInt32LE();
            reader.ReadUInt32LE(); // ramdisk_addr
            info.SecondSize = reader.ReadUInt32LE();
            reader.ReadUInt32LE(); // second_addr
            reader.ReadUInt32LE(); // tags_addr
            info.PageSize = reader.ReadUInt32LE();
            // @40 的 dt_size/header_version 已在版本判定阶段读入 field40，
            // 流位置现处于 @40，需跳过 4 字节再读 os_version
            stream.Position = 44;
            info.DtSize = field40;   // v0：dt_size；v1/v2：header_version（dt 恒为 0）
            info.OsVersion = reader.ReadUInt32LE();
            info.Name = reader.ReadCString(16);
            info.Cmdline = reader.ReadCString(512);
            info.Id = reader.ReadBytes(32);
            info.ExtraCmdline = reader.ReadCString(1024);

            if (version is 1 or 2)
            {
                stream.Position = HeaderV0Size;
                info.RecoveryDtboSize = reader.ReadUInt32LE();
                stream.Position = HeaderV0Size + 12; // header_size 跳过
                if (version == 2)
                {
                    stream.Position = HeaderV1Size;
                    info.DtbSize = reader.ReadUInt32LE();
                    info.DtbAddr = reader.ReadUInt64LE();
                }
            }
        }

        if (info.PageSize == 0) info.PageSize = 4096;

        // 数据区：从 page_size 开始，section 之间页对齐，最后 section 之后的内容进 Trailer
        stream.Position = info.PageSize;
        info.Kernel = ReadSection(reader, info.KernelSize);
        if (version < 3)
        {
            PadToPage(stream, info.PageSize);
            info.Ramdisk = ReadSection(reader, info.RamdiskSize);
            PadToPage(stream, info.PageSize);
            info.Second = ReadSection(reader, info.SecondSize);
            if (version >= 1)
            {
                PadToPage(stream, info.PageSize);
                info.RecoveryDtbo = ReadSection(reader, info.RecoveryDtboSize);
                if (version == 2)
                {
                    PadToPage(stream, info.PageSize);
                    info.Dtb = ReadSection(reader, info.DtbSize);
                }
            }
            else
            {
                PadToPage(stream, info.PageSize);
                info.Dt = ReadSection(reader, info.DtSize);
            }
        }
        else
        {
            PadToPage(stream, info.PageSize);
            info.Ramdisk = ReadSection(reader, info.RamdiskSize);
        }
        info.Trailer = ReadRest(stream, fileSize);
        return info;
    }

    private static BootInfo ParseVendorBoot(Stream stream, long fileSize)
    {
        using var reader = new BinaryReader(stream, Encoding.Default, leaveOpen: true);
        // vendor_boot v3/v4：
        // magic@0, header_version@8, page_size@12, kernel_addr@16, ramdisk_addr@20,
        // vendor_ramdisk_size@24, cmdline@28[3072], tags_addr@3100, name@3104[16],
        // header_size@3120, dtb_size@3124, dtb_addr@3128, [v4: signature_size@3136]
        stream.Position = 8;
        var info = new BootInfo { IsVendorBoot = true };
        info.HeaderVersion = (int)reader.ReadUInt32LE();
        info.PageSize = reader.ReadUInt32LE();
        reader.ReadUInt32LE(); // kernel_addr
        reader.ReadUInt32LE(); // ramdisk_addr
        info.VendorRamdiskSize = reader.ReadUInt32LE();
        string full = reader.ReadCString(CmdlineVendorSize);
        info.Cmdline = full.Length > 2048 ? full[..2048] : full;
        info.ExtraCmdline = full.Length > 2048 ? full[2048..] : string.Empty;
        stream.Position = 3100;
        reader.ReadUInt32LE(); // tags_addr
        info.Name = reader.ReadCString(16);
        stream.Position = 3120;
        reader.ReadUInt32LE(); // header_size
        info.DtbSize = reader.ReadUInt32LE();
        info.DtbAddr = reader.ReadUInt64LE();
        if (info.HeaderVersion >= 4)
        {
            stream.Position = VendorHeaderV3Size;
            info.VendorSignatureSize = reader.ReadUInt32LE();
        }
        if (info.PageSize == 0) info.PageSize = 4096;

        stream.Position = info.PageSize;
        info.VendorRamdisk = ReadSection(reader, info.VendorRamdiskSize);
        if (info.HeaderVersion < 4)
        {
            // v3：vendor_ramdisk 后是 dtb
            PadToPage(stream, info.PageSize);
            info.Dtb = ReadSection(reader, info.DtbSize);
        }
        // v4：vendor_ramdisk 之后是 ramdisk 表 / dtb / bootconfig / 签名，整体作为 Trailer 保留
        info.Trailer = ReadRest(stream, fileSize);
        return info;
    }

    /// <summary>读取一个 section 的原始数据（不做页对齐）。</summary>
    private static byte[] ReadSection(BinaryReader reader, uint size)
    {
        if (size == 0) return Array.Empty<byte>();
        return reader.ReadBytes((int)size);
    }

    private static byte[] ReadRest(Stream stream, long fileSize)
    {
        long remain = fileSize - stream.Position;
        if (remain <= 0) return Array.Empty<byte>();
        using var reader = new BinaryReader(stream, Encoding.Default, leaveOpen: true);
        return reader.ReadBytes((int)remain);
    }

    private static void PadToPage(Stream stream, uint pageSize)
    {
        long pos = stream.Position;
        long aligned = (pos + pageSize - 1) & ~((long)pageSize - 1);
        stream.Position = aligned;
    }

    private static long AlignPage(long pos, uint pageSize) => (pos + pageSize - 1) & ~((long)pageSize - 1);

    /// <summary>将 boot 镜像信息写回为 boot.img（按 header_version 输出对应布局）。</summary>
    public static void Write(BootInfo info, Stream output)
    {
        if (info.IsVendorBoot)
        {
            WriteVendorBoot(info, output);
            return;
        }

        using var writer = new BinaryWriter(output, Encoding.Default, leaveOpen: true);
        int version = info.HeaderVersion is 1 or 2 or 3 or 4 ? info.HeaderVersion : 0;

        writer.Write(BootMagic);
        if (version >= 3)
        {
            writer.WriteUInt32LE(info.KernelSize);
            writer.WriteUInt32LE(info.RamdiskSize);
            writer.WriteUInt32LE(info.OsVersion);
            writer.WriteUInt32LE((uint)HeaderV3Size); // header_size
            writer.Write(new byte[16]);              // reserved[4]
            writer.WriteUInt32LE((uint)version);
            WriteFixedString(writer, info.Cmdline + info.ExtraCmdline, CmdlineV3Size);
        }
        else
        {
            writer.WriteUInt32LE(info.KernelSize);
            writer.Write(0u); // kernel_addr
            writer.WriteUInt32LE(info.RamdiskSize);
            writer.Write(0u); // ramdisk_addr
            writer.WriteUInt32LE(info.SecondSize);
            writer.Write(0u); // second_addr
            writer.Write(0u); // tags_addr
            writer.WriteUInt32LE(info.PageSize);
            writer.WriteUInt32LE(version == 0 ? info.DtSize : (uint)version); // union: dt_size / header_version
            writer.WriteUInt32LE(info.OsVersion);
            WriteFixedString(writer, info.Name, 16);
            WriteFixedString(writer, info.Cmdline, 512);
            writer.Write(info.Id is { Length: 32 } ? info.Id : new byte[32]);
            WriteFixedString(writer, info.ExtraCmdline, 1024);

            if (version >= 1)
            {
                writer.WriteUInt32LE(info.RecoveryDtboSize);   // @1632
                // recovery_dtbo_offset（uint64，@1636）在 section 写入阶段确定后回填
                writer.Write(0ul);
                writer.WriteUInt32LE((uint)(version == 2 ? HeaderV2Size : HeaderV1Size)); // header_size @1644
                if (version == 2)
                {
                    writer.WriteUInt32LE(info.DtbSize);
                    writer.WriteUInt64LE(info.DtbAddr);
                }
            }
        }

        // 头部补齐到页大小（v3 头 1580 < page，同样补齐）
        long headerEnd = output.Position;
        long pageAligned = AlignPage(headerEnd, info.PageSize);
        output.Write(new byte[pageAligned - headerEnd]);

        // 数据区（v0-v2: kernel/ramdisk/second[/dtbo][/dtb]；v3+: 仅 kernel/ramdisk）
        writer.Write(info.Kernel);
        PadWrite(output, info.PageSize);
        writer.Write(info.Ramdisk);

        if (version < 3)
        {
            PadWrite(output, info.PageSize);
            writer.Write(info.Second);
            PadWrite(output, info.PageSize);

            if (version >= 1)
            {
                long dtboStart = output.Position;
                writer.Write(info.RecoveryDtbo);
                if (info.RecoveryDtboSize > 0)
                {
                    // 回填 recovery_dtbo_offset（绝对偏移，v1/v2 均有该字段）
                    long cur = output.Position;
                    output.Position = HeaderV0Size + 4;
                    writer.WriteUInt64LE((ulong)dtboStart);
                    output.Position = cur;
                }
                if (version == 2)
                {
                    PadWrite(output, info.PageSize);
                    writer.Write(info.Dtb);
                }
            }
            else
            {
                writer.Write(info.Dt);
            }
        }

        // Trailer（原镜像末尾字节：padding / 签名等）
        if (info.Trailer.Length > 0)
            writer.Write(info.Trailer);
    }

    private static void WriteVendorBoot(BootInfo info, Stream output)
    {
        using var writer = new BinaryWriter(output, Encoding.Default, leaveOpen: true);
        int version = Math.Max(3, info.HeaderVersion);

        writer.Write(VendorBootMagic);
        writer.WriteUInt32LE((uint)version);
        writer.WriteUInt32LE(info.PageSize);
        writer.Write(0u); // kernel_addr
        writer.Write(0u); // ramdisk_addr
        writer.WriteUInt32LE(info.VendorRamdiskSize);
        WriteFixedString(writer, info.Cmdline + info.ExtraCmdline, CmdlineVendorSize);
        writer.Write(0u); // tags_addr
        WriteFixedString(writer, info.Name, 16);
        writer.WriteUInt32LE((uint)(version >= 4 ? VendorHeaderV4Size : VendorHeaderV3Size)); // header_size
        writer.WriteUInt32LE(info.DtbSize);
        writer.WriteUInt64LE(info.DtbAddr);
        if (version >= 4)
            writer.WriteUInt32LE(info.VendorSignatureSize);

        long headerEnd = output.Position;
        long pageAligned = AlignPage(headerEnd, info.PageSize);
        output.Write(new byte[pageAligned - headerEnd]);

        writer.Write(info.VendorRamdisk);
        if (version < 4)
        {
            // v3：vendor_ramdisk 之后页对齐写 dtb（dtb 为空时也要补齐，保证与解析端往返一致）
            PadWrite(output, info.PageSize);
            writer.Write(info.Dtb);
        }
        if (info.Trailer.Length > 0)
            writer.Write(info.Trailer);
    }

    private static void WriteFixedString(BinaryWriter writer, string value, int size)
    {
        var buf = new byte[size];
        if (!string.IsNullOrEmpty(value))
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            Array.Copy(bytes, buf, Math.Min(bytes.Length, size - 1));
        }
        writer.Write(buf);
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
