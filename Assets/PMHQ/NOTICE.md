# PMHQ native component

The Windows x64 runtime is reconstructed on first use from the original PMHQ v9.0.6 DLL from
<https://github.com/linyuchen/PMHQ/releases/tag/v9.0.6>. The original code is a
third-party component and is not covered by QQListener's MIT license. The plugin
downloads a 41,798-byte binary delta from the signed QQListener distribution catalog;
neither the delta nor a complete PMHQ DLL is embedded in the plugin.

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
| Reconstructed pmhq.dll | ffb7e841a811ebead258730d7bb6f71c06a7e07e776ca1128c054bececa6c004 |
| Original upstream pmhq.dll | 345a8fc083f08968a73c237441c6b56010757f613857bc96cec7449623e46ae4 |
| Official pmhq-win-x64.zip | 64da6159976aee0eb1b67bc0c82ca3c540d9bfd4a41cc17d5a308e24dbfd7587 |
| Downloaded delta | 46b1b076f0d97bb1c2f877a19ab92313351111a2dbdfe5d480b2a5002bec9dbc |

The fixed asset URL is
<https://github.com/linyuchen/PMHQ/releases/download/v9.0.6/pmhq-win-x64.zip>.
Each supported release pins all four hashes and its host ABI/layout in Distribution/pmhq/.
Installation rejects mismatches and only upgrades when the upstream version has a
matching catalog entry; it atomically publishes the DLL after verification. Verified local DLLs
and the original cache can be reused offline. The local-pipe.2 variant fixes section virtual-address gaps in the earlier DLL;
Windows rejected that layout with error 193 although Wine accepted it.

The wrapper source is in Tools/PMHQ.LocalSign/dll-only-patch/local_pipe.c.
Build instructions and isolated probe sources are in Tools/PMHQ.LocalSign/.
Original code/data sections are preserved on disk; the wrapper changes the
runtime configuration filename namespace in memory before initialization.

Isolated Wine 10 checks cover local initialization, anchor decoding, the native
account layout, command parsing/error serialization, and TLS isolation on the
loading thread, a pre-existing thread and a new thread. They do not validate
native Windows, real QQ hook installation, signatures or message delivery.
