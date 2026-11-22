/*
 * bsdbg-host - out-of-process host for ButterscotchDbg.
 *
 * Loads the bsdbg.dll matching this executable's architecture, owns the game
 * session, and exposes the C API to UndertaleModTool over a named pipe.  One
 * host process owns one session; the parent creates the pipe server and starts
 * this executable with the pipe name.
 *
 * Wire format (little endian, 16-byte header):
 *
 *   uint32 payloadLength
 *   uint16 protocolVersion (1)
 *   uint16 messageType    (0 hello, 1 request, 2 response, 3 event)
 *   uint32 requestId
 *   uint16 opcode
 *   uint16 status         (requests: 0; responses: 0 ok, 1 error)
 *
 * Payloads are UTF-8.  Control commands use compact binary payloads so the
 * host never needs a JSON parser; inspection results and events pass through
 * the JSON produced by bsdbg.
 */

#define WIN32_LEAN_AND_MEAN
#define _WIN32_WINNT 0x0601
#define _CRT_SECURE_NO_WARNINGS
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <dbghelp.h>
#pragma comment(lib, "dbghelp.lib")
#pragma comment(lib, "ws2_32.lib")
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

#include "bsdbg.h"

#define HOST_PROTOCOL 1
#define MAX_FRAME (64u * 1024u * 1024u)
#define MAX_COMMAND_FRAME (1u * 1024u * 1024u)
#define MAX_BREAKPOINTS 1000000u
#define MAX_LOG_MESSAGE (16u * 1024u)
#define MAX_ARGS 64

enum {
    MSG_HELLO = 0,
    MSG_REQUEST = 1,
    MSG_RESPONSE = 2,
    MSG_EVENT = 3
};

enum {
    OP_PING = 0,
    OP_START = 1,
    OP_SHUTDOWN = 2,
    OP_PAUSE = 3,
    OP_RESUME = 4,
    OP_STEP_INTO = 5,
    OP_STEP_OVER = 6,
    OP_STEP_OUT = 7,
    OP_STEP_FRAME = 8,
    OP_SET_BREAKPOINTS = 9,
    OP_CLEAR_BREAKPOINTS = 10,
    OP_GET_STATE = 11,
    OP_GET_CALL_STACK = 12,
    OP_GET_INSTANCES = 13,
    OP_GET_GLOBALS = 14,
    OP_GET_FRAME_COUNT = 15,
    OP_SET_INSTANCE_VARIABLE = 16,
    OP_SET_GLOBAL_VARIABLE = 17,
    OP_SET_FRAME_VARIABLE = 18,
    OP_APPLY_CODE_PATCH = 19,
    OP_INJECT = 20,
    OP_PUBLISH_SURFACE = 21,
    OP_WITHDRAW_SURFACE = 22
};

enum {
    HOST_EV_STATE = 100,
    HOST_EV_BREAK = 101,
    HOST_EV_EXIT = 102,
    HOST_EV_ERROR = 103,
    HOST_EV_RELOADED = 104,
    HOST_EV_LOG = 105
};

#pragma pack(push, 1)
typedef struct FrameHeader {
    uint32_t length;
    uint16_t version;
    uint16_t type;
    uint32_t id;
    uint16_t opcode;
    uint16_t status;
} FrameHeader;
#pragma pack(pop)

typedef struct OutFrame {
    struct OutFrame* next;
    uint16_t type;
    uint16_t opcode;
    uint16_t status;
    uint32_t id;
    uint32_t length;
    char* payload;
} OutFrame;

/* Function table resolved from bsdbg.dll.  __cdecl is the C API default. */
#define BSDBG_CALL __cdecl
typedef BsdbgSession* (BSDBG_CALL* PFN_Create)(const BsdbgConfig*);
typedef void (BSDBG_CALL* PFN_Destroy)(BsdbgSession*);
typedef int32_t (BSDBG_CALL* PFN_Start)(BsdbgSession*);
typedef int32_t (BSDBG_CALL* PFN_IsRunning)(BsdbgSession*);
typedef int32_t (BSDBG_CALL* PFN_IsPaused)(BsdbgSession*);
typedef int32_t (BSDBG_CALL* PFN_Pause)(BsdbgSession*);
typedef int32_t (BSDBG_CALL* PFN_Resume)(BsdbgSession*);
typedef int32_t (BSDBG_CALL* PFN_StepFrame)(BsdbgSession*);
typedef int32_t (BSDBG_CALL* PFN_StepInto)(BsdbgSession*);
typedef int32_t (BSDBG_CALL* PFN_StepOver)(BsdbgSession*);
typedef int32_t (BSDBG_CALL* PFN_StepOut)(BsdbgSession*);
typedef int32_t (BSDBG_CALL* PFN_GetFrameCount)(BsdbgSession*);
typedef int32_t (BSDBG_CALL* PFN_SetBreakpoints)(BsdbgSession*, const BsdbgBreakpoint*, int32_t);
typedef int32_t (BSDBG_CALL* PFN_ClearBreakpoints)(BsdbgSession*);
typedef char* (BSDBG_CALL* PFN_GetJson)(BsdbgSession*);
typedef int32_t (BSDBG_CALL* PFN_SetInstanceVariable)(BsdbgSession*, int32_t, const char*, const char*, char**);
typedef int32_t (BSDBG_CALL* PFN_SetGlobalVariable)(BsdbgSession*, const char*, const char*, char**);
typedef int32_t (BSDBG_CALL* PFN_SetFrameVariable)(BsdbgSession*, int32_t, int32_t, int32_t, const char*, char**);
typedef int32_t (BSDBG_CALL* PFN_ApplyCodePatch)(BsdbgSession*, const char*, char**);
typedef void (BSDBG_CALL* PFN_InjectKey)(int32_t, int32_t);
typedef void (BSDBG_CALL* PFN_InjectKeyText)(uint32_t);
typedef void (BSDBG_CALL* PFN_InjectMouseButton)(int32_t, int32_t);
typedef void (BSDBG_CALL* PFN_InjectMousePos)(double, double);
typedef void (BSDBG_CALL* PFN_InjectMouseWheel)(double);
typedef void (BSDBG_CALL* PFN_InjectGamepadButton)(int32_t, int32_t, float);
typedef void (BSDBG_CALL* PFN_InjectGamepadAxis)(int32_t, float);
typedef void (BSDBG_CALL* PFN_FreeString)(char*);
typedef int32_t (BSDBG_CALL* PFN_AbiVersion)(void);
typedef void (BSDBG_CALL* PFN_SetEventCallback)(BsdbgSession*, BsdbgEventCallback, void*);
typedef void (BSDBG_CALL* PFN_SetLogCallback)(BsdbgSession*, BsdbgLogCallback, void*);

typedef struct BsdbgApi {
    HMODULE module;
    PFN_Create Create;
    PFN_Destroy Destroy;
    PFN_Start Start;
    PFN_IsRunning IsRunning;
    PFN_IsPaused IsPaused;
    PFN_Pause Pause;
    PFN_Resume Resume;
    PFN_StepFrame StepFrame;
    PFN_StepInto StepInto;
    PFN_StepOver StepOver;
    PFN_StepOut StepOut;
    PFN_GetFrameCount GetFrameCount;
    PFN_SetBreakpoints SetBreakpoints;
    PFN_ClearBreakpoints ClearBreakpoints;
    PFN_GetJson GetStateJson;
    PFN_GetJson GetCallStackJson;
    PFN_GetJson GetInstancesJson;
    PFN_GetJson GetGlobalsJson;
    PFN_SetInstanceVariable SetInstanceVariable;
    PFN_SetGlobalVariable SetGlobalVariable;
    PFN_SetFrameVariable SetFrameVariable;
    PFN_ApplyCodePatch ApplyCodePatch;
    PFN_InjectKey InjectKey;
    PFN_InjectKeyText InjectKeyText;
    PFN_InjectMouseButton InjectMouseButton;
    PFN_InjectMousePos InjectMousePos;
    PFN_InjectMouseWheel InjectMouseWheel;
    PFN_InjectGamepadButton InjectGamepadButton;
    PFN_InjectGamepadAxis InjectGamepadAxis;
    PFN_FreeString FreeString;
    PFN_AbiVersion AbiVersion;
    PFN_SetEventCallback SetEventCallback;
    PFN_SetLogCallback SetLogCallback;
} BsdbgApi;

typedef struct Host {
    SOCKET socket;
    HANDLE writerThread;
    CRITICAL_SECTION queueLock;
    CONDITION_VARIABLE queueCv;
    OutFrame* queueHead;
    OutFrame* queueTail;
    int queueCount;
    volatile LONG pipeBroken;
    volatile LONG writerDone;
    volatile LONG writerExited;
    volatile LONG inFlight;
    volatile LONG shuttingDown;

    BsdbgApi api;
    BsdbgSession* session;
    int started;
    intptr_t embedWindow;
    int32_t embedWindowKind;

    /* Configuration, owned until Bsdbg_Create copies it. */
    char* dataPath;
    char* workingDirectory;
    char* saveFolder;
    char** gameArgs;
    int gameArgCount;
    int renderer;
    int windowWidth;
    int windowHeight;
    int headless;
    int fullscreen;
    int stopOnEntry;
    double speed;
    uint32_t seed;

    wchar_t dumpDir[MAX_PATH * 2];
} Host;

static Host g_host;
static wchar_t g_dumpDir[MAX_PATH * 2];
static char g_arch[16];

/* ===[ Small helpers ]=== */

static void host_fail(const char* message) {
    fwprintf(stderr, L"bsdbg-host: %hs\n", message);
    fflush(stderr);
}

static void host_fail_w(const wchar_t* message) {
    fwprintf(stderr, L"bsdbg-host: %ls\n", message);
    fflush(stderr);
}

static char* utf16_to_utf8(const wchar_t* text) {
    int length;
    char* result;

    if (text == NULL)
        return NULL;
    length = WideCharToMultiByte(CP_UTF8, 0, text, -1, NULL, 0, NULL, NULL);
    if (length <= 0)
        return NULL;
    result = (char*) malloc((size_t) length);
    if (result == NULL)
        return NULL;
    if (WideCharToMultiByte(CP_UTF8, 0, text, -1, result, length, NULL, NULL) <= 0) {
        free(result);
        return NULL;
    }
    return result;
}

static char* utf8_dup(const char* text) {
    size_t length;
    char* result;

    if (text == NULL)
        return NULL;
    length = strlen(text);
    result = (char*) malloc(length + 1);
    if (result == NULL)
        return NULL;
    memcpy(result, text, length + 1);
    return result;
}

static void ensure_directory(const wchar_t* path) {
    wchar_t buffer[MAX_PATH * 2];
    size_t length;

    if (path == NULL || path[0] == L'\0')
        return;
    length = wcslen(path);
    if (length >= sizeof(buffer) / sizeof(buffer[0]))
        return;
    wcscpy(buffer, path);

    for (wchar_t* cursor = buffer + 3; *cursor != L'\0'; cursor++) {
        if (*cursor != L'\\' && *cursor != L'/')
            continue;
        wchar_t saved = *cursor;
        *cursor = L'\0';
        CreateDirectoryW(buffer, NULL);
        *cursor = saved;
    }
    CreateDirectoryW(buffer, NULL);
}

static void init_dump_directory(void) {
    wchar_t defaultDir[MAX_PATH * 2];
    DWORD length;

    if (g_host.dumpDir[0] != L'\0') {
        wcscpy(g_dumpDir, g_host.dumpDir);
    } else {
        length = GetEnvironmentVariableW(L"LOCALAPPDATA", defaultDir,
                                         (DWORD) (sizeof(defaultDir) / sizeof(defaultDir[0])));
        if (length == 0 || length >= sizeof(defaultDir) / sizeof(defaultDir[0])) {
            wcscpy(defaultDir, L".");
        } else {
            wcscat(defaultDir, L"\\UndertaleModTool\\crashes");
        }
        wcscpy(g_dumpDir, defaultDir);
    }
    ensure_directory(g_dumpDir);
}

static void write_crash_summary(const wchar_t* dumpPath, EXCEPTION_POINTERS* info) {
    wchar_t textPath[MAX_PATH * 2];
    wchar_t summary[1024];
    HANDLE file;
    DWORD written;
    DWORD code = 0;
    void* address = NULL;

    if (info != NULL && info->ExceptionRecord != NULL) {
        code = info->ExceptionRecord->ExceptionCode;
        address = info->ExceptionRecord->ExceptionAddress;
    }
    _snwprintf(textPath, sizeof(textPath) / sizeof(textPath[0]), L"%ls.txt", dumpPath);
    _snwprintf(summary, sizeof(summary) / sizeof(summary[0]),
               L"ButterscotchDbg host crash\r\nExit exception: 0x%08lX\r\nAddress: %p\r\n"
               L"Architecture: %hs\r\n",
               (unsigned long) code, address, g_arch);

    file = CreateFileW(textPath, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS,
                       FILE_ATTRIBUTE_NORMAL, NULL);
    if (file != INVALID_HANDLE_VALUE) {
        WriteFile(file, summary, (DWORD) (wcslen(summary) * sizeof(wchar_t)), &written, NULL);
        CloseHandle(file);
    }
}

static LONG WINAPI crash_filter(EXCEPTION_POINTERS* info) {
    wchar_t dumpPath[MAX_PATH * 2];
    SYSTEMTIME time;
    HANDLE file;
    MINIDUMP_EXCEPTION_INFORMATION dumpInfo;
    DWORD pid = GetCurrentProcessId();
    ULONGLONG tick = GetTickCount64();

    GetLocalTime(&time);
    _snwprintf(dumpPath, sizeof(dumpPath) / sizeof(dumpPath[0]),
               L"%ls\\bsdbg-host-%hs-%lu-%04u%02u%02u-%02u%02u%02u-%llu.dmp",
               g_dumpDir[0] != L'\0' ? g_dumpDir : L".",
               g_arch, (unsigned long) pid,
               time.wYear, time.wMonth, time.wDay,
               time.wHour, time.wMinute, time.wSecond,
               (unsigned long long) tick);

    file = CreateFileW(dumpPath, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS,
                       FILE_ATTRIBUTE_NORMAL, NULL);
    if (file != INVALID_HANDLE_VALUE) {
        memset(&dumpInfo, 0, sizeof(dumpInfo));
        dumpInfo.ThreadId = GetCurrentThreadId();
        dumpInfo.ExceptionPointers = info;
        dumpInfo.ClientPointers = FALSE;
        MiniDumpWriteDump(GetCurrentProcess(), pid, file, MiniDumpNormal, &dumpInfo, NULL, NULL);
        CloseHandle(file);
    }
    write_crash_summary(dumpPath, info);

    /* Let Windows terminate the process; UTMT sees the pipe close and the
     * exit code, while its own address space remains untouched. */
    return EXCEPTION_EXECUTE_HANDLER;
}

/* ===[ Pipe output queue ]=== */

static void queue_push(uint16_t type, uint16_t opcode, uint16_t status, uint32_t id,
                       const char* payload, uint32_t length) {
    OutFrame* frame;
    int critical;

    if (InterlockedCompareExchange(&g_host.pipeBroken, 0, 0) != 0)
        return;
    if (length > MAX_FRAME)
        return;

    critical = type == MSG_RESPONSE ||
               (type == MSG_EVENT && (opcode == HOST_EV_BREAK || opcode == HOST_EV_EXIT ||
                                      opcode == HOST_EV_ERROR || opcode == HOST_EV_RELOADED));

    frame = (OutFrame*) calloc(1, sizeof(OutFrame));
    if (frame == NULL)
        return;
    frame->type = type;
    frame->opcode = opcode;
    frame->status = status;
    frame->id = id;
    frame->length = length;
    if (length > 0) {
        frame->payload = (char*) malloc(length);
        if (frame->payload == NULL) {
            free(frame);
            return;
        }
        memcpy(frame->payload, payload, length);
    }

    EnterCriticalSection(&g_host.queueLock);
    if (!critical && g_host.queueCount >= 1024) {
        LeaveCriticalSection(&g_host.queueLock);
        free(frame->payload);
        free(frame);
        return;
    }
    if (g_host.queueTail != NULL)
        g_host.queueTail->next = frame;
    else
        g_host.queueHead = frame;
    g_host.queueTail = frame;
    g_host.queueCount++;
    WakeAllConditionVariable(&g_host.queueCv);
    LeaveCriticalSection(&g_host.queueLock);
}

static void queue_drain(void) {
    OutFrame* frame;

    EnterCriticalSection(&g_host.queueLock);
    frame = g_host.queueHead;
    g_host.queueHead = NULL;
    g_host.queueTail = NULL;
    g_host.queueCount = 0;
    LeaveCriticalSection(&g_host.queueLock);

    while (frame != NULL) {
        OutFrame* next = frame->next;
        free(frame->payload);
        free(frame);
        frame = next;
    }
}

static BOOL write_all(SOCKET socket, const void* data, DWORD length) {
    const char* cursor = (const char*) data;
    while (length > 0) {
        int sent = send(socket, cursor, (int) length, 0);
        if (sent <= 0)
            return FALSE;
        cursor += sent;
        length -= (DWORD) sent;
    }
    return TRUE;
}

static DWORD WINAPI writer_thread(LPVOID parameter) {
    (void) parameter;
    for (;;) {
        OutFrame* frame;
        FrameHeader header;
        BOOL ok;

        EnterCriticalSection(&g_host.queueLock);
        while (g_host.queueHead == NULL && !g_host.writerDone)
            SleepConditionVariableCS(&g_host.queueCv, &g_host.queueLock, INFINITE);
        if (g_host.queueHead == NULL && g_host.writerDone) {
            LeaveCriticalSection(&g_host.queueLock);
            break;
        }
        frame = g_host.queueHead;
        g_host.queueHead = frame->next;
        if (g_host.queueHead == NULL)
            g_host.queueTail = NULL;
        g_host.queueCount--;
        LeaveCriticalSection(&g_host.queueLock);

        memset(&header, 0, sizeof(header));
        header.length = frame->length;
        header.version = HOST_PROTOCOL;
        header.type = frame->type;
        header.id = frame->id;
        header.opcode = frame->opcode;
        header.status = frame->status;

        InterlockedIncrement(&g_host.inFlight);
        ok = write_all(g_host.socket, &header, sizeof(header));
        if (ok && frame->length > 0)
            ok = write_all(g_host.socket, frame->payload, frame->length);
        InterlockedDecrement(&g_host.inFlight);

        free(frame->payload);
        free(frame);

        if (!ok) {
            InterlockedExchange(&g_host.pipeBroken, 1);
            queue_drain();
            shutdown(g_host.socket, SD_BOTH);
            break;
        }
    }
    InterlockedExchange(&g_host.writerExited, 1);
    return 0;
}

static void queue_flush(DWORD timeoutMs) {
    ULONGLONG deadline = GetTickCount64() + timeoutMs;
    for (;;) {
        int count;
        EnterCriticalSection(&g_host.queueLock);
        count = g_host.queueCount;
        LeaveCriticalSection(&g_host.queueLock);
        if ((count == 0 && InterlockedCompareExchange(&g_host.inFlight, 0, 0) == 0) ||
            GetTickCount64() >= deadline)
            return;
        Sleep(5);
    }
}

static BOOL read_all(SOCKET socket, void* data, DWORD length) {
    char* cursor = (char*) data;
    while (length > 0) {
        int received = recv(socket, cursor, (int) length, 0);
        if (received <= 0)
            return FALSE;
        cursor += received;
        length -= (DWORD) received;
    }
    return TRUE;
}

static BOOL read_frame(FrameHeader* header, char** payload) {
    if (!read_all(g_host.socket, header, sizeof(*header)))
        return FALSE;
    if (header->version != HOST_PROTOCOL || header->length > MAX_FRAME)
        return FALSE;
    *payload = NULL;
    if (header->length > 0) {
        *payload = (char*) malloc((size_t) header->length + 1);
        if (*payload == NULL)
            return FALSE;
        if (!read_all(g_host.socket, *payload, header->length)) {
            free(*payload);
            *payload = NULL;
            return FALSE;
        }
        (*payload)[header->length] = '\0';
    }
    return TRUE;
}

/* ===[ bsdbg callbacks ]=== */

static void bsdbg_event_cb(void* user, int32_t eventType, const char* json) {
    uint16_t opcode;

    (void) user;
    if (json == NULL)
        return;
    if (eventType < 0 || eventType > 4)
        return;
    opcode = (uint16_t) (HOST_EV_STATE + eventType);
    queue_push(MSG_EVENT, opcode, 0, 0, json, (uint32_t) strlen(json));
}

static void bsdbg_log_cb(void* user, int32_t level, const char* message) {
    size_t length;
    char* payload;

    (void) user;
    if (message == NULL)
        return;
    length = strlen(message);
    if (length > MAX_LOG_MESSAGE)
        length = MAX_LOG_MESSAGE;
    payload = (char*) malloc(4 + length);
    if (payload == NULL)
        return;
    memcpy(payload, &level, 4);
    memcpy(payload + 4, message, length);
    queue_push(MSG_EVENT, HOST_EV_LOG, 0, 0, payload, (uint32_t) (4 + length));
    free(payload);
}

/* ===[ bsdbg loading ]=== */

static void* resolve(HMODULE module, const char* name) {
    void* address = (void*) GetProcAddress(module, name);
    if (address == NULL)
        fwprintf(stderr, L"bsdbg-host: missing export %hs\n", name);
    return address;
}

static BOOL load_api(BsdbgApi* api, const wchar_t* explicitPath) {
    wchar_t modulePath[MAX_PATH * 2];
    wchar_t dllPath[MAX_PATH * 2];
    HMODULE module;

    memset(api, 0, sizeof(*api));
    if (explicitPath != NULL && explicitPath[0] != L'\0') {
        wcscpy(dllPath, explicitPath);
    } else {
        DWORD length = GetModuleFileNameW(NULL, modulePath,
                                          (DWORD) (sizeof(modulePath) / sizeof(modulePath[0])));
        if (length == 0 || length >= sizeof(modulePath) / sizeof(modulePath[0])) {
            host_fail("cannot resolve the host executable path");
            return FALSE;
        }
        wchar_t* slash = wcsrchr(modulePath, L'\\');
        if (slash == NULL) {
            host_fail("cannot resolve the host directory");
            return FALSE;
        }
        *(slash + 1) = L'\0';
        _snwprintf(dllPath, sizeof(dllPath) / sizeof(dllPath[0]), L"%lsbsdbg.dll", modulePath);
    }

    module = LoadLibraryExW(dllPath, NULL,
                            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (module == NULL)
        module = LoadLibraryW(dllPath);
    if (module == NULL) {
        fwprintf(stderr, L"bsdbg-host: cannot load %ls (error %lu)\n", dllPath, GetLastError());
        return FALSE;
    }

    api->module = module;
    api->Create = (PFN_Create) resolve(module, "Bsdbg_Create");
    api->Destroy = (PFN_Destroy) resolve(module, "Bsdbg_Destroy");
    api->Start = (PFN_Start) resolve(module, "Bsdbg_Start");
    api->IsRunning = (PFN_IsRunning) resolve(module, "Bsdbg_IsRunning");
    api->IsPaused = (PFN_IsPaused) resolve(module, "Bsdbg_IsPaused");
    api->Pause = (PFN_Pause) resolve(module, "Bsdbg_Pause");
    api->Resume = (PFN_Resume) resolve(module, "Bsdbg_Resume");
    api->StepFrame = (PFN_StepFrame) resolve(module, "Bsdbg_StepFrame");
    api->StepInto = (PFN_StepInto) resolve(module, "Bsdbg_StepInto");
    api->StepOver = (PFN_StepOver) resolve(module, "Bsdbg_StepOver");
    api->StepOut = (PFN_StepOut) resolve(module, "Bsdbg_StepOut");
    api->GetFrameCount = (PFN_GetFrameCount) resolve(module, "Bsdbg_GetFrameCount");
    api->SetBreakpoints = (PFN_SetBreakpoints) resolve(module, "Bsdbg_SetBreakpoints");
    api->ClearBreakpoints = (PFN_ClearBreakpoints) resolve(module, "Bsdbg_ClearBreakpoints");
    api->GetStateJson = (PFN_GetJson) resolve(module, "Bsdbg_GetStateJson");
    api->GetCallStackJson = (PFN_GetJson) resolve(module, "Bsdbg_GetCallStackJson");
    api->GetInstancesJson = (PFN_GetJson) resolve(module, "Bsdbg_GetInstancesJson");
    api->GetGlobalsJson = (PFN_GetJson) resolve(module, "Bsdbg_GetGlobalsJson");
    api->SetInstanceVariable = (PFN_SetInstanceVariable) resolve(module, "Bsdbg_SetInstanceVariable");
    api->SetGlobalVariable = (PFN_SetGlobalVariable) resolve(module, "Bsdbg_SetGlobalVariable");
    api->SetFrameVariable = (PFN_SetFrameVariable) resolve(module, "Bsdbg_SetFrameVariable");
    api->ApplyCodePatch = (PFN_ApplyCodePatch) resolve(module, "Bsdbg_ApplyCodePatch");
    api->InjectKey = (PFN_InjectKey) resolve(module, "Bsdbg_InjectKey");
    api->InjectKeyText = (PFN_InjectKeyText) resolve(module, "Bsdbg_InjectKeyText");
    api->InjectMouseButton = (PFN_InjectMouseButton) resolve(module, "Bsdbg_InjectMouseButton");
    api->InjectMousePos = (PFN_InjectMousePos) resolve(module, "Bsdbg_InjectMousePos");
    api->InjectMouseWheel = (PFN_InjectMouseWheel) resolve(module, "Bsdbg_InjectMouseWheel");
    api->InjectGamepadButton = (PFN_InjectGamepadButton) resolve(module, "Bsdbg_InjectGamepadButton");
    api->InjectGamepadAxis = (PFN_InjectGamepadAxis) resolve(module, "Bsdbg_InjectGamepadAxis");
    api->FreeString = (PFN_FreeString) resolve(module, "Bsdbg_FreeString");
    api->AbiVersion = (PFN_AbiVersion) resolve(module, "Bsdbg_AbiVersion");
    api->SetEventCallback = (PFN_SetEventCallback) resolve(module, "Bsdbg_SetEventCallback");
    api->SetLogCallback = (PFN_SetLogCallback) resolve(module, "Bsdbg_SetLogCallback");

    if (api->Create == NULL || api->Destroy == NULL || api->Start == NULL ||
        api->GetStateJson == NULL || api->FreeString == NULL || api->AbiVersion == NULL ||
        api->SetEventCallback == NULL || api->SetLogCallback == NULL) {
        FreeLibrary(module);
        memset(api, 0, sizeof(*api));
        return FALSE;
    }
    return TRUE;
}

/* ===[ Session lifecycle ]=== */

static void stop_session(void) {
    if (g_host.session != NULL) {
        g_host.api.Destroy(g_host.session);
        g_host.session = NULL;
    }
    g_host.started = 0;
}

static BOOL start_session(char* error, size_t errorSize) {
    BsdbgConfig config;

    if (g_host.session != NULL) {
        snprintf(error, errorSize, "a session is already running");
        return FALSE;
    }

    memset(&config, 0, sizeof(config));
    config.structSize = (uint32_t) sizeof(config);
    config.dataWinPath = g_host.dataPath;
    config.workingDirectory = g_host.workingDirectory;
    config.saveFolder = g_host.saveFolder;
    config.windowWidth = g_host.windowWidth;
    config.windowHeight = g_host.windowHeight;
    config.renderer = g_host.renderer;
    config.headless = g_host.headless;
    config.fullscreen = g_host.fullscreen;
    config.speed = g_host.speed;
    config.seed = g_host.seed;
    config.gameArgs = (const char* const*) g_host.gameArgs;
    config.gameArgCount = g_host.gameArgCount;
    config.stopOnEntry = g_host.stopOnEntry;
    config.embedWindow = g_host.embedWindow;
    config.embedWindowKind = g_host.embedWindowKind;

    g_host.session = g_host.api.Create(&config);
    if (g_host.session == NULL) {
        snprintf(error, errorSize, "Bsdbg_Create failed (invalid configuration or another session is active)");
        return FALSE;
    }
    g_host.api.SetEventCallback(g_host.session, bsdbg_event_cb, &g_host);
    g_host.api.SetLogCallback(g_host.session, bsdbg_log_cb, &g_host);
    if (g_host.api.Start(g_host.session) != 0) {
        snprintf(error, errorSize, "Bsdbg_Start failed");
        g_host.api.Destroy(g_host.session);
        g_host.session = NULL;
        return FALSE;
    }
    g_host.started = 1;
    return TRUE;
}

/* ===[ Payload parsing helpers ]=== */

typedef struct Reader {
    const unsigned char* cursor;
    const unsigned char* end;
} Reader;

static BOOL reader_i32(Reader* reader, int32_t* value) {
    if ((size_t) (reader->end - reader->cursor) < 4)
        return FALSE;
    memcpy(value, reader->cursor, 4);
    reader->cursor += 4;
    return TRUE;
}

static BOOL reader_u32(Reader* reader, uint32_t* value) {
    if ((size_t) (reader->end - reader->cursor) < 4)
        return FALSE;
    memcpy(value, reader->cursor, 4);
    reader->cursor += 4;
    return TRUE;
}
static BOOL reader_u64(Reader* reader, uint64_t* value) {
    if ((size_t) (reader->end - reader->cursor) < 8)
        return FALSE;
    memcpy(value, reader->cursor, 8);
    reader->cursor += 8;
    return TRUE;
}

static BOOL reader_double(Reader* reader, double* value) {
    return reader_u64(reader, (uint64_t*) value);
}

static BOOL reader_float(Reader* reader, float* value) {
    uint32_t bits;
    if (!reader_u32(reader, &bits))
        return FALSE;
    memcpy(value, &bits, 4);
    return TRUE;
}

static char* reader_blob(Reader* reader, uint32_t maxLength, uint32_t* outLength) {
    uint32_t length;
    char* text;

    if (!reader_u32(reader, &length) || length > maxLength)
        return NULL;
    if ((size_t) (reader->end - reader->cursor) < length)
        return NULL;
    text = (char*) malloc((size_t) length + 1);
    if (text == NULL)
        return NULL;
    memcpy(text, reader->cursor, length);
    text[length] = '\0';
    reader->cursor += length;
    if (outLength != NULL)
        *outLength = length;
    return text;
}

/* ===[ Request handling ]=== */

static void send_response(uint32_t id, uint16_t opcode, uint16_t status,
                          const char* payload, uint32_t length) {
    queue_push(MSG_RESPONSE, opcode, status, id, payload, length);
}

static void send_error(uint32_t id, uint16_t opcode, const char* message) {
    send_response(id, opcode, 1, message, (uint32_t) strlen(message));
}

static void handle_inject(uint32_t id, const char* payload, uint32_t length) {
    Reader reader;
    int32_t kind;
    int32_t a;
    int32_t b;
    uint32_t character;
    double x;
    double y;
    float value;

    reader.cursor = (const unsigned char*) payload;
    reader.end = reader.cursor + length;
    if (!reader_i32(&reader, &kind)) {
        send_error(id, OP_INJECT, "malformed injection payload");
        return;
    }
    if (g_host.api.InjectKey == NULL || g_host.api.InjectMousePos == NULL ||
        g_host.api.InjectGamepadAxis == NULL) {
        send_error(id, OP_INJECT, "the runtime does not support input injection");
        return;
    }

    switch (kind) {
    case 0: /* key */
        if (reader_i32(&reader, &a) && reader_i32(&reader, &b))
            g_host.api.InjectKey(a, b);
        else {
            send_error(id, OP_INJECT, "malformed key payload");
            return;
        }
        break;
    case 1: /* text */
        if (!reader_u32(&reader, &character)) {
            send_error(id, OP_INJECT, "malformed text payload");
            return;
        }
        g_host.api.InjectKeyText(character);
        break;
    case 2: /* mouse button */
        if (reader_i32(&reader, &a) && reader_i32(&reader, &b))
            g_host.api.InjectMouseButton(a, b);
        else {
            send_error(id, OP_INJECT, "malformed mouse button payload");
            return;
        }
        break;
    case 3: /* mouse position */
        if (reader_double(&reader, &x) && reader_double(&reader, &y))
            g_host.api.InjectMousePos(x, y);
        else {
            send_error(id, OP_INJECT, "malformed mouse position payload");
            return;
        }
        break;
    case 4: /* mouse wheel */
        if (!reader_double(&reader, &x)) {
            send_error(id, OP_INJECT, "malformed wheel payload");
            return;
        }
        g_host.api.InjectMouseWheel(x);
        break;
    case 5: /* gamepad button */
        if (reader_i32(&reader, &a) && reader_i32(&reader, &b) && reader_float(&reader, &value))
            g_host.api.InjectGamepadButton(a, b, value);
        else {
            send_error(id, OP_INJECT, "malformed gamepad button payload");
            return;
        }
        break;
    case 6: /* gamepad axis */
        if (reader_i32(&reader, &a) && reader_float(&reader, &value))
            g_host.api.InjectGamepadAxis(a, value);
        else {
            send_error(id, OP_INJECT, "malformed gamepad axis payload");
            return;
        }
        break;
    default:
        send_error(id, OP_INJECT, "unknown injection kind");
        return;
    }
    send_response(id, OP_INJECT, 0, NULL, 0);
}

static void handle_publish_surface(uint32_t id, const char* payload, uint32_t length) {
    Reader reader;
    int32_t kind;
    uint64_t handle;

    if (g_host.session != NULL) {
        send_error(id, OP_PUBLISH_SURFACE,
                   "the surface must be published before the session starts");
        return;
    }
    reader.cursor = (const unsigned char*) payload;
    reader.end = reader.cursor + length;
    if (!reader_i32(&reader, &kind) || !reader_u64(&reader, &handle) || handle == 0 ||
        (kind != 1 && kind != 2 && kind != 3)) {
        send_error(id, OP_PUBLISH_SURFACE, "malformed surface payload");
        return;
    }
    g_host.embedWindow = (intptr_t) handle;
    g_host.embedWindowKind = kind;
    send_response(id, OP_PUBLISH_SURFACE, 0, NULL, 0);
}

static void handle_withdraw_surface(uint32_t id) {
    if (g_host.session != NULL) {
        send_error(id, OP_WITHDRAW_SURFACE, "cannot withdraw the surface while running");
        return;
    }
    g_host.embedWindow = 0;
    g_host.embedWindowKind = BSDBG_EMBED_NONE;
    send_response(id, OP_WITHDRAW_SURFACE, 0, NULL, 0);
}
static void send_json_result(uint32_t id, uint16_t opcode, char* json) {
    if (json == NULL) {
        send_error(id, opcode, "the debug session returned no data");
        return;
    }
    send_response(id, opcode, 0, json, (uint32_t) strlen(json));
    g_host.api.FreeString(json);
}

static BOOL require_session(uint32_t id, uint16_t opcode) {
    if (g_host.session == NULL) {
        send_error(id, opcode, "the debug session is not running");
        return FALSE;
    }
    return TRUE;
}

static void handle_set_breakpoints(uint32_t id, const char* payload, uint32_t length) {
    int32_t count;
    BsdbgBreakpoint* breakpoints;

    if (!require_session(id, OP_SET_BREAKPOINTS))
        return;
    if (length % 12 != 0) {
        send_error(id, OP_SET_BREAKPOINTS, "breakpoint payload length is not a multiple of 12");
        return;
    }
    count = (int32_t) (length / 12);
    if (count < 0 || (uint32_t) count > MAX_BREAKPOINTS) {
        send_error(id, OP_SET_BREAKPOINTS, "too many breakpoints");
        return;
    }
    breakpoints = (BsdbgBreakpoint*) payload;
    if (g_host.api.SetBreakpoints(g_host.session, breakpoints, count) != 0)
        send_error(id, OP_SET_BREAKPOINTS, "the runtime rejected the breakpoint list");
    else
        send_response(id, OP_SET_BREAKPOINTS, 0, NULL, 0);
}

static void handle_set_instance_variable(uint32_t id, const char* payload, uint32_t length) {
    Reader reader;
    int32_t instanceId;
    char* name;
    char* value;
    char* error = NULL;
    char generic[128];
    int32_t result;

    if (!require_session(id, OP_SET_INSTANCE_VARIABLE))
        return;
    reader.cursor = (const unsigned char*) payload;
    reader.end = reader.cursor + length;
    if (!reader_i32(&reader, &instanceId)) {
        send_error(id, OP_SET_INSTANCE_VARIABLE, "malformed instance variable payload");
        return;
    }
    name = reader_blob(&reader, 4096, NULL);
    value = reader_blob(&reader, MAX_COMMAND_FRAME, NULL);
    if (name == NULL || value == NULL) {
        free(name);
        free(value);
        send_error(id, OP_SET_INSTANCE_VARIABLE, "malformed instance variable payload");
        return;
    }
    result = g_host.api.SetInstanceVariable(g_host.session, instanceId, name, value, &error);
    free(name);
    free(value);
    if (result == 0) {
        send_response(id, OP_SET_INSTANCE_VARIABLE, 0, NULL, 0);
    } else {
        if (error == NULL) {
            snprintf(generic, sizeof(generic), "SetInstanceVariable failed (%d)", (int) result);
            send_error(id, OP_SET_INSTANCE_VARIABLE, generic);
        } else {
            send_error(id, OP_SET_INSTANCE_VARIABLE, error);
            g_host.api.FreeString(error);
        }
    }
}

static void handle_set_global_variable(uint32_t id, const char* payload, uint32_t length) {
    Reader reader;
    char* name;
    char* value;
    char* error = NULL;
    char generic[128];
    int32_t result;

    if (!require_session(id, OP_SET_GLOBAL_VARIABLE))
        return;
    reader.cursor = (const unsigned char*) payload;
    reader.end = reader.cursor + length;
    name = reader_blob(&reader, 4096, NULL);
    value = reader_blob(&reader, MAX_COMMAND_FRAME, NULL);
    if (name == NULL || value == NULL) {
        free(name);
        free(value);
        send_error(id, OP_SET_GLOBAL_VARIABLE, "malformed global variable payload");
        return;
    }
    result = g_host.api.SetGlobalVariable(g_host.session, name, value, &error);
    free(name);
    free(value);
    if (result == 0) {
        send_response(id, OP_SET_GLOBAL_VARIABLE, 0, NULL, 0);
    } else {
        if (error == NULL) {
            snprintf(generic, sizeof(generic), "SetGlobalVariable failed (%d)", (int) result);
            send_error(id, OP_SET_GLOBAL_VARIABLE, generic);
        } else {
            send_error(id, OP_SET_GLOBAL_VARIABLE, error);
            g_host.api.FreeString(error);
        }
    }
}

static void handle_set_frame_variable(uint32_t id, const char* payload, uint32_t length) {
    Reader reader;
    int32_t frameIndex;
    int32_t isArgument;
    int32_t slot;
    char* value;
    char* error = NULL;
    char generic[128];
    int32_t result;

    if (!require_session(id, OP_SET_FRAME_VARIABLE))
        return;
    reader.cursor = (const unsigned char*) payload;
    reader.end = reader.cursor + length;
    if (!reader_i32(&reader, &frameIndex) ||
        !reader_i32(&reader, &isArgument) ||
        !reader_i32(&reader, &slot)) {
        send_error(id, OP_SET_FRAME_VARIABLE, "malformed frame variable payload");
        return;
    }
    value = reader_blob(&reader, MAX_COMMAND_FRAME, NULL);
    if (value == NULL) {
        send_error(id, OP_SET_FRAME_VARIABLE, "malformed frame variable payload");
        return;
    }
    result = g_host.api.SetFrameVariable(g_host.session, frameIndex, isArgument, slot, value, &error);
    free(value);
    if (result == 0) {
        send_response(id, OP_SET_FRAME_VARIABLE, 0, NULL, 0);
    } else {
        if (error == NULL) {
            snprintf(generic, sizeof(generic), "SetFrameVariable failed (%d)", (int) result);
            send_error(id, OP_SET_FRAME_VARIABLE, generic);
        } else {
            send_error(id, OP_SET_FRAME_VARIABLE, error);
            g_host.api.FreeString(error);
        }
    }
}

static void handle_apply_code_patch(uint32_t id, const char* payload, uint32_t length) {
    char* json;
    char* error = NULL;
    char generic[128];
    int32_t result;

    if (!require_session(id, OP_APPLY_CODE_PATCH))
        return;
    if (length == 0 || length > MAX_FRAME) {
        send_error(id, OP_APPLY_CODE_PATCH, "empty or oversized code patch");
        return;
    }
    json = (char*) malloc((size_t) length + 1);
    if (json == NULL) {
        send_error(id, OP_APPLY_CODE_PATCH, "out of memory");
        return;
    }
    memcpy(json, payload, length);
    json[length] = '\0';
    result = g_host.api.ApplyCodePatch(g_host.session, json, &error);
    free(json);
    if (result == 0) {
        send_response(id, OP_APPLY_CODE_PATCH, 0, NULL, 0);
    } else {
        if (error == NULL) {
            snprintf(generic, sizeof(generic), "ApplyCodePatch failed (%d)", (int) result);
            send_error(id, OP_APPLY_CODE_PATCH, generic);
        } else {
            send_error(id, OP_APPLY_CODE_PATCH, error);
            g_host.api.FreeString(error);
        }
    }
}

static void handle_request(uint32_t id, uint16_t opcode, const char* payload, uint32_t length) {
    int32_t result;
    char buffer[128];

    if (opcode != OP_APPLY_CODE_PATCH && length > MAX_COMMAND_FRAME) {
        send_error(id, opcode, "command payload is too large");
        return;
    }

    switch (opcode) {
    case OP_PING:
        send_response(id, opcode, 0, "{\"pong\":true}", 13);
        break;
    case OP_START: {
        char error[256];
        if (start_session(error, sizeof(error))) {
            send_response(id, opcode, 0, NULL, 0);
        } else {
            fprintf(stderr, "bsdbg-host: start_session failed: %s\n", error);
            fflush(stderr);
            send_error(id, opcode, error);
            char failure[512];
            _snprintf(failure, sizeof(failure),
                      "{\"message\":\"Failed to start Butterscotch: %s\"}", error);
            failure[sizeof(failure) - 1] = '\0';
            queue_push(MSG_EVENT, HOST_EV_ERROR, 0, 0, failure, (uint32_t) strlen(failure));
            queue_push(MSG_EVENT, HOST_EV_EXIT, 0, 0, "{\"code\":-1}", 11);
            InterlockedExchange(&g_host.shuttingDown, 1);
        }
        break;
    }
    case OP_SHUTDOWN:
        InterlockedExchange(&g_host.shuttingDown, 1);
        stop_session();
        send_response(id, opcode, 0, NULL, 0);
        break;
    case OP_PAUSE:
        if (!require_session(id, opcode)) break;
        result = g_host.api.Pause(g_host.session);
        if (result == 0) send_response(id, opcode, 0, NULL, 0);
        else send_error(id, opcode, "pause failed");
        break;
    case OP_RESUME:
        if (!require_session(id, opcode)) break;
        result = g_host.api.Resume(g_host.session);
        if (result == 0) send_response(id, opcode, 0, NULL, 0);
        else send_error(id, opcode, "resume failed");
        break;
    case OP_STEP_INTO:
        if (!require_session(id, opcode)) break;
        result = g_host.api.StepInto(g_host.session);
        if (result == 0) send_response(id, opcode, 0, NULL, 0);
        else send_error(id, opcode, "step into failed (not paused?)");
        break;
    case OP_STEP_OVER:
        if (!require_session(id, opcode)) break;
        result = g_host.api.StepOver(g_host.session);
        if (result == 0) send_response(id, opcode, 0, NULL, 0);
        else send_error(id, opcode, "step over failed (not paused?)");
        break;
    case OP_STEP_OUT:
        if (!require_session(id, opcode)) break;
        result = g_host.api.StepOut(g_host.session);
        if (result == 0) send_response(id, opcode, 0, NULL, 0);
        else send_error(id, opcode, "step out failed (not paused?)");
        break;
    case OP_STEP_FRAME:
        if (!require_session(id, opcode)) break;
        result = g_host.api.StepFrame(g_host.session);
        if (result == 0) send_response(id, opcode, 0, NULL, 0);
        else send_error(id, opcode, "step frame failed");
        break;
    case OP_SET_BREAKPOINTS:
        handle_set_breakpoints(id, payload, length);
        break;
    case OP_CLEAR_BREAKPOINTS:
        if (!require_session(id, opcode)) break;
        result = g_host.api.ClearBreakpoints(g_host.session);
        if (result == 0) send_response(id, opcode, 0, NULL, 0);
        else send_error(id, opcode, "clear breakpoints failed");
        break;
    case OP_GET_STATE:
        if (!require_session(id, opcode)) break;
        send_json_result(id, opcode, g_host.api.GetStateJson(g_host.session));
        break;
    case OP_GET_CALL_STACK:
        if (!require_session(id, opcode)) break;
        send_json_result(id, opcode, g_host.api.GetCallStackJson(g_host.session));
        break;
    case OP_GET_INSTANCES:
        if (!require_session(id, opcode)) break;
        send_json_result(id, opcode, g_host.api.GetInstancesJson(g_host.session));
        break;
    case OP_GET_GLOBALS:
        if (!require_session(id, opcode)) break;
        send_json_result(id, opcode, g_host.api.GetGlobalsJson(g_host.session));
        break;
    case OP_GET_FRAME_COUNT:
        if (!require_session(id, opcode)) break;
        snprintf(buffer, sizeof(buffer), "{\"frameCount\":%d}",
                 (int) g_host.api.GetFrameCount(g_host.session));
        send_response(id, opcode, 0, buffer, (uint32_t) strlen(buffer));
        break;
    case OP_SET_INSTANCE_VARIABLE:
        handle_set_instance_variable(id, payload, length);
        break;
    case OP_SET_GLOBAL_VARIABLE:
        handle_set_global_variable(id, payload, length);
        break;
    case OP_SET_FRAME_VARIABLE:
        handle_set_frame_variable(id, payload, length);
        break;
    case OP_APPLY_CODE_PATCH:
        handle_apply_code_patch(id, payload, length);
        break;
    case OP_INJECT:
        handle_inject(id, payload, length);
        break;
    case OP_PUBLISH_SURFACE:
        handle_publish_surface(id, payload, length);
        break;
    case OP_WITHDRAW_SURFACE:
        handle_withdraw_surface(id);
        break;
    default:
        send_error(id, opcode, "unknown command");
        break;
    }
}

/* ===[ Startup ]=== */

static void free_config_strings(void) {
    free(g_host.dataPath);
    free(g_host.workingDirectory);
    free(g_host.saveFolder);
    g_host.dataPath = NULL;
    g_host.workingDirectory = NULL;
    g_host.saveFolder = NULL;
    if (g_host.gameArgs != NULL) {
        for (int i = 0; i < g_host.gameArgCount; i++)
            free(g_host.gameArgs[i]);
        free(g_host.gameArgs);
        g_host.gameArgs = NULL;
        g_host.gameArgCount = 0;
    }
}

static int parse_int(const wchar_t* value, int fallback) {
    wchar_t* end = NULL;
    long parsed = wcstol(value, &end, 10);
    if (end == value || (end != NULL && *end != L'\0'))
        return fallback;
    return (int) parsed;
}

static BOOL parse_args(int argc, wchar_t** argv, int* port,
                       wchar_t* explicitDll, size_t explicitDllSize) {
    g_host.renderer = BSDBG_RENDERER_MODERN_GL;
    g_host.speed = 1.0;
    g_host.gameArgs = (char**) calloc(MAX_ARGS, sizeof(char*));
    if (g_host.gameArgs == NULL)
        return FALSE;

    for (int i = 1; i < argc; i++) {
        const wchar_t* arg = argv[i];
        const wchar_t* value = (i + 1 < argc) ? argv[i + 1] : NULL;
        if (wcscmp(arg, L"--port") == 0 && value != NULL) {
            *port = parse_int(value, 0);
            i++;
        } else if (wcscmp(arg, L"--data") == 0 && value != NULL) {
            g_host.dataPath = utf16_to_utf8(value);
            i++;
        } else if (wcscmp(arg, L"--workdir") == 0 && value != NULL) {
            g_host.workingDirectory = utf16_to_utf8(value);
            i++;
        } else if (wcscmp(arg, L"--save") == 0 && value != NULL) {
            g_host.saveFolder = utf16_to_utf8(value);
            i++;
        } else if (wcscmp(arg, L"--renderer") == 0 && value != NULL) {
            g_host.renderer = parse_int(value, BSDBG_RENDERER_MODERN_GL);
            i++;
        } else if (wcscmp(arg, L"--width") == 0 && value != NULL) {
            g_host.windowWidth = parse_int(value, 0);
            i++;
        } else if (wcscmp(arg, L"--height") == 0 && value != NULL) {
            g_host.windowHeight = parse_int(value, 0);
            i++;
        } else if (wcscmp(arg, L"--headless") == 0) {
            g_host.headless = 1;
        } else if (wcscmp(arg, L"--fullscreen") == 0) {
            g_host.fullscreen = 1;
        } else if (wcscmp(arg, L"--stop-on-entry") == 0) {
            g_host.stopOnEntry = 1;
        } else if (wcscmp(arg, L"--speed") == 0 && value != NULL) {
            g_host.speed = wcstod(value, NULL);
            if (g_host.speed <= 0.0)
                g_host.speed = 1.0;
            i++;
        } else if (wcscmp(arg, L"--seed") == 0 && value != NULL) {
            g_host.seed = (uint32_t) wcstoul(value, NULL, 10);
            i++;
        } else if (wcscmp(arg, L"--game-arg") == 0 && value != NULL) {
            if (g_host.gameArgCount < MAX_ARGS)
                g_host.gameArgs[g_host.gameArgCount++] = utf16_to_utf8(value);
            i++;
        } else if (wcscmp(arg, L"--dump-dir") == 0 && value != NULL) {
            wcsncpy(g_host.dumpDir, value, (sizeof(g_host.dumpDir) / sizeof(g_host.dumpDir[0])) - 1);
            i++;
        } else if (wcscmp(arg, L"--dll") == 0 && value != NULL) {
            wcsncpy(explicitDll, value, explicitDllSize);
            explicitDll[explicitDllSize - 1] = L'\0';
            i++;
        } else {
            fwprintf(stderr, L"bsdbg-host: unknown argument %ls\n", arg);
            return FALSE;
        }
    }

    if (*port <= 0 || *port > 65535) {
        host_fail("--port is required");
        return FALSE;
    }
    if (g_host.dataPath == NULL || g_host.dataPath[0] == '\0') {
        host_fail("--data is required");
        return FALSE;
    }
    if (g_host.renderer < BSDBG_RENDERER_SOFTWARE || g_host.renderer > BSDBG_RENDERER_NOOP) {
        host_fail("invalid renderer");
        return FALSE;
    }
    return TRUE;
}

static SOCKET connect_socket(int port) {
    SOCKET socketHandle;
    struct sockaddr_in address;
    int attempts;

    socketHandle = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (socketHandle == INVALID_SOCKET)
        return INVALID_SOCKET;

    memset(&address, 0, sizeof(address));
    address.sin_family = AF_INET;
    address.sin_port = htons((u_short) port);
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);

    for (attempts = 0; attempts < 100; attempts++) {
        if (connect(socketHandle, (struct sockaddr*) &address, sizeof(address)) == 0) {
            int noDelay = 1;
            setsockopt(socketHandle, IPPROTO_TCP, TCP_NODELAY,
                       (const char*) &noDelay, sizeof(noDelay));
            return socketHandle;
        }
        Sleep(50);
    }
    fprintf(stderr, "bsdbg-host: cannot connect to 127.0.0.1:%d (error %d)\n",
            port, WSAGetLastError());
    closesocket(socketHandle);
    return INVALID_SOCKET;
}

static void send_hello(void) {
    char hello[512];
    int abi = g_host.api.AbiVersion != NULL ? g_host.api.AbiVersion() : 0;
    _snprintf(hello, sizeof(hello),
              "{\"protocol\":%d,\"abi\":%d,\"arch\":\"%s\",\"pid\":%lu,\"build\":\"%s %s\"}",
              HOST_PROTOCOL, abi, g_arch, (unsigned long) GetCurrentProcessId(),
              __DATE__, __TIME__);
    hello[sizeof(hello) - 1] = '\0';
    queue_push(MSG_HELLO, 0, 0, 0, hello, (uint32_t) strlen(hello));
}

int wmain(int argc, wchar_t** argv) {
    wchar_t explicitDll[MAX_PATH * 2] = L"";
    WSADATA winsock;
    SOCKET socketHandle;
    int port = 0;
    FrameHeader header;
    char* payload;
    BOOL running = TRUE;

#if defined(_M_ARM64)
    strcpy(g_arch, "arm64");
#elif defined(_WIN64)
    strcpy(g_arch, "x64");
#else
    strcpy(g_arch, "x86");
#endif

    memset(&g_host, 0, sizeof(g_host));
    g_host.socket = INVALID_SOCKET;
    if (WSAStartup(MAKEWORD(2, 2), &winsock) != 0) {
        host_fail("WSAStartup failed");
        return 2;
    }

    /* Keep bare-name DLL loads out of the game directory.  The runtime resolves
     * game/save DLLs explicitly; system names remain available. */
    {
        HMODULE kernel32 = GetModuleHandleW(L"kernel32.dll");
        typedef BOOL (WINAPI* PFN_SetDefaultDllDirectories)(DWORD);
        PFN_SetDefaultDllDirectories setDefaults = kernel32 != NULL
            ? (PFN_SetDefaultDllDirectories) GetProcAddress(kernel32, "SetDefaultDllDirectories")
            : NULL;
        if (setDefaults != NULL)
            setDefaults(LOAD_LIBRARY_SEARCH_APPLICATION_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    }
    {
        HMODULE user32 = LoadLibraryW(L"user32.dll");
        if (user32 != NULL) {
            typedef BOOL (WINAPI* PFN_SetProcessDpiAwarenessContext)(HANDLE);
            PFN_SetProcessDpiAwarenessContext setContext =
                (PFN_SetProcessDpiAwarenessContext) GetProcAddress(user32, "SetProcessDpiAwarenessContext");
            if (setContext != NULL)
                setContext((HANDLE) -4); /* PER_MONITOR_AWARE_V2 */
            else {
                typedef BOOL (WINAPI* PFN_SetProcessDPIAware)(void);
                PFN_SetProcessDPIAware setAware =
                    (PFN_SetProcessDPIAware) GetProcAddress(user32, "SetProcessDPIAware");
                if (setAware != NULL)
                    setAware();
            }
        }
    }

    if (!parse_args(argc, argv, &port,
                    explicitDll, (sizeof(explicitDll) / sizeof(explicitDll[0]))))
        return 2;

    init_dump_directory();
    SetUnhandledExceptionFilter(crash_filter);

    if (!load_api(&g_host.api, explicitDll[0] != L'\0' ? explicitDll : NULL))
        return 2;

    socketHandle = connect_socket(port);
    if (socketHandle == INVALID_SOCKET)
        return 2;
    g_host.socket = socketHandle;

    InitializeCriticalSection(&g_host.queueLock);
    InitializeConditionVariable(&g_host.queueCv);
    g_host.writerThread = CreateThread(NULL, 0, writer_thread, NULL, 0, NULL);
    if (g_host.writerThread == NULL) {
        host_fail("failed to create the output writer thread");
        closesocket(socketHandle);
        WSACleanup();
        return 2;
    }

    send_hello();
    queue_flush(2000);

    while (running && !InterlockedCompareExchange(&g_host.pipeBroken, 0, 0)) {
        payload = NULL;
        if (!read_frame(&header, &payload)) {
            free(payload);
            break;
        }
        if (header.type == MSG_REQUEST) {
            handle_request(header.id, header.opcode, payload, header.length);
        }
        free(payload);

        if (InterlockedCompareExchange(&g_host.shuttingDown, 0, 0) != 0)
            running = FALSE;
    }

    stop_session();
    queue_flush(2000);

    InterlockedExchange(&g_host.writerDone, 1);
    EnterCriticalSection(&g_host.queueLock);
    WakeAllConditionVariable(&g_host.queueCv);
    LeaveCriticalSection(&g_host.queueLock);
    if (g_host.writerThread != NULL) {
        WaitForSingleObject(g_host.writerThread, 5000);
        CloseHandle(g_host.writerThread);
    }
    if (g_host.socket != INVALID_SOCKET) {
        closesocket(g_host.socket);
        g_host.socket = INVALID_SOCKET;
    }
    WSACleanup();
    DeleteCriticalSection(&g_host.queueLock);

    free_config_strings();
    if (g_host.api.module != NULL)
        FreeLibrary(g_host.api.module);
    return 0;
}