#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <bcrypt.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>
#include <string.h>

#define COMPUTE_RVA 0x12e8d3
#define COMPUTE_END 0x132836
static const unsigned char official_sha256[32] = {0x27,0x3f,0x73,0x84,0xbe,0x59,0x03,0x7a,0xe1,0x71,0xe8,0x37,0x1b,0xb8,0xb4,0x8e,0x38,0x80,0xf3,0xc9,0xd5,0x59,0xa0,0x30,0x0e,0xcb,0xc0,0x13,0x80,0xb8,0xfd,0xc3};

static int verify_file(const wchar_t *path) {
    BCRYPT_ALG_HANDLE algorithm = NULL;
    BCRYPT_HASH_HANDLE hash = NULL;
    HANDLE file = INVALID_HANDLE_VALUE;
    unsigned char buffer[32768], digest[32];
    int ok = 0;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, NULL, 0) < 0) goto done;
    if (BCryptCreateHash(algorithm, &hash, NULL, 0, NULL, 0, 0) < 0) goto done;
    file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE) goto done;
    for (;;) {
        DWORD count = 0;
        if (!ReadFile(file, buffer, sizeof(buffer), &count, NULL)) goto done;
        if (!count) break;
        if (BCryptHashData(hash, buffer, count, 0) < 0) goto done;
    }
    if (BCryptFinishHash(hash, digest, sizeof(digest), 0) < 0) goto done;
    ok = !memcmp(digest, official_sha256, sizeof(digest));
done:
    if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
    if (hash) BCryptDestroyHash(hash);
    if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
    return ok;
}
static uintptr_t module_base(DWORD pid, const wchar_t *name) {
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
    if (snapshot == INVALID_HANDLE_VALUE) return 0;
    MODULEENTRY32W m = {.dwSize = sizeof(m)};
    uintptr_t address = 0;
    if (Module32FirstW(snapshot, &m)) do {
        if (!_wcsicmp(m.szModule, name)) { address = (uintptr_t)m.modBaseAddr; break; }
    } while (Module32NextW(snapshot, &m));
    CloseHandle(snapshot);
    return address;
}
static uintptr_t module_by_path(DWORD pid, const wchar_t *path) {
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
    if (snapshot == INVALID_HANDLE_VALUE) return 0;
    MODULEENTRY32W m = {.dwSize = sizeof(m)};
    uintptr_t address = 0;
    if (Module32FirstW(snapshot, &m)) do {
        if (!_wcsicmp(m.szExePath, path)) { address = (uintptr_t)m.modBaseAddr; break; }
    } while (Module32NextW(snapshot, &m));
    CloseHandle(snapshot);
    return address;
}
static uintptr_t remote_system_proc(DWORD pid, FARPROC proc) {
    HMODULE module = NULL;
    wchar_t path[32768];
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)proc, &module)) return 0;
    if (!GetModuleFileNameW(module, path, 32768)) return 0;
    const wchar_t *name = wcsrchr(path, L'\\');
    uintptr_t remote = module_base(pid, name ? name + 1 : path);
    return remote ? remote + (uintptr_t)proc - (uintptr_t)module : 0;
}
/* Return -1 for timeout; never free remote arguments while a timed-out call
 * could still be reading them. */
static int remote_call(HANDLE process, uintptr_t address, void *argument, DWORD *result) {
    HANDLE thread = CreateRemoteThread(process, NULL, 0, (LPTHREAD_START_ROUTINE)address, argument, 0, NULL);
    if (!thread) return 0;
    DWORD wait = WaitForSingleObject(thread, 15000);
    int ok = wait == WAIT_OBJECT_0 ? GetExitCodeThread(thread, result) != 0 : -1;
    CloseHandle(thread);
    return ok;
}
typedef struct { HANDLE handles[4096]; DWORD ids[4096]; size_t count; } FrozenThreads;
static void resume_threads(FrozenThreads *threads) {
    while (threads->count) {
        HANDLE thread = threads->handles[--threads->count];
        ResumeThread(thread);
        CloseHandle(thread);
    }
}
static int freeze_threads(DWORD pid, uintptr_t base, FrozenThreads *threads) {
    /* Rescan after freezing the first snapshot so threads created while that
     * snapshot was taken cannot remain running during the code write. */
    for (unsigned pass = 0; pass < 8; ++pass) {
        HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snapshot == INVALID_HANDLE_VALUE) break;
        THREADENTRY32 t = {.dwSize = sizeof(t)};
        int ok = Thread32First(snapshot, &t) != 0;
        size_t previous = threads->count;
        if (ok) do {
            if (t.th32OwnerProcessID != pid) continue;
            size_t i = 0;
            while (i < threads->count && threads->ids[i] != t.th32ThreadID) ++i;
            if (i != threads->count) continue;
            if (threads->count == 4096) { ok = 0; break; }
            HANDLE thread = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION, FALSE, t.th32ThreadID);
            if (!thread) { ok = 0; break; }
            if (SuspendThread(thread) == (DWORD)-1) { CloseHandle(thread); ok = 0; break; }
            threads->ids[threads->count] = t.th32ThreadID;
            threads->handles[threads->count++] = thread;
            CONTEXT context = {0};
            context.ContextFlags = CONTEXT_CONTROL;
            if (!GetThreadContext(thread, &context) || (context.Rip >= base + COMPUTE_RVA && context.Rip < base + COMPUTE_END)) { ok = 0; break; }
        } while (Thread32Next(snapshot, &t));
        CloseHandle(snapshot);
        if (!ok) break;
        if (threads->count && previous == threads->count) return 1;
    }
    resume_threads(threads);
    return 0;
}
#ifndef PMHQ_PATCHER_LIBRARY
int wmain(int argc, wchar_t **argv) {
    if (argc < 3 || argc > 4) {
        fputs("Usage: PMHQ-LocalSign-Patch.exe <PMHQ PID> <absolute local-sign.dll path> [--fixture=N]\nOnly official Windows x64 PMHQ v9.0.6 is accepted.\n", stderr);
        return 2;
    }
    wchar_t *pid_end = NULL;
    unsigned long parsed = wcstoul(argv[1], &pid_end, 10);
    if (!parsed || !pid_end || *pid_end || parsed > MAXDWORD) return 2;
    DWORD pid = (DWORD)parsed;
    int fixture = -1;
    if (argc == 4) {
        if (wcsncmp(argv[3], L"--fixture=", 10)) return 2;
        wchar_t *end = NULL;
        long value = wcstol(argv[3] + 10, &end, 10);
        if (!end || *end || value < 0 || value > 5) return 2;
        fixture = (int)value;
    }
    HANDLE process = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ, FALSE, pid);
    if (!process) { fprintf(stderr, "OpenProcess failed: %lu\n", GetLastError()); return 3; }
    wchar_t process_path[32768], dll_path[32768];
    DWORD path_size = 32768;
    int exit_code = 4;
    if (!QueryFullProcessImageNameW(process, 0, process_path, &path_size) || !verify_file(process_path)) {
        fputs("Rejected: target executable hash does not match official PMHQ v9.0.6 Windows x64.\n", stderr);
        goto done;
    }
    uintptr_t base = module_base(pid, L"pmhq-win-x64.exe");
    if (!base) { fputs("Rejected: expected PMHQ main module not found.\n", stderr); goto done; }
    DWORD dll_path_size = GetFullPathNameW(argv[2], 32768, dll_path, NULL);
    if (!dll_path_size || dll_path_size >= 32768 || GetFileAttributesW(dll_path) == INVALID_FILE_ATTRIBUTES) goto done;
    HMODULE local = LoadLibraryExW(dll_path, NULL, DONT_RESOLVE_DLL_REFERENCES);
    if (!local) goto done;
    FARPROC install = GetProcAddress(local, "PMHQLocalSignPrepare"), test = GetProcAddress(local, "PMHQLocalSignSelfTest"), compute = GetProcAddress(local, "PMHQLocalSignCompute");
    if (!install || !test || !compute) { FreeLibrary(local); goto done; }
    uintptr_t install_offset = (uintptr_t)install - (uintptr_t)local;
    uintptr_t test_offset = (uintptr_t)test - (uintptr_t)local;
    uintptr_t compute_offset = (uintptr_t)compute - (uintptr_t)local;
    FreeLibrary(local);
    SIZE_T bytes = (wcslen(dll_path) + 1) * sizeof(wchar_t), wrote = 0;
    void *remote_path = VirtualAllocEx(process, NULL, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote_path) goto done;
    if (!WriteProcessMemory(process, remote_path, dll_path, bytes, &wrote) || wrote != bytes) {
        VirtualFreeEx(process, remote_path, 0, MEM_RELEASE); goto done;
    }
    uintptr_t load_library = remote_system_proc(pid, GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW"));
    DWORD result = 0;
    int called = load_library ? remote_call(process, load_library, remote_path, &result) : 0;
    if (called != -1) VirtualFreeEx(process, remote_path, 0, MEM_RELEASE);
    if (called != 1) { fputs("Remote DLL load failed or timed out.\n", stderr); goto done; }
    uintptr_t remote_dll = module_by_path(pid, dll_path);
    if (!remote_dll) { fputs("Remote DLL not present.\n", stderr); goto done; }
    called = remote_call(process, remote_dll + install_offset, NULL, &result);
    if (called != 1 || result) {
        fprintf(stderr, "Patch installation failed: call=%d, status=%lu. Restart PMHQ before retrying after a timeout.\n", called, result);
        goto done;
    }
    FrozenThreads threads = {0};
    if (!freeze_threads(pid, base, &threads)) { fputs("Target busy or thread inspection failed; no code was patched. Retry when idle.\n", stderr); goto done; }
    unsigned char jump[14] = {0xff, 0x25, 0, 0, 0, 0}, before[14];
    uintptr_t hook = remote_dll + compute_offset;
    memcpy(jump + 6, &hook, 8);
    DWORD old_protection = 0, ignored = 0;
    SIZE_T original_size = 0;
    BOOL changed = FALSE, patched = FALSE, restored = TRUE;
    if (ReadProcessMemory(process, (void *)(base + COMPUTE_RVA), before, sizeof(before), &original_size) && original_size == sizeof(before)) {
        if (!memcmp(before, jump, sizeof(jump))) patched = TRUE;
        else if (!memcmp(before, "\x55\x41\x57\x41\x56\x41\x55\x41\x54\x56\x57\x53\x48\x81", 14)) {
            changed = VirtualProtectEx(process, (void *)(base + COMPUTE_RVA), sizeof(jump), PAGE_EXECUTE_READWRITE, &old_protection);
            if (changed) {
                patched = WriteProcessMemory(process, (void *)(base + COMPUTE_RVA), jump, sizeof(jump), &wrote) && wrote == sizeof(jump);
                if (!patched) WriteProcessMemory(process, (void *)(base + COMPUTE_RVA), before, sizeof(before), &wrote);
                FlushInstructionCache(process, (void *)(base + COMPUTE_RVA), sizeof(jump));
                restored = VirtualProtectEx(process, (void *)(base + COMPUTE_RVA), sizeof(jump), old_protection, &ignored);
            }
        }
    }
    resume_threads(&threads);
    if (!patched || !restored) { fprintf(stderr, "Code patch failed: patched=%d protection_restored=%d error=%lu\n", patched, restored, GetLastError()); goto done; }
    unsigned char branch[14]; SIZE_T got = 0;
    uintptr_t destination = 0;
    if (!ReadProcessMemory(process, (void *)(base + COMPUTE_RVA), branch, sizeof(branch), &got) || got != sizeof(branch)) goto done;
    memcpy(&destination, branch + 6, 8);
    if (memcmp(branch, "\xff\x25\x00\x00\x00\x00", 6) || destination != hook) goto done;
    puts("Patched PMHQ v9.0.6: compute -> local QQ pmhq_sign pipe; no remote signing fallback.");
    puts("Official executable on disk is unchanged. Startup manager behavior is unchanged. Real QQ signature validation is not performed by this patcher.");
    if (fixture >= 0) {
        called = remote_call(process, remote_dll + test_offset, (void *)(uintptr_t)fixture, &result);
        printf("Isolated ABI fixture %d: call=%d status=%lu\n", fixture, called, result);
        if (called != 1 || result) { exit_code = 5; goto done; }
    }
    exit_code = 0;
done:
    CloseHandle(process);
    return exit_code;
}
#endif
