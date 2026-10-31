# Loader、内核更新与切换

2026-10-31。此记录覆盖 Loader 拆分、远程分发和生命周期修改；之前 PMHQ 原生探针的结论仍限于其记录的平台与版本。

## 分发与恢复

- 业务程序集仍名为 `QQListener.dll`，保留 Avalonia 资源 URI。CIPX 入口改为 `QQListener.Loader.dll`，通过 ABI 1 调用业务初始化。ClassIsland、Avalonia、DI 和 WinRT 合同共享宿主实例。
- 同一构建生成独立核心 ZIP、内置版 CIPX 和下载版 CIPX。分发使用 `Sylphy-0xd3ac/QQListener` 的公开 `runtime-builds` Release，不依赖用户的 Actions 下载令牌。
- 清单使用 P-256 / SHA-256 签名。安装验证包长度、SHA-256、路径、解压大小、文件清单和程序集名称；启动时再次对照原始包验证文件。新版本写入独立目录，成功初始化后才切换 active 指针。
- 网络失败使用现有版本。加载或初始化失败标记为 rejected，下次启动选择上一可用版本。初始化可能已修改 DI，因此不在同一次宿主启动中再次注册旧核心。
- PMHQ 补丁从同一签名清单下载。只为有明确原始包、补丁、输出哈希和 HostAbi/Layout 的上游版本提供升级；缺少新版适配时保留已有版本。

## 原生加载与切换

`LoadLibraryW` 失败后，旧实现已写入驻留记录，后续重试因此报告已有监听。记录现在只在成功加载后写入；远程调用超时单独标为结果不确定。启动检查以同一 PID 的模块和 PMHQ 命令管道为依据，不再用其他进程的 EXE 或全局签名管道推断当前 QQ 的状态。

远程调用页在 QQ 线程内紧接加载函数调用 `GetLastError`，分别保存完整 64 位返回值、32 位错误码和完成标记。目标线程未完成时保留参数与未知原生状态，避免重复装载。Windows CI 用当前测试进程加载无效 PE，检查 193 错误码来自目标线程。

SnowLuma 官方 bridge 使用 unload 后检查管道消失的流程。插件只卸载自己持有的映像，并在切换前检查管道、未完成注入及残留句柄。PMHQ 发行包未提供已确认的安全停止合同，因此不直接 `FreeLibrary` 仍有工作线程和回调的 DLL；从 PMHQ 切出仍需重启 QQ。

## 验证边界

测试覆盖下载版通过代理获取签名核心、内置版离线落盘、禁止降级、错误签名与哈希、路径穿越、大小和文件清单、并发安装、加载失败后的版本选择、PMHQ 无适配时不下载新版、补丁缓存修复和切换期间未完成的注入。

`Tools/QQListener.LoaderVerifier` 从实际 CIPX 安装核心，在独立上下文中解析 DI、构造设置页并读取 Avalonia 图标。界面预览另行检查下载窗口及安装向导。

这些检查不能证明真实 Windows QQ 中的 PMHQ 初始化、签名或投递成功。实际 `LoadLibraryW` 故障仍需结合用户机器上的完整错误码和日志诊断，不能将错误码改进称为已经验证修复了所有加载故障。

## Windows 193 reproduction and PE correction

Windows run 41211008156 rejected the previous reconstructed DLL with `LoadLibraryW failed=193`; its SHA-256 matched the reviewed `2c667c5b...` file. Wine accepted that same file and passed the TLS fixture. That difference invalidates using Wine success as evidence of Windows loadability.

The merged image left gaps at RVA `0x102000..0x103000` (payload header space) and `0x105000..0x106000` (omitted payload debug section). Microsoft's [PE specification](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format#section-table-section-headers) requires image section VAs to be ascending and adjacent. The merger now extends preceding virtual zero-fill spans to cover those gaps; it preserves all code/data RVAs, original raw section bytes and TLS metadata.

The corrected runtime is `9.0.6-local-pipe.2`. The former delta remains only as a negative CI fixture. The native check loads the official DLL, requires error 193 from the old reconstructed layout, then checks TLS and the production remote-call path with the corrected DLL. Only supported versions appear in the distribution catalog. Existing local-pipe.1 installations are upgraded to the new minimum runtime rather than reused.

## Windows import protection correction

Run 41220467667 reached a second failure with the section-gap correction: exception `0xc0000005`, write target ending at the original IAT RVA `0x91000`. The official DLL passed, while the old image still returned 193. The merger had cleared the IAT directory and moved import descriptors into another section; Windows consequently attempted to write the still-read-only original IAT. Wine's different protection handling concealed this error.

`local-pipe.3` preserves the original IAT directory and imports. Its freestanding wrapper reuses the original `GetModuleHandleW`, `GetProcAddress`, `GetLastError`, `VirtualProtect` and `ProcessPrng` IAT slots, resolving `SetEnvironmentVariableW` through the existing Kernel32 entry. The merger rejects helper imports rather than producing a second discontiguous IAT. Nonces still come from Windows' CSPRNG. No original code, data or TLS RVA moves.
