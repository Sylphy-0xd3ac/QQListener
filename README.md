# QQListener

> 让每一条重要消息都能被看见、听见。

[![Python](https://img.shields.io/badge/Python-3.10-blue.svg)](https://python.org)
[![PySide6](https://img.shields.io/badge/PySide6-6.7+-green.svg)](https://doc.qt.io/qtforpython/)
[![License](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

QQListener 是面向 NT QQ 的本地消息提醒工具。程序通过用户单独安装并接受许可的
SnowLuma 官方核心接收原生消息帧，不依赖 Windows Toast、WinSDK 或 UI 自动化。

## 功能

- 原生解析私聊与群聊中的文本、图片和文件消息
- 文件卡片可在确认后打开，也可直接单击打开
- 按精确 QQ 号设置重要人物、人物白名单和人物黑名单
- 按精确群号设置群白名单和群黑名单，黑名单优先
- 重要关键词、@我、呼叫提醒和 Edge TTS / 系统 TTS 播报
- 现代 Fluent 界面、矢量图标、托盘入口与真实核心连接状态
- 所有消息与设置均在本地处理，不提供遥测上传

## 系统要求

- Windows 10 1903 或更高版本，64 位
- NT QQ（新版 QQ）
- Python 3.10（从源码运行时）

SnowLuma 是专有组件，不随 QQListener 分发。首次启用核心时，安装向导会展示其
EULA、LICENSE 与隐私说明，并从官方 release 下载；网络受限时默认推荐
`https://ghfast.top`，也可清空代理后直连 GitHub。

## 快速开始

从 [GitHub Releases](https://github.com/BSOD-MEMZ/QQListener/releases) 下载并运行安装包。
首次运行后：

1. 启动并登录 NT QQ。
2. 在核心向导中阅读条款、选择下载源并安装核心。
3. 打开“设置 → 规则”，按需填写重要人物 QQ 号及白/黑名单。
4. 回到首页，确认状态显示“接收管道已连接”。只有这个状态代表监听链路真正可用。

白名单启用但内容为空时会拒绝所有消息；黑名单与白名单冲突时，以黑名单为准。

## 从源码运行

```bash
uv sync
uv run main.py
```

可选依赖：

```bash
uv sync --extra system-tts
```

开发检查：

```bash
uv run ruff check src tests scripts
uv run pytest -q
```

更多架构与扩展说明见 [DEVELOPMENT.md](DEVELOPMENT.md)。

## 常见问题

### 显示核心映射完成，但收不到消息

“核心映像已映射”只是注入阶段成功。请以首页或核心页的实时状态为准；若仍显示
“等待接收管道”，监听链路尚未连通。先确认 QQ 主进程正在运行，再尝试暂停/恢复核心。

### 下载核心时证书校验失败

Windows 版本使用系统证书存储。请先校准系统时间并检查代理/杀毒软件的 HTTPS
证书拦截；也可在核心页使用推荐代理 `https://ghfast.top` 后重试。

### 文件消息为什么没有自动下载

通知只解析并展示文件元数据，避免后台下载大文件。点击文件卡片或确认按钮时，程序才会
把安全的本地路径或 HTTP(S) 地址交给系统打开。

## 许可证

QQListener 源代码采用 [MIT License](LICENSE)。SnowLuma 核心使用其自身许可，二者相互独立。

> 本程序仅供合法的学习与日常提醒场景使用，请遵守 QQ、SnowLuma 及所在地法律法规。
