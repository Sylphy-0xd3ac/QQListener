#include <windows.h>

static HANDLE probe_event = NULL;

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved) {
    (void)instance;
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        probe_event = CreateEventW(
            NULL,
            TRUE,
            TRUE,
            L"Local\\QQListenerManualMapProbe"
        );
        return probe_event != NULL;
    }
    if (reason == DLL_PROCESS_DETACH && probe_event != NULL) {
        CloseHandle(probe_event);
        probe_event = NULL;
    }
    return TRUE;
}
