# Performance.md — 性能基准

记录关键路径的耗时对比。数据来源与测量条件同 [Memory.md](./Memory.md)：Release 构建、.NET 10、Windows、单轮测量（IO 主导场景，波动主要来自磁盘缓存）。

## 基准 1：Ext4 大文件提取

样本：含 500MB 单文件的 ext4 镜像，完整 `ExtractTo`。

| 指标 | Before | After | 变化 |
|---|---|---|---|
| 耗时 | 0.5 s | 0.2 s | ↓ 60% |

收益来源：消除 2 次 500MB 内存拷贝（ToArray + trimmed Array.Copy），同步省去大数组分配/回收。

## 基准 2：Boot Ramdisk 打包

样本：合成 ramdisk 目录（50/100/200MB），打包为 cpio → gzip。

| 场景 | Before | After | 变化 |
|---|---|---|---|
| Ramdisk 50MB | 0.9 s | 0.9 s | ≈ |
| Ramdisk 100MB | 1.8 s | 1.8 s | ≈ |
| Ramdisk 200MB | 3.6 s | 3.5 s | ≈ |

结论：ramdisk 打包耗时无明显变化——该路径是**内存优化**（消除 1-1.5GB 驻留），不是 CPU 优化；耗时由 gzip 压缩主导（相同压缩级别）。

## 基准 3：Ext4 镜像构建（inode 表）

| 指标 | Before | After | 变化 |
|---|---|---|---|
| 耗时 | 12.9 s | 16 s | IO 主导，不可比 |

结论：inode 表流式化对耗时无实质影响（写 3.9GB 镜像由磁盘 IO 主导）；收益在内存驻留（见 [Memory.md](./Memory.md) 基准 3）。后续可对比同轮跑（磁盘缓存状态一致）才有意义。

## gzip 输出大小（回归检查）

| 场景 | Before | After | 变化 |
|---|---|---|---|
| Ramdisk 50MB | 52,460,235 B | 52,460,239 B | +4 B |
| Ramdisk 100MB | 104,905,556 B | 104,905,563 B | +7 B |
| Ramdisk 200MB | 209,796,153 B | 209,796,157 B | +4 B |

cpio 内容完全一致（格式、对齐、TRAILER、512 补齐未变），gzip 输出差异为 GZipStream 头部/流状态字节，可忽略。

## 复现

```bash
dotnet run --project _benchmark -- prepare
dotnet run --project _benchmark -- measure after
```
