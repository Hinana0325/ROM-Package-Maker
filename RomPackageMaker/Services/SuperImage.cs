using System.Text;

namespace RomPackageMaker.Services;

/// <summary>
/// Android super 动态分区镜像解析。
/// 读取 super partition metadata，提取各逻辑分区的原始数据。
/// </summary>
internal static class SuperImage
{
    public const uint Magic = 0x56454C41; // "ALEV" LE = super 镜像标志

    public sealed class SuperPartition
    {
        public string Name = string.Empty;
        public long TotalSize;
        public List<Extent> Extents = new();
    }

    public sealed class Extent
    {
        public long StartSector;   // 物理扇区偏移（相对于 super 镜像开头）
        public long NumSectors;
        public bool IsZero;        // zero extent（不需要实际数据）
    }

    private const uint SectorSize = 512;

    /// <summary>解析 super 镜像中的逻辑分区列表。</summary>
    public static List<SuperPartition> Parse(Stream stream)
    {
        var partitions = new List<SuperPartition>();
        long pos = stream.Position;
        stream.Position = 0;

        try
        {
            using var reader = new BinaryReader(stream, Encoding.Default, leaveOpen: true);
            uint magic = reader.ReadUInt32LE();
            if (magic != Magic) throw new InvalidDataException("不是有效的 super 镜像。");

            ushort major = reader.ReadUInt16LE();
            ushort minor = reader.ReadUInt16LE();
            uint headerSize = reader.ReadUInt32LE();
            reader.ReadUInt32LE(); // header_checksum
            uint tablesSize = reader.ReadUInt32LE();
            reader.ReadUInt32LE(); // tables_checksum

            // 表位置
            uint partOffset = reader.ReadUInt32LE();
            uint partCount = reader.ReadUInt32LE();
            uint extOffset = reader.ReadUInt32LE();
            uint extCount = reader.ReadUInt32LE();
            // 跳过 groups 和 block_devices
            reader.ReadUInt32LE(); // group offset
            reader.ReadUInt32LE(); // group count
            reader.ReadUInt32LE(); // block_devices offset
            reader.ReadUInt32LE(); // block_devices count

            // 表的基地址：header_size 之后
            long tablesBase = headerSize;

            // 读取分区表
            int partDescSize = major >= 1 && minor >= 1 ? 80 : 56;
            var partList = new List<(string Name, uint FirstExtent, uint NumExtents)>();
            for (uint i = 0; i < partCount; i++)
            {
                stream.Position = tablesBase + partOffset + i * partDescSize;
                var nameBytes = reader.ReadBytes(36);
                int nullIdx = Array.IndexOf(nameBytes, (byte)0);
                string name = Encoding.UTF8.GetString(nameBytes, 0, nullIdx < 0 ? 36 : nullIdx);
                stream.Position = tablesBase + partOffset + i * partDescSize + 36;
                reader.ReadUInt32LE(); // attributes
                uint firstExtent = reader.ReadUInt32LE();
                uint numExtents = reader.ReadUInt32LE();
                partList.Add((name, firstExtent, numExtents));
            }

            // 读取 extent 表
            int extDescSize = 16; // num_sectors(8) + target_type(4) + target_data(4)
            var extList = new List<(long NumSectors, uint TargetType, long TargetData)>();
            for (uint i = 0; i < extCount; i++)
            {
                stream.Position = tablesBase + extOffset + i * extDescSize;
                long numSectors = reader.ReadInt64LE();
                uint targetType = reader.ReadUInt32LE();
                long targetData = reader.ReadInt64LE(); // 注意：实际是 uint32，这里读 8 字节保险
                extList.Add((numSectors, targetType, targetData));
            }

            // 组装分区
            foreach (var (name, firstExt, numExt) in partList)
            {
                var sp = new SuperPartition { Name = name };
                for (uint e = 0; e < numExt; e++)
                {
                    int idx = (int)(firstExt + e);
                    if (idx >= extList.Count) break;
                    var (numSec, tgtType, tgtData) = extList[idx];
                    sp.Extents.Add(new Extent
                    {
                        StartSector = tgtType == 0 ? tgtData : 0,
                        NumSectors = numSec,
                        IsZero = tgtType == 1,
                    });
                    sp.TotalSize += numSec * SectorSize;
                }
                partitions.Add(sp);
            }

            return partitions;
        }
        finally
        {
            stream.Position = pos;
        }
    }

    /// <summary>将指定逻辑分区的原始数据提取到输出流。</summary>
    public static void ExtractPartition(Stream superStream, SuperPartition partition, Stream output)
    {
        foreach (var ext in partition.Extents)
        {
            if (ext.IsZero)
            {
                output.Write(new byte[ext.NumSectors * SectorSize]);
            }
            else
            {
                superStream.Position = ext.StartSector * SectorSize;
                superStream.CopyExact(output, ext.NumSectors * SectorSize);
            }
        }
    }
}
