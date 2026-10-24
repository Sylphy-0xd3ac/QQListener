# QQListener

[![ClassIsland](https://img.shields.io/badge/ClassIsland-plugin-512BD4?style=flat-square)](https://github.com/ClassIsland/ClassIsland)
[![Windows x64](https://img.shields.io/badge/Windows-x64-0078D4?style=flat-square)](#安装)
[![MIT](https://img.shields.io/badge/License-MIT-blue?style=flat-square)](LICENSE)

将 NT QQ 的消息转发为 ClassIsland 提醒，支持群聊筛选、重要消息分级和课后集中提醒。

[下载插件](https://github.com/Sylphy-0xd3ac/QQListener/releases) · [使用说明](docs/usage.md) · [更新日志](CHANGELOG.md) · [反馈问题](https://github.com/Sylphy-0xd3ac/QQListener/issues)

## 功能

- 按群聊、联系人、关键词和时间段筛选消息，支持黑白名单、重要人物、@ 与呼叫提醒。
- 在提醒中显示文字、图片、文件和引用内容，正文窗口跟随 ClassIsland 的提醒状态与主题。
- 暂停期间暂存消息，恢复后合并展示；支持上课暂停和课后延迟。
- 按优先级依次提醒，同一会话的短时连续消息可合并显示。
- 可选引用群消息回复预设文本，或为群消息添加 QQ 的 OK 表情回应。
- 提供语音、提示音、工作流触发器与动作，以及“最后 QQ 消息”组件。
- 在底部「调试 → 消息引擎」选择 SnowLuma 或按需下载的 PMHQ 实验引擎。

## 安装

需要 Windows x64、ClassIsland 和已登录的 NT QQ。不支持 TIM；macOS、Linux 不支持原生消息捕获。

1. 从 [Releases](https://github.com/Sylphy-0xd3ac/QQListener/releases) 下载 `.cipx` 文件，在 ClassIsland 的插件管理中从本地安装，随后重启 ClassIsland。
2. 打开 QQListener 设置，在「高级设置 → 重新触发安装向导」中阅读许可说明并安装 SnowLuma 内核。
3. 开启监听，配置需要提醒的群聊、联系人与关键词。确认接收管道已连接后，发送一条测试消息。

SnowLuma 内核不随插件分发，首次使用需单独下载并接受其许可。插件设置、回复方式及常见问题见[使用说明](docs/usage.md)。

PMHQ 为实验性选项。首次使用从官方获取固定的 v9.0.6，校验后应用随插件提供的补丁；后续可离线复用。直接使用本机 QQ，无需 `auth_token` 或 manager 启动配置。切换时会显示提示，并等待原 QQ 进程退出后再连接。真实 QQ 注入、签名及发包尚未完成 Windows 验收，使用方法见[说明](docs/usage.md#pmhq-实验引擎)。

## 开发

项目使用 .NET 8 和 ClassIsland.PluginSdk，以下命令在仓库根目录执行。

```sh
dotnet restore QQListener.sln --locked-mode
dotnet build QQListener.sln -c Release --no-restore
dotnet test QQListener.sln -c Release --no-build --no-restore
```

构建 Windows 安装包、运行界面预览和发布校验的方法见[开发指南](DEVELOPMENT.md)。

## 反馈

请通过 [Issues](https://github.com/Sylphy-0xd3ac/QQListener/issues) 报告问题，并提供 Windows、ClassIsland、QQ 与插件版本、复现步骤及错误信息。

提交截图或日志前，请移除聊天内容、QQ 号和下载凭证。图片问题可提供占位区域的错误码，不要公开完整下载链接、`rkey` 或原始消息帧。

## 致谢与许可

本项目基于以下项目的工作：

- [BSOD-MEMZ/QQListener](https://github.com/BSOD-MEMZ/QQListener)：原生捕获实现与项目起源。
- [alright-qwq/QQListener](https://github.com/alright-qwq/QQListener)：ClassIsland 插件形态与规则模型。
- [SnowLuma](https://github.com/SnowLuma/SnowLuma)：QQ 原生捕获内核及协议参考。
- [PMHQ](https://github.com/linyuchen/PMHQ) 与 [LuckyLilliaBot](https://github.com/LLOneBot/LuckyLilliaBot)：实验引擎及其 HTTP/WebSocket 接口参考。
- [ClassIsland](https://github.com/ClassIsland/ClassIsland)：提醒、组件和自动化支持。

QQListener 源代码采用 MIT 许可证，见 [LICENSE](LICENSE)。SnowLuma 采用其自身许可，不属于本项目 MIT 许可的授权范围。
PMHQ 原始二进制从官方获取，不随插件分发，采用其自身许可；版本和修改说明见 [NOTICE](Assets/PMHQ/NOTICE.md)。
