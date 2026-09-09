namespace RomPackageMaker.Engine;

/// <summary>单个 MOVE 操作的重叠分类。</summary>
public enum MoveOverlapKind
{
    /// <summary>src 与 dst 无重叠块：chunk 前向复制直接安全。</summary>
    NoOverlap,

    /// <summary>存在重叠块，但所有重叠块在读取序列中先于写入序列：chunk 前向复制仍安全。</summary>
    OverlapForwardSafe,

    /// <summary>存在"先写后读"的重叠块：chunk 前向复制会破坏数据，需要反向拷贝 / 分段缓冲 / 整读。</summary>
    OverlapForwardUnsafe,
}

/// <summary>一个分区内 MOVE 操作的统计汇总。</summary>
public sealed class MoveStats
{
    public string PartitionName { get; init; } = string.Empty;
    public int MoveCount { get; init; }
    public long TotalMoveBytes { get; init; }
    public long MaxSrcBytes { get; init; }
    public long MaxDstBytes { get; init; }
    public int NoOverlapCount { get; init; }
    public int OverlapSafeCount { get; init; }
    public int OverlapUnsafeCount { get; init; }

    public bool AnyForwardUnsafe => OverlapUnsafeCount > 0;
}

/// <summary>
/// MOVE 操作重叠分析器（只读诊断，不改动任何行为）。
/// 判断依据：chunk 前向复制（边读边写）是否安全。
/// 对每个 MOVE 操作，按 update_engine 语义模拟"从 src extents 顺序读取、按 dst extents 顺序写入"：
///   - 块同时属于 src 与 dst 时为重叠块；
///   - 若存在重叠块且其"写入序列号 &lt; 读取序列号"（先写后读），前向 chunk 复制会读到被覆盖的数据 → 不安全；
///   - 其余情况（无重叠，或重叠块均先读后写）→ 前向 chunk 复制安全。
/// 当前实现（整读再写）在任何重叠下都正确，此分析用于决定优化方案的适用性。
/// </summary>
public static class PayloadMoveAnalyzer
{
    public static List<MoveStats> Analyze(PayloadInfo info)
    {
        var results = new List<MoveStats>();
        foreach (var part in info.Partitions)
        {
            results.Add(AnalyzePartition(part));
        }
        return results;
    }

    public static MoveStats AnalyzePartition(PayloadPartitionInfo part)
    {
        int moveCount = 0, noOverlap = 0, safe = 0, unsafeCount = 0;
        long totalBytes = 0, maxSrc = 0, maxDst = 0;

        foreach (var op in part.Operations)
        {
            if (op.Type != PayloadOpType.Move) continue;
            moveCount++;

            long srcBytes = op.SrcExtents.Sum(e => (long)e.NumBlocks * part.BlockSize);
            long dstBytes = op.DstExtents.Sum(e => (long)e.NumBlocks * part.BlockSize);
            totalBytes += srcBytes;
            if (srcBytes > maxSrc) maxSrc = srcBytes;
            if (dstBytes > maxDst) maxDst = dstBytes;

            switch (Classify(op, part.BlockSize))
            {
                case MoveOverlapKind.NoOverlap: noOverlap++; break;
                case MoveOverlapKind.OverlapForwardSafe: safe++; break;
                default: unsafeCount++; break;
            }
        }

        return new MoveStats
        {
            PartitionName = part.Name,
            MoveCount = moveCount,
            TotalMoveBytes = totalBytes,
            MaxSrcBytes = maxSrc,
            MaxDstBytes = maxDst,
            NoOverlapCount = noOverlap,
            OverlapSafeCount = safe,
            OverlapUnsafeCount = unsafeCount,
        };
    }

    public static MoveOverlapKind Classify(PayloadOperation op, long blockSize)
    {
        if (op.Type != PayloadOpType.Move) return MoveOverlapKind.NoOverlap;

        // 展开 src / dst 的块读取/写入序列
        var readOrder = ExpandBlocks(op.SrcExtents, blockSize);
        var writeOrder = ExpandBlocks(op.DstExtents, blockSize);
        if (readOrder.Count == 0 || writeOrder.Count == 0) return MoveOverlapKind.NoOverlap;

        // 记录每个块在读取序列中的位置；仅出现在 src 中的块无写入，无需判断
        var readIndex = new Dictionary<ulong, int>(readOrder.Count);
        for (int i = 0; i < readOrder.Count; i++)
        {
            // 同一块可能在 src extents 中重复出现（异常），取最早读取位置
            if (!readIndex.ContainsKey(readOrder[i])) readIndex[readOrder[i]] = i;
        }

        // 枚举写入序列：若目标块同时是源块，且写入序 < 读取序 → 先写后读 → 不安全
        bool anyOverlap = false;
        for (int w = 0; w < writeOrder.Count; w++)
        {
            if (readIndex.TryGetValue(writeOrder[w], out int r))
            {
                anyOverlap = true;
                if (w < r) return MoveOverlapKind.OverlapForwardUnsafe;
            }
        }

        return anyOverlap ? MoveOverlapKind.OverlapForwardSafe : MoveOverlapKind.NoOverlap;
    }

    private static List<ulong> ExpandBlocks(List<PayloadExtent> extents, long blockSize)
    {
        var list = new List<ulong>();
        foreach (var ext in extents)
        {
            for (ulong b = ext.StartBlock; b < ext.EndBlock; b++) list.Add(b);
        }
        return list;
    }
}
