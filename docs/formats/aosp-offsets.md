# AOSP 镜像格式关键偏移（实测核对）

> 来源：AOSP `bootimg.h` / osm0sis mkbootimg，并在本项目用小米 nezha HyperOS 3.0 线刷包真实样本验证过。
> **改任何解析/写出代码前先对照本表，并按字节位置写断言，不要用自身引擎的往返自洽当验证。**

## boot.img（v0 / v1 / v2 / v3 / v4）

| 字段                        | 偏移  | 大小   | 说明                                    |
| ------------------------- | --- | ---- | ------------------------------------- |
| magic                     | 0   | 8    | `ANDROID!`                            |
| kernel_size               | 8   | 4    |                                       |
| kernel_addr               | 12  | 4    | 加载地址，**必须保留原值，不能写 0**                |
| ramdisk_size              | 16  | 4    |                                       |
| ramdisk_addr              | 20  | 4    | 同上                                    |
| second_size               | 24  | 4    |                                       |
| second_addr               | 28  | 4    |                                       |
| tags_addr                 | 32  | 4    |                                       |
| page_size                 | 36  | 4    |                                       |
| header_version            | 40  | 4    | v3/v4 起此字段存在                          |
| os_version / patch level  | 44+ | —    |                                       |
| cmdline                   | —   | 1536 |                                       |

版本差异：

- **v3 头紧凑布局，header_size = 1580**（kernel@8 / ramdisk@12 / os_version@16 / header_size@20 /
  reserved[4]@24 / header_version@40 / cmdline@44[1536]）
- **v4 = v3 + signature_size@1580，header_size = 1584**（不是 1580）
- 各段之间按 page_size 对齐的填充（gap）**必须原样回写**，否则真机镜像往返会差几十字节

## vendor_boot.img

| 字段                              | v3 偏移 | v4 偏移 | 大小   |
| ------------------------------- | ----- | ----- | ---- |
| magic                           | 0     | 0     | 8    |
| header_version                  | 8     | 8     | 4    |
| page_size                       | 12    | 12    | 4    |
| kernel_addr                     | 16    | 16    | 4    |
| ramdisk_addr                    | 20    | 20    | 4    |
| vendor_ramdisk_size             | 24    | 24    | 4    |
| cmdline                         | 28    | 28    | 2048 |
| tags_addr                       | 2076  | 2076  | 4    |
| name                            | 2080  | 2080  | 16   |
| header_size                     | 2096  | 2096  | 4    |
| dtb_size                        | 2100  | 2100  | 4    |
| dtb_addr                        | 2104  | 2104  | 8    |
| vendor_ramdisk_table_size       | —     | 2112  | 4    |
| vendor_ramdisk_table_entry_num  | —     | 2116  | 4    |
| vendor_ramdisk_table_entry_size | —     | 2120  | 4    |
| bootconfig_size                 | —     | 2124  | 4    |
| **头大小**                         | **2112** | **2128** |      |

> 历史坑：本项目曾误用 3136 / 3140（整体偏移 1024 字节），导致 4.3 MB 的 DTB 全部丢失、加载地址被写成 0。

## Android sparse image

头 28 字节：

| 字段             | 偏移 | 大小 | 说明                      |
| -------------- | -- | -- | ----------------------- |
| magic          | 0  | 4  | `0xED26FF3A`            |
| major_version  | 4  | 2  | u16（曾误写成 4 字节，导致解析整体错位） |
| minor_version  | 6  | 2  | u16                     |
| file_hdr_sz    | 8  | 2  | 28                      |
| chunk_hdr_sz   | 10 | 2  | 12                      |
| blk_sz         | 12 | 4  |                         |
| total_blks     | 16 | 4  |                         |
| total_chunks   | 20 | 4  |                         |
| image_checksum | 24 | 4  |                         |

chunk 头 12 字节：`type(u16) + reserved(u16) + blocks(u32) + total_sz(u32)`

- `0xCAC1` raw / `0xCAC2` fill / `0xCAC3` don't-care / `0xCAC4` crc32
- **don't-care 单个 chunk 可超过 2 GiB**（真机 14.25 GiB super.img 中实测最大 2.62 GiB）——
  必须分块循环写出，`new byte[chunkBlocks * blkSz]` 会 `OverflowException`

## super.img（liblp 动态分区）

- 4096 保留区 + 4096 geometry slot（含 32 字节 SHA256 校验，magic `0x616C4467`）
- metadata slot 起始 **8192**（保留 4096 + 主备 geometry 各 4096），magic `0x414C5030`
- header 256 字节：version / header_size / tables_size + 4 个表描述符
  （partition / extent / group / block_device）
- 表偏移相对 `header_size` 末尾；条目大小：partition 52 / extent 24 / group 48 / block_device 64

## 验证方式

```bash
# 自测（159 项，其中 25 项为按本表字节位置的规范断言）
dotnet run --project _selftest

# 真机样本：boot 类镜像做字节级往返比对，报告首个差异位置
cd _realtest && dotnet run -- boot <镜像文件>
```
