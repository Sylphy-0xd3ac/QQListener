# QQListener 原生注入内核设计（Python 版，忠实复刻 SnowLuma）

- 日期：2026-08-03
- 状态：待实现

## 背景与目标

QQListener 当前的消息捕获依赖一组可插拔引擎（WinSDK 通知中心、UIA 窗口自动化、
OneBot v11 转发、HTTP 推送），实现在 `src/core/notification_engines.py`。这些引擎
要么只能拿到降级后的通知文本（WinSDK/UIA），要么需要外挂一个独立后端进程
（OneBot/HTTP），且消息模型统一被压成 `list[str]`，导致图片、文件等富媒体在通知
链路里只能显示成 `[图片]` / `[文件]` 占位符。

本设计用一条**自有的原生注入捕获链**替换全部引擎。参考项目 SnowLuma
（`SnowLuma/SnowLuma`，Source-Available Non-Commercial License）已经把「注入 QQ →
hook 收发包 → 命名管道 → 解析 QQ 私有 protobuf」这条链在 TypeScript + 专有 C++ 里
走通。我们**不运行 SnowLuma 的 Node 运行时**，而是用 Python 重写其中可重写的部分，
复用其无法重写的专有二进制。

### 明确目标

- 用 Python 实现 QQ 进程枚举、**手动映射（manual map）注入**、命名管道客户端、
  QQ wire protobuf 解码，产出**结构化**消息对象（含图片/文件 URL）。
- 彻底脱离 Node.js 运行时与 SnowLuma 运行时。
- 把结构化消息接入 QQListener 现有的 TTS 播报与通知窗（含缩略图/文件图标）。
- 为未来的 macOS 版本预留架构位置（但本设计不覆盖 mac）。

### 明确非目标（本版不做）

- **发送方向**：不实现 control 管道的 `sendRequest`（OIDB action 等主动发消息/操作）。
  本版只收不发。使用场景是「监听催作业」，只需要收。
- **降级路径**：不保留任何旧引擎。QQ 版本不兼容导致 hook 失效时，程序即为「不工作」，
  不退回 UIA/WinSDK。
- **macOS hook**：release 未提供 darwin 的 SnowLuma 钩子二进制，其源码
  （`apps/qq/macos/qq_hook_dylib.cpp`）未公开。mac 版等于从零逆向 QQ mac 客户端写
  hook，与本项目不是一个量级，划到未来另立项目。
- **不打包分发 `.dll`**：SnowLuma 的钩子载荷是专有组件，LICENSE 明确排除授权与再分发。

## 关键背景事实（已核实到字节级）

数据链（Windows）：

```
QQ.exe
  └─ ② snowluma-win32-x64.dll   专有钩子，manual-map 注入进 QQ，hook 收发包
        开两条命名管道：
          \\.\pipe\mojo.<pid>.control   （发请求 → QQ，本版不用）
          \\.\pipe\mojo.<pid>.recv      （QQ 推消息 → 我们）
              ↕ HQP1 二进制帧
        ③ 管道客户端（Python 重写）
              ↕ QQ SSO 包（cmd 字符串 + protobuf body）
        ④ proto 解码（Python 重写）
```

四块的归属：

| 块               | 是什么                           | 我们怎么办                   | 依据                                    |
| ---------------- | -------------------------------- | ---------------------------- | --------------------------------------- |
| ① `.node` 注入器 | 标准 manual-map 注入             | **Python 重写**（不反编译）  | `packages/bridge/src/injector.ts`       |
| ② `.dll` 钩子    | 住在 QQ 里 hook 收发包的专有核心 | **原样复用**，不重写、不分发 | LICENSE §5                              |
| ③ 管道客户端     | HQP1 二进制帧协议                | **Python 照抄**              | `packages/bridge/src/qq-hook-client.ts` |
| ④ proto 解码     | QQ wire protobuf 字段号          | **Python 照抄字段号**        | `packages/proto-defs/src/*.ts`          |

### 为什么必须 manual map（防封）

`.node` 注入器用手动映射而非 `LoadLibrary`，是**反风控的核心手段**：manual map 不会把
模块挂进 QQ 的已加载模块表，风控扫不到。用 `LoadLibraryW` 直接注入会让 `.dll` 出现在
模块列表里，等于自曝。**因此 Python 注入器必须忠实复刻 manual map，不得走任何「正常
加载」捷径。** 这是本设计的硬约束。

`injector.ts` 的 `loadModuleManual(pid, dllPath)` 返回句柄结构，直接暴露它做了什么：

```ts
interface ManualMapHandle {
  base: bigint;
  entry: bigint;
  exceptionTable: bigint;
  size: number;
}
```

推断出的注入步骤（Python 需复刻）：

1. `VirtualAllocEx` 在目标进程分配 `size` 字节（→ `base`）。
2. 按 PE 节表把 `.dll` 各段拷入。
3. 应用基址重定位表（base 非固定）。
4. 修复 IAT（解析导入）。
5. **注册 x64 异常表**（`RtlAddFunctionTable`，即 `exceptionTable` 字段来历）。
6. 远程调用 `entry`（DllMain, `DLL_PROCESS_ATTACH`）。

> 注：`.node` 注入器的 C++ 源码在 SnowLuma 私有仓、未发布。因此「按 SnowLuma 干」在
> 注入这块指的是：按标准 manual-map 技术 + 上述句柄结构**忠实重建其行为**，而非照抄
> 一份看不到的源码。

### HQP1 帧格式（照抄 `qq-hook-client.ts`）

每 PID 两条命名管道：`mojo.<pid>.control` 与 `mojo.<pid>.recv`。
帧 = 40 字节头 + cmd + msg + body，**全部小端**：

| 偏移 | 类型  | 字段                                          |
| ---- | ----- | --------------------------------------------- |
| 0    | u32   | magic `0x31504851`（= ASCII "QHP1"）          |
| 4    | u16   | version = 1                                   |
| 6    | u16   | op                                            |
| 8    | u32   | requestId                                     |
| 12   | i32   | status                                        |
| 16   | u32   | flags                                         |
| 20   | u32   | cmdLen                                        |
| 24   | u32   | msgLen                                        |
| 28   | u32   | bodyLen                                       |
| 32   | u64   | value0（loginState 帧里 = uin 数字）          |
| 40   | bytes | cmd（cmdLen）+ msg（msgLen）+ body（bodyLen） |

op：`hello=1, sendRequest=2, sendAck=3, sendReply=4, error=5, recvPacket=6, loginState=7`
flags：`WantReply=1<<0, LoggedIn=1<<2`

收消息路径：连 `recv` 管道 → 收 op=6 帧 → body 是一个 QQ SSO 包
`{seq, error, cmd, uin, body}`，`cmd` 是服务名字符串，`body` 是 protobuf。
登录状态从 op=7 帧取（`loggedIn` 标志 + `value0` = uin）。

### proto 解码（照抄 `proto-defs` 字段号）

`recv` body 按 `message.ts`（PushMsg → RichText → Elem）解到元素级。图片元素
`NotOnlineImage` 关键字段：15=origUrl、14=bigUrl、12=thumbUrl、7=picMd5、
2=fileLen、9/8=宽/高。`pb<N, T>` 里的 `N` 就是 protobuf 字段号，直接抄。

## 架构

新增 Python 包 `src/native/`：

```
src/native/
  __init__.py
  injector.py      ① manual-map 注入器（复刻 SnowLuma .node 的行为）
  hqp1.py          ③ HQP1 帧编解码（照抄帧格式）
  hook_client.py   ③ 管道会话：recv 管道 + hello/login，收 op=6/op=7
  proto/           ④ QQ wire protobuf 定义（照抄字段号）
    __init__.py
    message.py     PushMsg / RichText / Elem
    element.py     NotOnlineImage / TextElem / 等元素
  sso_decode.py    ④ SSO 包 → list[Segment]
  capture.py       ─ 串联：枚举 QQ → 注入 → 连管道 → 解包 → 产出 CapturedMessage
  binary_locator.py 定位用户放置的 snowluma-win32-x64.dll（不入仓库）
```

删除 `src/core/notification_engines.py`（全部旧引擎）。`src/core/worker.py`
改为直接驱动 `src/native/capture.py`。消息处理、过滤（重要人物/关键词）、TTS、
通知窗沿用现有实现，只是输入从 `list[str]` 换成结构化 `CapturedMessage`。

### 数据模型（拆掉「只有文本」这堵墙）

```python
@dataclass
class Segment:
    type: str            # text | at | image | file | record | video | reply | face
    text: str = ""       # text 段的文字；at 段的目标名
    url: str = ""        # image/file/record/video 的下载 URL（proto 直接给）
    name: str = ""       # 文件名
    md5: str = ""
    target_id: str = ""  # at 的 QQ 号
    extra: dict = field(default_factory=dict)

@dataclass
class CapturedMessage:
    scene: str           # "group" | "c2c"
    peer_id: str         # 群号 / 好友号
    peer_name: str
    sender_id: str
    sender_name: str
    segments: list[Segment]
    raw_seq: int
```

有了 `Segment.url`，现有的 `_download_url()` 与 `_file_icon_for_path()`（原
`notification_engines.py` 内的工具函数，迁移到 `src/utils/media.py`）即可真正用于
下载缩略图 / 匹配文件图标。通知窗内嵌图片、文件显示图标+文件名，落地最初的
「图片化 / 文件化」诉求。

### proto 首批覆盖的元素

按拍板结论，首批实现：`text`、`at`、`image`、`file`、`record`、`reply`、`video`。
`face`、转发卡片（JSON/XML ark）留到 Phase 1 后期或后续迭代。未识别的元素类型
降级为 `Segment(type=<原类型>, text="[<类型>]")`，不静默丢弃。

## 数据流（一条群图片消息）

```
QQ 收到群消息
  → ② .dll hook 到，从 recv 管道推出 op=6 帧
  → ③ hqp1 解出 {cmd:"trpc.msg.olpush...", body:<protobuf>}
  → ④ sso_decode 按 message.py 解 RichText → Elem
       NotOnlineImage：取 15=origUrl / 12=thumbUrl / 7=md5
  → capture 组装 CapturedMessage(segments=[text, image(url=...)])
  → worker 过滤（重要人物/关键词，沿用现有逻辑）
  → TTS 念文本 + 通知窗内嵌缩略图（下载 url）
```

## 错误处理与稳定性（复刻 SnowLuma 的既有经验）

以下不是新发明，是 SnowLuma 在 `pipe-watcher.ts` / `hook-session.ts` 里踩平的坑，
直接复刻：

- **枚举失败 ≠ 进程消失**（对应 SnowLuma issue #158）：进程枚举超时/失败时保留上次
  快照，不能因一次超时就对每个存活 PID 触发 process-gone 而拆掉健康会话。
- **收包心跳看门狗**：QQ 的 `SsoHeartBeat` 响应到达一次后才武装看门狗；此前的沉默
  按 UNKNOWN 处理而非「不健康」，避免不暴露该命令的 QQ 版本被误判。武装后 90s 无包
  判定 recv 路径僵死并重连（15s 二次确认）。
- **登录状态对账**：hook 可能漏推 loginState（自动登录竞态）。用端口探测
  （对应 `qq-port-probe`）兜底，每 3s 对账一次。
- **注入幂等**：管道已存在（`mojo.<pid>.control` 可连）就复用现有 hook，不重复注入，
  避免 QQ 进程内挂两份 hook。
- **`.dll` 缺失**：`binary_locator.py` 找不到用户放置的 `.dll` 时，给出明确指引
  （放置路径 + 从 SnowLuma release 获取），而非模糊报错。

## 测试策略

- **① 注入器**：先在自写的傀儡进程（一个已知导出/已知行为的最小 DLL + 宿主 exe）上
  验证 manual-map 全流程（alloc / 节拷贝 / 重定位 / IAT / 异常表 / entry 调用），
  再上真 QQ。**这是唯一必须真机 + 真 QQ 的部分**，无法纯单测。
- **③ HQP1**：纯字节编解码。用 SnowLuma 源码里的常量做 golden test
  （magic / header 布局 / op 值 / flags），离线可测。往返编解码性质测试。
- **④ proto**：抓若干真实 recv 帧存为 fixture，断言解出的 URL / 文件名 / at 目标
  正确。参考 SnowLuma `tests/msg-push/*` 的 fixture 思路。
- **端到端**：Phase 0 探针脚本即第一个 e2e——收到一条真实群消息并打印。

## 分阶段实施

| Phase | 内容                                                                                    | 脱离 Node? | 脱离 SnowLuma 运行时?  |
| ----- | --------------------------------------------------------------------------------------- | ---------- | ---------------------- |
| 0     | 探针：手动注入（先借 SnowLuma `.node`）→ Python 连 recv 管道 → 解析并打印一条真实群消息 | 否         | 否                     |
| 1     | ③④ Python 客户端 + proto 解码，产出 `CapturedMessage`，接入 TTS/通知，删除旧引擎        | 是         | 否（注入仍借 `.node`） |
| 2     | ① Python manual-map 注入器，替换借来的 `.node`                                          | 是         | **是**                 |
| 3+    | macOS hook（从零逆向 QQ mac 客户端）                                                    | —          | 未来另立项目           |

### Phase 0 验收标准

- Python 脚本能连上 `mojo.<pid>.recv` 命名管道。
- 能正确按 HQP1 帧格式切出 op=6 帧与 op=7 帧。
- 能从一条真实群文本消息解出发送者与文本；从一条图片消息解出 origUrl。
- 产出可存档为 proto 单测的 fixture。

## 风险与依赖

1. **结构性依赖 SnowLuma 的 `.dll`**：`.dll` 专有、跟 QQ 版本绑死。本栈天花板由
   SnowLuma 的 `.dll` 更新节奏决定；QQ 大更新时只能等其发布新 `.dll`。这是既定
   取舍，非缺陷。
2. **LICENSE 合规**：见下方「LICENSE 合规边界」专节。
3. **manual map 的工程量与稳定性**：Phase 2 的注入器是全项目最硬的一块，且直接关系
   防封效果，必须忠实复刻、充分测试后再上真机。
4. **QQ 版本兼容**：无降级路径。不兼容即不工作，需向用户明确暴露此状态。

## LICENSE 合规边界（SnowLuma §5）

SnowLuma LICENSE §5 明确：Native Addon（`snowluma-*.dll`/`.node`/`.so`）为专有组件，
**不在该许可范围内**，除以随附形式运行外，不授予**逆向、复制、修改、单独再分发、
再许可**的任何权利。本项目据此划定以下硬边界：

- **不打包、不内置**：QQListener 的任何安装包/仓库都**不得**包含 SnowLuma 二进制。
  这些路径进 `.gitignore`。
- **不逆向 SnowLuma 二进制**：我们重写的是①注入技术、③管道 HQP1 协议、④proto 字段号——
  这些是公开知识/线协议，**不通过逆向 SnowLuma 的 `.dll`/`.node` 获得**。`.dll` 全程
  原样运行，不逆、不改、不单独分发。
- **macOS 路径的合规约束**：因 §5 禁止逆向，未来 mac hook **不得逆向 SnowLuma 的二进制**。
  合规路径只有两条：(a) 净室实现——从零自写 mac hook，全程不接触 SnowLuma 二进制；
  (b) 按 §6 向著作权人（`motricseven@foxmail.com`）取得书面授权。
- **内核获取（下载 UX）的合规规则**（Plan B 的「内核获取」模块遵守）：
  - 下载源**只指向 SnowLuma 官方 release**（`github.com/SnowLuma/SnowLuma/releases`）。
    程序是「替用户点下载」，二进制来自著作权人自己的分发点。
  - **不自建镜像、不做中转**：把二进制放到自有服务器让程序拉 = 再分发，违 §5。
  - **加速代理由用户配置**：提供「GitHub 官方」单选 + 一个**预填但可编辑**的代理输入框
    （如 ghproxy 类第三方代理）。代理是第三方服务、由用户填写与选择，程序不硬编码、
    不钦定、不自建。再分发责任在代理运营方，不在本项目。做成可编辑框亦更耐用（公共代理
    常更换域名/下线）。
  - 下载流程：下载官方 release zip → 解出 `native/snowluma-win32-x64.dll` → 校验版本/hash
    → 放入 `./native/`。下载前展示 SnowLuma 许可并要求用户确认。
  - 初期（Phase 0/1）需一并获取 `.node` 注入器；Plan C 用 Python 复刻注入器后，用户仅
    需单个 `.dll`。
