# SnowLuma / PMHQ 接入审查

审查日期：2026-10-24。范围：双引擎改动及受影响的捕获、协议、图片、回复、通知、原生清理和打包路径。

当前 PMHQ 实现直接装载本机 DLL，通过命名管道收发，不再运行 Rust 启动器，也不再要求 auth_token 或 manager。此前仅替换远程签名 compute 的实验已经退出运行路径。

托管回归和隔离原生检查已通过；真实 Windows QQ 注入、签名及消息投递尚未验收。PMHQ 继续标为实验性。本文不构成对 PMHQ 闭源主体的完整审计。

针对发包入口、独立签名管道及本轮缺陷复现的补充结论见[发包与签名路径审查](pmhq-signing.md)。当前默认路径没有调用 `pmhq_sign`。

## 固定输入

| 项目 | 版本与依据 |
| --- | --- |
| [LuckyLilliaBot](https://github.com/LLOneBot/LuckyLilliaBot/tree/dc2b2b90092ef0f571b5546878288b83fff1e436) | `dc2b2b90092ef0f571b5546878288b83fff1e436`，PMHQ API、消息/OIDB/media 定义 |
| [SnowLuma](https://github.com/SnowLuma/SnowLuma/tree/f8cb15c27bca3d2d1bd044a921e55bfc44bb5e60) | `f8cb15c27bca3d2d1bd044a921e55bfc44bb5e60`，HQP1、消息元素、图片、引用和群回应 |
| [PMHQ 发行包](https://github.com/linyuchen/PMHQ/releases/tag/v9.0.6) | Windows x64 v9.0.6，原 DLL 哈希及新增代码见 [NOTICE](../../Assets/PMHQ/NOTICE.md) |
| [原启动器配置](https://github.com/linyuchen/PMHQ/blob/7551952bf6779e89c27d9c1155894ecf58b4d8c7/doc/config.md) | 区分原 manager auth_token 与本机 API token；当前插件运行路径均不使用 |

LLBot、SnowLuma 本地源码分别按固定提交核对了 1092、1236 个 blob。Windows 脚本换行差异归一化后相符；SnowLuma 本地 CLAUDE.md 的差异没有作为实现依据。LLBot 的无关 OneBot/Milky API 不在本次逐项审查范围内。

原生分析使用 Ghidra 11.3.2 和 Capstone。定位了 DLL 初始化、anchor 派生、命令管道、账号存储和 JSON 序列化；没有声称拿到了 PMHQ Rust 主体源码。

## 架构及对照

| 路径 | 对照结果及当前实现 |
| --- | --- |
| 引擎管理 | `QqEngineService` 串行管理 SnowLuma / PMHQ。默认 SnowLuma，切换清队列和窗口，等待旧 QQ 退出；禁止同进程混装 |
| SnowLuma 传输 | 对照其 qq-hook-client / hook-packet-client，保留 HQP1 帧、请求回包和登录语义；control 请求限时，recv 长连接不套该超时 |
| PMHQ 初始化 | 新 `local_pipe.c` 在原 DLL 内生成 nonce 和初始化参数，再调用原入口。四组定位信息可以本机正确解码，不请求 manager；运行时文件改用独立名称，避免旧启动器配置覆盖本机参数 |
| PMHQ 进程 | `PmhqHost` 使用现有手动映射器装载 DLL。没有常驻辅助 EXE、HTTP/WS 服务、启动凭证或绑定/上报客户端 |
| PMHQ 管道 | DLL 的协议与 LLBot 使用的启动器 HTTP 协议不同。命令为一行 `{id,cmd,body}`；原生回包为 `{kind:"resp",id,code,data,msg}`，不是 HTTP 的 echo/pb 包装 |
| 接收 | 原生 trace 的 type 为 recv/send。仅无 echo 的 MsgPush 进入共同 SsoDecode；请求回包和发送镜像不显示为聊天 |
| 发包 | 本机命令进入 DLL active_send，再调用捕获的 QQ sendSso 指针；最终签名尚未验证。必须 id 匹配、code 为 0，拒绝无效 data；不记录 msg/pb，不在断线或超时后自动重发。群文字回复还必须有正数群序号回执 |
| 账号 | 同时检查 QQ PID、native 最近观察到的 UIN 和原生包 UIN。发包前重读缓存；观察到账号变化时撤销会话，退出或换号必须重启 QQ。UIN 按固定版本的 LazyLock/Mutex/String 布局读取一致快照，读取不稳定时拒绝请求；这不是实时登录状态查询 |
| 图片 | 共用 NT/旧图、群/私聊 rkey 选择及刷新逻辑。换账号清缓存。既有 SnowLuma 独立 oracle 16 组继续覆盖混合 Elem、域名、回退和去重，见 [fixtures](../../Tests/QQListener.Protocol.Tests/Fixtures/README.md) |
| 文件、资料 | 共用 QqControlApi 的群文件、私聊文件和资料 OIDB；自动下载仍由用户开关控制 |
| 引用回复 | 对照 SnowLuma element-builder、message 和 LLBot 消息定义，群聊引用原序号，私聊发普通文本，沿用相应 clientSequence 规则 |
| OK 回应 | 对照双方 group/action 定义，使用 `0x9082_1`、code 124，仅群聊支持 |
| ClassIsland | 共同筛选、去重、积压、合并、优先级、宿主时长、置顶和取消逻辑；不复制第二套通知实现 |
| 打包 | 仅内置修改 DLL，解压检查 SHA-256。原 EXE 已移除。旧 pmhq_config.json 不读取、不覆盖。SnowLuma 保持原独立安装及许可流程 |

LLBot 会为历史同步发送额外 InfoSync，本插件只提醒新消息，不复制该注册请求。PmhqClient 的 HTTP/WS 适配仅保留为开发对照，当前 PMHQ 引擎使用 PmhqLocalClient。

## 修正项目

| 程度 | 修正 | 验证 |
| --- | --- | --- |
| 高 | 切换和换号后旧窗口不得复用新路由；驻留标记包含 PID 与启动时间，切回仍需等旧 QQ 退出 | EngineLifecycleTests、路由/session/bundle 测试 |
| 高 | detach 或注销 unwind 失败后停止后续映像释放，避免仍运行的原生回调落入已释放内存 | RemoteImageCleanupTests、CoreServiceTests |
| 高 | HTTP echo 不代表成功；本机管道同样严格检查响应 id/code/data，失败正文不进入错误日志 | PmhqProtocolTests、PmhqClientTests、PmhqLocalClientTests |
| 高 | 本机管道核对当前用户及客户端 QQ PID；发包前重读 native UIN 缓存，观察到变化时拒绝旧账号回复 | 错误 PID、换号和退出替身测试；原生缓存刷新时机及真实 Windows PID API 待验收 |
| 高 | 请求在排队前绑定管道，禁止断线前请求发往新连接；旧连接清理不能清除新连接状态 | 真实命名管道阻塞写、断线重连和迟到续体回归，修复前复现失败 |
| 高 | 空发送回包拒绝成功；群回复要求群序号，私聊序号不能代替群回执 | MessageSenderTests 三个修复前失败用例及错误 wire type 测试；对照 SnowLuma 群/私聊规则 |
| 中 | 接收回调 enrichment 可能同步发包，若与读回包共用一个循环会死锁；改为独立、有界的投递通道 | PmhqLocalClientTests 的真实命名管道请求/回包交叉用例 |
| 中 | control 请求连接单独限时 3 秒，连接后的读、异步写和 flush 共用截止时间；本机发送部分失败即断开管道，防止 JSON 帧串接 | NamedPipeDeadlineTests；本机客户端源码检查 |
| 中 | 关闭监听时不 enrichment/积压，捕获与投递队列有界 | source/queue/disabled 测试 |
| 中 | 并发重复消息写锁内复查；原生接收及会话观察者异常隔离 | 100 路并发去重、RecvCaptureTests、session observer 测试 |
| 中 | 消除原启动器 --dump-sign 输出和临时凭证配置；本机包装入口关闭签名、TEA、protobuf dump | local_pipe.c、PmhqHost 源码及隔离加载检查 |

## 验证结果

- Release 解决方案测试：635 通过，0 失败、0 跳过，其中协议/生命周期 373，模型/服务 262。
- Windows 目标的全部工程锁定依赖交叉编译通过，0 警告、0 错误。DLL 从仓库源码重建后与内置文件逐字节一致。
- 新本机管道测试覆盖原生 response 结构、无凭证命令、数字 id、失败不泄露、UTF-8/JSON 分片、enrichment 发包不死锁、连接 PID、换号和退出前置检查。
- PMHQ → ClassIsland 集成使用两条合成 protobuf push，经 decoder、规则、暂停摘要到通知请求，核对等级、时长和 session；不是真实宿主运行结果。
- 隔离 Wine 10 原生 probe：本机生成初始化参数后，四组 anchor 正确；新 DLL 入口状态 0；原生账号布局相符；命令经 DLL 实际管道读取器处理，正确序列化 code=-1 的未就绪失败，见 [记录](evidence/pmhq-local-runtime.json)。
- 原生命令 probe 使用 PMHQ_SRDI_TEST 禁用 QQ 钩子，并仅在短命 fixture 进程中替换输出队列回调以捕获响应。它验证读取、解析、错误序列化，未证明完整 native event writer 或真实 QQ 投递。可重复的源码和方法见 [native probes](../../Tools/PMHQ.LocalSign/README.md)。

## 未完成的验收与限制

1. 没有原生 Windows、真实 QQ 正常注入、签名、群/私聊图片下载、引用回复、群回应及消息投递结果。无需凭证已经在启动结构和隔离初始化上实现，不等于真实 QQ 收发可用。
2. 只支持一个 QQ 主进程，停止连接不热卸载 PMHQ；切换引擎、退出登录或换账号后需重启 QQ。
3. PMHQ 原 DLL 闭源，只能对新增包装代码、已分析路径和托管接入给出审查结果，无法为整个第三方内核给出源码级保证。
4. 账号快照、内核就绪标志和新增原生调用按 v9.0.6 固定地址工作，必须与内置 SHA-256 一同更新；不能直接替换成其他版本。
   该 UIN 是发送钩子维护的最近观察值，不能保证立即识别退出或换号。托管换号测试使用账号读取替身；真实 DLL 内部账号刷新时机及 Windows 一致快照读取仍需实机验收。
5. SnowLuma 旧 CoreService 在原生操作超时后的晚到状态，仍可能短暂覆盖全局诊断文本；命令和捕获已有会话隔离，该显示竞态尚未全面改为 generation 范围。

Windows 验收应分别记录两套引擎的单聊/群聊文字、群图/私聊图、手动/自动文件下载、引用与 OK 回应、暂停恢复、取消通知、换号及切换重启，并确认旧窗口不会向新账号发送。
