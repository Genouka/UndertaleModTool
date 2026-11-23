/*
 * bsdbg-host (POSIX) - out-of-process Butterscotch debugger host for Android,
 * Linux and macOS. Talks to UndertaleModTool over a Unix domain socket using
 * the same 16-byte framed protocol as the Windows host.
 */

#include <errno.h>
#include <dlfcn.h>
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <time.h>
#include <unistd.h>

#include "bsdbg.h"

#define HOST_PROTOCOL 1
#define MAX_FRAME (64u * 1024u * 1024u)
#define MAX_COMMAND_FRAME (1u * 1024u * 1024u)
#define LOG_LIMIT (16u * 1024u)

enum {
    MSG_HELLO = 0, MSG_REQUEST = 1, MSG_RESPONSE = 2, MSG_EVENT = 3
};
enum {
    OP_PING = 0, OP_START = 1, OP_SHUTDOWN = 2, OP_PAUSE = 3, OP_RESUME = 4,
    OP_STEP_INTO = 5, OP_STEP_OVER = 6, OP_STEP_OUT = 7, OP_STEP_FRAME = 8,
    OP_SET_BREAKPOINTS = 9, OP_CLEAR_BREAKPOINTS = 10, OP_GET_STATE = 11,
    OP_GET_CALL_STACK = 12, OP_GET_INSTANCES = 13, OP_GET_GLOBALS = 14,
    OP_GET_FRAME_COUNT = 15, OP_SET_INSTANCE_VARIABLE = 16,
    OP_SET_GLOBAL_VARIABLE = 17, OP_SET_FRAME_VARIABLE = 18,
    OP_APPLY_CODE_PATCH = 19, OP_INJECT = 20, OP_PUBLISH_SURFACE = 21,
    OP_WITHDRAW_SURFACE = 22
};
enum {
    HOST_EV_STATE = 100, HOST_EV_BREAK = 101, HOST_EV_EXIT = 102,
    HOST_EV_ERROR = 103, HOST_EV_RELOADED = 104, HOST_EV_LOG = 105
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

#define BSDBG_CALL
typedef BsdbgSession* (BSDBG_CALL* PFN_Create)(const BsdbgConfig*);
typedef void (BSDBG_CALL* PFN_Destroy)(BsdbgSession*);
typedef int32_t (BSDBG_CALL* PFN_Start)(BsdbgSession*);
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
typedef void (BSDBG_CALL* PFN_FreeString)(char*);
typedef int32_t (BSDBG_CALL* PFN_AbiVersion)(void);
typedef void (BSDBG_CALL* PFN_SetEventCallback)(BsdbgSession*, BsdbgEventCallback, void*);
typedef void (BSDBG_CALL* PFN_SetLogCallback)(BsdbgSession*, BsdbgLogCallback, void*);
typedef void (BSDBG_CALL* PFN_InjectKey)(int32_t, int32_t);
typedef void (BSDBG_CALL* PFN_InjectKeyText)(uint32_t);
typedef void (BSDBG_CALL* PFN_InjectMouseButton)(int32_t, int32_t);
typedef void (BSDBG_CALL* PFN_InjectMousePos)(double, double);
typedef void (BSDBG_CALL* PFN_InjectMouseWheel)(double);
typedef void (BSDBG_CALL* PFN_InjectGamepadButton)(int32_t, int32_t, float);
typedef void (BSDBG_CALL* PFN_InjectGamepadAxis)(int32_t, float);

typedef struct {
    void* library;
    PFN_Create Create;
    PFN_Destroy Destroy;
    PFN_Start Start;
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
    PFN_FreeString FreeString;
    PFN_AbiVersion AbiVersion;
    PFN_SetEventCallback SetEventCallback;
    PFN_SetLogCallback SetLogCallback;
    PFN_InjectKey InjectKey;
    PFN_InjectKeyText InjectKeyText;
    PFN_InjectMouseButton InjectMouseButton;
    PFN_InjectMousePos InjectMousePos;
    PFN_InjectMouseWheel InjectMouseWheel;
    PFN_InjectGamepadButton InjectGamepadButton;
    PFN_InjectGamepadAxis InjectGamepadAxis;
} Api;

static int g_socket = -1;
static volatile int g_broken;
static volatile int g_done;
static pthread_mutex_t g_queueLock = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t g_queueCv = PTHREAD_COND_INITIALIZER;
static OutFrame* g_queueHead;
static OutFrame* g_queueTail;
static int g_queueCount;
static Api g_api;
static BsdbgSession* g_session;
static char* g_dataPath;
static char* g_workDir;
static char* g_saveFolder;
static char* g_gameArgs[64];
static int g_gameArgCount;
static int g_renderer = BSDBG_RENDERER_MODERN_GL;
static int g_headless;
static int g_fullscreen;
static int g_stopOnEntry;
static int g_width;
static int g_height;
static double g_speed = 1.0;
static uint32_t g_seed;
static intptr_t g_embedWindow;
static int32_t g_embedKind;

static void queue_push(uint16_t type, uint16_t opcode, uint16_t status, uint32_t id,
                       const char* payload, uint32_t length) {
    OutFrame* frame;
    int critical = type == MSG_RESPONSE ||
                   (type == MSG_EVENT && (opcode == HOST_EV_BREAK || opcode == HOST_EV_EXIT ||
                                          opcode == HOST_EV_ERROR || opcode == HOST_EV_RELOADED));
    if (g_broken || length > MAX_FRAME)
        return;
    frame = (OutFrame*) calloc(1, sizeof(*frame));
    if (!frame)
        return;
    frame->type = type;
    frame->opcode = opcode;
    frame->status = status;
    frame->id = id;
    frame->length = length;
    if (length) {
        frame->payload = (char*) malloc(length);
        if (!frame->payload) { free(frame); return; }
        memcpy(frame->payload, payload, length);
    }
    pthread_mutex_lock(&g_queueLock);
    if (!critical && g_queueCount >= 1024) {
        pthread_mutex_unlock(&g_queueLock);
        free(frame->payload);
        free(frame);
        return;
    }
    if (g_queueTail) g_queueTail->next = frame; else g_queueHead = frame;
    g_queueTail = frame;
    g_queueCount++;
    pthread_cond_broadcast(&g_queueCv);
    pthread_mutex_unlock(&g_queueLock);
}

static int send_all(int fd, const void* data, uint32_t length) {
    const char* cursor = (const char*) data;
    while (length) {
        ssize_t sent = send(fd, cursor, length, MSG_NOSIGNAL);
        if (sent <= 0) return 0;
        cursor += sent;
        length -= (uint32_t) sent;
    }
    return 1;
}

static int recv_all(int fd, void* data, uint32_t length) {
    char* cursor = (char*) data;
    while (length) {
        ssize_t got = recv(fd, cursor, length, 0);
        if (got <= 0) return 0;
        cursor += got;
        length -= (uint32_t) got;
    }
    return 1;
}

static void* writer_thread(void* unused) {
    (void) unused;
    for (;;) {
        OutFrame* frame;
        FrameHeader header;
        int ok;
        pthread_mutex_lock(&g_queueLock);
        while (!g_queueHead && !g_done)
            pthread_cond_wait(&g_queueCv, &g_queueLock);
        if (!g_queueHead && g_done) { pthread_mutex_unlock(&g_queueLock); break; }
        frame = g_queueHead;
        g_queueHead = frame->next;
        if (!g_queueHead) g_queueTail = NULL;
        g_queueCount--;
        pthread_mutex_unlock(&g_queueLock);
        memset(&header, 0, sizeof(header));
        header.length = frame->length;
        header.version = HOST_PROTOCOL;
        header.type = frame->type;
        header.id = frame->id;
        header.opcode = frame->opcode;
        header.status = frame->status;
        ok = send_all(g_socket, &header, sizeof(header));
        if (ok && frame->length)
            ok = send_all(g_socket, frame->payload, frame->length);
        free(frame->payload);
        free(frame);
        if (!ok) { g_broken = 1; shutdown(g_socket, SHUT_RDWR); break; }
    }
    return NULL;
}

static int read_frame(FrameHeader* header, char** payload) {
    if (!recv_all(g_socket, header, sizeof(*header))) return 0;
    if (header->version != HOST_PROTOCOL || header->length > MAX_FRAME) return 0;
    *payload = NULL;
    if (header->length) {
        *payload = (char*) malloc((size_t) header->length + 1);
        if (!*payload) return 0;
        if (!recv_all(g_socket, *payload, header->length)) {
            free(*payload);
            *payload = NULL;
            return 0;
        }
        (*payload)[header->length] = '\0';
    }
    return 1;
}

static void send_response(uint32_t id, uint16_t opcode, uint16_t status,
                          const char* payload, uint32_t length) {
    queue_push(MSG_RESPONSE, opcode, status, id, payload, length);
}

static void send_error(uint32_t id, uint16_t opcode, const char* message) {
    send_response(id, opcode, 1, message, (uint32_t) strlen(message));
}

static void send_json(uint32_t id, uint16_t opcode, char* json) {
    if (!json) { send_error(id, opcode, "the debug session returned no data"); return; }
    send_response(id, opcode, 0, json, (uint32_t) strlen(json));
    g_api.FreeString(json);
}

static void event_callback(void* user, int32_t type, const char* json) {
    (void) user;
    if (!json || type < 0 || type > 4) return;
    queue_push(MSG_EVENT, (uint16_t) (HOST_EV_STATE + type), 0, 0, json, (uint32_t) strlen(json));
}

static void log_callback(void* user, int32_t level, const char* message) {
    char* payload;
    size_t length;
    (void) user;
    if (!message) return;
    length = strlen(message);
    if (length > LOG_LIMIT) length = LOG_LIMIT;
    payload = (char*) malloc(4 + length);
    if (!payload) return;
    memcpy(payload, &level, 4);
    memcpy(payload + 4, message, length);
    queue_push(MSG_EVENT, HOST_EV_LOG, 0, 0, payload, (uint32_t) (4 + length));
    free(payload);
}

static void stop_session(void) {
    if (g_session) {
        g_api.Destroy(g_session);
        g_session = NULL;
    }
}

static int start_session(char* error, size_t errorSize) {
    BsdbgConfig config;
    if (g_session) { snprintf(error, errorSize, "a session is already running"); return 0; }
    memset(&config, 0, sizeof(config));
    config.structSize = (uint32_t) sizeof(config);
    config.dataWinPath = g_dataPath;
    config.workingDirectory = g_workDir;
    config.saveFolder = g_saveFolder;
    config.renderer = g_renderer;
    config.headless = g_headless;
    config.fullscreen = g_fullscreen;
    config.windowWidth = g_width;
    config.windowHeight = g_height;
    config.speed = g_speed;
    config.seed = g_seed;
    config.stopOnEntry = g_stopOnEntry;
    config.gameArgs = (const char* const*) g_gameArgs;
    config.gameArgCount = g_gameArgCount;
    config.embedWindow = g_embedWindow;
    config.embedWindowKind = g_embedKind;
    g_session = g_api.Create(&config);
    if (!g_session) {
        snprintf(error, errorSize, "Bsdbg_Create failed");
        return 0;
    }
    g_api.SetEventCallback(g_session, event_callback, NULL);
    g_api.SetLogCallback(g_session, log_callback, NULL);
    if (g_api.Start(g_session) != 0) {
        snprintf(error, errorSize, "Bsdbg_Start failed");
        g_api.Destroy(g_session);
        g_session = NULL;
        return 0;
    }
    return 1;
}

typedef struct Reader { const unsigned char* cursor; const unsigned char* end; } Reader;
static int read_i32(Reader* r, int32_t* value) {
    if ((size_t) (r->end - r->cursor) < 4) return 0;
    memcpy(value, r->cursor, 4); r->cursor += 4; return 1;
}
static int read_u32(Reader* r, uint32_t* value) {
    if ((size_t) (r->end - r->cursor) < 4) return 0;
    memcpy(value, r->cursor, 4); r->cursor += 4; return 1;
}
static int read_u64(Reader* r, uint64_t* value) {
    if ((size_t) (r->end - r->cursor) < 8) return 0;
    memcpy(value, r->cursor, 8); r->cursor += 8; return 1;
}
static int read_double(Reader* r, double* value) { return read_u64(r, (uint64_t*) value); }
static int read_float(Reader* r, float* value) {
    uint32_t bits;
    if (!read_u32(r, &bits)) return 0;
    memcpy(value, &bits, 4); return 1;
}
static char* read_blob(Reader* r, uint32_t max, uint32_t* outLength) {
    uint32_t length;
    char* text;
    if (!read_u32(r, &length) || length > max) return NULL;
    if ((size_t) (r->end - r->cursor) < length) return NULL;
    text = (char*) malloc((size_t) length + 1);
    if (!text) return NULL;
    memcpy(text, r->cursor, length);
    text[length] = '\0';
    r->cursor += length;
    if (outLength) *outLength = length;
    return text;
}

static void handle_request(uint32_t id, uint16_t opcode, const char* payload, uint32_t length) {
    int32_t result;
    char buffer[128];
    Reader reader;
    char* error = NULL;
    char* json;

    if (opcode != OP_APPLY_CODE_PATCH && length > MAX_COMMAND_FRAME) {
        send_error(id, opcode, "command payload is too large");
        return;
    }

    switch (opcode) {
    case OP_PING:
        send_response(id, opcode, 0, "{\"pong\":true}", 13);
        break;
    case OP_START: {
        char message[256];
        if (start_session(message, sizeof(message))) {
            send_response(id, opcode, 0, NULL, 0);
        } else {
            send_error(id, opcode, message);
            snprintf(buffer, sizeof(buffer), "{\"message\":\"Failed to start Butterscotch: %s\"}", message);
            queue_push(MSG_EVENT, HOST_EV_ERROR, 0, 0, buffer, (uint32_t) strlen(buffer));
            queue_push(MSG_EVENT, HOST_EV_EXIT, 0, 0, "{\"code\":-1}", 11);
        }
        break;
    }
    case OP_SHUTDOWN:
        stop_session();
        send_response(id, opcode, 0, NULL, 0);
        g_done = 1;
        break;
    case OP_PAUSE: case OP_RESUME: case OP_STEP_INTO: case OP_STEP_OVER:
    case OP_STEP_OUT: case OP_STEP_FRAME: case OP_CLEAR_BREAKPOINTS:
        if (!g_session) { send_error(id, opcode, "the debug session is not running"); break; }
        switch (opcode) {
        case OP_PAUSE: result = g_api.Pause(g_session); break;
        case OP_RESUME: result = g_api.Resume(g_session); break;
        case OP_STEP_INTO: result = g_api.StepInto(g_session); break;
        case OP_STEP_OVER: result = g_api.StepOver(g_session); break;
        case OP_STEP_OUT: result = g_api.StepOut(g_session); break;
        case OP_STEP_FRAME: result = g_api.StepFrame(g_session); break;
        default: result = g_api.ClearBreakpoints(g_session); break;
        }
        if (result == 0) send_response(id, opcode, 0, NULL, 0);
        else send_error(id, opcode, "the debug session rejected the command");
        break;
    case OP_GET_STATE:
    case OP_GET_CALL_STACK:
    case OP_GET_INSTANCES:
    case OP_GET_GLOBALS:
        if (!g_session) { send_error(id, opcode, "the debug session is not running"); break; }
        switch (opcode) {
        case OP_GET_STATE: json = g_api.GetStateJson(g_session); break;
        case OP_GET_CALL_STACK: json = g_api.GetCallStackJson(g_session); break;
        case OP_GET_INSTANCES: json = g_api.GetInstancesJson(g_session); break;
        default: json = g_api.GetGlobalsJson(g_session); break;
        }
        send_json(id, opcode, json);
        break;
    case OP_GET_FRAME_COUNT:
        if (!g_session) { send_error(id, opcode, "the debug session is not running"); break; }
        snprintf(buffer, sizeof(buffer), "{\"frameCount\":%d}", (int) g_api.GetFrameCount(g_session));
        send_response(id, opcode, 0, buffer, (uint32_t) strlen(buffer));
        break;
    case OP_SET_BREAKPOINTS: {
        int32_t count;
        if (!g_session) { send_error(id, opcode, "the debug session is not running"); break; }
        if (length % 12 != 0) { send_error(id, opcode, "malformed breakpoint payload"); break; }
        count = (int32_t) (length / 12);
        if (g_api.SetBreakpoints(g_session, (const BsdbgBreakpoint*) payload, count) == 0)
            send_response(id, opcode, 0, NULL, 0);
        else
            send_error(id, opcode, "the runtime rejected the breakpoint list");
        break;
    }
    case OP_SET_INSTANCE_VARIABLE:
    case OP_SET_GLOBAL_VARIABLE:
    case OP_SET_FRAME_VARIABLE: {
        int32_t instanceId = 0, frameIndex = 0, isArgument = 0, slot = 0;
        char* first;
        char* second;
        if (!g_session) { send_error(id, opcode, "the debug session is not running"); break; }
        reader.cursor = (const unsigned char*) payload;
        reader.end = reader.cursor + length;
        if (opcode == OP_SET_INSTANCE_VARIABLE && !read_i32(&reader, &instanceId)) {
            send_error(id, opcode, "malformed variable payload"); break;
        }
        if (opcode == OP_SET_FRAME_VARIABLE &&
            (!read_i32(&reader, &frameIndex) || !read_i32(&reader, &isArgument) ||
             !read_i32(&reader, &slot))) {
            send_error(id, opcode, "malformed variable payload"); break;
        }
        first = read_blob(&reader, 4096, NULL);
        second = read_blob(&reader, MAX_COMMAND_FRAME, NULL);
        if (!first || !second) {
            free(first); free(second);
            send_error(id, opcode, "malformed variable payload");
            break;
        }
        if (opcode == OP_SET_INSTANCE_VARIABLE)
            result = g_api.SetInstanceVariable(g_session, instanceId, first, second, &error);
        else if (opcode == OP_SET_GLOBAL_VARIABLE)
            result = g_api.SetGlobalVariable(g_session, first, second, &error);
        else
            result = g_api.SetFrameVariable(g_session, frameIndex, isArgument, slot, first, &error);
        free(first);
        free(second);
        if (result == 0) send_response(id, opcode, 0, NULL, 0);
        else if (error) { send_error(id, opcode, error); g_api.FreeString(error); }
        else send_error(id, opcode, "the debug session rejected the value");
        break;
    }
    case OP_APPLY_CODE_PATCH: {
        char* patch;
        if (!g_session || !length) { send_error(id, opcode, "no session or empty patch"); break; }
        patch = (char*) malloc((size_t) length + 1);
        if (!patch) { send_error(id, opcode, "out of memory"); break; }
        memcpy(patch, payload, length);
        patch[length] = '\0';
        result = g_api.ApplyCodePatch(g_session, patch, &error);
        free(patch);
        if (result == 0) send_response(id, opcode, 0, NULL, 0);
        else if (error) { send_error(id, opcode, error); g_api.FreeString(error); }
        else send_error(id, opcode, "hot reload failed");
        break;
    }
    case OP_INJECT: {
        int32_t kind;
        int32_t a, b;
        uint32_t character;
        double x, y;
        float value;
        reader.cursor = (const unsigned char*) payload;
        reader.end = reader.cursor + length;
        if (!read_i32(&reader, &kind)) { send_error(id, opcode, "malformed injection payload"); break; }
        switch (kind) {
        case 0:
            if (read_i32(&reader, &a) && read_i32(&reader, &b) && g_api.InjectKey) g_api.InjectKey(a, b);
            else { send_error(id, opcode, "malformed key payload"); break; }
            break;
        case 1:
            if (read_u32(&reader, &character) && g_api.InjectKeyText) g_api.InjectKeyText(character);
            else { send_error(id, opcode, "malformed text payload"); break; }
            break;
        case 2:
            if (read_i32(&reader, &a) && read_i32(&reader, &b) && g_api.InjectMouseButton) g_api.InjectMouseButton(a, b);
            else { send_error(id, opcode, "malformed mouse button payload"); break; }
            break;
        case 3:
            if (read_double(&reader, &x) && read_double(&reader, &y) && g_api.InjectMousePos) g_api.InjectMousePos(x, y);
            else { send_error(id, opcode, "malformed mouse position payload"); break; }
            break;
        case 4:
            if (read_double(&reader, &x) && g_api.InjectMouseWheel) g_api.InjectMouseWheel(x);
            else { send_error(id, opcode, "malformed wheel payload"); break; }
            break;
        case 5:
            if (read_i32(&reader, &a) && read_i32(&reader, &b) && read_float(&reader, &value) && g_api.InjectGamepadButton)
                g_api.InjectGamepadButton(a, b, value);
            else { send_error(id, opcode, "malformed gamepad payload"); break; }
            break;
        case 6:
            if (read_i32(&reader, &a) && read_float(&reader, &value) && g_api.InjectGamepadAxis)
                g_api.InjectGamepadAxis(a, value);
            else { send_error(id, opcode, "malformed gamepad payload"); break; }
            break;
        default:
            send_error(id, opcode, "unknown injection kind");
            break;
        }
        send_response(id, opcode, 0, NULL, 0);
        break;
    }
    case OP_PUBLISH_SURFACE: {
        int32_t kind;
        uint64_t handle;
        if (g_session) { send_error(id, opcode, "publish the surface before starting"); break; }
        reader.cursor = (const unsigned char*) payload;
        reader.end = reader.cursor + length;
        if (!read_i32(&reader, &kind) || !read_u64(&reader, &handle) || !handle ||
            (kind != 1 && kind != 2 && kind != 3)) {
            send_error(id, opcode, "malformed surface payload"); break;
        }
        g_embedWindow = (intptr_t) handle;
        g_embedKind = kind;
        send_response(id, opcode, 0, NULL, 0);
        break;
    }
    case OP_WITHDRAW_SURFACE:
        if (g_session) { send_error(id, opcode, "cannot withdraw the surface while running"); break; }
        g_embedWindow = 0;
        g_embedKind = BSDBG_EMBED_NONE;
        send_response(id, opcode, 0, NULL, 0);
        break;
    default:
        send_error(id, opcode, "unknown command");
        break;
    }
}

static void* resolve(void* library, const char* name) {
    void* address = dlsym(library, name);
    if (!address) fprintf(stderr, "bsdbg-host: missing export %s\n", name);
    return address;
}

static int load_api(const char* explicitPath) {
    void* library = explicitPath ? dlopen(explicitPath, RTLD_NOW) : dlopen("libbsdbg.so", RTLD_NOW);
    if (!library) {
        fprintf(stderr, "bsdbg-host: dlopen(libbsdbg.so) failed: %s\n", dlerror());
        return 0;
    }
    memset(&g_api, 0, sizeof(g_api));
    g_api.library = library;
    g_api.Create = (PFN_Create) resolve(library, "Bsdbg_Create");
    g_api.Destroy = (PFN_Destroy) resolve(library, "Bsdbg_Destroy");
    g_api.Start = (PFN_Start) resolve(library, "Bsdbg_Start");
    g_api.Pause = (PFN_Pause) resolve(library, "Bsdbg_Pause");
    g_api.Resume = (PFN_Resume) resolve(library, "Bsdbg_Resume");
    g_api.StepFrame = (PFN_StepFrame) resolve(library, "Bsdbg_StepFrame");
    g_api.StepInto = (PFN_StepInto) resolve(library, "Bsdbg_StepInto");
    g_api.StepOver = (PFN_StepOver) resolve(library, "Bsdbg_StepOver");
    g_api.StepOut = (PFN_StepOut) resolve(library, "Bsdbg_StepOut");
    g_api.GetFrameCount = (PFN_GetFrameCount) resolve(library, "Bsdbg_GetFrameCount");
    g_api.SetBreakpoints = (PFN_SetBreakpoints) resolve(library, "Bsdbg_SetBreakpoints");
    g_api.ClearBreakpoints = (PFN_ClearBreakpoints) resolve(library, "Bsdbg_ClearBreakpoints");
    g_api.GetStateJson = (PFN_GetJson) resolve(library, "Bsdbg_GetStateJson");
    g_api.GetCallStackJson = (PFN_GetJson) resolve(library, "Bsdbg_GetCallStackJson");
    g_api.GetInstancesJson = (PFN_GetJson) resolve(library, "Bsdbg_GetInstancesJson");
    g_api.GetGlobalsJson = (PFN_GetJson) resolve(library, "Bsdbg_GetGlobalsJson");
    g_api.SetInstanceVariable = (PFN_SetInstanceVariable) resolve(library, "Bsdbg_SetInstanceVariable");
    g_api.SetGlobalVariable = (PFN_SetGlobalVariable) resolve(library, "Bsdbg_SetGlobalVariable");
    g_api.SetFrameVariable = (PFN_SetFrameVariable) resolve(library, "Bsdbg_SetFrameVariable");
    g_api.ApplyCodePatch = (PFN_ApplyCodePatch) resolve(library, "Bsdbg_ApplyCodePatch");
    g_api.FreeString = (PFN_FreeString) resolve(library, "Bsdbg_FreeString");
    g_api.AbiVersion = (PFN_AbiVersion) resolve(library, "Bsdbg_AbiVersion");
    g_api.SetEventCallback = (PFN_SetEventCallback) resolve(library, "Bsdbg_SetEventCallback");
    g_api.SetLogCallback = (PFN_SetLogCallback) resolve(library, "Bsdbg_SetLogCallback");
    g_api.InjectKey = (PFN_InjectKey) resolve(library, "Bsdbg_InjectKey");
    g_api.InjectKeyText = (PFN_InjectKeyText) resolve(library, "Bsdbg_InjectKeyText");
    g_api.InjectMouseButton = (PFN_InjectMouseButton) resolve(library, "Bsdbg_InjectMouseButton");
    g_api.InjectMousePos = (PFN_InjectMousePos) resolve(library, "Bsdbg_InjectMousePos");
    g_api.InjectMouseWheel = (PFN_InjectMouseWheel) resolve(library, "Bsdbg_InjectMouseWheel");
    g_api.InjectGamepadButton = (PFN_InjectGamepadButton) resolve(library, "Bsdbg_InjectGamepadButton");
    g_api.InjectGamepadAxis = (PFN_InjectGamepadAxis) resolve(library, "Bsdbg_InjectGamepadAxis");
    if (!g_api.Create || !g_api.Destroy || !g_api.Start || !g_api.FreeString || !g_api.AbiVersion)
        return 0;
    return 1;
}

static int connect_socket(const char* path) {
    struct sockaddr_un address;
    int fd;
    int attempts;
    memset(&address, 0, sizeof(address));
    address.sun_family = AF_UNIX;
    strncpy(address.sun_path, path, sizeof(address.sun_path) - 1);
    for (attempts = 0; attempts < 100; attempts++) {
        fd = socket(AF_UNIX, SOCK_STREAM, 0);
        if (fd >= 0 && connect(fd, (struct sockaddr*) &address, sizeof(address)) == 0)
            return fd;
        if (fd >= 0) close(fd);
        usleep(50000);
    }
    fprintf(stderr, "bsdbg-host: cannot connect to %s\n", path);
    return -1;
}

static void send_hello(void) {
    char hello[512];
    int abi = g_api.AbiVersion ? g_api.AbiVersion() : 0;
#if defined(__aarch64__) || defined(_M_ARM64)
    const char* arch = "arm64";
#elif defined(__x86_64__) || defined(_M_X64)
    const char* arch = "x64";
#elif defined(__i386__) || defined(_M_IX86)
    const char* arch = "x86";
#else
    const char* arch = sizeof(void*) == 8 ? "x64" : "x86";
#endif
    snprintf(hello, sizeof(hello),
             "{\"protocol\":%d,\"abi\":%d,\"arch\":\"%s\",\"pid\":%d,\"build\":\"posix\"}",
             HOST_PROTOCOL, abi, arch, (int) getpid());
    queue_push(MSG_HELLO, 0, 0, 0, hello, (uint32_t) strlen(hello));
}

static int parse_int(const char* value, int fallback) {
    char* end = NULL;
    long parsed = strtol(value, &end, 10);
    if (end == value || (end && *end)) return fallback;
    return (int) parsed;
}

int main(int argc, char** argv) {
    const char* socketPath = NULL;
    const char* dll = NULL;
    FrameHeader header;
    char* payload;
    pthread_t writer;
    int running = 1;

    for (int i = 1; i < argc; i++) {
        if (!strcmp(argv[i], "--socket") && i + 1 < argc) socketPath = argv[++i];
        else if (!strcmp(argv[i], "--dll") && i + 1 < argc) dll = argv[++i];
        else if (!strcmp(argv[i], "--data") && i + 1 < argc) g_dataPath = argv[++i];
        else if (!strcmp(argv[i], "--workdir") && i + 1 < argc) g_workDir = argv[++i];
        else if (!strcmp(argv[i], "--save") && i + 1 < argc) g_saveFolder = argv[++i];
        else if (!strcmp(argv[i], "--renderer") && i + 1 < argc) g_renderer = parse_int(argv[++i], BSDBG_RENDERER_MODERN_GL);
        else if (!strcmp(argv[i], "--width") && i + 1 < argc) g_width = parse_int(argv[++i], 0);
        else if (!strcmp(argv[i], "--height") && i + 1 < argc) g_height = parse_int(argv[++i], 0);
        else if (!strcmp(argv[i], "--headless")) g_headless = 1;
        else if (!strcmp(argv[i], "--fullscreen")) g_fullscreen = 1;
        else if (!strcmp(argv[i], "--stop-on-entry")) g_stopOnEntry = 1;
        else if (!strcmp(argv[i], "--speed") && i + 1 < argc) g_speed = atof(argv[++i]);
        else if (!strcmp(argv[i], "--seed") && i + 1 < argc) g_seed = (uint32_t) strtoul(argv[++i], NULL, 10);
        else if (!strcmp(argv[i], "--game-arg") && i + 1 < argc && g_gameArgCount < 64)
            g_gameArgs[g_gameArgCount++] = argv[++i];
        else if (!strcmp(argv[i], "--dump-dir") && i + 1 < argc) { }
        else fprintf(stderr, "bsdbg-host: unknown argument %s\n", argv[i]);
    }
    if (!socketPath || !g_dataPath) {
        fprintf(stderr, "bsdbg-host: --socket and --data are required\n");
        return 2;
    }
    if (g_speed <= 0.0) g_speed = 1.0;
    if (!load_api(dll)) return 2;

    g_socket = connect_socket(socketPath);
    if (g_socket < 0) return 2;
    pthread_create(&writer, NULL, writer_thread, NULL);
    send_hello();

    while (running && !g_broken) {
        if (!read_frame(&header, &payload)) break;
        if (header.type == MSG_REQUEST) handle_request(header.id, header.opcode, payload, header.length);
        free(payload);
        if (g_done) running = 0;
    }
    stop_session();
    g_done = 1;
    pthread_cond_broadcast(&g_queueCv);
    pthread_join(writer, NULL);
    close(g_socket);
    if (g_api.library) dlclose(g_api.library);
    return 0;
}
