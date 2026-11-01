/*
 * Windows 7 does not have GetSystemTimePreciseAsFileTime (Windows 8+ only).
 * Magick.Native-Q8-x86.dll hard-binds KERNEL32!GetSystemTimePreciseAsFileTime in
 * its import table, so loading it on Windows 7 fails with "unable to locate
 * entry point ... KERNEL32.dll" and the program cannot start.
 *
 * This DLL exports the same function name (falling back to GetSystemTimeAsFileTime
 * internally). At build time Magick.Native-Q8-x86.dll's import module name is
 * redirected from KERNEL32.dll to Win7Compat.dll, so the import resolves on
 * Windows 7 and the program starts. Precision falls back to system time; this is
 * only used for non-critical cases such as unique file names.
 *
 * Deliberately does not include windows.h: it keeps the build hermetic (no CRT,
 * no default library pragmas) and avoids conflicting dllimport declarations.
 */

typedef struct _FILETIME {
    unsigned long dwLowDateTime;
    unsigned long dwHighDateTime;
} FILETIME, *LPFILETIME;

__declspec(dllimport) void __stdcall GetSystemTimeAsFileTime(LPFILETIME lpft);

#pragma comment(linker, "/EXPORT:GetSystemTimePreciseAsFileTime=_GetSystemTimePreciseAsFileTime@4")

void __stdcall GetSystemTimePreciseAsFileTime(LPFILETIME lpft)
{
    GetSystemTimeAsFileTime(lpft);
}

typedef int BOOL;

BOOL __stdcall DllMain(void *hinstDLL, unsigned long fdwReason, void *lpvReserved)
{
    (void)hinstDLL;
    (void)fdwReason;
    (void)lpvReserved;
    return 1;
}
