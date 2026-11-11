# 局域网文件共享服务修复版

本工具通过一个 EXE 共享电脑中的文件。
手机和其他电脑通过浏览器访问共享文件。
使用前请确认三项条件：

1. 使用 Windows 10 或 11 的 64 位电脑。
2. 将访问设备连接到同一局域网。
3. 共享期间保持电脑和程序运行。

## 下载与使用

[下载单文件 EXE](https://github.com/Ivanshi1995/LAN-Sharing-Service-Fixed/releases/latest/download/LanFileShare.exe)。
仓库为私有仓库，下载前需要登录获授权的账号。

1. 双击 `LanFileShare.exe`。
2. 点击“公共共享”中的“选择共享文件夹”。
3. 勾选文件夹，再点击“确认选择”。
4. 打开“启用公共共享”开关。
5. 复制“公共共享地址”。
6. 在其他设备的浏览器中打开该地址。

如果需要上传文件，将公共共享改为“读写”。
公共共享无需登录，请只选择允许共享的目录。
如果需要账户控制，在管理页面添加用户。
再为该用户设置密码、共享目录和读写权限。

EXE 已包含 .NET 运行环境和网页资源。
如果电脑缺少 WebView2，程序改用默认浏览器。
关闭程序会停止共享。

支持的操作见[功能清单](docs/功能清单.md)。

设置、账户和聊天数据保存在以下目录：

`%LOCALAPPDATA%\LanFileShare-Fixed`

更新时，退出程序后替换 EXE。
保留上述目录即可保留设置和账户。
单文件版不使用原项目的增量更新。

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

## 从源码编译

仅开发者需要安装 .NET 8 SDK。
普通用户直接下载上面的 EXE。

1. 在仓库根目录打开终端。
2. 执行 `dotnet build LanFileShare.csproj -c Release`。
3. 打开 `bin/Release/net8.0-windows/LanFileShare.exe`。

上述命令生成依赖本机运行环境的开发版本。
单文件发布命令为：

`dotnet publish LanFileShare.csproj -p:PublishProfile=SingleFile -o publish`

发布目录仅包含 `LanFileShare.exe`。

## 验证

| 验证项目 | 结果 |
|---|---|
| Windows 发布配置重新编译 | 通过，0 个错误，2 条原有空值警告。 |
| 后端与真实 HTTP 接口场景 | 34 项通过，0 项失败。 |
| 文件校验脚本 | 14 项通过，0 项失败。 |
| 最终 EXE 的接口与资源验证 | 23 项通过，0 项失败。 |
| EXE 重启后的数据保存 | 通过。 |

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
| 2026-11-10 | 发布 v2.4.1 单文件版。 | `Properties/PublishProfiles/SingleFile.pubxml`。 |
| 2026-11-11 | 显示共享文件夹入口，修复窗口缩放。 | `lan-file-share-server.html` 和 `MainForm.cs`。 |
