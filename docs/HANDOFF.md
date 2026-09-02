# QQListener 原生栈 交接文档（面向 Codex）

> **补充（2026-09-02）——本文档下面的正文已有多处过时，先读这一节。**
>
> 已经落地、与正文描述不同的部分：
>
> - **装载器（Plan C）已完成**：`src/native/injector.py` + `src/core/core_service.py`
>   已接通「点火」，正文第六节「阶段二」不再是待办。
> - **外层字段号已用真帧校准**：`sso_decode.py` 里不再有占位值，
>   `MESSAGE_PUSH_CMDS = {"trpc.msg.olpush.OlPushService.MsgPush"}` 已确认。
> - **不再是"只收不发"**：control 管道现在用于文件地址（0x6d6_2 / 0xe37_1200）、
>   资料备注昵称（0xfe1_2）、图片 rkey（0x9067_202），以及**用户手动触发的文本回复**
>   （`MessageSvc.PbSendMsg`，见 `src/native/message_sender.py`）。回复只在用户点了
>   通知里的「确认 → 发送」之后才发生，没有任何自动应答。
> - **消息元素已补齐**：@（`TextElem.pbReserve` / `attr6Buf`）、引用
>   （`SrcMsg(45)`，含被引用消息自身元素的递归解析）、视频（`VideoFile(19)`）。
> - **图片地址必须带 rkey**：见 `src/native/rkey_resolver.py` 与
>   DEVELOPMENT.md「图片地址与 rkey」。缺 rkey 时 CDN 回
>   `{"retcode":-5503010,"retmsg":"invalid rkey"}`，这是之前"图片炸了只剩占位符"的原因。
> - **通知窗口已重写**：图片内联渲染、文件/视频卡片「下载 → 进度环 → 打开」
>   （`src/ui/notify_media.py`），引用块 + 「引用消息」分割线，发送者号码点击才展开。
> - **默认以 daemon 运行**，并新增本机控制通道 `src/core/ipc.py`
>   （`qqlistener status/start/pause/toggle/unload/show/reload/quit`）。
> - **性能模式** `Lite_Mode`：通知窗口不使用 Fluent 控件，关掉全屏遮罩/阴影/动画；
>   悬浮球只在状态真正变化时重绘。
>
> 合规红线（第五节）依然有效：不分发、不逆向 SnowLuma 二进制，内核只从官方 release 下载。
>
> 架构与协议细节以 `DEVELOPMENT.md` 为准，本文件保留作为迁移期的背景资料。


> 目的：把「QQListener 迁到基于 SnowLuma 的原生捕获栈」这件事的完整上下文交接给一个零背景的接手者。
> 你（接手者）读完这份文档 + 文末列出的两份设计文档，应能独立继续，重点是完成 **Plan C：用 Python 复刻 SnowLuma 的内核装载器**。
> 全程中文交流即可。

---

## 一、这个项目在做什么

QQListener 是一个「班级群消息监听 + 语音播报」的桌面程序（PySide6，Windows 为主）。
原本用一组可插拔引擎抓消息（Windows 通知中心 / UI 自动化 / OneBot / HTTP），已经**全部废弃删除**。
现在改成一条自有的**原生捕获链**，做法**忠实照抄公开互操作项目 SnowLuma**（`SnowLuma/SnowLuma`，GitHub，源码可见非商业许可），
但**不运行 SnowLuma 的 Node 运行时**——协议、解码用 Python 重写，装载器也要用 Python 重写。

一句话定位：**把 SnowLuma 的 QQ 互操作内核装进 QQ 进程，从它开的本地命名管道里只读地收消息，用 Python 解码后播报/通知。** 只收不发。

这是个人在自己机器、自己账号上的互操作工具（学校参赛作品），性质与 NapCat / LiteLoaderQQNT / Lagrange 等公开 QQ 互操作项目一致。

---

## 二、术语与数据链（务必先建立心智模型）

SnowLuma 的原生部分其实是**两个独立的二进制**，角色完全不同：

```
QQ.exe 进程
  └─ ② snowluma-win32-x64.dll   ← 「内核载荷」：SnowLuma 专有钩子，装进 QQ 后
        hook QQ 收发包，并开两条本地命名管道：
          \\.\pipe\mojo.<pid>.control   （往 QQ 发请求；本项目暂不用）
          \\.\pipe\mojo.<pid>.recv      （QQ 推消息给我们）
              ↕ 二进制帧协议（下称 HQP1）
        ③ 管道客户端（已用 Python 重写，见 src/native/hook_client.py）
              ↕ QQ 私有 SSO 包（cmd 字符串 + protobuf 负载）
        ④ 解码（已用 Python 重写，见 src/native/proto/、sso_decode.py）

  ① snowluma-win32-x64.node       ← 「装载器」：SnowLuma 的 Node 原生插件，
        负责把 ②.dll 用手动映射方式装进 QQ 进程。
        这就是 Plan C 要用 Python 复刻的东西（见第六节）。
```

四块的归属与现状：

| 块 | 是什么 | 现状 |
| --- | --- | --- |
| ① 装载器（.node） | 把 .dll 装进 QQ（手动映射） | **未做，Plan C 用 Python 复刻** |
| ② 内核载荷（.dll） | 住在 QQ 里 hook 收发包的专有钩子 | **原样复用**，不重写、不逆向、不分发 |
| ③ 管道客户端（HQP1） | 帧协议 | ✅ 已 Python 重写 + 单测 |
| ④ 解码（proto） | QQ 消息 protobuf | ✅ 已 Python 重写 + 单测（外层字段号待真帧校准） |

---

## 三、当前进度（已合并进 main）

分支 `spec/native-injection-core` 已经过 PR #1 合并进 `main`。已完成：

- **捕获核心** `src/native/`：
  - `hqp1.py` — HQP1 帧编解码 + op=6/op=7 映射
  - `proto/wire.py` — 手写 protobuf 读取器（只读，按已知字段号取值）
  - `proto/element.py` — 消息元素解码（文本 / 图片 NotOnlineImage / 图片 CustomFace / 群文件 GroupFileElem）
  - `proto/message.py` — Elem 分派
  - `model.py` — `Segment` / `CapturedMessage` 结构化消息
  - `sso_decode.py` — SSO 包 → `CapturedMessage`
  - `hook_client.py` — 异步 recv 会话（用假传输 `FakeTransport` 即可离线测）
  - `pipe_transport.py` — Windows 命名管道传输 `Win32NamedPipeTransport`（用 pywin32）+ 测试用 `FakeTransport`
  - `capture.py` — 编排：枚举 QQ.exe → 连管道 → 解码 → 回调 `CapturedMessage`
  - `binary_locator.py` — 定位用户放置的内核 .dll
- **核心开关状态机** `src/core/core_controller.py`：三态 `DETACHED / RUNNING / PAUSED`，监听器模式。
  - 悬浮球 `src/ui/status_ball.py`、主页徽章 `src/ui/settings_window.py`：**单击=开关，长按=二次确认卸载**。
  - **重要：`toggle_core()` / `unload_core()` 目前只切状态 + 打日志，没有真装载动作**——这正是 Plan C 要补的「点火」。
- **worker** `src/core/worker.py`：由核心态驱动 `RecvCapture`，只在 `RUNNING` 时捕获，无降级回退。
- **通知处理** `src/utils/message_processor.py`：`process_captured()` 复用过滤/去重/重要人物/at 逻辑，图片下 URL 存 `Pic_Path`，文件存 `data["file"]` 供通知窗点击打开。
- **核心管理页 + 内核下载** `src/core/core_updater.py` + 设置「核心」页：状态 / 版本 / 检查更新（连 SnowLuma 官方 release 下载安装内核到 `native/`）。
- **首次向导** `src/ui/core_setup_dialog.py`：受支持平台首次运行，强制阅读并同意 SnowLuma EULA/LICENSE/隐私（运行时从官方仓库拉，不打包），同意后下载安装内核。
- 修复：`setting.json` 现锚定应用根目录（原为相对路径，开机自启时工作目录是系统目录导致读不到配置、每次当首次运行）。

测试：`uv run pytest tests/native` → 全绿（约 56 项，全部离线可跑）。

---

## 四、关键协议事实（已核实，实现照此）

### HQP1 帧格式（来自 SnowLuma 公开源码 packages/bridge/src/qq-hook-client.ts）
帧 = 40 字节头 + cmd + msg + body，**全部小端**：

| 偏移 | 类型 | 字段 |
| --- | --- | --- |
| 0 | u32 | magic `0x31504851`（ASCII "QHP1"） |
| 4 | u16 | version = 1 |
| 6 | u16 | op |
| 8 | u32 | requestId |
| 12 | i32 | status |
| 16 | u32 | flags |
| 20/24/28 | u32×3 | cmdLen / msgLen / bodyLen |
| 32 | u64 | value0 |

op：`hello=1, sendRequest=2, sendAck=3, sendReply=4, error=5, recvPacket=6, loginState=7`
flags：`WantReply=1<<0, LoggedIn=1<<2`

**op=6（recvPacket）映射到逻辑包**：`seq=value0, error=status, cmd=cmd字段, uin=msg字段, body=body字段`。
**op=7（loginState）**：`loggedIn = (flags & (1<<2)) 或 status!=0`，`uin = msg 或 str(value0)`。

管道名：`\\.\pipe\mojo.<pid>.recv`（收）/ `\\.\pipe\mojo.<pid>.control`（发，暂不用）。

### proto 字段号
**已确定（取自 SnowLuma proto-defs element.ts 的 Elem 接口，权威，勿改）：**
- `Elem`：text=1, notOnlineImage=4, customFace=8, groupFile=13, videoFile=19, srcMsg=45, transElem=5
- `NotOnlineImage`：origUrl=15, bigUrl=14, thumbUrl=12, picMd5=7, fileLen=2
- `CustomFace`：origUrl=16, bigUrl=15, thumbUrl=14, md5=13, size=25
- `GroupFileElem`：filename=1, fileSize=2, fileId=3, fileKey=5（**群文件推送里没有下载 URL，需 control 管道发 OIDB 换地址——超出「只收不发」范围，暂不做**）

**待真帧校准（现在是占位值，见代码注释「待核 / 占位」）：**
- `sso_decode.py` 里 `_F_MSG / _F_ROUTING / _F_CONTENT / _F_BODY / _F_RICHTEXT / _F_GROUP / _F_SENDER_UIN` —— PushMsg 外层嵌套字段号
- `sso_decode.py` 里 `MESSAGE_PUSH_CMDS = {"trpc.msg.olpush.OlPushService.MsgPush"}` —— 真实 cmd 字符串
- `message.py` 里 `RICHTEXT_ELEMS = 2` —— RichText 的 elems 列表字段号

校准方法见第六节「阶段一」。

---

## 五、合规红线（SnowLuma 许可 §5，必须遵守，勿越）

- **不打包、不再分发** SnowLuma 的任何二进制（`snowluma-*.dll/.node/.so`）与其法律文本；这些路径已在 `.gitignore`。
- **不逆向 SnowLuma 的二进制**。我们复刻的是**公开可查的技术与协议**（手动映射装载、HQP1 帧格式、proto 字段号来自其开源 TS），不通过反编译其 .dll/.node 获取任何东西。②.dll 全程原样运行，不逆、不改、不单独分发。
- 内核只从 **SnowLuma 官方 release** 下载；加速代理由用户自己填，程序不内置、不自建镜像。
- 只收不发（不实现 control 管道的主动发送）。
- 平台仅 Windows x64（macOS 无官方内核，且 §5 禁止逆向其二进制，故 mac 版划为「净室从零自写」的未来事项，不在本次范围）。

---

## 六、还剩的工作（按优先级）

### 阶段一 · 真帧校准（小，但必须在装好内核的真机上做）
前提：一台 Windows + 登录的 NT QQ，且 ②.dll 已装进 QQ（首次可**借 SnowLuma 官方 release 跑一次**来完成装载，管道就会起来）。
步骤：
1. 找到 QQ.exe 的 pid，跑 `uv run python scripts/probe_recv.py <pid> --dump-dir tests/native/fixtures`
2. 在群里发「文本 + 图片」，观察打印的帧，**记下群消息真实 cmd 字符串**，fixtures 目录会存下真帧
3. 用真帧把第四节「待真帧校准」那几个占位字段号改成真值（用 `wire.decode_fields` 逐层剥开对照）
4. 跑 `uv run python scripts/capture_demo.py`，确认一条真消息带图片 URL 打印出来
5. 用真帧补 `tests/native/test_sso_decode.py` 的 golden 断言

### 阶段二 · Plan C：Python 装载器（**大头，本次交接核心任务**）
目标：新增 `src/native/injector.py`，用 Python + ctypes 复刻 SnowLuma 的手动映射装载，把 ②.dll 装进 QQ 进程；再把 `core_controller` 的桩换成真装载/卸载。

**接口（对齐 SnowLuma 的句柄形状，来自其公开 injector.ts）：**
```python
@dataclass
class MapHandle:
    base: int
    entry: int
    exception_table: int
    size: int

def inject(pid: int, dll_path: str) -> MapHandle: ...
def unload(pid: int, handle: MapHandle) -> None: ...
```

**手动映射要做的步骤**（这是公开、文档化的 PE 装载流程，SnowLuma 用的就是它；用途是把互操作内核装进本进程族的 QQ，`RtlAddFunctionTable` 那步是 x64 上让钩子内部的异常处理能正常工作所必需）：
1. 打开目标进程（`OpenProcess`，需要相应访问权限）
2. 读 .dll 文件，解析 PE 头 / 节表 / 重定位表 / 导入表（建议用纯 Python 的 `pefile` 库，避免手写解析）
3. 在目标进程分配一块 `SizeOfImage` 的内存（`VirtualAllocEx`）→ 得到 `base`
4. 按节表把各节写入目标进程（`WriteProcessMemory`）
5. 按 `base` 与 PE 首选基址的差值，应用基址重定位表
6. 解析导入表，把导入函数地址填进 IAT
7. 注册 x64 异常表（把 `.pdata` 的 RUNTIME_FUNCTION 表通过在目标进程内调用 `RtlAddFunctionTable` 注册）→ 得到 `exception_table`
8. 在目标进程远程调用 .dll 的入口（DllMain，`DLL_PROCESS_ATTACH`）→ 入口地址即 `entry`
9. `unload` 时反向：远程调 DllMain(`DLL_PROCESS_DETACH`)、注销异常表、释放内存

**为什么用手动映射而不是普通 LoadLibrary**：与 SnowLuma 保持一致——手动映射不把模块登记进进程的已加载模块表，是 SnowLuma 稳定运行的做法。请忠实复刻，不要改用 LoadLibrary（会破坏这一性质，且行为与 SnowLuma 不一致）。

**建议实现顺序（测试驱动）：**
- 先写一个自制的、行为已知的最小测试 DLL + 一个宿主进程，在**它**上面验证「分配→写节→重定位→修导入→注册异常表→调入口」整条流程能通（入口里做个可观察的副作用，比如写一个文件/共享内存做断言）。这一步可脱离 QQ 独立测。
- 再上真 QQ：装载 ②.dll 后，`\\.\pipe\mojo.<pid>.recv` 应出现；用 `scripts/capture_demo.py` 验证能收到消息。
- 最后把 `core_controller.toggle_core()` 从「仅切状态」接到真动作：`RUNNING` 时确保已装载（未装则 `inject`），`DETACHED`（长按卸载）时 `unload`。装载副作用建议放在 `app` 层订阅 `core_controller` 状态变化里执行（保持 `core_controller` 纯状态、可测），或新增一个 `src/core/core_service.py` 承接。

**注意**：`injector.py` 是 Windows-only，导入要延迟（ctypes/pefile 在非 win32 环境不应在模块顶层就崩）；测试用假目标或跳过标记，保证在 mac/Linux 上 `pytest` 仍能收集通过（参照 `pipe_transport.Win32NamedPipeTransport` 的延迟导入写法）。

### 阶段三 · 收尾（零碎，可后置）
- 补齐剩余消息元素：at / 语音(record) / 视频(videoFile=19) / 回复(srcMsg=45) / 表情(face=2)。字段号部分要真帧核对；`model.message_text` 已预留全部渲染分支。
- 生命周期硬化（SnowLuma 的经验，见 spec）：收包心跳看门狗、断线重连、进程枚举失败不误判为「进程消失」、登录态对账。worker 现在只有基础重试。
- 群文件点击打开：需 control 管道发 OIDB 换下载地址，破「只收不发」，单独立项再议。

---

## 七、工程约定 / 怎么跑

- 包管理：`uv`。装依赖 `uv sync --group dev`。跑测试 `uv run pytest tests/native -q`。
- 代码规范：`uv run ruff check` / `uv run ruff format`。行宽 100、双引号、Python 3.10。
- TDD：先写失败测试 → 跑挂 → 最小实现 → 跑绿 → 提交。小步提交。
- 提交信息用中文/英文均可，尾部带 `Co-Authored-By`。
- 离线可测的一律离线测（用 `FakeTransport`、合成 protobuf 字节、傀儡进程）；只有「真机装载 + 真帧」这两处必须上 Windows + 真 QQ。
- 新依赖：Plan C 建议加 `pefile`（纯 Python，解析 PE 用）；`pywin32` 已在 win32 依赖里。

## 八、延伸阅读（仓库内）
- 设计规格：`docs/superpowers/specs/2026-08-03-native-injection-core-design.md`（含完整背景、非目标、合规小节）
- 捕获栈实现计划：`docs/superpowers/plans/2026-08-03-native-capture-core.md`（HQP1/proto/客户端的分步 TDD，可参照其风格写 Plan C）
- SnowLuma 公开源码参考（**只读其开源 TS 与 proto-defs，勿逆向其二进制**）：
  - `packages/bridge/src/qq-hook-client.ts`（HQP1 帧、frame→packet 映射）
  - `packages/bridge/src/injector.ts`（装载器对外形状：`loadModuleManual` 返回 `{base, entry, exceptionTable, size}`；注意其 C++ 实现未公开，Plan C 按公开的手动映射技术自行实现，不照抄看不到的源码）
  - `packages/proto-defs/src/element.ts`（消息元素字段号）
  - `compat/qq.json`（QQ 版本兼容表，运行时可拉来校验）

---

### 给接手者（Codex）的第一步建议
先在 mac/当前环境把能离线做的做了：**阶段二的傀儡进程验证** + `injector.py` 的骨架 + 单测（用假目标/跳过标记保证跨平台可收集）。等拿到 Windows 机器，再做阶段一校准 + 真机装载联调。核心交付物就是 `src/native/injector.py` 把「点火」接通。
