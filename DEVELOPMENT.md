# 插件开发

本仓库仅维护 QQListener 的 ClassIsland 插件。以下命令均在仓库根目录执行。

## 环境

- .NET 8 SDK，具体版本与滚动策略由 [global.json](global.json) 指定。
- Windows x64、NT QQ 与 ClassIsland，用于真实消息收发和宿主集成验证。
- PowerShell 7（`pwsh`），用于打包阶段生成校验文件。

普通构建和测试按当前平台选择目标。macOS、Linux 上可以运行跨平台测试和离线界面预览，但不支持原生 QQ 捕获。

## 构建与测试

```sh
dotnet restore QQListener.sln --locked-mode
dotnet build QQListener.sln -c Release --no-restore
dotnet test QQListener.sln -c Release --no-build --no-restore
```

离线界面验证：

```sh
dotnet run --project Samples/QQListener.UiPreview/QQListener.UiPreview.csproj -c Release --no-build --no-restore -- --render artifacts/ui-previews
```

预览文件输出至 `artifacts/ui-previews/`。测试使用离线样例、模拟管道和 HTTP 响应，不需要登录 QQ，也不能替代 Windows 上的实际收发验证。

图片协议的独立交叉对照数据及再生成方法见 [Fixtures](Tests/QQListener.Protocol.Tests/Fixtures/README.md)。

## 打包

```sh
dotnet build QQListener.csproj -c Release -p:CreateCipx=true -p:RestoreLockedMode=true
```

输出文件为 `cipx/QQListener.cipx` 与 `cipx/checksums.md`。

`CreateCipx=true` 固定使用 `net8.0-windows10.0.19041.0`，并包含 Windows SDK 和 WinRT 运行库。在非 Windows 平台执行同一命令会交叉编译 Windows 包，并不生成其他平台的捕获实现。

打包时默认允许还原依赖。若需要使用 `--no-restore`，须先以相同的 `-p:CreateCipx=true` 参数还原，不能复用普通跨平台构建的还原结果。

## 发布校验

发布版本使用四段式版本号，并同步维护以下位置：

- `Directory.Build.props` 中的 `QQListenerVersion`。
- `manifest.yml` 中的 `version`。
- `CHANGELOG.md` 的最新条目。
- `Tools/QQListener.ReleaseVerifier/ReleaseMetadataVerifier.cs` 中的预期版本及相关测试。

在完成解决方案构建、测试和 CIPX 打包后运行：

```sh
dotnet run --project Tools/QQListener.ReleaseVerifier/QQListener.ReleaseVerifier.csproj -c Release --no-build --no-restore -- --root . --configuration Release
```

验证标签时追加 `--tag <四段式版本号>`。校验包括版本一致性、包结构、内嵌程序集及校验和。历史标签保留，不通过重写标签修正旧版本清单。

## 代码位置

- `Protocol/`：消息帧和富消息元素解析。
- `Oidb/`：控制管道请求、图片下载凭证、附件地址与回复协议。
- `Core/`：内核安装、运行状态与暂停积压。
- `Native/`：QQ 进程发现、手动映射与捕获连接。
- `Services/`：规则处理、消息队列、提醒生命周期与附件下载。
- `Views/`：设置页面和消息窗口。
- `Tests/`：协议、模型、服务与发布校验测试。
