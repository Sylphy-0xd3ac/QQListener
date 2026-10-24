# PMHQ local runtime

The plugin uses the PMHQ v9.0.6 DLL directly through a per-QQ named pipe. It does
not run the original Rust launcher or require its manager startup token.

The native wrapper is `dll-only-patch/local_pipe.c`. It derives local initialization
parameters with the pinned DLL's own leaf functions, validates the decoded anchor,
sets the local attach environment, and calls the original entry. It separates the
runtime filename namespace from stale launcher configurations and disables sign,
TEA and protobuf dumping. The original native QQ hook/send implementation remains
in the DLL.

The managed host is `Core/PmhqHost.cs`; transport and protocol are
`Core/PmhqLocalClient.cs` and `Protocol/PmhqNativeProtocol.cs`. Windows pipe clients
must have the current user and the selected QQ PID. Requests are correlated by
numeric id; a failed or interrupted send is never replayed.

PMHQ contains static TLS. `PmhqHost` uses `WindowsProcess.LoadLibraryFile` to call
the system's LoadLibraryW inside QQ with the full DLL path. The return value is
read as a 64-bit module handle. Incomplete remote calls retain their argument
memory; the DLL stays loaded until QQ exits. The manual mapper rejects TLS images.

## Build

Use Zig 0.15.2 and Python 3 with pefile:

```sh
python dll-only-patch/build.py --original /path/to/official/v9.0.6/pmhq.dll --local-pipe
```

The builder checks the original SHA-256 and merges PE imports, exports, unwind
records and relocations. The output is `dll-only-patch/build/pmhq.dll`.
The local pipe variant is the default; `--legacy-compute` reproduces the earlier
compute-only experiment, which is not used by the plugin. Python and Zig are
build tools, not plugin runtime dependencies.

## Isolated native probes

Compile on a development machine, then run on Windows or in an isolated Wine 10
prefix with the absolute DLL path as the only argument:

```sh
zig cc -target x86_64-windows-gnu -O0 -municode tests/proof_probe.c -o proof-probe.exe
zig cc -target x86_64-windows-gnu -O0 -municode tests/pipe_probe.c -o pipe-probe.exe
zig cc -target x86_64-windows-gnu -O0 -municode tests/tls_loader_probe.c -o tls-loader-probe.exe
```

All probes use PMHQ's explicit `PMHQ_SRDI_TEST` option. They do not inject QQ.
The proof probe checks all four decoded anchors. The pipe probe checks wrapper
status and native account layout, sends a fixture command over the actual native
pipe reader, and captures the serialized response before the transport queue.
That queue callback is replaced only inside the short-lived fixture process:
this is a command-parser/error-serialization check, not proof of the complete
native event writer or actual QQ message delivery.

The TLS probe checks the loading thread, a thread created before LoadLibraryW,
and a thread created afterward. It exercises the pinned DLL's Rust TLS accessor,
checks distinct per-thread blocks and verifies the host's static TLS is unchanged.
Earlier initialization probes already used LoadLibraryW and therefore did not
cover the former production manual-mapping path. This probe validates the loader's
TLS behavior in a fixture, not remote injection or signing in real QQ.

See `Assets/PMHQ/NOTICE.md` for binary identities and
`docs/reviews/dual-engine.md` for integration review and remaining validation.
