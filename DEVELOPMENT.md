# 插件开发

本仓库仅维护 QQListener 的 ClassIsland 插件。以下命令均在仓库根目录执行。

## 环境

- .NET 8 SDK，具体版本与滚动策略由 [global.json](global.json) 指定。
- Windows x64、NT QQ 与 ClassIsland，用于真实消息收发和宿主集成验证。

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

双引擎的传输、会话隔离、分发文件身份及上游对照见 [review 记录](docs/reviews/dual-engine.md)。PMHQ 本地签名补丁源码位于 `Tools/PMHQ.LocalSign/`；它的原始 Rust 内核源码不在本仓库。

当前 PMHQ 引擎使用 `PmhqLocalClient`，不运行原 EXE 或 HTTP/WS 服务。`PmhqClient` 与 SampleVerifier 的 `--pmhq-smoke` 仅保留为官方 HTTP 接口的开发对照，不参与插件运行。原生本机初始化及命令管道的隔离验证边界见 review 记录；真实 QQ 收发仍需 Windows 验收。

## 打包

```sh
dotnet build QQListener.csproj -c Release -p:CreateCipx=true -p:RestoreLockedMode=true
```

输出文件为 `cipx/QQListener.cipx` 与 `cipx/checksums.md`。

`CreateCipx=true` 固定使用 `net8.0-windows10.0.19041.0`，并包含 Windows SDK 和 WinRT 运行库。在非 Windows 平台执行同一命令会交叉编译 Windows 包，并不生成其他平台的捕获实现。

打包时默认允许还原依赖。若需要使用 `--no-restore`，须先以相同的 `-p:CreateCipx=true` 参数还原，不能复用普通跨平台构建的还原结果。

## 发布校验

对外发布使用 SemVer（例如 `2.3.0-beta.2`）。ClassIsland 清单与程序集使用四段数字版本（此版本为 `2.3.0.2`），前三段与发布版本一致；第四段是同一版本系列内递增的构建号，后续测试版和正式版均递增，不归零。同步维护以下位置：

- `Directory.Build.props` 中的数字版本 `QQListenerVersion` 与发布版本 `QQListenerReleaseVersion`。
- `manifest.yml` 中的 `version`。
- `CHANGELOG.md` 的最新条目。
- `Tools/QQListener.ReleaseVerifier/ReleaseMetadataVerifier.cs` 中的预期版本及相关测试。

在完成解决方案构建、测试和 CIPX 打包后运行：

```sh
dotnet run --project Tools/QQListener.ReleaseVerifier/QQListener.ReleaseVerifier.csproj -c Release --no-build --no-restore -- --root . --configuration Release
```

验证标签时追加 `--tag <SemVer 发布版本>`。校验包括版本一致性、包结构、内嵌程序集及校验和。历史标签保留，不通过重写标签修正旧版本清单。

## 自动发布

向远端推送与 `QQListenerReleaseVersion` 一致的版本标签即可发布，例如：

```sh
git tag 2.3.0-beta.2
git push origin 2.3.0-beta.2
```

Windows 和 macOS 的构建、测试及界面验证全部通过后，发布任务下载同一次运行中已校验的 Windows 安装包，上传 `QQListener.cipx` 和 `checksums.md`。发布说明取自 `CHANGELOG.md` 中对应版本的条目，并附加 ClassIsland 识别所需的 `CLASSISLAND_PKG_MD5` 校验标记。

带预发布标识的标签会发布为 GitHub Prerelease，不替换最新稳定版；正式版才更新 Latest。历史四段式标签仍可识别。

附件上传完成前 Release 保持草稿；失败后可重跑工作流继续发布。已经公开的 Release 不会被重跑覆盖。普通分支推送、Pull Request 和手动触发只执行检查，不公开 Release。

发布脚本的离线验证使用模拟 GitHub CLI，不访问真实 Release：

```sh
bash Tests/Release/run.sh
```

## 代码位置

- `Protocol/`：消息帧和富消息元素解析。
- `Oidb/`：控制管道请求、图片下载凭证、附件地址与回复协议。
- `Core/`：内核安装、运行状态与暂停积压。
- `Native/`：QQ 进程发现、手动映射与捕获连接。
- `Services/`：规则处理、消息队列、提醒生命周期与附件下载。
- `Views/`：设置页面和消息窗口。
- `Tests/`：协议、模型、服务与发布校验测试。

## Loader 与核心分发

`Bootstrap/QQListener.Loader.csproj` 是 manifest 入口，`QQListener.dll` 保留业务逻辑和 Avalonia 资源。Loader 与核心通过 `InitializeCore` ABI 1 连接，ClassIsland / Avalonia / DI 合同由宿主共享，业务依赖从独立目录解析。核心初始化只能在宿主建立服务容器前执行；运行期间下载的更新在下次启动应用。

`-p:CreateCipx=true` 调用 `Tools/QQListener.PackageBuilder`，同时输出：

- `cipx/QQListener.cipx`：Loader 与内置核心。
- `cipx/QQListener.Download.cipx`：仅 Loader。
- `cipx/QQListener.Core.zip` 和 `core-release.json`：核心独立发布输入。

main 构建在双平台检查通过后由 `Tools/PublishRuntime.sh` 发布到本仓库 `runtime-builds` prerelease。核心资源按 SHA-256 命名，清单按构建编号排序，禁止较旧的并发构建覆盖较新版本。公开 Release 资源支持匿名客户端；Actions artifacts 仍用于 CI 作业间传递。

仓库 Secret `RUNTIME_SIGNING_KEY` 保存 P-256 私钥；公钥为 `Assets/Updates/runtime-public.pem`。私钥不得提交到 Git。更新清单使用 SHA-256 / ECDSA DER 签名，代理只负责传输。轮换公钥需要先发布带有新信任根的 Loader。

新增 PMHQ 适配时，在 `Distribution/pmhq/` 增加版本 JSON，并将补丁加入 `Assets/PMHQ/win-x64/pmhq-v<RuntimeVersion>.patch`。JSON 包含上游包、原始 DLL、补丁及输出哈希、长度、宿主 ABI 和内存布局。CI 发布这些文件，但不会把补丁嵌入核心程序集。没有匹配记录的上游版本不会自动安装。

持久化结构：`runtime/versions/<sha256>/` 为已验证文件，旁边保存对应原始 ZIP；`active.json`、`pending.json`、`previous.json` 记录版本指针，`rejected/` 记录加载失败的构建。安装使用临时目录与原子移动，启动时重新校验原始包和文件清单。原生内核仍位于 `native/`，与托管核心更新独立。
