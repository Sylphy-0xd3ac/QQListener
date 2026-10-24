#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stddef.h>
#include <stdlib.h>
#include <stdio.h>
#ifdef PMHQ_FREESTANDING
#include "../dll-only-patch/minicrt.h"
#endif
#include "sign_json.h"

#define COMPUTE_RVA 0x12e8d3
#define HEX_RVA 0x12e67b
#define FREE_RVA 0x11fe87
#define RUST_ERROR UINT64_C(0x8000000000000000)
#define SIGN_ERROR UINT64_C(0x8000000000000005)
#define MAX_HEX (8U * 1024U * 1024U)
#define MAX_REPLY (64U * 1024U)
#define PIPE_NAME L"\\\\.\\pipe\\pmhq_sign"

typedef struct { uint64_t cap; unsigned char *ptr; uint64_t len; } RustVec;
typedef union { RustVec values[3]; uint64_t words[9]; } ComputeResult;
typedef struct {
    uint32_t flags, extra_seq;
    const char *cmd; uint64_t cmd_len;
    const char *body; uint64_t body_len;
    uint64_t context[11];
    uint32_t seq;
} ComputeArgs;
_Static_assert(offsetof(ComputeArgs, seq) == 0x80, "verified v9.0.6 argument ABI");
_Static_assert(sizeof(RustVec) == 24, "verified Rust Vec layout");

typedef void (*HexDecode)(RustVec *, const char *, uint64_t);
typedef void (*VecFree)(uint64_t, void *);
typedef ComputeResult *(*ComputeFn)(ComputeResult *, void *, const ComputeArgs *);
static unsigned char *image;
static HexDecode decode_hex;
static VecFree free_vec;
static volatile LONG installed;
static const unsigned char compute_head[32] = {0x55,0x41,0x57,0x41,0x56,0x41,0x55,0x41,0x54,0x56,0x57,0x53,0x48,0x81,0xec,0x08,0x06,0,0,0x48,0x8d,0xac,0x24,0x80,0,0,0,0x48,0xc7,0x85,0x80,0x05};
static const unsigned char hex_head[32] = {0x55,0x41,0x57,0x41,0x56,0x41,0x55,0x41,0x54,0x56,0x57,0x53,0x48,0x81,0xec,0xc8,0,0,0,0x48,0x8d,0xac,0x24,0x80,0,0,0,0x48,0xc7,0x45,0x40,0xfe};
static const unsigned char free_head[5] = {0xe9,0xda,0xc6,0xf9,0xff};

static void result_error(ComputeResult *out, int32_t code) {
    memset(out, 0, sizeof(*out));
    out->words[0] = RUST_ERROR;
    out->words[1] = SIGN_ERROR;
    memcpy(&out->words[2], &code, sizeof(code));
}
static void release_result(ComputeResult *out, unsigned count) {
    for (unsigned i = 0; i < count; ++i) if (out->values[i].cap && out->values[i].cap != RUST_ERROR) free_vec(out->values[i].cap, out->values[i].ptr);
}
static DWORD remaining(ULONGLONG deadline) {
    ULONGLONG now = GetTickCount64();
    return now >= deadline ? 0 : (DWORD)(deadline - now);
}
static int pipe_io(HANDLE pipe, void *buffer, DWORD size, DWORD *done, int write, ULONGLONG deadline) {
    OVERLAPPED ov = {0};
    ov.hEvent = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (!ov.hEvent) return 0;
    BOOL ok = write ? WriteFile(pipe, buffer, size, done, &ov) : ReadFile(pipe, buffer, size, done, &ov);
    DWORD error = ok ? ERROR_SUCCESS : GetLastError();
    if (!ok && error == ERROR_IO_PENDING) {
        DWORD wait = WaitForSingleObject(ov.hEvent, remaining(deadline));
        if (wait == WAIT_OBJECT_0) ok = GetOverlappedResult(pipe, &ov, done, FALSE);
        else {
            CancelIoEx(pipe, &ov);
            GetOverlappedResult(pipe, &ov, done, TRUE);
            ok = FALSE;
        }
    }
    CloseHandle(ov.hEvent);
    return ok && *done != 0;
}
static int local_request(const char *request, size_t size, char *reply, size_t *reply_size) {
    ULONGLONG deadline = GetTickCount64() + 5000;
    HANDLE pipe;
    for (;;) {
        pipe = CreateFileW(PIPE_NAME, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, NULL);
        if (pipe != INVALID_HANDLE_VALUE) break;
        DWORD error = GetLastError(), left = remaining(deadline);
        if (error != ERROR_PIPE_BUSY || !left || !WaitNamedPipeW(PIPE_NAME, left)) return 0;
    }
    /* The official server is a byte-mode JSON-line pipe. */
    size_t sent = 0, used = 0;
    int ok = 0;
    while (sent < size) {
        DWORD n = 0;
        if (!pipe_io(pipe, (void *)(request + sent), (DWORD)(size - sent), &n, 1, deadline)) goto done;
        sent += n;
    }
    while (used < MAX_REPLY) {
        DWORD n = 0;
        if (!pipe_io(pipe, reply + used, (DWORD)(MAX_REPLY - used), &n, 0, deadline)) goto done;
        char *newline = memchr(reply + used, '\n', n);
        used += n;
        if (newline) {
            /* No second frame or non-whitespace trailer is accepted. */
            for (char *p = newline + 1; p < reply + used; ++p) if (*p != '\r' && *p != '\n' && *p != ' ' && *p != '\t') goto done;
            *reply_size = (size_t)(newline - reply);
            ok = 1;
            break;
        }
    }
done:
    CloseHandle(pipe);
    return ok;
}
static char *make_request(const ComputeArgs *a, size_t *size) {
    if (!a || !a->cmd || !a->cmd_len || a->cmd_len > 512 || !a->body || a->body_len > MAX_HEX) return NULL;
    Slice body = {a->body, (size_t)a->body_len};
    if (!hex_valid(body)) return NULL;
    size_t capacity = (size_t)(a->cmd_len * 2 + a->body_len + 96);
    char *request = malloc(capacity);
    if (!request) return NULL;
    char *p = request;
    memcpy(p, "{\"cmd\":\"", 8); p += 8;
    for (size_t i = 0; i < a->cmd_len; ++i) {
        unsigned char c = (unsigned char)a->cmd[i];
        if (c < 32 || c > 126) { free(request); return NULL; }
        if (c == '"' || c == '\\') *p++ = '\\';
        *p++ = (char)c;
    }
    memcpy(p, "\",\"body\":\"", 10); p += 10;
    memcpy(p, a->body, (size_t)a->body_len); p += a->body_len;
    int n = snprintf(p, capacity - (size_t)(p - request), "\",\"seq\":%lu}\n", (unsigned long)a->seq);
    if (n < 0 || (size_t)n >= capacity - (size_t)(p - request)) { free(request); return NULL; }
    *size = (size_t)(p - request) + (size_t)n;
    return request;
}
__declspec(dllexport) ComputeResult *PMHQLocalSignCompute(ComputeResult *out, void *unused, const ComputeArgs *args) {
    (void)unused;
    result_error(out, -9101);
    size_t request_size = 0, reply_size = 0;
    char *request = make_request(args, &request_size);
    if (!request) return out;
    char *data = malloc(MAX_REPLY);
    if (!data) { free(request); result_error(out, -9102); return out; }
    if (!local_request(request, request_size, data, &reply_size)) {
        result_error(out, -9103);
    } else {
        SignReply reply;
        if (!parse_sign_reply(data, reply_size, &reply)) result_error(out, -9104);
        else if (reply.code) result_error(out, reply.code);
        else {
            /* v9.0.6 remote-compute success ABI: sign, token, extra, each a
             * Rust Vec {capacity, pointer, length}; not JSON field order. */
            Slice values[3] = {reply.sign, reply.token, reply.extra};
            memset(out, 0, sizeof(*out));
            for (unsigned i = 0; i < 3; ++i) {
                decode_hex(&out->values[i], values[i].data, values[i].size);
                if (out->values[i].cap == RUST_ERROR) {
                    release_result(out, i);
                    result_error(out, -9105);
                    break;
                }
            }
        }
    }
    SecureZeroMemory(request, request_size);
    SecureZeroMemory(data, MAX_REPLY);
    free(request);
    free(data);
    return out;
}

/* Prepare ABI helpers only. The external patcher verifies and writes the code
 * while all target threads are suspended; the packer's in-process memory API
 * wrappers can reject VirtualProtect from an injected module. */
__declspec(dllexport) DWORD WINAPI PMHQLocalSignPrepare(void *unused) {
    (void)unused;
    if (installed) return 0;
    IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)image;
    IMAGE_NT_HEADERS64 *nt = (IMAGE_NT_HEADERS64 *)(image + dos->e_lfanew);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || nt->Signature != IMAGE_NT_SIGNATURE || nt->FileHeader.Machine != IMAGE_FILE_MACHINE_AMD64 || nt->OptionalHeader.SizeOfImage != 29405184) return 10;
    if (memcmp(image + COMPUTE_RVA, compute_head, 32) || memcmp(image + HEX_RVA, hex_head, 32) || memcmp(image + FREE_RVA, free_head, 5)) return 11;
    decode_hex = (HexDecode)(image + HEX_RVA);
    free_vec = (VecFree)(image + FREE_RVA);
    InterlockedExchange(&installed, 1);
    return 0;
}

/* Isolated fixture only: invokes the patched official function and then its
 * own allocator's free path. A passing result does not validate real QQ. */
__declspec(dllexport) DWORD WINAPI PMHQLocalSignSelfTest(void *case_value) {
    if (!installed) return 20;
    unsigned scenario = (unsigned)(uintptr_t)case_value;
    const char *cmd = "UnitTest.LocalOnly", *body = "0001abff";
    ComputeArgs args = {0};
    args.cmd = cmd; args.cmd_len = strlen(cmd);
    args.body = scenario == 4 ? "invalid" : body;
    args.body_len = strlen(args.body);
    args.seq = 0xfedcba98U;
    ComputeResult result;
    ComputeFn target = (ComputeFn)(image + COMPUTE_RVA);
    target(&result, NULL, &args);
    if (scenario) {
        int32_t code = 0;
        memcpy(&code, &result.words[2], 4);
        int32_t expected = scenario == 1 ? -77 : scenario == 2 ? -9104 : scenario == 3 ? -9103 : scenario == 4 ? -9101 : scenario == 5 ? -9103 : -9999;
        if (result.words[0] != RUST_ERROR) { release_result(&result, 3); return 21; }
        return result.words[1] == SIGN_ERROR && code == expected ? 0 : 22;
    }
    if (result.words[0] == RUST_ERROR) return 23;
    const unsigned char sign[] = {0,0x11,0x22,0x33}, token[] = {0x44,0x55}, extra[] = {0x66,0x77,0x88};
    int valid = result.values[0].len == sizeof(sign) && result.values[1].len == sizeof(token) && result.values[2].len == sizeof(extra)
        && !memcmp(result.values[0].ptr, sign, sizeof(sign)) && !memcmp(result.values[1].ptr, token, sizeof(token)) && !memcmp(result.values[2].ptr, extra, sizeof(extra));
    release_result(&result, 3);
    return valid ? 0 : 24;
}
BOOL WINAPI DllMain(HINSTANCE self, DWORD reason, void *reserved) {
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        image = (unsigned char *)GetModuleHandleW(NULL);
        DisableThreadLibraryCalls(self);
    }
    return TRUE;
}
