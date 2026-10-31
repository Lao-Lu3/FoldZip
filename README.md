# FoldZip

免费开源的 Windows 压缩工具（C# / WPF）。

![平台](https://img.shields.io/badge/platform-Windows%2010%2F11-blue) ![协议](https://img.shields.io/badge/license-MIT-green) ![版本](https://img.shields.io/badge/version-0.1.0-orange)

## 特性

- **自研压缩内核**：ZIP 读写、TAR 读写、GZip 读写均为纯 C# 实现（不依赖 System.IO.Compression 的封装层），完整解析 ZIP 本地头 / 中央目录 / EOCD / ZIP64（读取）
- **格式支持**
  - 读取 + 创建：`zip` `tar` `tar.gz` `tgz` `gz`
  - 读取（外部引擎）：`7z` `rar` —— 把 `7z.exe` 放到程序目录（或 `engines` 子目录）即可
  - 顺带支持以 ZIP 为容器格式的 `jar` `apk` `docx` 等的浏览与解压
- **中文文件名兼容**：UTF-8 标志名正确解码；无标志的旧包自动回退 GB18030，解决乱码问题
- **安全**：解压前逐条目做路径穿越（zip-slip）检测；TAR 的符号链接/硬链接条目列出但不落盘
- **完整性**：解压时逐条目做 CRC32 校验，GZip 做 CRC32 + ISIZE 双校验
- **界面**：亮/暗双主题、拖拽添加、进度条、常驻日志区（错误不自动消失）、一键复制日志
- **单文件便携**：发布产物为单个 exe，无需安装

## 界面预览

（发布时补截图）

## 下载与运行

从 [Releases](../../releases) 下载 `FoldZip.exe`：

- **框架依赖版**（约几 MB）：需要先安装 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- **自包含版**（体积较大）：解压即用，无需安装任何运行时

## 从源码构建

```bash
# 需要 .NET 10 SDK（Windows）
dotnet build FoldZip.slnx -c Release

# 运行内核自测（zip/tar/gzip 往返 + 与 .NET BCL 互操作验证）
dotnet run --project src/FoldZip.SelfTest -c Release

# 运行界面
dotnet run --project src/FoldZip.App -c Release

# 发布单文件
dotnet publish src/FoldZip.App -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -o publish
```

## 内核自测覆盖

`FoldZip.SelfTest` 会对同一棵测试目录（含中文文件名、空文件、二进制、深层嵌套、超长 TAR 文件名）做：

1. 自研写 → 自研读 往返比对
2. .NET BCL 写 → 自研读（互操作）
3. 自研写 → .NET BCL 读（互操作）
4. ZIP Store 模式、TAR 长名（ustar 前缀拆分 + GNU 'L' 回退）、GZip trailer 校验、tar.gz 链式解压
5. 7z 外部引擎（检测到 7z.exe 时自动参与）

## 路线图

- [ ] ZIP64 写入（>4GB / >65535 条目）
- [ ] 内置 Deflate 之外的现代算法（Zstd / Brotli 容器）
- [ ] 7z 创建（接入 LZMA SDK）
- [ ] Windows 右键菜单集成
- [ ] 分卷压缩与加密压缩包（读取）

## 许可证

[MIT](LICENSE)
