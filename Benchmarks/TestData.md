# TestData.md — 基准样本说明

基准样本由 `_benchmark` 工程合成生成，存放于 `Benchmarks/data/`（已加入 .gitignore，不入库）。

## 样本列表

| 样本 | 大小 | 生成方式 |
|---|---|---|
| `data/ext4_500mb.img` | 512 MB | `Ext4Writer.Build` 构造：目录含单个 500MB 文件 `big.apk`（1MB 随机模式重复填充），block size 4096 |
| `data/ramdisk_50mb/` | ~50 MB | 600 个小文件（256B–4KB，模拟 init 脚本/配置/模块）+ 8MB 块文件填充 |
| `data/ramdisk_100mb/` | ~100 MB | 同上，目标 100MB |
| `data/ramdisk_200mb/` | ~200 MB | 同上，目标 200MB |
| `data/ext4_write_src/` | ~3.8 GB | 单文件 `big.bin`（3.8GB）+ 1000 个 4KB 小文件；`Ext4Writer.Build` 产物 ~3.9GB 镜像（31 组） |

## 为什么不使用真实 ROM 样本

- 厂商刷机包受版权/体积限制，不适合入库与 CI。
- 合成的目标：**构造与真实场景一致的内存压力形态**——大文件（对应真实 ROM 中数百 MB 的 APK/字体）与海量小文件（对应 ramdisk 结构）。
- 真实样本验证由 `_realtest` 负责（真机/真实镜像字节级比对），基准聚焦可复现的量化测量。

## 复现命令

```bash
dotnet run --project _benchmark -- prepare          # 生成读取样本（约 900MB 磁盘）
dotnet run --project _benchmark -- prepare-write    # 生成写入样本（约 3.8GB + 镜像 3.9GB）
dotnet run --project _benchmark -- measure          # 测量读取（默认 after）
dotnet run --project _benchmark -- bench-write      # 测量写入（默认 after）
```

样本生成是确定性的（固定 Random seed 42/7），跨机器可复现。

## 基准数据积累规则

- 每次优化合入后，若涉及上述路径，重跑 `measure` 并更新 Memory.md / Performance.md。
- Before 数据：`git archive HEAD` 导出旧版 → 导出树中运行 `measure before`（_benchmark 工程兼容新旧实现，反射自动选择路径）。
- 记录格式：保持表格 + 测量条件（构建配置 / 框架版本 / 单轮 or 多轮）。
