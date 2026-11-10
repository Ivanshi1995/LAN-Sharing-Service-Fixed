# 局域网文件共享服务修复版

本仓库保存 Windows 局域网文件共享工具的修复版。
本次修复用于处理五项一级安全与可靠性问题。
使用前请确认三项条件：

1. 安装 .NET 8 SDK 和 WebView2 Runtime。
2. 更新后刷新客户端页面。
3. 重新开始旧版本尚未完成的上传。

代码来源：[原始项目](https://github.com/tanbaolong79/LAN-Sharing-Service)。
修复基于原始提交 `a04b5cf20986cec9509fb2045b0355fbecaec026`。
原项目的功能和配置说明见[原始说明](docs/README.upstream.md)。

## 修复范围

| 顺序 | 问题 | 当前行为 |
|---|---|---|
| 1 | 静态文件泄露 | 仅公开 `/CDN`，账户和共享文件经过专用接口。 |
| 2 | 续传会话混用 | 会话绑定用户、目标路径和完整文件校验值。 |
| 3 | 上传权限失效 | 写入和完成时重新检查权限与目标目录。 |
| 4 | 上传资源与文件覆盖 | 限制分块和配额；替换失败时保留旧文件。 |
| 5 | 文件索引并发读写 | 搜索读取不可变快照；目录变化触发重建。 |

文件校验使用 SHA-256 算法。
校验值用于识别文件内容，并核对上传结果。
上传前，客户端会读取完整文件。
完成上传时，目标磁盘需额外容纳一份文件。
如果空间不足，代码拒绝发布文件。

## 编译与启动

1. 在仓库根目录打开终端。
2. 执行 `dotnet build LanFileShare.csproj -c Release`。
3. 打开 `bin/Release/net8.0-windows/LanFileShare.exe`。

如果缺少 .NET 8 桌面运行时，程序无法启动。
如需部署，请同时保留生成目录内的资源和依赖。

## 验证

| 验证项目 | 结果 |
|---|---|
| Windows 发布配置重新编译 | 通过，0 个错误，2 条原有空值警告。 |
| 后端与真实 HTTP 接口场景 | 34 项通过，0 项失败。 |
| 文件校验脚本 | 14 项通过，0 项失败。 |

验证日期为 2026-11-10。
验证范围和限制见[验证说明](docs/verification.md)。
逐项结果见[后端验证记录](docs/verification.json)。
仓库自动构建会执行相同的验证命令。

## 复验

如果已安装 .NET 8 SDK 和 Node.js，请依次执行：

1. 执行 `dotnet build LanFileShare.csproj -c Release`。
2. 执行 `dotnet run --project tests/Regression/Regression.csproj -c Release`。
3. 执行 `node --test tests/fingerprint.test.js`。

## 变更记录

| 日期 | 变更 | 依据 |
|---|---|---|
| 2026-11-10 | 修复五项一级问题，添加回归测试。 | 修复提交 `7b0bee99be93d7eeec52793542fcdf6ea4604b4a` 及其前序提交。 |
| 2026-11-10 | 新仓库添加说明和自动验证。 | `.github/workflows/dotnet-desktop.yml`。 |
