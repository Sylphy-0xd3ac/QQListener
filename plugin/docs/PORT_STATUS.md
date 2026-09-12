# ClassIsland 迁移工作区状态

## 2026-09-12：续接 Claude 最后待办

继续会话 `a3cdcfd2-c3a1-4a39-8d13-a0d1cd0dfa94` 在 9 月 6～7 日提出的声音、PendingQueue 和构建包待办。
9 月 5 日之后 Claude 又修改过此工作树，因此下方 9 月 5 日快照仅作历史记录，不代表当前代码状态。

- 提示音和 TTS 在同一「声音」配置项中独立选择，默认关提示音、开 TTS，旧配置缺字段时也采用这些默认值。
- 修复核心状态 PID 丢失：已映射或复用外部钩子的进程从候选列表排除后，空闲状态汇报会保留仍然活跃的真实 PID，不再用默认参数 `0` 覆盖；并在扫描空闲与注入返回两处重新读取最新连接状态，避免管道已经连接却又被覆盖回 Waiting。界面对尚无真实 PID 的启动状态不显示 `pid=0`。
- 打包原版 `asset/notify_sound.mp3`、`asset/important_sound.mp3`，通过 ClassIsland 的提醒设置指定插件目录中的绝对路径，普通／重要和呼叫按原版对应音效选择。
- 显式清空遮罩工厂默认填入的语音文本，正文或摘要只朗读一次。
- 暂停恢复时把积压合成一个摘要；超限丢弃旧消息并显示条数，卸载时清空。媒体、附件和引用按每条消息保留。
- 摘要 TTS 念前三条并报告总数、未念数和超限数；停留时间取各条消息设置中的最长值。回复入口显示最后一条消息的目标。
- 实际 `QqMessageWindow` 控件的离线渲染已验证两条跨会话消息、正文和引用图片、文件卡片及明确的回复目标；图像在 `artifacts/ui-previews-20260912/`。
- 最终 macOS Release 构建为 0 警告、0 错误；离线测试 **474/474** 通过（243 + 231），23 个 Toast 样例通过。
- Windows 整套 Release 交叉编译为 0 警告、0 错误；CIPX 版本与校验和验证通过，包内两份音效与原始 MP3 字节完全一致。桌面包 MD5：`2ea887656fbb83ad9891a147f5d34fe4`。
- 本次没有运行真实 Windows QQ，也未在 Windows ClassIsland 内进行扬声器试听；交叉编译与离线测试不代表这些运行时验收已经完成。

## 2026-09-05 历史快照

2026-09-05，分支 `worktree-classisland-plugin-port`。

## 与 Claude 原计划的对应

原计划为 `classisland-fluffy-starfish.md`，对应 Claude 会话 `a3cdcfd2-c3a1-4a39-8d13-a0d1cd0dfa94`。本目录继续该工作区，不另起插件或删除旧 Python 实现。

- 阶段 0：SDK 统一为 `ClassIsland.PluginSdk / Core 2.1.0.1`；所有插件相关工程统一目标框架，macOS 为 `net8.0`、Windows 为 `net8.0-windows10.0.19041.0`。Windows 通知 API 实现仍条件编译。
- 阶段 1、2：保留 Claude 的协议、管道和 OIDB 代码及离线测试；尚无真实 Windows QQ 验收，不能视为运行时已接通。
- 阶段 3、4：没有继续实现进程注入、挂钩输入或相关内核安装与接线；旧 Toast 数据源仍保留。
- 阶段 5：富消息窗口、下载、引用、自动关闭和本地样例验证已接入；真实输入、回复、Windows 宿主调度和语音验收仍未完成。

## 工程与自动检查

- 原有 `QQListener.Tests` 已升级到当前 SDK 并可在 macOS 运行；无 WinRT 依赖的 `WindowsNotificationReader` 状态机在所有平台编译测试，非 Windows 运行时依然使用占位输入源。
- 协议测试与实际控件预览工程已加入 `QQListener.sln`，不再游离于整套构建之外。
- 各项目按目标框架保存 `packages.net8.0.lock.json` / `packages.net8.0-windows10.0.19041.0.lock.json`，避免跨平台还原相互改写；SDK 固定文件、锁文件与样例 JSON 不再被根目录忽略规则遗漏。
- 根目录 `.github/workflows/plugin.yml` 才是本仓库实际生效的插件自动检查入口：Windows/macOS 锁定还原、Release 构建、两套测试、23 个 Toast 样例和无窗口 UI 验证。只上传测试结果及预览图，不发布插件包；原 Python 构建流程不改动。
- `plugin/.github/workflows/build.yml` 为继承的独立插件发布模板，在本仓库的子目录中不会被 GitHub 执行。两套测试的 TRX 文件使用独立名称，避免结果互相覆盖。

## 已接上的展示功能

- `MessageEvent.Segments` 保留富消息结构，旧 Toast 的纯文本正文仍可显示。
- 正文与 @ 连续排版；图片自动加载，附件显示独立卡片；引用递归显示正文和媒体。
- 顶栏继续由 ClassIsland 显示发送者；正文不再重复显示“新消息”、时间与标题栏，也不显示号码或关闭按钮。消息一律按设定时长自动关闭，点击、滚动与聚焦都不延长计时。
- 正文使用原生 `AcrylicBlur`（不支持时尝试 `Blur`）与亚克力材质，不再直接透出背后的清晰文字；窗口与卡片贴合，不留外侧模糊边框。
- 布局参照原版 Python `MessageEntry` / `MessageBlock`：引用在上、正文在下；引用使用主题色竖线与较小缩略图；图片不套大块背景卡片，文件与视频使用紧凑横排卡片。
- macOS 原生模糊容器与 XAML 卡片使用相同圆角，清除容器后方的方形窗口底色，避免边角露出方块。原生测试已确认容器圆角与裁切属性生效。
- 下载目录在插件设置中配置；文件完成后使用系统默认应用打开，未知文件类型打开下载目录。
- 图片下载上限 12 MiB，其他附件 100 MiB；流读取有超时，失败或取消时移除未完成文件。同名附件不会覆盖已有文件。
- 设置页“测试提醒”提供文字、图片与附件、引用消息三个离线样例，使用同一套实际窗口控件。

## 本地验证入口

2026-09-05 本机验证：两套离线测试 **357/357** 通过（原有功能及工程检查 250、协议与展示 107），23 个 Toast 样例全部通过，实际控件预览与交互验证通过。macOS Release 与 Windows 整套交叉编译均为 0 警告、0 错误；两种目标的锁定依赖还原均通过，Windows 构建的版本元数据校验通过。

Windows 交叉编译不代表已经在 Windows 宿主运行验收；新增 GitHub 工作流也需要提交后才能实际触发。

```sh
cd plugin
dotnet restore QQListener.sln --locked-mode
dotnet build QQListener.sln -c Release --no-restore -p:ContinuousIntegrationBuild=true
dotnet test QQListener.sln -c Release --no-build --no-restore
dotnet run --project Samples/QQListener.SampleVerifier/QQListener.SampleVerifier.csproj -c Release --no-build --no-restore
dotnet run --project Samples/QQListener.UiPreview/QQListener.UiPreview.csproj -c Release --no-build --no-restore -- --render artifacts/ui-previews
```

最后一条命令使用 Avalonia 无窗口渲染生成 `Text.png`、`Media.png`、`Quote.png`，并检查图片、无标题/底部操作栏、延迟窗口取消、交互后仍到时自动关闭，以及模拟 HTTP 图片下载完成后的显示状态。测试不联系 QQ 或外部附件地址。

超出可视区域的样例还会生成 `*-scrolled.png`，用于检查滚动后的附件卡片。

交互式打开同一套预览窗口：

```sh
dotnet run --project Samples/QQListener.UiPreview/QQListener.UiPreview.csproj
```

此独立预览宿主不包含 ClassIsland 的发送者顶栏、声音和提醒调度。完整宿主内预览使用插件设置页的三个测试按钮。媒体样例的图片使用随插件打包的图标；样例文件与视频没有远端地址，不假装能够下载。

本机 macOS 使用已经安装的 x64 SDK：把上述 `dotnet` 替换为 `arch -x86_64 /Users/duzexuan/.dotnet-x64/dotnet`。

`--native-check artifacts/ui-previews` 使用真实窗口检查系统实际启用的透明模式。本机已确认返回 `AcrylicBlur`。可额外传入 `--capture` 请求系统截图；截图需要屏幕录制权限，本机命令行截图失败，因此不把无窗口渲染图当作原生背景模糊的像素验收。

macOS 上交叉编译 Windows 目标（仅编译，不运行 Windows API）：

```sh
dotnet restore QQListener.sln --locked-mode -p:QQListenerTargetFramework=net8.0-windows10.0.19041.0 -p:EnableWindowsTargeting=true
dotnet build QQListener.sln -c Release --no-restore -p:QQListenerTargetFramework=net8.0-windows10.0.19041.0 -p:EnableWindowsTargeting=true -p:ContinuousIntegrationBuild=true
```

切回本机目标时重新运行不带覆盖参数的 `dotnet restore QQListener.sln --locked-mode`，以刷新 `obj` 里的资产文件。修改包版本后先分别生成两个目标的锁文件，再恢复锁定检查。

## 尚未完成

- 本工作区保留 Claude 已写的协议、管道与 OIDB 文件；这次没有继续实现进程注入与挂钩捕获。
- 运行时输入仍是原有 Windows 通知中心；真实 QQ 富消息输入和回复没有接到界面。
- 未完成内核安装向导、原生生命周期迁移或真实 Windows QQ 端到端验证，不能把当前版本当作完整迁移版发布。
- Windows 默认应用打开、ClassIsland 实际宿主内的提醒调度与语音仍需在相应环境验证。
