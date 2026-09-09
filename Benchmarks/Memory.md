# Memory.md — 内存基准

记录关键路径的峰值内存对比。所有数据通过 `_benchmark` 工程实测（Release 构建、.NET 10、Windows），Before 来自 git HEAD `5b3d40d` 的代码，After 为当前工作区代码。

> 主指标使用 **Managed Heap Peak**（GC.GetTotalMemory 峰值）：直接反映托管分配，不受 OS 内存复用影响，Before/After 可比性最强。Private Peak Delta 受采样时机影响，仅作参考。

## 基准 1：Ext4 大文件提取

样本：含 500MB 单文件（`big.apk`）的 ext4 镜像，`ExtractTo` 完整提取。

| 指标 | Before (5b3d40d) | After (当前) | 变化 |
|---|---|---|---|
| Managed Heap Peak | 1013 MB | **3 MB** | **↓ ~338×** |
| Private Peak Delta | 3 MB* | 2 MB | — |
| Gen2 GC 次数 | 5 | 1 | ↓ 4 次 |
| 耗时 | 0.5 s | 0.2 s | ↓ 60% |

\* Before 的 Private 采样在 25ms 间隔下错过峰值窗口（托管堆 1013MB 未反映到私有内存增量）；Managed Heap 为可靠指标。

改动：`Ext4Reader.ReadFileData`（MemoryStream + ToArray + trimmed 三段拷贝）→ `WriteFileData(Stream)` 直接落盘。

## 基准 2：Boot Ramdisk 打包

样本：合成 ramdisk 目录（600 小文件 + 8MB 块文件填充），打包为 cpio → gzip。

| 场景 | Before Managed Heap | After Managed Heap | 变化 |
|---|---|---|---|
| Ramdisk 50MB | 1315 MB | **147 MB** | ↓ ~9× |
| Ramdisk 100MB | 772 MB* | **505 MB** | ↓ ~1.5× |
| Ramdisk 200MB | 1581 MB | **613 MB** | ↓ ~2.6× |

\* 100MB 场景 Before 波动较大（GC 状态影响），量级结论不受影响。

改动：`RomPackService.CreateCpio`（每文件 ReadAllBytes + MemoryStream 全量累积 + ToArray）→ `WriteCpio(Stream)` 边读边写进 GZipStream。

## 基准 3：Ext4 镜像构建（inode 表）

样本：3.8GB 单文件 + 1000 小文件目录，`Ext4Writer.Build` 写为 ~3.9GB 镜像（31 个块组；inode 表驻留 = 组数 × 2MB/组）。

| 指标 | Before (5b3d40d) | After (当前) | 变化 |
|---|---|---|---|
| Managed Heap Peak | 85 MB | **55 MB** | ↓ 35% |
| Private Peak Delta | 83 MB | 46 MB | ↓ 45% |
| Gen2 GC 次数 | 4 | 5 | ≈（波动） |
| 耗时 | 12.9 s | 16 s | IO 主导，不可比 |

改动：`inodeTables = new byte[groups][]` 全量驻留（8GB 分区 = 64 组 ≈ 128MB，随分区线性增长）→ 按组逐组填充并立即写入，驻留从 O(组数×2MB) 降到 O(单组 2MB)。

> **剩余热点（已定位，未改）**：After 的 55MB 峰值来自数据写入段——每数据块 `new byte[4096]`（92.7 万次分配，GC Gen0 段提交）+ `InodeEntry.DataBlocks` 全量 `List<ulong>` 驻留（3.8GB 文件 ≈ 7.4MB）。后续可复用块缓冲 + 流式 DataBlocks。

## 结论

- **Ext4 提取是本次最大收益**：托管堆峰值从 1GB 级降到 3MB，同时耗时减半（省去两次 500MB 拷贝）。
- **Ramdisk 打包**：从 GB 级降到百 MB 级；After 的剩余内存来自 gzip 结果 `MemoryStream.ToArray()`（产品真实行为），后续可将 gzip 结果直接流式写文件再降一档。
- **Ext4 写入（inode 表）**：结构性驻留已消除（全量表 → 单组表）；剩余峰值转移到数据写入段，构成下一批优化点。
- 三个改动均消除 LOH 大对象分配（Gen2 压力同步下降）。

## 复现

```bash
dotnet run --project _benchmark -- prepare        # 生成样本到 Benchmarks/data/
dotnet run --project _benchmark -- measure after   # 当前版本
# Before：git archive HEAD 导出后，在导出树中运行 measure before
```
