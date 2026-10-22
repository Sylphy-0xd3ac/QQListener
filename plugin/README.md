# QQListener

QQListener 是 ClassIsland 插件：通过 SnowLuma 官方内核捕获新版 **NT QQ** 的聊天消息，按规则显示提醒。原生捕获仅支持 Windows x64；TIM 不在支持范围内。

## 五步快速开始

1. 安装并登录新版 NT QQ。
2. 在 ClassIsland 中安装 QQListener，打开插件设置。
3. 在「高级设置 → 重新触发安装向导」中阅读许可说明并安装官方内核。
4. 开启监听，配置需要提醒的群聊、联系人和关键词。内核安装后及下次加载插件时会自动装载，无需重新点击装载。
5. 发送一条测试消息，在设置页确认核心连接状态与最近接收结果。

只接收聊天消息，撤回、群成员变化、请求、回执和私聊控制事件不会生成提醒。默认排除自己发送的消息，按协议中的发送者 QQ 号与当前账号比较；可以在设置中关闭排除。

提醒背景跟随宿主岛的实际背景、透明度与明暗主题。正文从岛的下沿滑出，到时或取消后向上收回；性能模式关闭动画与模糊。

## 声音与暂停积压

设置页「声音」可分别选择提示音和 TTS，默认关闭提示音、开启 TTS。提示音使用 QQL 原版
`notify_sound.mp3`（普通消息）与 `important_sound.mp3`（重要／呼叫消息），由 ClassIsland 播放。

暂停监听期间，符合规则的消息按设置上限保留（默认 50 条），超限丢弃最早的消息。恢复后
合并成一个摘要，显示每条消息的发送者、图片、文件与引用，并提示丢弃条数；只有一条且
没有丢弃时仍显示原消息。TTS 最多念前三条，并报出总数和超限条数。摘要的回复入口明确
指向最后一条消息；卸载核心时清空积压。

「上课时暂停提醒」默认开启，上课期间继续接收符合规则的消息并在内存中积压。可设置
「下课后延迟提醒（秒）」（0~3600，默认 0），延迟期间新消息也会积压；若再次上课，
等下一次下课后重新计时。手动暂停未恢复时，下课也不会释放。

原生消息下方显示「确定 / 取消」：确定引用原消息发送「确定按钮的默认回复」（默认“收到”），
取消只收起通知。发送失败保留重试入口；不会自动回复。

## 自动化、组件与规则

QQListener 提供以下自动化 Trigger ID，供你在 ClassIsland 工作流中手动选择使用；插件不会自动创建工作流。

- `QQListener.NormalMessageReceived`：收到普通 QQ 消息。
- `QQListener.ImportantMessageReceived`：收到重要 QQ 消息。
- `QQListener.CallingMessageReceived`：收到 QQ 呼叫。

可在工作流中使用的 Action ID：

- `QQListener.SetListenerEnabled`：设置监听开关。
- `QQListener.ShowLatestMessage`：重新显示最后一条 QQ 消息。
- `QQListener.ClearLatestMessage`：清空最后一条 QQ 消息。

“最后 QQ 消息”组件可设置最低消息等级（普通、重要、呼叫）、是否显示正文，以及隐私模式。隐私模式只显示 QQ 来源和消息等级，不显示发送人、群聊或正文。

规则预设可作为起点，再按你的需要调整：

- 作业提醒：关键词为“作业、提交、截止、收作业”。
- 老师呼叫：联系人为“老师、班主任”，并启用“呼叫”。
- 考试通知：关键词为“考试、监考、考场、通知”。

规则预览和诊断都只在本机运行：不联网，也不会上传消息内容。

## 故障排查

- **没有消息**：确认 Windows x64、新版 NT QQ 已登录，内核已经安装且监听开启；查看核心状态、最近忽略原因及报错。
- **等待 QQ / 管道**：插件会持续发现 QQ 进程并自动装载、重连；内核缺失时在高级设置重新触发安装向导。
- **上课或暂停期间没有提醒**：消息暂存于内存中，恢复并满足课后延迟后会合并展示；超过设置上限会丢弃最早的消息，摘要显示丢弃条数。
- **未显示自己的消息**：默认开启了「排除自己发送的消息」。
- **回复失败**：确认对应 QQ 仍在运行并已登录；重试前可检查设置中的默认回复文本。
- **重复消息**：可适当增加重复消息冷却时间。

旧版通知中心输入中的“未知应用身份”诊断仅属于历史 Toast 路径，当前原生捕获不依赖 Windows 通知权限，也不回退到 Toast 输入。

## 开发与发布

插件构建使用 `global.json` 指定的 .NET 8 SDK；Zed 的 Roslyn 语言服务另需 .NET 10 运行时。使用自定义安装目录时，在 Zed 的 `lsp.roslyn.binary.env` 中设置 `DOTNET_ROOT`，并把 SDK 目录加入 `PATH`。若 macOS ARM64 运行时启动报 `CoreCLR 0x8007000C`，可安装 x64 SDK、.NET 10 运行时和 `roslyn-language-server.osx-x64`，通过 `lsp.roslyn.binary.path` 指定其可执行文件，设置 `DOTNET_ROOT_X64`，参数保留 `--stdio` 和 `--autoLoadProjects`。

当前迁移工作区的富消息预览、验证命令和未完成项见 [迁移状态](docs/PORT_STATUS.md)。

发布包在 `plugin` 目录执行 `dotnet build QQListener.csproj --configuration Release -p:CreateCipx=true` 生成，输出为 `cipx/QQListener.cipx`。打包固定使用 Windows 专用目标 `net8.0-windows10.0.19041.0`，包含 Windows SDK 和 WinRT 运行库；macOS 上执行同一命令会交叉编译 Windows 包。若使用 `--no-restore`，须先用相同的 `-p:CreateCipx=true` 参数还原依赖。普通构建和测试仍按当前平台选择目标。

历史标签 `1.0.2.0` 指向清单版本为 `1.0.1.0` 的历史提交，保留不动。发布版本以 `Directory.Build.props` 的 `QQListenerVersion` 和 `manifest.yml` 的四段式版本为准。
