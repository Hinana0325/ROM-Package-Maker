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
/// - v3: 紧凑头（kernel_size@8、ramdisk_size@12、os_version@16、header_size@20、reserved[4]@24、
///        header_version@40、cmdline@44 共 1536 字节），头大小 1580
/// - v4: v3 + signature_size@1580，头大小 1584
/// - vendor_boot v3：VNDRBOOT 魔数，cmdline@28[2048]、tags_addr@2076、name@2080[16]、
///        header_size@2096、dtb_size@2100、dtb_addr@2104，头大小 2112
/// - vendor_boot v4：v3 + vendor_ramdisk_table_size@2112、entry_num@2116、entry_size@2120、
///        bootconfig_size@2124，头大小 2128
/// 分区之间的页对齐填充字节原样保留（Gaps），最后一个 section 之后的所有字节
/// （页对齐填充 / 签名 / bootconfig）作为 Trailer 原样保留，保证往返一致。
/// </summary>
internal static class BootImage
{
    private static readonly byte[] BootMagic = Encoding.ASCII.GetBytes("ANDROID!");
    private static readonly byte[] VendorBootMagic = Encoding.ASCII.GetBytes("VNDRBOOT");

    // 头部尺寸（不含 padding）
    private const int HeaderV0Size = 1632;   // 0x660
    private const int HeaderV1Size = 1648;
    private const int HeaderV2Size = 1660;
    private const int HeaderV3Size = 1580;   // v3
    private const int HeaderV4Size = 1584;   // v4（追加 signature_size@1580）
    private const int VendorHeaderV3Size = 2112;
    private const int VendorHeaderV4Size = 2128;

    private const int CmdlineV3Size = 512 + 1024;       // BOOT_ARGS_SIZE + BOOT_EXTRA_ARGS_SIZE
    private const int CmdlineVendorSize = 2048;         // VENDOR_BOOT_ARGS_SIZE

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
        public uint SignatureSize;          // boot v4 signature_size@1580
        public uint KernelAddr;             // v0-v2 / vendor_boot 加载地址
        public uint RamdiskAddr;
        public uint SecondAddr;             // v0-v2
        public uint TagsAddr;
        public uint VendorRamdiskTableSize;      // vendor_boot v4 @2112
        public uint VendorRamdiskTableEntryNum;  // vendor_boot v4 @2116
        public uint VendorRamdiskTableEntrySize; // vendor_boot v4 @2120
        public uint BootconfigSize;              // vendor_boot v4 @2124
        public byte[] Kernel = Array.Empty<byte>();
        public byte[] Ramdisk = Array.Empty<byte>();
        public byte[] Second = Array.Empty<byte>();
        public byte[] Dt = Array.Empty<byte>();
        public byte[] RecoveryDtbo = Array.Empty<byte>();
        public byte[] Dtb = Array.Empty<byte>();
        public byte[] VendorRamdisk = Array.Empty<byte>();
        /// <summary>最后一个 section 之后的原始字节（padding / 签名 / bootconfig），写回时原样追加。</summary>
        public byte[] Trailer = Array.Empty<byte>();
        /// <summary>分区之间的页对齐填充原始字节（依次对应 header 后、各 section 后），写回时原样还原。</summary>
        public List<byte[]> Gaps = new();
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
            // reserved@24[4], header_version@40, cmdline@44[1536], [v4: signature_size@1580]
            stream.Position = 8;
            info.KernelSize = reader.ReadUInt32LE();
            info.RamdiskSize = reader.ReadUInt32LE();
            info.OsVersion = reader.ReadUInt32LE();
            stream.Position = 44;
            string full = reader.ReadCString(CmdlineV3Size);
            info.Cmdline = full.Length > 512 ? full[..512] : full;
            info.ExtraCmdline = full.Length > 512 ? full[512..] : string.Empty;
            if (version >= 4)
            {
                stream.Position = HeaderV3Size;
                info.SignatureSize = reader.ReadUInt32LE();
            }
        }
        else
        {
            // v0/v1/v2 公共布局
            stream.Position = 8;
            info.KernelSize = reader.ReadUInt32LE();
            info.KernelAddr = reader.ReadUInt32LE();
            info.RamdiskSize = reader.ReadUInt32LE();
            info.RamdiskAddr = reader.ReadUInt32LE();
            info.SecondSize = reader.ReadUInt32LE();
            info.SecondAddr = reader.ReadUInt32LE();
            info.TagsAddr = reader.ReadUInt32LE();
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

        // 数据区：头部补齐页边界（填充字节保留进 Gaps[0]），随后依次是各 section
        int headerEnd = version switch
        {
            >= 4 => HeaderV4Size,
            3 => HeaderV3Size,
            2 => HeaderV2Size,
            1 => HeaderV1Size,
            _ => HeaderV0Size,
        };
        stream.Position = headerEnd;
        ReadGap(stream, info.PageSize, info.Gaps);
        info.Kernel = ReadSection(reader, info.KernelSize);
        if (version < 3)
        {
            ReadGap(stream, info.PageSize, info.Gaps);
            info.Ramdisk = ReadSection(reader, info.RamdiskSize);
            ReadGap(stream, info.PageSize, info.Gaps);
            info.Second = ReadSection(reader, info.SecondSize);
            if (version >= 1)
            {
                ReadGap(stream, info.PageSize, info.Gaps);
                info.RecoveryDtbo = ReadSection(reader, info.RecoveryDtboSize);
                if (version == 2)
                {
                    ReadGap(stream, info.PageSize, info.Gaps);
                    info.Dtb = ReadSection(reader, info.DtbSize);
                }
            }
            else
            {
                ReadGap(stream, info.PageSize, info.Gaps);
                info.Dt = ReadSection(reader, info.DtSize);
            }
        }
        else
        {
            ReadGap(stream, info.PageSize, info.Gaps);
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
        // vendor_ramdisk_size@24, cmdline@28[2048], tags_addr@2076, name@2080[16],
        // header_size@2096, dtb_size@2100, dtb_addr@2104,
        // [v4: vendor_ramdisk_table_size@2112, entry_num@2116, entry_size@2120, bootconfig_size@2124]
        stream.Position = 8;
        var info = new BootInfo { IsVendorBoot = true };
        info.HeaderVersion = (int)reader.ReadUInt32LE();
        info.PageSize = reader.ReadUInt32LE();
        info.KernelAddr = reader.ReadUInt32LE();
        info.RamdiskAddr = reader.ReadUInt32LE();
        info.VendorRamdiskSize = reader.ReadUInt32LE();
        info.Cmdline = reader.ReadCString(CmdlineVendorSize);
        info.ExtraCmdline = string.Empty;
        stream.Position = 2076;
        info.TagsAddr = reader.ReadUInt32LE();
        info.Name = reader.ReadCString(16);
        stream.Position = 2100;
        info.DtbSize = reader.ReadUInt32LE();
        info.DtbAddr = reader.ReadUInt64LE();
        if (info.HeaderVersion >= 4)
        {
            stream.Position = VendorHeaderV3Size;
            info.VendorRamdiskTableSize = reader.ReadUInt32LE();
            info.VendorRamdiskTableEntryNum = reader.ReadUInt32LE();
            info.VendorRamdiskTableEntrySize = reader.ReadUInt32LE();
            info.BootconfigSize = reader.ReadUInt32LE();
        }
        if (info.PageSize == 0) info.PageSize = 4096;

        stream.Position = info.HeaderVersion >= 4 ? VendorHeaderV4Size : VendorHeaderV3Size;
        ReadGap(stream, info.PageSize, info.Gaps);
        info.VendorRamdisk = ReadSection(reader, info.VendorRamdiskSize);
        if (info.HeaderVersion < 4)
        {
            // v3：vendor_ramdisk 后是 dtb
            ReadGap(stream, info.PageSize, info.Gaps);
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

    /// <summary>读取当前位置到下一个页边界之间的填充字节并记录（部分真机镜像此处含非零数据，需原样保留）。</summary>
    private static void ReadGap(Stream stream, uint pageSize, List<byte[]> gaps)
    {
        long pos = stream.Position;
        long aligned = (pos + pageSize - 1) & ~((long)pageSize - 1);
        int len = (int)(aligned - pos);
        if (len <= 0)
        {
            gaps.Add(Array.Empty<byte>());
            return;
        }
        using var reader = new BinaryReader(stream, Encoding.Default, leaveOpen: true);
        gaps.Add(reader.ReadBytes(len));
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
            writer.WriteUInt32LE((uint)(version >= 4 ? HeaderV4Size : HeaderV3Size)); // header_size
            writer.Write(new byte[16]);              // reserved[4]
            writer.WriteUInt32LE((uint)version);
            WriteFixedString(writer, info.Cmdline + info.ExtraCmdline, CmdlineV3Size);
            if (version >= 4)
                writer.WriteUInt32LE(info.SignatureSize);   // signature_size@1580
        }
        else
        {
            writer.WriteUInt32LE(info.KernelSize);
            writer.WriteUInt32LE(info.KernelAddr);
            writer.WriteUInt32LE(info.RamdiskSize);
            writer.WriteUInt32LE(info.RamdiskAddr);
            writer.WriteUInt32LE(info.SecondSize);
            writer.WriteUInt32LE(info.SecondAddr);
            writer.WriteUInt32LE(info.TagsAddr);
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

        // 头部补齐到页大小（v3 头 1580 < page，同样补齐；原始填充字节从 Gaps 还原）
        int gapIdx = 0;
        WriteGap(output, info.PageSize, info.Gaps, ref gapIdx);

        // 数据区（v0-v2: kernel/ramdisk/second[/dtbo][/dtb]；v3+: 仅 kernel/ramdisk）
        writer.Write(info.Kernel);
        WriteGap(output, info.PageSize, info.Gaps, ref gapIdx);
        writer.Write(info.Ramdisk);

        if (version < 3)
        {
            WriteGap(output, info.PageSize, info.Gaps, ref gapIdx);
            writer.Write(info.Second);
            WriteGap(output, info.PageSize, info.Gaps, ref gapIdx);

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
                    WriteGap(output, info.PageSize, info.Gaps, ref gapIdx);
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
        writer.WriteUInt32LE(info.KernelAddr);     // @16
        writer.WriteUInt32LE(info.RamdiskAddr);    // @20
        writer.WriteUInt32LE(info.VendorRamdiskSize); // @24
        WriteFixedString(writer, info.Cmdline + info.ExtraCmdline, CmdlineVendorSize); // @28[2048]
        writer.WriteUInt32LE(info.TagsAddr);       // @2076
        WriteFixedString(writer, info.Name, 16);   // @2080
        writer.WriteUInt32LE((uint)(version >= 4 ? VendorHeaderV4Size : VendorHeaderV3Size)); // header_size @2096
        writer.WriteUInt32LE(info.DtbSize);        // @2100
        writer.WriteUInt64LE(info.DtbAddr);        // @2104
        if (version >= 4)
        {
            writer.WriteUInt32LE(info.VendorRamdiskTableSize);      // @2112
            writer.WriteUInt32LE(info.VendorRamdiskTableEntryNum);  // @2116
            writer.WriteUInt32LE(info.VendorRamdiskTableEntrySize); // @2120
            writer.WriteUInt32LE(info.BootconfigSize);              // @2124
        }

        int gapIdx = 0;
        WriteGap(output, info.PageSize, info.Gaps, ref gapIdx);

        writer.Write(info.VendorRamdisk);
        if (version < 4)
        {
            // v3：vendor_ramdisk 之后页对齐写 dtb（dtb 为空时也要补齐，保证与解析端往返一致）
            WriteGap(output, info.PageSize, info.Gaps, ref gapIdx);
            writer.Write(info.Dtb);
        }
        if (info.Trailer.Length > 0)
            writer.Write(info.Trailer);
    }

    /// <summary>写回一段页对齐填充：优先使用解析时保留的原始字节，不足/超出部分补零。</summary>
    private static void WriteGap(Stream output, uint pageSize, List<byte[]> gaps, ref int gapIdx)
    {
        long pos = output.Position;
        long aligned = (pos + pageSize - 1) & ~((long)pageSize - 1);
        int len = (int)(aligned - pos);
        if (len <= 0) return;

        byte[] gap = gapIdx < gaps.Count ? gaps[gapIdx] : Array.Empty<byte>();
        gapIdx++;
        if (gap.Length == len)
        {
            output.Write(gap);
        }
        else
        {
            output.Write(gap.AsSpan(0, Math.Min(gap.Length, len)));
            output.Write(new byte[len - Math.Min(gap.Length, len)]);
        }
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
