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
       │                    └─ 文本 / @ / 图片 / 文件 / 视频 / 引用
       └─ control 命名管道 ↔ 文件 URL、资料（备注/昵称）、图片 rkey、发送回复

CapturedMessage
  └─ MessageProcessor
       ├─ 精确群号/QQ 号白黑名单
       ├─ 重要人物、关键词、@我与呼叫优先级
       └─ NotifyManager → NotifyWindow → 富媒体渲染 + 可选 TTS

守护进程
  └─ ControlServer（QLocalServer）← main.py 子命令 / 第二个实例
```

### 守护模式与控制通道

`main.py` 不带参数即 daemon 模式。启动时先建 `QApplication`，再抢占控制通道：
抢到就是本机唯一实例；抢不到且对面有回应，说明已有实例在跑，本次改为唤起它并退出
（残留的死 socket 会被清掉后重试）。

控制协议是一行 JSON 请求 + 一行 JSON 响应：

```json
{"command": "status"}
{"ok": true, "command": "status", "data": {"core_state": "running", ...}}
```

指令集见 `src/core/ipc.py::COMMANDS`。处理器 `QQListenerApp._handle_control` 跑在 Qt 主
线程上，可以安全触碰 UI 与状态机。传输是命名管道 / Unix socket，不监听 TCP。

核心控制状态和真实运行状态是两个层次：

- `CoreState` 表示用户要求的运行、暂停或卸载状态。
- `CoreRuntimeState` 表示 QQ 进程、接收管道以及捕获线程的真实状态。

只有 `CoreRuntimeState.CONNECTED` 才代表消息监听链路已连通。DLL 映射成功不能替代此判断。

### 暂停 = 只压不弹

`PAUSED` **不**停捕获会话：钩子和管道都还在，worker 照常收包并走完整解析
（图片预取、文件地址、rkey 签名都在入队前做完——这些凭证是短效的，拖到恢复时再解
就过期了），结果压进 `src/core/pending_queue.py` 的有界队列（默认 50 条，满了丢最旧）。
只有 `DETACHED` 才真正停会话并清空队列。

`QQListenerApp._on_core_state_changed` 监听状态机；切回 `RUNNING` 时把队列倒出来交给
`build_digest_payload()` 合成一个摘要载荷再弹窗。状态可能由后台线程切换，所以用
`QTimer.singleShot(0, ...)` 回主线程建窗口。

**运行态快照只能有一个写入方。** 暂停时捕获仍在跑，worker 却不能再写
`CoreRuntimeState`——它会和 `core_service` 抢着写同一份快照，界面就在"已暂停"和
"接收管道已连接/未找到 QQ"之间来回跳。约定：

- `worker._publish_runtime()` 只在 `RUNNING` 时才落笔
- 暂停态统一由 `worker._publish_paused()` 发布（只有它知道积压了多少条），
  `core_service.reconcile()` 遇到 `PAUSED` 直接返回、不碰快照

另外退避等待要用 `worker._sleep_or_state_change()`，状态一变立刻醒；用裸
`asyncio.sleep()` 的话，发完 pause/start 要等满一个退避周期界面才更新，
用起来像"指令没生效"。

## 主要目录

```text
src/
├─ core/
│  ├─ app.py             应用生命周期
│  ├─ core_service.py    核心发现、映射和控制
│  ├─ core_runtime.py    实时连接状态
│  ├─ ipc.py             守护进程控制通道
│  ├─ pending_queue.py   暂停期间的消息积压队列
│  ├─ settings.py        配置加载、迁移与保存
│  └─ worker.py          原生捕获工作线程
├─ native/
│  ├─ capture.py         recv/control 管道连接
│  ├─ control_client.py  HQP1 控制请求
│  ├─ file_resolver.py   私聊/群聊文件 URL 解析
│  ├─ profile_resolver.py 通过 0xFE1_2 解析备注与昵称
│  ├─ hqp1.py            HQP1 帧协议
│  ├─ injector.py        64 位 Windows 手动映射
│  ├─ message_sender.py  MessageSvc.PbSendMsg 文本回复（群聊带引用）
│  ├─ rkey_resolver.py   图片下载凭证 rkey（0x9067_202）+ 按类型缓存
│  ├─ sso_decode.py      SSO PushMsg 解码
│  └─ proto/             最小 Protobuf wire/message 解析
├─ ui/
│  ├─ settings_window.py Fluent 主界面与设置页
│  ├─ notify_window.py   通知窗口（原生控件，不走 Fluent）
│  ├─ notify_media.py    图片渲染、文件/视频卡片、下载进度环
│  ├─ file_icons.py     系统文件图标（拿不到才用内置），HiDPI 感知
│  ├─ status_ball.py     实时状态悬浮球
│  └─ tray_icon.py       系统托盘
└─ utils/
   ├─ downloads.py         附件下载与落盘目录
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
被引用消息的发送者走同一条缓存。

标题默认**只**显示名字与群名；群号与 QQ 号放在 `Sender_Detail` 里，点标题那一行才展开
（`Show_IDs` 可让它默认展开）。

`Settings` 会在加载与保存时移除旧 Toast/目录扫描/OneBot 等配置键，防止它们继续出现在
运行时配置中。

## 消息与附件

`CapturedMessage` 保留消息类型、群号、发送者 QQ/UID、接收账号 UID、时间、序列号和元素列表。
`MessageProcessor` 把它压成通知层的纯 dict。通知窗口真正渲染的是 `Messages`——
一个消息条目列表，每条形如 `{sender, detail, text, segments, quote}`；单条消息就是
长度为 1 的列表，积压摘要则把多条拼进去。`Sender` / `Segments` / `Quote` 等平铺字段
保留给旧调用方和测试通知。`Reply` 是回复路由（摘要取最后一条）。UI 层不依赖任何
wire 字段号。

窗口渲染规则：条与条之间是**无标签**的素分割线；某一条自己带引用时，仍在它内部显示
引用块 + 「引用消息」分割线。单条时发送者是大标题，摘要时标题变成"暂停期间的 N 条消息"、
发送者下放到每条自己那行（各自可点开号码）。

文件与视频只展示文件名、大小和类型，点一下才下载（`src/utils/downloads.py`），落到
`Download_Dir`（留空 = 系统 Downloads），完成后交 `QDesktopServices` 用系统默认程序打开。
卡片图标先问系统要（`QFileIconProvider`，按扩展名建一个空探针文件来问），系统给不出
才退回 `asset/FileIcon/` 里那几张；出图按屏幕 `devicePixelRatio` 取像素，HiDPI 上不糊。
图片在 `Auto_Show_Thumb` 打开时由 worker 预取到临时目录，通知里内联渲染。

文件打开目标必须是：

- 已存在的本地文件；或
- `http://` / `https://` 地址。

其他 scheme 会被拒绝。群文件地址经 OIDB `0x6d6_2` 请求，私聊文件地址经
OIDB `0xe37_1200` 请求，并通过与接收管道对应的 control 管道收发。

### 图片地址与 rkey

NT 图片走 `CommonElem(serviceType=48, businessType=10/20)`，地址由
`PictureInfo{urlPath, domain, ext.originalParameter}` 拼出，**必须**再带一枚服务器签发的
短效 `rkey`：

1. 推送里 `ExtBizInfo.pic.bytesPbReserveC2c.field30` 常常自带（私聊图片）；
2. 没有就走 OIDB `0x9067_202` 取一批，按 rkey 类型缓存（`src/native/rkey_resolver.py`）。
   类型跟着 URL 里的 `appid` 走（1406=私聊、1407=群），而不是承载它的那条消息的场景。

缺 rkey 时 CDN 返回 `{"retcode":-5503010,"retmsg":"invalid rkey"}`，过期返回
`-5503007 download url has expired`——这两个码可以直接用来判断是漏签还是缓存过期。

老元素 `NotOnlineImage(4)` / `CustomFace(8)` 的 `origUrl` 是相对路径，需补
`http://gchat.qpic.cn`（带 `rkey`/`fileid` 的补 `multimedia.nt.qq.com.cn`）；只有 MD5
时退回 `gchatpic_new` 地址。NT 图片会附带一份同 MD5 的老元素兄弟，解析后按 MD5 去重，
避免同一张图出现两次。

### 引用消息

`SrcMsg(45)` 解析出 `origSeqs/senderUin/time` 与被引用消息自己的 `elemsRaw`，后者用同一套
元素解码器递归解析，因此引用块里的图片/文件同样可渲染可下载。QQ NT 会在 `srcMsg` 之后塞一个
结构性 @（`MentionExtra.type=2, uin=0`）和一段空白文本，两者都不是用户内容，解析时丢弃；
真实的用户 @（`type=1` 或非零 uin）保留。

### 回复

`MessageSvc.PbSendMsg`（`src/native/message_sender.py`）：群聊走
`RoutingHead.grp.groupCode`，私聊走 `RoutingHead.c2c{uin, uid}` 且 `contentHead.c2cCmd=11`。
群聊回复在文本元素前插一个 `SrcMsg`，呈现为引用回复；私聊的 `origSeqs` 语义不同，不带引用。
路由信息由 `build_reply_route()` 压成纯 dict 随通知下发，UI 线程可直接用。

### TTS 不能拖慢通知

三条硬约束，改 `src/utils/tts.py` 时别破坏：

1. `set_system_volume_max()` **只能在后台线程调用**。Windows 上它要过 COM，冷机器上能耗
   数秒；它曾经在 `TTSManager.speak()` 里同步跑，而 speak() 在通知窗口的构造函数里——
   表现就是"消息来了窗口半天不出来"。现在它在 `TTSThread.run()` 里，并有 10 秒节流。
2. EdgeTTS 要连微软服务器，**必须有超时**（`EDGE_TTS_TIMEOUT_S`）。没有超时的话，网络一慢
   就无限期挂起，而通知的自动关闭在等 `tts_manager.is_active`，窗口会一直钉在屏幕上。
3. `TTSManager.stop()` **不许 `wait()`**。旧线程可能卡在网络调用里，`quit()` 打断不了它；
   每来一条新通知就冻结 UI 一秒。现在是断开信号后放生，它自己跑完 `deleteLater`。

通知侧还有一道保险：`TTS_CLOSE_GRACE_MS`，播报迟迟不结束也照样关窗。

音量分两层，别混：

- `Playback_Volume`（默认 50）是**本程序自己这一路**的播放音量，作用在
  `pygame.Sound.set_volume()` 和 pyttsx3 的 `volume` 属性上。
- `Force_System_Volume`（默认关）才会去动**系统**音量。Windows 上同时处理主音量
  （`IAudioEndpointVolume`）和本进程在合成器里的那一路（`ISimpleAudioVolume`）——
  只拉主音量盖不过自己这一路被调低。两处都要 `comtypes.CoInitialize()`：
  工作线程不会自动初始化 COM。失败一律 `warning` 级别，别再吞掉。

EdgeTTS 不是本地引擎：`edge_tts` 是微软 Edge「大声朗读」那套在线服务的客户端，
会开 WebSocket 到微软的语音端点取合成音频。所以它必然要联网，也必然受校园网影响。

### 后台线程与"没有 traceback 的崩溃"

**正在运行的 QThread 必须一直有人持有，而且不能 parent 到会先被销毁的控件上。**
最后一个引用消失时 Python GC 会析构它，C++ 侧发现线程还在跑就直接
`qFatal("QThread: Destroyed while thread is still running")` —— 进程 abort，
不经过 Python，日志里一个 traceback 都没有（实测退出码 139/SIGSEGV）。

所以 `TTSThread` / `ReplyTask` / `DownloadTask` 一律**不设 parent**，改为
`src/utils/qt_tasks.py::keep_alive()` 登记，线程 `finished` 时自己注销。
这条尤其容易在"不阻塞 UI"的改动里被破坏：`stop()` 里把引用置 None 放生的写法，
必须配合 keep_alive 才成立。

崩溃可见性由 `src/core/crash_handler.py` 兜底，三个来源各需单独接管：

1. Qt 槽函数里未捕获的 Python 异常 → `sys.excepthook`（PySide6 走完它就终止进程）
2. 子线程里的异常 → `threading.excepthook`（和主线程那个不是一回事）
3. Qt 自身的致命错误 → `qInstallMessageHandler`（在 C++ 侧 abort，**根本不经过 Python**）

GUI 模式（`--windowed`）下 stderr 是 None，不接管这三处就等于什么都看不到。

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
3. 图片在通知里直接渲染出来（而不是只有 `[图片]`），点击用系统看图程序打开。
4. 文件/视频卡片点一下开始下载、显示进度环、完成后变「打开」并落在下载目录里。
5. 引用消息在分割线上方显示，被引用的图片/文件同样可渲染。
6. 点「确认」后按钮区变成输入框，发送后群聊显示为引用回复、私聊为普通消息。
7. 点发送者一行能展开/收起群号与 QQ 号。
8. 精确群号/QQ 号白黑名单和空白名单行为符合上述规则。
9. `qqlistener status/pause/start/quit` 能正确控制正在运行的守护进程。

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
