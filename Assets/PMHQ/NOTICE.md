# PMHQ native component

The Windows x64 runtime is reconstructed on first use from the original PMHQ v9.0.6 DLL from
<https://github.com/linyuchen/PMHQ/releases/tag/v9.0.6>. The original code is a
third-party component and is not covered by QQListener's MIT license. The plugin
ships only a 41,788-byte binary delta, not the original or modified complete DLL.

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
| Reconstructed pmhq.dll | 2c667c5bda5e517fd7b97afe21627f23178a68cd18ccc523375ed7935b3ed78b |
| Original upstream pmhq.dll | 345a8fc083f08968a73c237441c6b56010757f613857bc96cec7449623e46ae4 |
| Official pmhq-win-x64.zip | 64da6159976aee0eb1b67bc0c82ca3c540d9bfd4a41cc17d5a308e24dbfd7587 |
| Bundled delta | 6eea307e7f5495093bff6ab920de12afd627d287581b662f99d497ea5a522995 |

The fixed asset URL is
<https://github.com/linyuchen/PMHQ/releases/download/v9.0.6/pmhq-win-x64.zip>.
All four hashes are pinned. Installation rejects mismatches, never selects latest,
and atomically publishes the DLL only after verification. Verified local DLLs
and the original cache can be reused offline. The reconstructed file is byte-for-byte
identical to the DLL shipped in QQListener 2.3.0-beta.1.

The wrapper source is in Tools/PMHQ.LocalSign/dll-only-patch/local_pipe.c.
Build instructions and isolated probe sources are in Tools/PMHQ.LocalSign/.
Original code/data sections are preserved on disk; the wrapper changes the
runtime configuration filename namespace in memory before initialization.

Isolated Wine 10 checks cover local initialization, anchor decoding, the native
account layout, command parsing/error serialization, and TLS isolation on the
loading thread, a pre-existing thread and a new thread. They do not validate
native Windows, real QQ hook installation, signatures or message delivery.
