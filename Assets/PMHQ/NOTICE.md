# PMHQ native component

The bundled Windows x64 DLL is based on the original PMHQ v9.0.6 DLL from
<https://github.com/linyuchen/PMHQ/releases/tag/v9.0.6>. The original code is a
third-party component and is not covered by QQListener's MIT license.

QQListener adds a local initialization entry wrapper and uses its own managed
named-pipe host. The Rust launcher is neither bundled nor executed. This runtime
does not require an auth_token, manager request or local HTTP service. Commands
reach a captured in-process QQ sendSso entry. The default runtime does not call
the separate pmhq_sign RPC; final signing and delivery require real QQ validation.

PmhqHost loads the DLL through LoadLibraryW in the selected QQ process so the
system loader initializes its static TLS. The manual mapper rejects TLS images.
The loaded module remains resident until QQ exits; it is not hot-unloaded.

| File | SHA-256 |
| --- | --- |
| Bundled pmhq.dll | 2c667c5bda5e517fd7b97afe21627f23178a68cd18ccc523375ed7935b3ed78b |
| Original upstream pmhq.dll | 345a8fc083f08968a73c237441c6b56010757f613857bc96cec7449623e46ae4 |

The wrapper source is in Tools/PMHQ.LocalSign/dll-only-patch/local_pipe.c.
Build instructions and isolated probe sources are in Tools/PMHQ.LocalSign/.
Original code/data sections are preserved on disk; the wrapper changes the
runtime configuration filename namespace in memory before initialization.

Isolated Wine 10 checks cover local initialization, anchor decoding, the native
account layout, command parsing/error serialization, and TLS isolation on the
loading thread, a pre-existing thread and a new thread. They do not validate
native Windows, real QQ hook installation, signatures or message delivery.
