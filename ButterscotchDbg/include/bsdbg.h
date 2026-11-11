#ifndef _BSDBG_H_
#define _BSDBG_H_

/*
 * ButterscotchDbg - embedded Butterscotch runner with a GML debugger API.
 *
 * Flat C ABI, one game per session. The game loop runs on a dedicated thread
 * owned by the session. All functions are thread safe unless noted otherwise.
 *
 * Every event/log callback receives a NUL-terminated UTF-8 string that is
 * owned by the native side and only valid for the duration of the callback.
 * Do not retain or free it (only strings returned by the Bsdbg_Get*Json
 * functions are caller-owned and must be freed with Bsdbg_FreeString).
 * Callbacks are invoked on the game thread; handlers must never call back
 * into the session in a blocking way.
 */

#include <stdint.h>
#include <stddef.h>

#if defined(_WIN32)
#  ifdef BSDBG_BUILD
#    define BSDBG_API __declspec(dllexport)
#  else
#    define BSDBG_API __declspec(dllimport)
#  endif
#else
#  define BSDBG_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct BsdbgSession BsdbgSession;

/* Renderer backends, mirrors Butterscotch enum GraphicsAPI. */
enum BsdbgRenderer {
    BSDBG_RENDERER_SOFTWARE  = 0,
    BSDBG_RENDERER_MODERN_GL = 1,
    BSDBG_RENDERER_LEGACY_GL = 2,
    BSDBG_RENDERER_NOOP      = 3,
};

/* Why the runner stopped. Reported via "reason" in event/state JSON. */
enum BsdbgStopReason {
    BSDBG_STOP_PAUSE       = 0, /* external pause request */
    BSDBG_STOP_BREAKPOINT  = 1,
    BSDBG_STOP_STEP        = 2, /* step into / over / out / frame completed */
    BSDBG_STOP_ENTRY       = 3, /* stop at first instruction of the game */
};

/* Event callback types. */
enum BsdbgEvent {
    BSDBG_EVENT_STATE    = 0, /* runner state change; payload = state JSON */
    BSDBG_EVENT_BREAK    = 1, /* hit a breakpoint/step/pause; payload = state JSON */
    BSDBG_EVENT_EXIT     = 2, /* game loop ended; payload = {"code":<int>} */
    BSDBG_EVENT_ERROR    = 3, /* fatal runner error; payload = {"message":"..."} */
    BSDBG_EVENT_RELOADED = 4, /* code patch applied; payload = {"applied":n,"skipped":n} */
    BSDBG_EVENT_LOG      = 5, /* only when log callback is null; payload = {"level":"info|warn|error","message":"..."} */
};

typedef void (*BsdbgEventCallback)(void* user, int32_t eventType, const char* json);
typedef void (*BsdbgLogCallback)(void* user, int32_t level, const char* message);
/* level: 0=debug 1=info 2=warn 3=error (mirrors Butterscotch log levels). */

typedef struct BsdbgConfig {
    uint32_t structSize;        /* must be sizeof(BsdbgConfig) */
    const char* dataWinPath;    /* required */
    const char* workingDirectory; /* null = directory of dataWinPath */
    const char* saveFolder;     /* null = default */
    int32_t windowWidth;        /* 0 = auto (gen8 default) */
    int32_t windowHeight;       /* 0 = auto */
    int32_t renderer;           /* enum BsdbgRenderer */
    int32_t headless;           /* 1 = no window (for tests) */
    int32_t fullscreen;
    double speed;               /* 1.0 = normal, 0 = use room speed */
    uint32_t seed;              /* 0 = random */
    const char* const* gameArgs;/* extra args passed to the game (after "-game <path>"); may be null */
    int32_t gameArgCount;
    int32_t stopOnEntry;        /* 1 = break at first executed instruction */
    intptr_t embedWindow;       /* window to render into; 0 = create a top-level window */
    int32_t embedWindowKind;    /* enum BsdbgEmbedWindowKind, see below */
} BsdbgConfig;

/* How embedWindow is interpreted. Single-window hosts (Android) pass the surface
 * handle they got from their native view; the runner then never creates a window. */
enum BsdbgEmbedWindowKind {
    BSDBG_EMBED_NONE            = 0,
    BSDBG_EMBED_WIN32_HWND      = 1,
    BSDBG_EMBED_ANDROID_SURFACE = 2, /* embedWindow is an android.view.Surface jobject */
};

typedef struct BsdbgBreakpoint {
    int32_t codeIndex;   /* index into the CODE chunk (matches UndertaleModTool's Code list index) */
    uint32_t offset;     /* bytecode offset, relative to the start of the code entry, in bytes */
    int32_t enabled;     /* 0 = registered but disabled */
} BsdbgBreakpoint;

/* ===[ Session lifecycle ]=== */

BSDBG_API BsdbgSession* Bsdbg_Create(const BsdbgConfig* config);
/* Requests exit and joins the game thread. Blocks until stopped. */
BSDBG_API void Bsdbg_Destroy(BsdbgSession* session);

/* Returns 0 on success, -1 on failure (see log / error event). */
BSDBG_API int32_t Bsdbg_Start(BsdbgSession* session);

/* ===[ Runner control ]=== */

BSDBG_API int32_t Bsdbg_IsRunning(BsdbgSession* session);
/* 1 when the runner is paused (at a breakpoint, stepping or user pause). */
BSDBG_API int32_t Bsdbg_IsPaused(BsdbgSession* session);
BSDBG_API int32_t Bsdbg_Pause(BsdbgSession* session);
BSDBG_API int32_t Bsdbg_Resume(BsdbgSession* session);
/* Advance exactly one full frame, then pause again. */
BSDBG_API int32_t Bsdbg_StepFrame(BsdbgSession* session);
/* Only valid while paused: execute the next GML instruction. */
BSDBG_API int32_t Bsdbg_StepInto(BsdbgSession* session);
/* Only valid while paused: run until the current call frame advances by one
 * instruction or returns (function calls are not entered). */
BSDBG_API int32_t Bsdbg_StepOver(BsdbgSession* session);
/* Only valid while paused: run until the current call frame returns. */
BSDBG_API int32_t Bsdbg_StepOut(BsdbgSession* session);
/* Reads the current frame count. */
BSDBG_API int32_t Bsdbg_GetFrameCount(BsdbgSession* session);

/* ===[ Breakpoints ]=== */
/* Replaces the full breakpoint list. May be called while running or paused. */
BSDBG_API int32_t Bsdbg_SetBreakpoints(BsdbgSession* session,
                                       const BsdbgBreakpoint* breakpoints,
                                       int32_t count);
BSDBG_API int32_t Bsdbg_ClearBreakpoints(BsdbgSession* session);

/* ===[ Inspection (valid while paused; while running returns last snapshot) ]=== */
/*
 * All returned strings are heap allocated UTF-8 JSON; free with Bsdbg_FreeString.
 *
 * State JSON (Bsdbg_GetStateJson):
 * {
 *   "running": true, "paused": false, "frameCount": 1234,
 *   "roomIndex": 3, "roomName": "room_ruins1",
 *   "stop": {                     // only present when paused
 *     "reason": "breakpoint|step|pause|entry",
 *     "frame": {                  // current topmost call frame
 *       "codeIndex": 12, "codeName": "gml_Script_foo", "ip": 40,
 *       "selfId": 100001, "selfObjectIndex": 5,
 *       "eventType": 3, "eventSubtype": 0, "eventObjectIndex": 5
 *     }
 *   }
 * }
 *
 * Call stack JSON (Bsdbg_GetCallStackJson):
 * {"frames":[
 *   {"codeIndex":12,"codeName":"gml_Script_foo","ip":40,
 *    "selfId":100001,"selfObjectIndex":5,
 *    "eventType":3,"eventSubtype":0,"eventObjectIndex":5,
 *    "locals":[{"slot":0,"varId":7,"name":"i","value":<value>}],
 *    "args":[{"slot":0,"value":<value>}]
 *   }, ...]}
 *
 * Instances JSON (Bsdbg_GetInstancesJson):
 * {"instances":[
 *   {"id":100001,"objectIndex":5,"objectName":"obj_player","x":1.0,"y":2.0,
 *    "speed":0.0,"direction":0.0,"hspeed":0.0,"vspeed":0.0,"depth":0,
 *    "spriteIndex":3,"imageIndex":0,"imageSpeed":1.0,"visible":true,"active":true,
 *    "persistent":false,"solid":false,
 *    "variables":[{"varId":7,"name":"hp","value":<value>}]
 *   }, ...]}
 *
 * Value JSON: {"type":"real|int32|int64|bool|string|undefined|array|struct|method|assetref",
 *              "value":<scalar or string>,       // omitted for undefined
 *              "items":[<value>,...],            // arrays and structs
 *              "assetType":"object|sprite|...",  // assetref only
 *              "id":100001}                      // instance refs
 */
BSDBG_API char* Bsdbg_GetStateJson(BsdbgSession* session);
BSDBG_API char* Bsdbg_GetCallStackJson(BsdbgSession* session);
BSDBG_API char* Bsdbg_GetInstancesJson(BsdbgSession* session);
BSDBG_API char* Bsdbg_GetGlobalsJson(BsdbgSession* session);
BSDBG_API void Bsdbg_FreeString(char* str);

/* Sets one instance variable by instance id and name. Value is a single value
 * JSON object (as documented above). Returns 0 on success. */
BSDBG_API int32_t Bsdbg_SetInstanceVariable(BsdbgSession* session, int32_t instanceId,
                                            const char* name, const char* valueJson,
                                            char** errorText);

/* Sets one global variable by name. Value is a single value JSON object.
 * Returns 0 on success. */
BSDBG_API int32_t Bsdbg_SetGlobalVariable(BsdbgSession* session, const char* name,
                                          const char* valueJson, char** errorText);

/* Sets a local variable or script argument in the current call stack.
 * frameIndex 0 is the topmost frame; isArgument != 0 selects scriptArgs.
 * slot is the slot reported by Bsdbg_GetCallStackJson. */
BSDBG_API int32_t Bsdbg_SetFrameVariable(BsdbgSession* session, int32_t frameIndex,
                                         int32_t isArgument, int32_t slot,
                                         const char* valueJson, char** errorText);

/* ===[ Hot reload ]=== */
/*
 * Applies a code patch produced by UndertaleModTool (see UndertaleModRunner.CodePatchBuilder).
 * JSON schema:
 * {
 *   "codes":     [{"index":123,"name":"gml_Script_foo","localsCount":2,
 *                  "argumentsCount":0,"bytecode":"<base64>"}],
 *   "strings":   [{"index":456,"value":"text"}],
 *   "variables": [{"index":789,"name":"foo","instanceType":1,"varId":-1}],
 *   "functions": [{"index":42,"name":"bar"}]
 * }
 * "codes" entries with index < current count replace the entry's bytecode;
 * indices >= count append new entries (name required). Strings/variables/
 * functions are appended and their ids must not collide with existing ones.
 * Applied at the next frame boundary (or immediately while paused).
 * Returns 0 on success; on failure *errorText receives a malloc'd message.
 */
BSDBG_API int32_t Bsdbg_ApplyCodePatch(BsdbgSession* session, const char* json,
                                       char** errorText);

/* ===[ Callbacks ]=== */
BSDBG_API void Bsdbg_SetEventCallback(BsdbgSession* session, BsdbgEventCallback callback, void* user);
BSDBG_API void Bsdbg_SetLogCallback(BsdbgSession* session, BsdbgLogCallback callback, void* user);

BSDBG_API void Bsdbg_FreeString(char* str);

/* Returns the ABI version of the built library (currently 1). */
BSDBG_API int32_t Bsdbg_AbiVersion(void);

#ifdef __cplusplus
}
#endif

#endif /* _BSDBG_H_ */
