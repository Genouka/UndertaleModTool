/*
 * ButterscotchDbg - session lifecycle, runner control and loop hooks.
 *
 * The game loop runs on a dedicated thread owned by the session. All public
 * entry points synchronise on the session's critical section / condition
 * variable. The runner thread blocks inside Bsdbg_DebugStopAndWait while the
 * session is paused (breakpoint, step or user pause).
 */

#include "bsdbg_internal.h"
#include "bsdbg_hooks.h"

#include "loop.h"
#include "utils.h"
#include "stb_ds.h"

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#  include <process.h>
#if defined(_WIN32) && defined(_MSC_VER)
#  include <malloc.h> /* _resetstkoflw */
#endif
#endif

BsdbgControl* g_bsdbg = nullptr;

/* ===[ Atomics ]=== */

static LONG bs_atomic_get(volatile LONG* p) {
#if defined(_WIN32)
    return InterlockedCompareExchange(p, 0, 0);
#else
    return __atomic_load_n(p, __ATOMIC_SEQ_CST);
#endif
}

static void bs_atomic_set(volatile LONG* p, LONG value) {
#if defined(_WIN32)
    InterlockedExchange(p, value);
#else
    __atomic_store_n(p, value, __ATOMIC_SEQ_CST);
#endif
}

static int bs_atomic_cas(volatile LONG* p, LONG expected, LONG desired) {
#if defined(_WIN32)
    return InterlockedCompareExchange(p, desired, expected) == expected;
#else
    return __atomic_compare_exchange_n(p, &expected, desired, 0,
                                       __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST);
#endif
}

static int bs_claim_global(BsdbgControl* c) {
#if defined(_WIN32)
    return InterlockedCompareExchangePointer((PVOID volatile*) &g_bsdbg,
                                             (PVOID) c, NULL) == NULL;
#else
    BsdbgControl* expected = NULL;
    return __atomic_compare_exchange_n(&g_bsdbg, &expected, c, 0,
                                       __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST);
#endif
}

static void bs_release_global(void) {
#if defined(_WIN32)
    InterlockedExchangePointer((PVOID volatile*) &g_bsdbg, NULL);
#else
    __atomic_store_n(&g_bsdbg, NULL, __ATOMIC_SEQ_CST);
#endif
}

/* ===[ Small helpers ]=== */

static BsdbgControl* bs_control(BsdbgSession* session) {
    return (BsdbgControl*) session;
}

static char* bs_strdup_checked(const char* str) {
    size_t length;
    char* copy;

    if (str == nullptr)
        return nullptr;
    length = strlen(str);
    copy = (char*) malloc(length + 1);
    if (copy == nullptr)
        return nullptr;
    memcpy(copy, str, length + 1);
    return copy;
}

static void bs_set_error(char** errorText, const char* message) {
    if (errorText == nullptr)
        return;
    *errorText = bs_strdup_checked(message);
}

static size_t bs_json_escape(char* out, size_t capacity, const char* text) {
    size_t written = 0;
    const unsigned char* p = (const unsigned char*) text;

    if (capacity == 0)
        return 0;
    if (text == nullptr) {
        out[0] = '\0';
        return 0;
    }

    while (*p != '\0' && written + 7 < capacity) {
        unsigned char ch = *p++;
        switch (ch) {
            case '"':  out[written++] = '\\'; out[written++] = '"';  break;
            case '\\': out[written++] = '\\'; out[written++] = '\\'; break;
            case '\n': out[written++] = '\\'; out[written++] = 'n';  break;
            case '\r': out[written++] = '\\'; out[written++] = 'r';  break;
            case '\t': out[written++] = '\\'; out[written++] = 't';  break;
            default:
                if (ch < 0x20) {
                    static const char hex[] = "0123456789abcdef";
                    out[written++] = '\\';
                    out[written++] = 'u';
                    out[written++] = '0';
                    out[written++] = '0';
                    out[written++] = hex[(ch >> 4) & 0xF];
                    out[written++] = hex[ch & 0xF];
                } else {
                    out[written++] = (char) ch;
                }
                break;
        }
    }

    out[written] = '\0';
    return written;
}

static const char* bs_level_name(int level) {
    switch (level) {
        case 0: return "debug";
        case 2: return "warn";
        case 3: return "error";
        default: return "info";
    }
}

/* ===[ Locking primitives ]=== */

void Bsdbg_Lock(BsdbgControl* c) {
    if (c == nullptr)
        return;
#if defined(_WIN32)
    EnterCriticalSection(&c->lock);
#else
    pthread_mutex_lock(&c->lock);
#endif
}

void Bsdbg_Unlock(BsdbgControl* c) {
    if (c == nullptr)
        return;
#if defined(_WIN32)
    LeaveCriticalSection(&c->lock);
#else
    pthread_mutex_unlock(&c->lock);
#endif
}

void Bsdbg_Wait(BsdbgControl* c) {
    if (c == nullptr)
        return;
#if defined(_WIN32)
    SleepConditionVariableCS(&c->cv, &c->lock, INFINITE);
#else
    pthread_cond_wait(&c->cv, &c->lock);
#endif
}

void Bsdbg_NotifyAll(BsdbgControl* c) {
    if (c == nullptr)
        return;
#if defined(_WIN32)
    WakeAllConditionVariable(&c->cv);
#else
    pthread_cond_broadcast(&c->cv);
#endif
}

BsdbgControl* Bsdbg_Control(void) {
    return g_bsdbg;
}

/* ===[ Events and logging ]=== */

void Bsdbg_EmitEvent(BsdbgControl* c, int32_t type, const char* json) {
    BsdbgEventCallback callback;
    void* user;

    if (c == nullptr)
        return;

    /* Callbacks are invoked without the lock held: the handler is allowed to
     * query the session (but must not block on it). */
    Bsdbg_Lock(c);
    callback = c->eventCb;
    user = c->eventUser;
    Bsdbg_Unlock(c);

    if (callback != nullptr)
        callback(user, type, json != nullptr ? json : "null");
}

void Bsdbg_Log(BsdbgControl* c, int level, const char* fmt, ...) {
    char message[4096];
    char json[8192];
    char escaped[8192];
    va_list args;
    BsdbgLogCallback callback;
    void* user;

    if (c == nullptr || fmt == nullptr)
        return;

    va_start(args, fmt);
    vsnprintf(message, sizeof(message), fmt, args);
    va_end(args);
    message[sizeof(message) - 1] = '\0';

    Bsdbg_Lock(c);
    callback = c->logCb;
    user = c->logUser;
    Bsdbg_Unlock(c);

    if (callback != nullptr) {
        callback(user, level, message);
        return;
    }

    bs_json_escape(escaped, sizeof(escaped), message);
    snprintf(json, sizeof(json), "{\"level\":\"%s\",\"message\":\"%s\"}",
             bs_level_name(level), escaped);
    Bsdbg_EmitEvent(c, BSDBG_EVENT_LOG, json);
}

void Bsdbg_UpdateActive(BsdbgControl* c) {
    LONG active = 0;
    int i;

    if (c == nullptr)
        return;

    if (bs_atomic_get(&c->interruptRequested) != 0 ||
        bs_atomic_get(&c->stopOnEntryPending) != 0 ||
        bs_atomic_get(&c->stepping) != 0 ||
        bs_atomic_get(&c->stepMode) != BSDBG_STEP_NONE ||
        bs_atomic_get(&c->paused) != 0)
        active = 1;

    for (i = 0; active == 0 && i < c->bpCount; i++) {
        if (c->breakpoints[i].enabled)
            active = 1;
    }

    bs_atomic_set(&c->active, active);
}

/* ===[ Breakpoints ]=== */

void Bsdbg_SetBreakpointsInternal(BsdbgControl* c, const BsdbgBreakpoint* list,
                                  int count) {
    if (c == nullptr)
        return;
    if (count < 0)
        count = 0;

    Bsdbg_Lock(c);

    if (count > 0) {
        if (count > c->bpCapacity) {
            int capacity = c->bpCapacity > 0 ? c->bpCapacity : 8;
            BsdbgBreakpoint* grown;

            while (capacity < count)
                capacity *= 2;

            grown = (BsdbgBreakpoint*) realloc(c->breakpoints,
                                               (size_t) capacity * sizeof(BsdbgBreakpoint));
            if (grown == nullptr) {
                Bsdbg_Unlock(c);
                return;
            }

            c->breakpoints = grown;
            c->bpCapacity = capacity;
        }

        if (list != nullptr)
            memcpy(c->breakpoints, list, (size_t) count * sizeof(BsdbgBreakpoint));
        else
            memset(c->breakpoints, 0, (size_t) count * sizeof(BsdbgBreakpoint));
        c->bpCount = count;
    } else {
        c->bpCount = 0;
    }

    Bsdbg_UpdateActive(c);
    Bsdbg_Unlock(c);
}

/* ===[ Stop / wait ]=== */

void Bsdbg_DebugStopAndWait(BsdbgControl* c, int reason, int location,
                            uint32_t codeIndex, uint32_t ip) {
    char* json;

    if (c == nullptr)
        return;

    Bsdbg_Lock(c);

    if (bs_atomic_get(&c->exitRequested) != 0) {
        Bsdbg_Unlock(c);
        return;
    }

    c->stopReason = reason;
    c->stopLocation = location;
    c->stopCodeIndex = codeIndex;
    c->stopIp = ip;
    c->stopDepth = 0;
    if (c->vm != nullptr && c->vm->currentCallFrame != nullptr) {
        /* Depth of the current frame: number of parents, top frame = 0. */
        CallFrame* frame = c->vm->currentCallFrame->parent;
        while (frame != nullptr) {
            c->stopDepth++;
            frame = frame->parent;
        }
    }

    /* Suppress the VM stop point that is currently being reported, so stepping
     * past / resuming it does not immediately stop again at the same ip. Frame
     * boundaries are not instructions, so they clear any stale suppression. */
    c->suppressValid = location == BSDBG_STOPLOC_VM ? 1 : 0;
    c->suppressCodeIndex = codeIndex;
    c->suppressIp = ip;

    bs_atomic_set(&c->stepping, 0);
    bs_atomic_set(&c->interruptRequested, 0);
    bs_atomic_set(&c->paused, 1);
    Bsdbg_UpdateActive(c);
    Bsdbg_Unlock(c);

    json = Bsdbg_BuildStateJson(c);
    Bsdbg_EmitEvent(c, BSDBG_EVENT_BREAK, json);
    free(json);

    Bsdbg_Lock(c);
    while (bs_atomic_get(&c->paused) != 0 && bs_atomic_get(&c->exitRequested) == 0)
        Bsdbg_Wait(c);
    Bsdbg_Unlock(c);
}

/* ===[ Configuration ownership ]=== */

static void bs_free_config(BsdbgConfig* config) {
    if (config == nullptr)
        return;

    free((void*) config->dataWinPath);
    free((void*) config->workingDirectory);
    free((void*) config->saveFolder);

    if (config->gameArgs != nullptr) {
        char** args = (char**) config->gameArgs;
        int i;
        for (i = 0; i < config->gameArgCount; i++)
            free(args[i]);
        free(args);
    }

    memset(config, 0, sizeof(*config));
}

static int bs_copy_config(BsdbgConfig* destination, const BsdbgConfig* source) {
    int i;

    memset(destination, 0, sizeof(*destination));
    destination->structSize = (uint32_t) sizeof(BsdbgConfig);
    destination->windowWidth = source->windowWidth;
    destination->windowHeight = source->windowHeight;
    destination->renderer = source->renderer;
    destination->headless = source->headless;
    destination->fullscreen = source->fullscreen;
    destination->speed = source->speed;
    destination->seed = source->seed;
    destination->gameArgCount = source->gameArgCount;
    destination->stopOnEntry = source->stopOnEntry;

    destination->dataWinPath = bs_strdup_checked(source->dataWinPath);
    if (destination->dataWinPath == nullptr)
        return -1;
    if (source->workingDirectory != nullptr) {
        destination->workingDirectory = bs_strdup_checked(source->workingDirectory);
        if (destination->workingDirectory == nullptr)
            return -1;
    }
    if (source->saveFolder != nullptr) {
        destination->saveFolder = bs_strdup_checked(source->saveFolder);
        if (destination->saveFolder == nullptr)
            return -1;
    }

    if (source->gameArgCount > 0) {
        char** copies;

        if (source->gameArgs == nullptr)
            return -1;

        copies = (char**) calloc((size_t) source->gameArgCount, sizeof(char*));
        if (copies == nullptr)
            return -1;

        for (i = 0; i < source->gameArgCount; i++) {
            copies[i] = bs_strdup_checked(source->gameArgs[i]);
            if (copies[i] == nullptr) {
                destination->gameArgs = (const char* const*) copies;
                return -1;
            }
        }
        destination->gameArgs = (const char* const*) copies;
    }

    return 0;
}

/* ===[ Runner thread ]=== */

static void bs_run_session(BsdbgControl* c) {
    CommandLineArgs args;
    char** gameArgs = nullptr;
    int32_t i;
    int result;
    char json[64];

    memset(&args, 0, sizeof(args));
    args.dataWinPath = c->config.dataWinPath;
    args.saveFolder = c->config.saveFolder;
    args.exitAtFrame = -1;
    args.speedMultiplier = c->config.speed > 0.0 ? c->config.speed : 1.0;
    args.fastForwardSpeed = 0.0;
    args.loadType = DATAWINLOADTYPE_LOAD_IN_MEMORY_AHEAD_OF_TIME;
    args.lazyRooms = true;
    args.lazyTextures = true;
    args.lazyAudio = true;
    args.renderer = (enum GraphicsAPI) c->config.renderer;
#if defined(_WIN32)
    args.osType = OS_WINDOWS;
#else
    args.osType = OS_LINUX;
#endif
    args.windowWidth = c->config.windowWidth;
    args.windowHeight = c->config.windowHeight;
    args.headless = c->config.headless != 0;
    args.fullscreen = c->config.fullscreen != 0;
    args.seed = (int) c->config.seed;
    args.hasSeed = c->config.seed != 0;
    args.debug = false;
    args.disableLogColours = true;
#ifdef ENABLE_VM_TRACING
    args.traceBytecodeAfterFrame = 0;
#endif

    /* argv[0] placeholder + "-game <data.win>" + caller supplied game args. */
    arrput(gameArgs, bs_strdup_checked("<bsdbg>"));
    arrput(gameArgs, bs_strdup_checked("-game"));
    arrput(gameArgs, bs_strdup_checked(c->config.dataWinPath));
    for (i = 0; i < c->config.gameArgCount; i++)
        arrput(gameArgs, bs_strdup_checked(c->config.gameArgs[i]));
    args.gameArgs = gameArgs;

    Bsdbg_Lock(c);
    bs_atomic_set(&c->running, 1);
    Bsdbg_UpdateActive(c);
    Bsdbg_Unlock(c);

    Bsdbg_EmitEvent(c, BSDBG_EVENT_STATE,
                    "{\"running\":true,\"paused\":false,\"frameCount\":0}");

    result = loop(args, "bsdbg");

    for (i = 0; i < (int32_t) arrlen(gameArgs); i++)
        free(gameArgs[i]);
    arrfree(gameArgs);

    Bsdbg_Lock(c);
    bs_atomic_set(&c->running, 0);
    bs_atomic_set(&c->paused, 0);
    bs_atomic_set(&c->stepping, 0);
    bs_atomic_set(&c->stepMode, BSDBG_STEP_NONE);
    bs_atomic_set(&c->stepSkipOnce, 0);
    bs_atomic_set(&c->interruptRequested, 0);
    bs_atomic_set(&c->frameStepRequested, 0);
    bs_atomic_set(&c->stopOnEntryPending, 0);
    c->stopLocation = BSDBG_STOPLOC_NONE;
    c->stopReason = BSDBG_STOP_PAUSE;
    c->runner = nullptr;
    c->vm = nullptr;
    c->dataWin = nullptr;
    Bsdbg_UpdateActive(c);
    Bsdbg_NotifyAll(c);
    Bsdbg_Unlock(c);

    snprintf(json, sizeof(json), "{\"code\":%d}", result);
    Bsdbg_EmitEvent(c, BSDBG_EVENT_EXIT, json);
}

#if defined(_WIN32) && defined(_MSC_VER)
/*
 * Isolation guard: hot-reloaded GML runs on this dedicated game thread. If a
 * bad patch faults (access violation, illegal instruction, stack overflow,
 * ...), unwind to this boundary and report it as a session error instead of
 * letting the exception terminate UndertaleModTool. The (possibly corrupt)
 * session is then marked crashed and torn down by the host; the UI process
 * stays alive.
 */
static void bs_emit_crash_event(BsdbgControl* c, int32_t type, const char* json) {
    BsdbgEventCallback callback = nullptr;
    void* user = nullptr;

    if (c == nullptr)
        return;

    /* The fault may have happened while the session lock was held; never
     * deadlock the crash handler. A best-effort unlocked read is acceptable
     * on this terminal path. */
    if (TryEnterCriticalSection(&c->lock)) {
        callback = c->eventCb;
        user = c->eventUser;
        LeaveCriticalSection(&c->lock);
    } else {
        callback = c->eventCb;
        user = c->eventUser;
    }
    if (callback != nullptr)
        callback(user, type, json != nullptr ? json : "null");
}

static int bs_crash_filter(BsdbgControl* c, EXCEPTION_POINTERS* info, char** errorText) {
    DWORD code = EXCEPTION_ACCESS_VIOLATION;
    void* address = nullptr;
    char message[256];
    char json[512];

    if (info != nullptr && info->ExceptionRecord != nullptr) {
        code = info->ExceptionRecord->ExceptionCode;
        address = info->ExceptionRecord->ExceptionAddress;
    }
    if (code == EXCEPTION_STACK_OVERFLOW)
        _resetstkoflw();

    if (c != nullptr) {
        bs_atomic_set(&c->crashed, 1);
        bs_atomic_set(&c->running, 0);
        bs_atomic_set(&c->paused, 0);
        bs_atomic_set(&c->stepping, 0);
        bs_atomic_set(&c->stepMode, BSDBG_STEP_NONE);
        bs_atomic_set(&c->stepSkipOnce, 0);
        bs_atomic_set(&c->interruptRequested, 0);
        bs_atomic_set(&c->frameStepRequested, 0);
        bs_atomic_set(&c->stopOnEntryPending, 0);

        /* Detach UI readers from the half-torn-down runner. Never free the
         * runner/data.win here: another thread may still be reading them. */
        if (TryEnterCriticalSection(&c->lock)) {
            c->runner = nullptr;
            c->vm = nullptr;
            c->dataWin = nullptr;
            Bsdbg_UpdateActive(c);
            Bsdbg_NotifyAll(c);
            LeaveCriticalSection(&c->lock);
        } else {
            InterlockedExchangePointer((PVOID volatile*) &c->runner, nullptr);
            InterlockedExchangePointer((PVOID volatile*) &c->vm, nullptr);
            InterlockedExchangePointer((PVOID volatile*) &c->dataWin, nullptr);
        }
    }

    snprintf(message, sizeof(message),
             "isolated game-thread exception 0x%08lX at %p; session terminated",
             (unsigned long) code, address);
    if (errorText != nullptr)
        *errorText = bs_strdup_checked(message);
    snprintf(json, sizeof(json), "{\"message\":\"%s\"}", message);
    bs_emit_crash_event(c, BSDBG_EVENT_ERROR, json);
    bs_emit_crash_event(c, BSDBG_EVENT_EXIT, "{\"code\":-1}");
    return EXCEPTION_EXECUTE_HANDLER;
}

static bool bs_guarded_apply_pending(BsdbgControl* c, char** errorText) {
    __try {
        BsdbgHotreload_ApplyPending(c);
        return true;
    } __except (bs_crash_filter(c, GetExceptionInformation(), errorText)) {
        return false;
    }
}

static unsigned __stdcall bs_session_thread(void* argument) {
    BsdbgControl* c = (BsdbgControl*) argument;
    ULONG stackGuarantee = 256 * 1024;

    /* Keep enough reserve for the __except handler after a stack overflow. */
    SetThreadStackGuarantee(&stackGuarantee);

    __try {
        bs_run_session(c);
    } __except (bs_crash_filter(c, GetExceptionInformation(), nullptr)) {
        /* The crash filter already reported the failure. */
    }
    return 0;
}
#elif defined(_WIN32)
/* TODO: MinGW/other compilers have no SEH __try/__except; use a separate
 * process host if crash isolation is required on those toolchains. */
static unsigned __stdcall bs_session_thread(void* argument) {
    bs_run_session((BsdbgControl*) argument);
    return 0;
}
#else
static void* bs_session_thread(void* argument) {
    bs_run_session((BsdbgControl*) argument);
    return nullptr;
}
#endif

/* ===[ Session lifecycle ]=== */

BsdbgSession* Bsdbg_Create(const BsdbgConfig* config) {
    BsdbgControl* c;

    if (config == nullptr)
        return nullptr;
    if (config->structSize != (uint32_t) sizeof(BsdbgConfig))
        return nullptr;
    if (config->dataWinPath == nullptr || config->dataWinPath[0] == '\0')
        return nullptr;
    if (config->renderer < BSDBG_RENDERER_SOFTWARE ||
        config->renderer > BSDBG_RENDERER_NOOP)
        return nullptr;
    if (config->gameArgCount < 0)
        return nullptr;

    c = (BsdbgControl*) calloc(1, sizeof(BsdbgControl));
    if (c == nullptr)
        return nullptr;

#if defined(_WIN32)
    InitializeCriticalSection(&c->lock);
    InitializeConditionVariable(&c->cv);
    c->thread = NULL;
#else
    if (pthread_mutex_init(&c->lock, nullptr) != 0) {
        free(c);
        return nullptr;
    }
    if (pthread_cond_init(&c->cv, nullptr) != 0) {
        pthread_mutex_destroy(&c->lock);
        free(c);
        return nullptr;
    }
    c->threadValid = 0;
#endif

    c->stopLocation = BSDBG_STOPLOC_NONE;
    c->stopReason = BSDBG_STOP_PAUSE;

    if (bs_copy_config(&c->config, config) != 0) {
        bs_free_config(&c->config);
#if defined(_WIN32)
        DeleteCriticalSection(&c->lock);
#else
        pthread_cond_destroy(&c->cv);
        pthread_mutex_destroy(&c->lock);
#endif
        free(c);
        return nullptr;
    }

    if (!bs_claim_global(c)) {
        bs_free_config(&c->config);
#if defined(_WIN32)
        DeleteCriticalSection(&c->lock);
#else
        pthread_cond_destroy(&c->cv);
        pthread_mutex_destroy(&c->lock);
#endif
        free(c);
        return nullptr;
    }

    return (BsdbgSession*) c;
}

int32_t Bsdbg_Start(BsdbgSession* session) {
    BsdbgControl* c = bs_control(session);

    if (c == nullptr)
        return -1;

    Bsdbg_Lock(c);
    if (!bs_atomic_cas(&c->started, 0, 1)) {
        Bsdbg_Unlock(c);
        return -1;
    }
    bs_atomic_set(&c->exitRequested, 0);
    bs_atomic_set(&c->paused, 0);
    bs_atomic_set(&c->stepping, 0);
    bs_atomic_set(&c->stepMode, BSDBG_STEP_NONE);
    bs_atomic_set(&c->stepSkipOnce, 0);
    bs_atomic_set(&c->interruptRequested, 0);
    bs_atomic_set(&c->frameStepRequested, 0);
    bs_atomic_set(&c->running, 0);
    c->stopLocation = BSDBG_STOPLOC_NONE;
    Bsdbg_Unlock(c);

#if defined(_WIN32)
    {
        uintptr_t thread = _beginthreadex(nullptr, 0, bs_session_thread, c, 0, nullptr);
        if (thread == 0) {
            Bsdbg_Lock(c);
            bs_atomic_set(&c->started, 0);
            Bsdbg_Unlock(c);
            Bsdbg_Log(c, 3, "Bsdbg_Start: failed to create the runner thread\n");
            Bsdbg_EmitEvent(c, BSDBG_EVENT_ERROR,
                            "{\"message\":\"failed to create the runner thread\"}");
            return -1;
        }
        c->thread = (HANDLE) thread;
    }
#else
    {
        int rc = pthread_create(&c->thread, nullptr, bs_session_thread, c);
        if (rc != 0) {
            Bsdbg_Lock(c);
            bs_atomic_set(&c->started, 0);
            Bsdbg_Unlock(c);
            Bsdbg_Log(c, 3, "Bsdbg_Start: pthread_create failed (%d)\n", rc);
            Bsdbg_EmitEvent(c, BSDBG_EVENT_ERROR,
                            "{\"message\":\"failed to create the runner thread\"}");
            return -1;
        }
        c->threadValid = 1;
    }
#endif

    return 0;
}

void Bsdbg_Destroy(BsdbgSession* session) {
    BsdbgControl* c = bs_control(session);
    int startCalled;

    if (c == nullptr)
        return;

    /* Already destroyed (or not owned by this slot). Destroy is one-shot. */
    if (g_bsdbg != c)
        return;

    bs_atomic_set(&c->exitRequested, 1);

    Bsdbg_Lock(c);
    Bsdbg_UpdateActive(c);
    Bsdbg_NotifyAll(c);
    Bsdbg_Unlock(c);

    startCalled = bs_atomic_get(&c->started) != 0;

#if defined(_WIN32)
    if (startCalled && c->thread != NULL) {
        WaitForSingleObject(c->thread, INFINITE);
        CloseHandle(c->thread);
        c->thread = NULL;
    }
#else
    if (startCalled && c->threadValid) {
        pthread_join(c->thread, nullptr);
        c->threadValid = 0;
    }
#endif

    bs_release_global();

    free(c->breakpoints);
    c->breakpoints = nullptr;
    free(c->pendingPatchJson);
    c->pendingPatchJson = nullptr;
    free(c->lastPatchError);
    c->lastPatchError = nullptr;

    bs_free_config(&c->config);

#if defined(_WIN32)
    DeleteCriticalSection(&c->lock);
#else
    pthread_cond_destroy(&c->cv);
    pthread_mutex_destroy(&c->lock);
#endif

    free(c);
}

/* ===[ Runner control ]=== */

int32_t Bsdbg_IsRunning(BsdbgSession* session) {
    BsdbgControl* c = bs_control(session);
    if (c == nullptr)
        return 0;
    return bs_atomic_get(&c->running) != 0 ? 1 : 0;
}

int32_t Bsdbg_IsPaused(BsdbgSession* session) {
    BsdbgControl* c = bs_control(session);
    if (c == nullptr)
        return 0;
    return bs_atomic_get(&c->paused) != 0 ? 1 : 0;
}

int32_t Bsdbg_Pause(BsdbgSession* session) {
    BsdbgControl* c = bs_control(session);

    if (c == nullptr)
        return -1;

    Bsdbg_Lock(c);
    if (bs_atomic_get(&c->running) == 0) {
        Bsdbg_Unlock(c);
        return -1;
    }
    bs_atomic_set(&c->interruptRequested, 1);
    Bsdbg_UpdateActive(c);
    Bsdbg_NotifyAll(c);
    Bsdbg_Unlock(c);
    return 0;
}

int32_t Bsdbg_Resume(BsdbgSession* session) {
    BsdbgControl* c = bs_control(session);

    if (c == nullptr)
        return -1;

    Bsdbg_Lock(c);
    bs_atomic_set(&c->paused, 0);
    bs_atomic_set(&c->stepping, 0);
    bs_atomic_set(&c->stepMode, BSDBG_STEP_NONE);
    bs_atomic_set(&c->stepSkipOnce, 0);
    bs_atomic_set(&c->interruptRequested, 0);
    bs_atomic_set(&c->frameStepRequested, 0);
    c->suppressValid = 0;
    Bsdbg_UpdateActive(c);
    Bsdbg_NotifyAll(c);
    Bsdbg_Unlock(c);
    return 0;
}

int32_t Bsdbg_StepFrame(BsdbgSession* session) {
    BsdbgControl* c = bs_control(session);

    if (c == nullptr)
        return -1;

    Bsdbg_Lock(c);
    bs_atomic_set(&c->frameStepRequested, 1);
    bs_atomic_set(&c->stepping, 0);
    bs_atomic_set(&c->stepMode, BSDBG_STEP_NONE);
    bs_atomic_set(&c->stepSkipOnce, 0);
    bs_atomic_set(&c->interruptRequested, 0);
    if (bs_atomic_get(&c->paused) != 0)
        bs_atomic_set(&c->paused, 0);
    Bsdbg_UpdateActive(c);
    Bsdbg_NotifyAll(c);
    Bsdbg_Unlock(c);
    return 0;
}

static int32_t bs_step(BsdbgControl* c, int mode) {
    if (c == nullptr)
        return -1;

    Bsdbg_Lock(c);
    if (bs_atomic_get(&c->paused) == 0) {
        Bsdbg_Unlock(c);
        return -1;
    }
    bs_atomic_set(&c->stepMode, mode);
    /* The VM hook runs before the instruction at `ip` executes: once the step
     * clears paused, that instruction runs and the following hook is the first
     * candidate. Skipping the next check would execute two instructions. */
    bs_atomic_set(&c->stepSkipOnce, 0);
    bs_atomic_set(&c->stepping, 1);
    bs_atomic_set(&c->interruptRequested, 0);
    bs_atomic_set(&c->frameStepRequested, 0);
    bs_atomic_set(&c->paused, 0);
    Bsdbg_UpdateActive(c);
    Bsdbg_NotifyAll(c);
    Bsdbg_Unlock(c);
    return 0;
}

int32_t Bsdbg_StepInto(BsdbgSession* session) {
    return bs_step(bs_control(session), BSDBG_STEP_INTO);
}

int32_t Bsdbg_StepOver(BsdbgSession* session) {
    return bs_step(bs_control(session), BSDBG_STEP_OVER);
}

int32_t Bsdbg_StepOut(BsdbgSession* session) {
    return bs_step(bs_control(session), BSDBG_STEP_OUT);
}

int32_t Bsdbg_GetFrameCount(BsdbgSession* session) {
    BsdbgControl* c = bs_control(session);
    int32_t frameCount;

    if (c == nullptr)
        return -1;

    Bsdbg_Lock(c);
    frameCount = c->runner != nullptr ? (int32_t) c->runner->frameCount : -1;
    Bsdbg_Unlock(c);
    return frameCount;
}

/* ===[ Breakpoints API ]=== */

int32_t Bsdbg_SetBreakpoints(BsdbgSession* session,
                             const BsdbgBreakpoint* breakpoints,
                             int32_t count) {
    BsdbgControl* c = bs_control(session);

    if (c == nullptr || count < 0)
        return -1;

    Bsdbg_SetBreakpointsInternal(c, breakpoints, (int) count);
    return 0;
}

int32_t Bsdbg_ClearBreakpoints(BsdbgSession* session) {
    BsdbgControl* c = bs_control(session);

    if (c == nullptr)
        return -1;

    Bsdbg_SetBreakpointsInternal(c, nullptr, 0);
    return 0;
}

/* ===[ Inspection ]=== */

char* Bsdbg_GetStateJson(BsdbgSession* session) {
    BsdbgControl* c = bs_control(session);
    return c != nullptr ? Bsdbg_BuildStateJson(c) : nullptr;
}

char* Bsdbg_GetCallStackJson(BsdbgSession* session) {
    BsdbgControl* c = bs_control(session);
    return c != nullptr ? Bsdbg_BuildCallStackJson(c) : nullptr;
}

char* Bsdbg_GetInstancesJson(BsdbgSession* session) {
    BsdbgControl* c = bs_control(session);
    return c != nullptr ? Bsdbg_BuildInstancesJson(c) : nullptr;
}

char* Bsdbg_GetGlobalsJson(BsdbgSession* session) {
    BsdbgControl* c = bs_control(session);
    return c != nullptr ? Bsdbg_BuildGlobalsJson(c) : nullptr;
}

int32_t Bsdbg_SetInstanceVariable(BsdbgSession* session, int32_t instanceId,
                                  const char* name, const char* valueJson,
                                  char** errorText) {
    BsdbgControl* c = bs_control(session);
    int paused;

    if (errorText != nullptr)
        *errorText = nullptr;
    if (c == nullptr || name == nullptr || valueJson == nullptr) {
        bs_set_error(errorText, "invalid arguments");
        return -1;
    }

    Bsdbg_Lock(c);
    paused = bs_atomic_get(&c->paused) != 0;
    Bsdbg_Unlock(c);

    if (!paused) {
        bs_set_error(errorText, "the runner is not paused");
        return -1;
    }

    return Bsdbg_SetInstanceVariableInternal(c, instanceId, name, valueJson,
                                             errorText);
}

int32_t Bsdbg_SetGlobalVariable(BsdbgSession* session, const char* name,
                                const char* valueJson, char** errorText) {
    BsdbgControl* c = bs_control(session);
    int paused;

    if (errorText != nullptr)
        *errorText = nullptr;
    if (c == nullptr || name == nullptr || valueJson == nullptr) {
        bs_set_error(errorText, "invalid arguments");
        return -1;
    }

    Bsdbg_Lock(c);
    paused = bs_atomic_get(&c->paused) != 0;
    Bsdbg_Unlock(c);

    if (!paused) {
        bs_set_error(errorText, "the runner is not paused");
        return -1;
    }

    return Bsdbg_SetGlobalVariableInternal(c, name, valueJson, errorText);
}

int32_t Bsdbg_SetFrameVariable(BsdbgSession* session, int32_t frameIndex,
                               int32_t isArgument, int32_t slot,
                               const char* valueJson, char** errorText) {
    BsdbgControl* c = bs_control(session);
    int paused;

    if (errorText != nullptr)
        *errorText = nullptr;
    if (c == nullptr || valueJson == nullptr) {
        bs_set_error(errorText, "invalid arguments");
        return -1;
    }

    Bsdbg_Lock(c);
    paused = bs_atomic_get(&c->paused) != 0;
    Bsdbg_Unlock(c);

    if (!paused) {
        bs_set_error(errorText, "the runner is not paused");
        return -1;
    }

    return Bsdbg_SetFrameVariableInternal(c, frameIndex, isArgument, slot,
                                          valueJson, errorText);
}

void Bsdbg_FreeString(char* str) {
    free(str);
}

/* ===[ Hot reload ]=== */

int32_t Bsdbg_ApplyCodePatch(BsdbgSession* session, const char* json,
                             char** errorText) {
    BsdbgControl* c = bs_control(session);
    int32_t result;
    int paused;

    if (errorText != nullptr)
        *errorText = nullptr;
    if (c == nullptr || json == nullptr) {
        bs_set_error(errorText, "invalid arguments");
        return -1;
    }

    result = BsdbgHotreload_Queue(c, json, errorText);
    if (result != 0)
        return result;

    /* While paused the VM is parked, so the patch can be applied right away. */
    Bsdbg_Lock(c);
    paused = bs_atomic_get(&c->paused) != 0;
    Bsdbg_Unlock(c);

    if (paused) {
#if defined(_WIN32) && defined(_MSC_VER)
        /* A malformed patch must not take down the WPF process. */
        if (!bs_guarded_apply_pending(c, errorText))
            return -1;
#else
        BsdbgHotreload_ApplyPending(c);
#endif
    }

    return 0;
}

/* ===[ Callbacks API ]=== */

void Bsdbg_SetEventCallback(BsdbgSession* session, BsdbgEventCallback callback,
                            void* user) {
    BsdbgControl* c = bs_control(session);

    if (c == nullptr)
        return;

    Bsdbg_Lock(c);
    c->eventCb = callback;
    c->eventUser = user;
    Bsdbg_Unlock(c);
}

void Bsdbg_SetLogCallback(BsdbgSession* session, BsdbgLogCallback callback,
                          void* user) {
    BsdbgControl* c = bs_control(session);

    if (c == nullptr)
        return;

    Bsdbg_Lock(c);
    c->logCb = callback;
    c->logUser = user;
    Bsdbg_Unlock(c);
}

int32_t Bsdbg_AbiVersion(void) {
    return 1;
}

/* ===[ Butterscotch loop / VM hooks ]=== */

void BsdbgHook_LoopStart(struct Runner* runner, struct VMContext* vm) {
    BsdbgControl* c = g_bsdbg;

    if (c == nullptr)
        return;

    Bsdbg_Lock(c);
    c->runner = runner;
    c->vm = vm;
    c->dataWin = runner != nullptr ? runner->dataWin : nullptr;
    if (c->config.stopOnEntry)
        bs_atomic_set(&c->stopOnEntryPending, 1);
    Bsdbg_UpdateActive(c);
    Bsdbg_Unlock(c);
}

int BsdbgHook_ShouldExit(void) {
    BsdbgControl* c = g_bsdbg;
    return c != nullptr && bs_atomic_get(&c->exitRequested) != 0;
}

void BsdbgHook_VmInstruction(struct VMContext* ctx, uint32_t ip) {
    BsdbgControl* c = g_bsdbg;

    if (c == nullptr || bs_atomic_get(&c->active) == 0)
        return;

    Bsdbg_OnVmInstruction(ctx, ip);
}

void BsdbgHook_FrameBoundary(struct Runner* runner) {
    BsdbgControl* c = g_bsdbg;
    int applyPatch = 0;

    (void) runner;

    if (c == nullptr)
        return;

    Bsdbg_Lock(c);
    if (c->pendingPatchJson != nullptr && c->vm != nullptr &&
        c->vm->currentCallFrame == nullptr)
        applyPatch = 1;
    Bsdbg_Unlock(c);

    if (applyPatch)
        BsdbgHotreload_ApplyPending(c);

    if (bs_atomic_get(&c->interruptRequested) != 0) {
        Bsdbg_Lock(c);
        applyPatch = bs_atomic_get(&c->interruptRequested) != 0;
        if (applyPatch)
            bs_atomic_set(&c->interruptRequested, 0);
        Bsdbg_Unlock(c);

        if (applyPatch)
            Bsdbg_DebugStopAndWait(c, BSDBG_STOP_PAUSE, BSDBG_STOPLOC_BOUNDARY,
                                   0, 0);
    }

    if (bs_atomic_get(&c->frameStepRequested) != 0) {
        uint32_t codeIndex = 0;
        uint32_t ip = 0;

        Bsdbg_Lock(c);
        applyPatch = bs_atomic_get(&c->frameStepRequested) != 0;
        if (applyPatch) {
            bs_atomic_set(&c->frameStepRequested, 0);
            if (c->vm != nullptr && c->vm->currentCallFrame != nullptr) {
                codeIndex = c->vm->currentCallFrame->codeIndex;
                ip = c->vm->ip;
            }
        }
        Bsdbg_Unlock(c);

        if (applyPatch)
            Bsdbg_DebugStopAndWait(c, BSDBG_STOP_STEP, BSDBG_STOPLOC_BOUNDARY,
                                   codeIndex, ip);
    }
}

void BsdbgHook_Log(int level, const char* message) {
    BsdbgControl* c = g_bsdbg;

    if (c == nullptr || message == nullptr)
        return;

    Bsdbg_Log(c, level, "%s", message);
}
