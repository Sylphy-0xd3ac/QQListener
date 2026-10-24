# PMHQ 发包与签名路径审查

审查日期：2026-10-24。对象为内置 PMHQ v9.0.6 Windows x64 DLL、本地初始化包装、托管管道客户端和共同消息发送接口。

当前实现没有调用 `pmhq_sign` 签名管道。已确认的链路是 `pmhq_cmd_<QQ PID>` → DLL `active_send` → 捕获的 QQ `sendSso` 函数指针。静态分析可以追到 QQ 进程内发包入口，不能据此确认 QQ 最终调用了哪个签名函数、服务端接受了签名或消息已经投递。PMHQ 保持实验性。

## 输入及对照

- PMHQ：上游 v9.0.6；原 DLL SHA-256 为 `345a8fc083f08968a73c237441c6b56010757f613857bc96cec7449623e46ae4`，内置修改 DLL 为 `2c667c5bda5e517fd7b97afe21627f23178a68cd18ccc523375ed7935b3ed78b`。使用 Ghidra 11.3.2 和 Capstone 分析原有函数；以下地址均为 RVA。
- [SnowLuma 消息发送](https://github.com/SnowLuma/SnowLuma/blob/f8cb15c27bca3d2d1bd044a921e55bfc44bb5e60/packages/core/src/bridge/apis/message.ts)：群消息要求正数 `groupSequence`；私聊只检查非零 result，不强制正数私聊序号。
- [SnowLuma 管道客户端](https://github.com/SnowLuma/SnowLuma/blob/f8cb15c27bca3d2d1bd044a921e55bfc44bb5e60/packages/bridge/src/hook-packet-client.ts)：通过本地控制通道发送 command/body 并检查回包错误。其原生发送器也使用捕获的进程内发送入口；这一点不能代替真实 QQ 签名验收。
- [LLBot PMHQ 客户端](https://github.com/LLOneBot/LuckyLilliaBot/blob/dc2b2b90092ef0f571b5546878288b83fff1e436/src/main/qqProtocol/pmhq.ts)：走启动器 HTTP/WS，echo 只用于关联、code 用于判断成功。可以对照包裹和错误语义，不能将它等同于当前直接 DLL 的传输实现。

## 实际调用路径

```text
QqControlApi / MessageSender
  → PmhqLocalClient.SendAsync
  → pmhq_cmd_<pid>，一行 JSON {id, cmd, body}
  → DLL 命令分发 0x29ed
  → active_send 0xd4e5
  → 捕获的 QQ sendSso 函数指针（原生命令、body、callback）
```

`0xd4e5` 的反编译和指令核对显示实际间接调用原生发送入口；其路径没有向 `pmhq_sign` 发起请求。C# 中的 id 是本机请求关联号，不能当作 QQ SSO 的传输序号，补一个同名签名管道调用也不能证明签名正确。

DLL 中独立存在签名 RPC：初始化函数 `0x126e6` 在 `dump_sign` 标志 `0xf571b` 启用时安装 `0x10a5a` 的签名 detour，并启动工作线程。线程入口 `0x97c7` 调用 `0x3cb5`，后者创建 `pmhq_sign` 管道。当前 `local_pipe.c` 清除 `PMHQ_DUMP_SIGN`，默认不启用这条分支。Host 检查同名管道仅用于阻止钩子冲突。

默认构建使用 `local_pipe.c`；历史 `--legacy-compute` 实验不进入当前 DLL。当前导出中没有 `PMHQLocalSignCompute`。原 DLL 中保留管道名称字符串，不代表默认执行了签名 RPC。

| 核对项 | 结论 |
| --- | --- |
| 本地生成初始化参数，无 Rust 启动器或 manager 请求 | 包装源码、构建输入和隔离初始化已验证 |
| 命令进入原生 active_send 并调用 QQ 发包指针 | 静态确认；隔离 probe 只验证未就绪失败路径 |
| 插件主动调用 `pmhq_sign` | 没有调用；默认配置未启用该 RPC |
| QQ 最终本地签名及真实消息投递 | 未验证，需要实际 Windows QQ 运行证据 |
| DLL 初始化标志代表签名就绪 | 不成立；属性已改名 `NativeInitialized` |

## 发现并修复的问题

### 高：断线前排队的请求可能被写入重连后的管道

旧 `SendAsync` 在取得写锁后才读取 `_pipe`。断线已经让请求失败，但排队任务仍可能取得新连接并发出旧消息；旧连接迟到的写失败还可能清除新连接状态。

现在在排队前捕获管道，取得写锁后核对同一个连接，只向捕获的管道写入。已接受的连接改用对象引用管理，清理采用比较交换，旧回调不能清除新连接。另加单接收者约束，防止第二个接收循环替换活动连接。

真实命名管道回归 `RequestsQueuedBeforeDisconnectNeverMoveToANewConnection` 使用阻塞写和暂停异步续体，先断线再连接新 peer；修复前旧请求确实出现在新 peer，修复后没有重发，随后新请求正常完成。`ASecondReaderCannotReplaceTheActiveConnection` 覆盖重复接收者。

### 高：空回包或错误类型的群回执可能被显示为发送成功

旧解析器将空字节解析为 result=0、sequence=0；群消息也可能错误采用私聊序号。现在拒绝空回包和已知字段的错误 wire type，按会话类型读取序号，群消息必须有正数群序号。缺少群回执时提示结果未确认，并提醒先检查会话，防止用户重复发送。

空回包、群回执缺失、用私聊回执代替群回执三个用例在修复前均失败，修复后通过。私聊保留 SnowLuma 的非空 result=0 回执语义，没有增加上游未要求的序号限制。有效回执仍不能证明对端已经阅读或设备已收到消息。

## 尚未解决的证据缺口

1. **账号检查读取的是缓存。** `PmhqHost.CurrentAccount` 读取 `0xf31d8` 的 `LazyLock<Mutex<String>>`。发送 detour `0x21244` 更新该记录，序列化函数 `0x2450b` 读取它；这不是登录/退出状态查询。托管端会拒绝已观察到的 UIN 变化，但在原生缓存尚未刷新时，不能保证立即识别退出或换号。账号替身测试不证明真实刷新时机；退出登录、换号仍须重启 QQ。
2. **没有完整的真实 Windows QQ 端到端结果。** 隔离 probe 使用 `PMHQ_SRDI_TEST` 禁用 QQ 钩子，并替换短命 fixture 中的输出队列回调；它证明命令读取、解析及失败序列化，未证明完整事件写出、真实签名或消息投递。
3. **闭源主体和固定地址。** 此次审查覆盖新增代码及上述反编译路径，无法对 PMHQ 全部闭源代码作“没有问题”的保证。地址和原生对象布局绑定内置 SHA-256，升级必须重新核对。

## 验证

- Release 全量测试：635 项通过，0 失败、0 跳过（协议/生命周期 373，模型/服务 262）。
- 新回归分别证明修复前失败、修复后通过；测试记录前缀为 `pmhq-sign-reviewed`。
- 原生初始化、anchor 和命令失败路径的既有隔离证据见 [pmhq-local-runtime.json](evidence/pmhq-local-runtime.json)。本次未把隔离结果扩展为真实 QQ 签名结论。
- 审查输入哈希和原生地址见 [静态证据索引](evidence/pmhq-signing-static.json)。更广范围的改动见 [双引擎审查](dual-engine.md)。
