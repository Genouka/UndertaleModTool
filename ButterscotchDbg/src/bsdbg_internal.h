#ifndef _BSDBG_INTERNAL_H_
#define _BSDBG_INTERNAL_H_

/*
 * Internal contract shared by the ButterscotchDbg implementation files.
 *
 * File ownership (do not cross-edit without coordination):
 *   bsdbg_session.c   - lifecycle, thread, loop hooks, control APIs
 *   bsdbg_debugger.c  - VM tick, breakpoints, stepping, state/inspect JSON, set-variable
 *   bsdbg_hotreload.c - code patch parsing/application
 */

#include "bsdbg.h"

#include "runner.h"

#include <stdbool.h>

#if defined(_WIN32)
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#else
#  include <pthread.h>
#endif

#if !defined(_WIN32)
/* `LONG` is the Windows 32-bit atomic flag type; mirror it everywhere else so
 * the volatile fast-path fields have a stable representation. */
typedef int32_t LONG;
#endif

/* Step modes. */
#define BSDBG_STEP_NONE 0
#define BSDBG_STEP_INTO 1
#define BSDBG_STEP_OVER 2
#define BSDBG_STEP_OUT  3

/* Where the runner is currently stopped. */
#define BSDBG_STOPLOC_NONE     0
#define BSDBG_STOPLOC_VM       1 /* blocked inside Bsdbg_OnVmInstruction */
#define BSDBG_STOPLOC_BOUNDARY 2 /* blocked inside BsdbgHook_FrameBoundary */

typedef struct BsdbgControl {
#if defined(_WIN32)
    CRITICAL_SECTION lock;
    CONDITION_VARIABLE cv;
#else
    pthread_mutex_t lock;
    pthread_cond_t cv;
#endif

    /* Volatile fast-path flags (read without the lock on the VM hot path). */
    volatile LONG active;            /* 1 while any debug feature can interrupt */
    volatile LONG interruptRequested;/* set by Pause() */
    volatile LONG exitRequested;     /* set by Stop()/Destroy() */
    volatile LONG paused;            /* runner is stopped */
    volatile LONG stepping;          /* step into/over/out in progress */
    volatile LONG stepMode;          /* BSDBG_STEP_* */
    volatile LONG stepSkipOnce;      /* skip the check for the next instruction */
    volatile LONG frameStepRequested;/* run exactly one frame, then stop */
    volatile LONG crashed;           /* 1 after an isolated native exception */
    volatile LONG stopOnEntryPending;/* break at first instruction */

    /* Stable under lock. */
    int stopReason;                  /* enum BsdbgStopReason */
    int stopLocation;                /* BSDBG_STOPLOC_* */
    uint32_t stopCodeIndex;
    uint32_t stopIp;
    /* Call frame depth at the stop: number of parent frames (top frame = 0).
     * Used by StepOver/StepOut on the VM tick. */
    int stopDepth;
    /* Breakpoint suppression so resuming does not immediately re-hit. */
    int suppressValid;
    uint32_t suppressCodeIndex;
    uint32_t suppressIp;

    BsdbgBreakpoint* breakpoints;
    int bpCount;
    int bpCapacity;

    BsdbgEventCallback eventCb;
    void* eventUser;
    BsdbgLogCallback logCb;
    void* logUser;

    /* Game state pointers, set by BsdbgHook_LoopStart, cleared on loop exit. */
    struct Runner* runner;
    struct VMContext* vm;
    struct DataWin* dataWin;

    volatile LONG running;           /* game thread alive */
    volatile LONG started;

#if defined(_WIN32)
    HANDLE thread;
#else
    pthread_t thread;
    int threadValid;
#endif

    BsdbgConfig config;              /* owned string copies */

    /* Pending hot reload (owned). Applied at a frame boundary. */
    char* pendingPatchJson;
    char* lastPatchError;
} BsdbgControl;

/* Global control pointer; null when no session exists. */
extern BsdbgControl* g_bsdbg;

/* ===[ session.c ]=== */
BsdbgControl* Bsdbg_Control(void);
void Bsdbg_Lock(BsdbgControl* c);
void Bsdbg_Unlock(BsdbgControl* c);
void Bsdbg_Wait(BsdbgControl* c);           /* waits on cv, lock held */
void Bsdbg_NotifyAll(BsdbgControl* c);
/* Emits an event to the registered callback (no lock held). json is UTF-8. */
void Bsdbg_EmitEvent(BsdbgControl* c, int32_t type, const char* json);
/* printf-style log forwarder (thread safe). */
void Bsdbg_Log(BsdbgControl* c, int level, const char* fmt, ...);
/* Updates `active` from the other volatile flags. Call with lock held. */
void Bsdbg_UpdateActive(BsdbgControl* c);
/* Shared stop path used by the VM tick and the frame boundary. Blocks the
 * calling (game) thread until resume/step/exit. */
void Bsdbg_DebugStopAndWait(BsdbgControl* c, int reason, int location,
                            uint32_t codeIndex, uint32_t ip);
/* Replaces the breakpoint list (copies). Thread safe. */
void Bsdbg_SetBreakpointsInternal(BsdbgControl* c, const BsdbgBreakpoint* list, int count);

/* ===[ debugger.c ]=== */
/* VM hot path entry (called from Butterscotch's interpreter loop). */
void Bsdbg_OnVmInstruction(struct VMContext* ctx, uint32_t ip);
/* JSON snapshot builders; return malloc'd UTF-8 or nullptr. Caller frees. */
char* Bsdbg_BuildStateJson(BsdbgControl* c);
char* Bsdbg_BuildCallStackJson(BsdbgControl* c);
char* Bsdbg_BuildInstancesJson(BsdbgControl* c);
char* Bsdbg_BuildGlobalsJson(BsdbgControl* c);
int32_t Bsdbg_SetInstanceVariableInternal(BsdbgControl* c, int32_t instanceId,
                                         const char* name, const char* valueJson,
                                         char** errorText);
int32_t Bsdbg_SetGlobalVariableInternal(BsdbgControl* c, const char* name,
                                        const char* valueJson, char** errorText);
int32_t Bsdbg_SetFrameVariableInternal(BsdbgControl* c, int32_t frameIndex,
                                       int32_t isArgument, int32_t slot,
                                       const char* valueJson, char** errorText);

/* ===[ hotreload.c ]=== */
/* Queues a patch (copies json). Returns 0/-1 with *errorText on validation errors. */
int32_t BsdbgHotreload_Queue(BsdbgControl* c, const char* json, char** errorText);
/* Called at frame boundaries when a patch is pending. Emits RELOADED/ERROR. */
void BsdbgHotreload_ApplyPending(BsdbgControl* c);
/* Releases storage owned by applied patches (names/strings). Call on session destroy. */
void BsdbgHotreload_FreeOwned(void);

#endif /* _BSDBG_INTERNAL_H_ */
