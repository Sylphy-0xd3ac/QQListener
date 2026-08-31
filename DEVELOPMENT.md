# QQListener 开发指南

本文档描述当前的原生消息架构。旧版 Windows Toast、WinSDK、UIA、聊天记录目录扫描及
OneBot/HTTP 推送配置已不再参与运行。

## 环境

- Python 3.10
- Windows 10 1903+ x64（核心注入与真实消息监听）
- macOS/Linux 可运行界面和大部分测试，但不执行注入

```bash
uv sync
uv run main.py
```

## 架构

```text
NT QQ
  └─ SnowLuma 核心（用户独立安装并接受其许可）
       ├─ recv 命名管道 → HQP1 拆帧 → SSO/Protobuf 解码
       │                    └─ 文本 / 图片 / 文件消息
       └─ control 命名管道 ← 私聊、群聊文件 URL 请求

CapturedMessage
  └─ MessageProcessor
       ├─ 精确群号/QQ 号白黑名单
       ├─ 重要人物、关键词、@我与呼叫优先级
       └─ NotifyManager → NotifyWindow → 可选 TTS
```

核心控制状态和真实运行状态是两个层次：

- `CoreState` 表示用户要求的运行、暂停或卸载状态。
- `CoreRuntimeState` 表示 QQ 进程、接收管道以及捕获线程的真实状态。

只有 `CoreRuntimeState.CONNECTED` 才代表消息监听链路已连通。DLL 映射成功不能替代此判断。

## 主要目录

```text
src/
├─ core/
│  ├─ app.py             应用生命周期
│  ├─ core_service.py    核心发现、映射和控制
│  ├─ core_runtime.py    实时连接状态
│  ├─ settings.py        配置加载、迁移与保存
│  └─ worker.py          原生捕获工作线程
├─ native/
│  ├─ capture.py         recv/control 管道连接
│  ├─ control_client.py  HQP1 控制请求
│  ├─ file_resolver.py   私聊/群聊文件 URL 解析
│  ├─ profile_resolver.py 通过 0xFE1_2 解析备注与昵称
│  ├─ hqp1.py            HQP1 帧协议
│  ├─ injector.py        64 位 Windows 手动映射
│  ├─ sso_decode.py      SSO PushMsg 解码
│  └─ proto/             最小 Protobuf wire/message 解析
├─ ui/
│  ├─ settings_window.py Fluent 主界面与设置页
│  ├─ notify_window.py   Fluent 通知及文件卡片
│  ├─ status_ball.py     实时状态悬浮球
│  └─ tray_icon.py       系统托盘
└─ utils/
   ├─ message_processor.py 规则、去重和通知模型
   └─ tts.py               语音播报
```

## 配置与规则

设置使用 JSON 保存。名单项必须是纯数字字符串：

```json
{
  "User_QQ": "123456789",
  "Important_Person_QQs": ["10001"],
  "Whitelist_Enabled": true,
  "Blacklist_Enabled": true,
  "Whitelist_Groups": ["20001"],
  "Blacklist_Groups": ["20002"],
  "Whitelist_Person_QQs": ["10001"],
  "Blacklist_Person_QQs": ["10002"],
  "Core_Download_Proxy": "https://ghfast.top"
}
```

过滤顺序：

1. 启用的黑名单先检查；群号或发送者/会话 QQ 命中即拒绝。
2. 启用的白名单随后检查；群聊由群号或人物 QQ 任一命中即允许，私聊由人物 QQ 命中允许。
3. 启用但为空的白名单拒绝所有消息（fail closed）。
4. 通过过滤后，再按重要人物 QQ、关键词、@我和呼叫规则计算优先级。

通知标题的姓名优先级为：私聊 `备注 > 昵称`；群聊
`备注 > 群昵称/群名片 > 昵称`。首次遇到未缓存的发送者时，worker 通过 control 管道调用
OIDB `0xFE1_2` 请求备注（属性 103）和昵称（属性 20002），同一 QQ 进程会话内复用结果。

`Settings` 会在加载与保存时移除旧 Toast/目录扫描/OneBot 等配置键，防止它们继续出现在
运行时配置中。

## 消息与附件

`CapturedMessage` 保留消息类型、群号、发送者 QQ/UID、接收账号 UID、时间、序列号和元素列表。
文件消息只在通知中展示文件名、大小及类型，不会预先下载整个文件。

文件打开目标必须是：

- 已存在的本地文件；或
- `http://` / `https://` 地址。

其他 scheme 会被拒绝。群文件地址经 OIDB `0x6d6_2` 请求，私聊文件地址经
OIDB `0xe37_1200` 请求，并通过与接收管道对应的 control 管道收发。

## 开发与验证

```bash
uv run ruff format src tests scripts
uv run ruff check src tests scripts
uv run pytest -q
```

协议测试应优先使用合成、去标识化 fixture，禁止把真实聊天帧、QQ 号、消息正文或临时抓包
提交到仓库。修改 decoder 时应分别覆盖私聊、群聊、文本、图片、文件、未知字段和截断帧。

真实 Windows 验收至少检查：

1. 首页由“等待接收管道”变为“接收管道已连接”。
2. 私聊和群聊文本只各产生一次通知。
3. 图片能显示缩略图（启用时）。
4. 私聊和群聊文件卡片可单击打开，确认按钮也可打开。
5. 精确群号/QQ 号白黑名单和空白名单行为符合上述规则。

## 打包

项目的打包入口是 `scripts/build_app.py`；它会收集 Windows 注入、音频和 Qt 运行时所需模块。
SnowLuma 专有二进制不得打入 QQListener 包，必须由首次运行向导在用户接受许可后单独下载。

```bash
uv run python scripts/build_app.py
```

## 新增消息元素

1. 在 `src/native/proto/` 中以最小字段范围解析，未知字段保持可忽略。
2. 将结果表示为 `MessageSegment`，不要让 UI 直接依赖 wire 字段号。
3. 在 `MessageProcessor` 中补充通知模型映射。
4. 为合成帧、截断输入和用户交互分别补测试。
5. Windows 真机验证后再把该元素标记为完整支持。
