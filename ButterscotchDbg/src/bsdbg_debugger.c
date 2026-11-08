/*
 * ButterscotchDbg - VM tick, breakpoints, stepping and inspection JSON.
 *
 * The VM tick runs on the game (runner) thread. Its fast path must stay as
 * small as possible: one global pointer load and one volatile flag read. The
 * slow path evaluates the breakpoint/step/interrupt conditions and parks the
 * game thread itself (the frame-boundary path uses Bsdbg_DebugStopAndWait);
 * the BREAK event carries the state JSON.
 *
 * Every JSON builder below acquires the session lock itself (the public
 * Bsdbg_Get*Json entry points call straight into these). While the runner is
 * not paused no VM structure is walked, because the game thread may be
 * mutating it concurrently.
 */

/* MSVC's CRT does not ship <strings.h>. Butterscotch's string_compat.h includes
 * it unless NO_STRINGS_H is defined (the regular CMake builds pass that on
 * Windows via compat/configure.sh). Define it for this translation unit so the
 * file also compiles standalone with cl.exe. */
#if defined(_MSC_VER) && !defined(NO_STRINGS_H)
#  define NO_STRINGS_H
#endif

#include "bsdbg_internal.h"

#include "wad_versions.h"
#include "json_writer.h"
#include "json_reader.h"
#include "utils.h"
#include "stb_ds.h"

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <ctype.h>

/* Absolute nesting limit for value JSON (arrays/structs). */
#define DBG_VALUE_MAX_DEPTH 3
/* Maximum number of array elements / struct fields emitted per value. */
#define DBG_VALUE_MAX_ITEMS 200
/* Maximum number of self variables emitted per instance or global scope. */
#define DBG_MAX_VARIABLES 500
/* Sanity cap for walking a (possibly corrupt) call-frame chain. */
#define DBG_MAX_CALL_DEPTH 4096
/* Largest integer exactly representable in a JSON number. */
#define DBG_MAX_SAFE_INTEGER 9007199254740991LL

/* ===[ Small helpers ]=== */

static int dbgAsciiLower(int c) {
    if (c >= 'A' && c <= 'Z')
        return c - 'A' + 'a';
    return c;
}

static bool dbgNamesEqualNoCase(const char* a, const char* b) {
    if (a == NULL || b == NULL)
        return false;
    while (*a != '\0' && *b != '\0') {
        if (dbgAsciiLower((unsigned char) *a) != dbgAsciiLower((unsigned char) *b))
            return false;
        a++;
        b++;
    }
    return *a == '\0' && *b == '\0';
}

/* Depth of the current call frame: number of parent frames, so the topmost
 * frame is 0. Matches Bsdbg_DebugStopAndWait's accounting. */
static int dbgFrameDepth(VMContext* ctx) {
    CallFrame* frame;
    int depth = 0;

    if (ctx == NULL || ctx->currentCallFrame == NULL)
        return 0;

    frame = ctx->currentCallFrame->parent;
    while (frame != NULL && DBG_MAX_CALL_DEPTH > depth) {
        depth++;
        frame = frame->parent;
    }
    return depth;
}

static const char* dbgCodeName(DataWin* dataWin, uint32_t codeIndex) {
    if (dataWin == NULL || dataWin->code.entries == NULL || codeIndex >= dataWin->code.count)
        return NULL;
    return dataWin->code.entries[codeIndex].name;
}

/* Resolves a self/global variable name. Falls back to scanning the VARI chunk
 * with local variables excluded (same rule VM_getVariableNameByVarId uses). */
static const char* dbgVarName(VMContext* ctx, int32_t varId) {
    const char* name;

    if (ctx == NULL || ctx->dataWin == NULL)
        return NULL;

    name = VM_getVariableNameByVarId(ctx, varId);
    if (name != NULL)
        return name;

    {
        uint32_t i;
        Variable* variables = ctx->dataWin->vari.variables;

        if (variables == NULL)
            return NULL;
        for (i = 0; i < ctx->dataWin->vari.variableCount; i++) {
            if (variables[i].varID == varId && variables[i].instanceType != INSTANCE_LOCAL)
                return variables[i].name;
        }
    }
    return NULL;
}

/* True when the two code names refer to the same code entry. The FUNC/CodeLocals
 * chunks sometimes spell script code names without the gml_Script_ prefix (or
 * with the gml_GlobalScript_ one). */
static bool dbgCodeNamesMatch(const char* a, const char* b) {
    static const char* const prefixes[] = { "gml_Script_", "gml_GlobalScript_" };
    size_t p;

    if (a == NULL || b == NULL)
        return false;
    if (strcmp(a, b) == 0)
        return true;

    for (p = 0; p < sizeof(prefixes) / sizeof(prefixes[0]); p++) {
        size_t length = strlen(prefixes[p]);

        if (strncmp(a, prefixes[p], length) == 0 && strcmp(a + length, b) == 0)
            return true;
        if (strncmp(b, prefixes[p], length) == 0 && strcmp(b + length, a) == 0)
            return true;
    }
    return false;
}

/* Maps a local variable id to its name using the FUNC/CodeLocals chunk entry
 * that belongs to the code entry being inspected. Returns NULL when unknown. */
static const char* dbgLocalName(DataWin* dataWin, const char* codeName, int32_t varId) {
    uint32_t i, j;

    if (dataWin == NULL || codeName == NULL || varId < 0 || dataWin->func.codeLocals == NULL)
        return NULL;

    for (i = 0; i < dataWin->func.codeLocalsCount; i++) {
        CodeLocals* codeLocals = &dataWin->func.codeLocals[i];

        if (!dbgCodeNamesMatch(codeLocals->name, codeName))
            continue;
        if (codeLocals->locals == NULL)
            continue;

        for (j = 0; j < codeLocals->localVarCount; j++) {
            if ((int32_t) codeLocals->locals[j].varID == varId)
                return codeLocals->locals[j].name;
        }
    }
    return NULL;
}

static int32_t dbgFindVarIdByName(VMContext* ctx, const char* name) {
    ptrdiff_t slot;
    uint32_t i;

    if (ctx == NULL || name == NULL)
        return -1;

    slot = shgeti(ctx->varNameMap, (char*) name);
    if (slot >= 0)
        return ctx->varNameMap[slot].value;

    if (ctx->dataWin != NULL && ctx->dataWin->vari.variables != NULL) {
        for (i = 0; i < ctx->dataWin->vari.variableCount; i++) {
            Variable* variable = &ctx->dataWin->vari.variables[i];

            if (variable->varID < 0 || variable->instanceType == INSTANCE_LOCAL)
                continue;
            if (variable->name != NULL && strcmp(variable->name, name) == 0)
                return variable->varID;
        }
    }
    return -1;
}

static const char* dbgAssetTypeName(uint8_t assetType) {
    switch (assetType) {
        case ASSET_TYPE_OBJECT:                   return "object";
        case ASSET_TYPE_SPRITE:                   return "sprite";
        case ASSET_TYPE_SOUND:                    return "sound";
        case ASSET_TYPE_ROOM:                     return "room";
        case ASSET_TYPE_PATH:                     return "path";
        case ASSET_TYPE_SCRIPT:                   return "script";
        case ASSET_TYPE_FONT:                     return "font";
        case ASSET_TYPE_TIMELINE:                 return "timeline";
        case ASSET_TYPE_SHADER:                   return "shader";
        case ASSET_TYPE_SEQUENCE:                 return "sequence";
        case ASSET_TYPE_ANIMCURVE:                return "animcurve";
        case ASSET_TYPE_PARTICLESYSTEM:           return "particlesystem";
        case ASSET_TYPE_TILEMAP:                  return "tilemap";
        case ASSET_TYPE_TILESET:                  return "tileset";
        case ASSET_TYPE_INSTANCE:                 return "instance";
        case ASSET_TYPE_PARTICLESYSTEMINSTANCE:   return "particlesysteminstance";
        default:                                  return "unknown";
    }
}

static const char* dbgStopReasonName(int reason) {
    switch (reason) {
        case BSDBG_STOP_BREAKPOINT: return "breakpoint";
        case BSDBG_STOP_STEP:       return "step";
        case BSDBG_STOP_ENTRY:      return "entry";
        default:                    return "pause";
    }
}

static void dbgSetError(char** errorText, const char* format, ...) {
    char buffer[512];
    va_list args;

    if (errorText == NULL)
        return;

    va_start(args, format);
    vsnprintf(buffer, sizeof(buffer), format, args);
    va_end(args);
    buffer[sizeof(buffer) - 1] = '\0';

    *errorText = safeStrdup(buffer);
}

/* ===[ Value JSON ]=== */

static void dbgWriteValue(JsonWriter* writer, VMContext* ctx, RValue value, int depth);

static void dbgWriteArrayItems(JsonWriter* writer, VMContext* ctx, GMLArray* array, int depth) {
    int emitted = 0;
    bool truncated = false;

    JsonWriter_beginArray(writer);

    if (array != NULL && array->type == GML_LEGACY_ARRAY) {
        int32_t row;

        for (row = 0; row < array->legacy.rowCount && !truncated; row++) {
            GMLArrayRow* current = &array->legacy.rows[row];
            int32_t column;

            for (column = 0; column < current->length; column++) {
                RValue item;

                if (DBG_VALUE_MAX_ITEMS <= emitted) {
                    truncated = true;
                    break;
                }
                item = current->data[column];
                item.ownsReference = false;
                dbgWriteValue(writer, ctx, item, depth + 1);
                emitted++;
            }
        }
    } else if (array != NULL) {
        int32_t index;

        for (index = 0; index < array->modern.length; index++) {
            RValue item;

            if (DBG_VALUE_MAX_ITEMS <= emitted) {
                truncated = true;
                break;
            }
            item = array->modern.data[index];
            item.ownsReference = false;
            dbgWriteValue(writer, ctx, item, depth + 1);
            emitted++;
        }
    }

    if (truncated)
        JsonWriter_rawValue(writer, "{\"type\":\"undefined\",\"truncated\":true}");

    JsonWriter_endArray(writer);
}

static bool dbgWriteStructItems(JsonWriter* writer, VMContext* ctx, Instance* structInstance, int depth) {
    IntRValueHashMap* map;
    uint32_t i;
    int emitted = 0;
    bool truncated = false;

    JsonWriter_beginObject(writer);

    map = structInstance != NULL ? &structInstance->selfVars : NULL;
    if (map != NULL && map->entries != NULL) {
        for (i = 0; i < map->capacity; i++) {
            IntRValueEntry* entry = &map->entries[i];
            const char* name;
            char fallback[32];
            RValue field;

            if (entry->key == INT_RVALUE_HASHMAP_EMPTY_KEY)
                continue;
            if (DBG_VALUE_MAX_ITEMS <= emitted) {
                truncated = true;
                break;
            }

            name = dbgVarName(ctx, entry->key);
            if (name == NULL) {
                snprintf(fallback, sizeof(fallback), "var_%d", entry->key);
                name = fallback;
            }

            field = entry->value;
            field.ownsReference = false;

            JsonWriter_key(writer, name);
            dbgWriteValue(writer, ctx, field, depth + 1);
            emitted++;
        }
    }

    JsonWriter_endObject(writer);

    return truncated;
}

static void dbgWriteValue(JsonWriter* writer, VMContext* ctx, RValue value, int depth) {
    JsonWriter_beginObject(writer);

    switch (value.type) {
        case RVALUE_UNDEFINED:
            JsonWriter_propertyString(writer, "type", "undefined");
            break;

        case RVALUE_REAL: {
            double real = (double) value.real;

            JsonWriter_propertyString(writer, "type", "real");
            if (isnan(real))
                JsonWriter_propertyString(writer, "value", "NaN");
            else if (isinf(real))
                JsonWriter_propertyString(writer, "value", real < 0.0 ? "-Infinity" : "Infinity");
            else
                JsonWriter_propertyDouble(writer, "value", real);
            break;
        }

        case RVALUE_INT32:
            JsonWriter_propertyString(writer, "type", "int32");
            JsonWriter_propertyInt(writer, "value", value.int32);
            /* Instance values are int32 RValues tagged as ASSET_TYPE_INSTANCE. */
            if (value.assetRefType == ASSET_TYPE_INSTANCE)
                JsonWriter_propertyInt(writer, "id", value.int32);
            break;

#ifndef NO_RVALUE_INT64
        case RVALUE_INT64:
            JsonWriter_propertyString(writer, "type", "int64");
            if (value.int64 > DBG_MAX_SAFE_INTEGER || value.int64 < -DBG_MAX_SAFE_INTEGER) {
                char buffer[32];

                snprintf(buffer, sizeof(buffer), "%lld", (longlong) value.int64);
                JsonWriter_propertyString(writer, "value", buffer);
            } else {
                JsonWriter_propertyInt(writer, "value", value.int64);
            }
            break;
#endif

        case RVALUE_BOOL:
            JsonWriter_propertyString(writer, "type", "bool");
            JsonWriter_propertyBool(writer, "value", value.int32 != 0);
            break;

        case RVALUE_STRING:
            JsonWriter_propertyString(writer, "type", "string");
            JsonWriter_propertyString(writer, "value", value.string != NULL ? value.string : "");
            break;

        case RVALUE_ARRAY:
            JsonWriter_propertyString(writer, "type", "array");
            if (depth < DBG_VALUE_MAX_DEPTH) {
                JsonWriter_key(writer, "items");
                dbgWriteArrayItems(writer, ctx, value.array, depth);
            } else {
                JsonWriter_propertyBool(writer, "truncated", true);
            }
            break;

        case RVALUE_METHOD:
            JsonWriter_propertyString(writer, "type", "method");
            break;

        case RVALUE_STRUCT:
            JsonWriter_propertyString(writer, "type", "struct");
            if (depth < DBG_VALUE_MAX_DEPTH) {
                JsonWriter_key(writer, "items");
                if (dbgWriteStructItems(writer, ctx, value.structInst, depth))
                    JsonWriter_propertyBool(writer, "truncated", true);
            } else {
                JsonWriter_propertyBool(writer, "truncated", true);
            }
            break;

        case RVALUE_ASSETREF:
            JsonWriter_propertyString(writer, "type", "assetref");
            JsonWriter_propertyString(writer, "assetType", dbgAssetTypeName(value.assetRefType));
            JsonWriter_propertyInt(writer, "id", value.int32);
            break;

        default:
            JsonWriter_propertyString(writer, "type", "undefined");
            break;
    }

    JsonWriter_endObject(writer);
}

/* Writes a selfVars hashmap as a "variables" array of {varId,name,value}. */
static void dbgWriteSelfVars(JsonWriter* writer, VMContext* ctx, IntRValueHashMap* map, const char* keyName) {
    uint32_t i;
    int emitted = 0;
    bool truncated = false;

    JsonWriter_key(writer, keyName);
    JsonWriter_beginArray(writer);

    if (map != NULL && map->entries != NULL) {
        for (i = 0; i < map->capacity; i++) {
            IntRValueEntry* entry = &map->entries[i];
            const char* name;
            char fallback[32];
            RValue field;

            if (entry->key == INT_RVALUE_HASHMAP_EMPTY_KEY)
                continue;
            if (DBG_MAX_VARIABLES <= emitted) {
                truncated = true;
                break;
            }

            name = dbgVarName(ctx, entry->key);
            if (name == NULL) {
                snprintf(fallback, sizeof(fallback), "var_%d", entry->key);
                name = fallback;
            }

            field = entry->value;
            field.ownsReference = false;

            JsonWriter_beginObject(writer);
            JsonWriter_propertyInt(writer, "varId", entry->key);
            JsonWriter_propertyString(writer, "name", name);
            JsonWriter_key(writer, "value");
            dbgWriteValue(writer, ctx, field, 0);
            JsonWriter_endObject(writer);
            emitted++;
        }
    }

    JsonWriter_endArray(writer);

    if (truncated)
        JsonWriter_propertyBool(writer, "truncated", true);
}

/* ===[ State JSON ]=== */

static void dbgWriteCallFrame(JsonWriter* writer, BsdbgControl* c, CallFrame* frame) {
    DataWin* dataWin = c->dataWin;
    const char* codeName;

    JsonWriter_beginObject(writer);

    codeName = frame != NULL ? dbgCodeName(dataWin, frame->codeIndex) : NULL;

    JsonWriter_propertyInt(writer, "codeIndex", frame != NULL ? (int64_t) frame->codeIndex : 0);
    JsonWriter_propertyString(writer, "codeName", codeName);
    JsonWriter_propertyInt(writer, "ip", (int64_t) c->stopIp);
    JsonWriter_propertyInt(writer, "selfId",
                            (frame != NULL && frame->self != NULL) ? (int64_t) frame->self->instanceId : -1);
    JsonWriter_propertyInt(writer, "selfObjectIndex",
                            (frame != NULL && frame->self != NULL) ? (int64_t) frame->self->objectIndex : -1);
    JsonWriter_propertyInt(writer, "eventType", frame != NULL ? (int64_t) frame->eventType : -1);
    JsonWriter_propertyInt(writer, "eventSubtype", frame != NULL ? (int64_t) frame->eventSubtype : -1);
    JsonWriter_propertyInt(writer, "eventObjectIndex", frame != NULL ? (int64_t) frame->eventObjectIndex : -1);

    JsonWriter_endObject(writer);
}

/* Converts the current session state to JSON. The caller must hold c->lock:
 * the VM stop path builds the payload while the runner is still parked. */
static char* dbgBuildStateJsonLocked(BsdbgControl* c) {
    JsonWriter writer;
    Runner* runner;
    DataWin* dataWin;
    int64_t frameCount = -1;
    int64_t roomIndex = -1;
    const char* roomName = NULL;
    bool paused;

    if (c == NULL)
        return NULL;

    runner = c->runner;
    dataWin = c->dataWin;
    paused = c->paused != 0;


    writer = JsonWriter_create();
    JsonWriter_beginObject(&writer);

    if (runner != NULL) {
        frameCount = (int64_t) runner->frameCount;
        roomIndex = (int64_t) runner->currentRoomIndex;
    }
    if (dataWin != NULL && roomIndex >= 0 && (uint64_t) roomIndex < dataWin->room.count)
        roomName = dataWin->room.rooms[roomIndex].name;

    JsonWriter_propertyBool(&writer, "running", c->running != 0);
    JsonWriter_propertyBool(&writer, "paused", paused);
    JsonWriter_propertyInt(&writer, "frameCount", frameCount);
    JsonWriter_propertyInt(&writer, "roomIndex", roomIndex);
    JsonWriter_propertyString(&writer, "roomName", roomName);

    if (paused) {
        CallFrame* frame = (c->vm != NULL) ? c->vm->currentCallFrame : NULL;

        JsonWriter_key(&writer, "stop");
        JsonWriter_beginObject(&writer);
        JsonWriter_propertyString(&writer, "reason", dbgStopReasonName(c->stopReason));
        JsonWriter_key(&writer, "frame");
        if (frame != NULL)
            dbgWriteCallFrame(&writer, c, frame);
        else
            JsonWriter_null(&writer);
        JsonWriter_endObject(&writer);
    }

    JsonWriter_endObject(&writer);

    {
        char* result = JsonWriter_copyOutput(&writer);

        JsonWriter_free(&writer);
        return result;
    }
}

char* Bsdbg_BuildStateJson(BsdbgControl* c) {
    char* result;

    if (c == NULL)
        return NULL;

    Bsdbg_Lock(c);
    result = dbgBuildStateJsonLocked(c);
    Bsdbg_Unlock(c);
    return result;
}

/* ===[ Call stack JSON ]=== */
static void dbgWriteFrameLocals(JsonWriter* writer, BsdbgControl* c, CallFrame* frame) {
    VMContext* ctx = c->vm;
    DataWin* dataWin = c->dataWin;
    const char* codeName = dbgCodeName(dataWin, frame->codeIndex);
    IntIntHashMap* map = frame->currentCodeLocalsSlotMap;
    bool usedMap = false;

    JsonWriter_key(writer, "locals");
    JsonWriter_beginArray(writer);

    if (map == NULL && ctx != NULL && ctx->codeLocalsSlotMaps != NULL && dataWin != NULL &&
        frame->codeIndex < dataWin->code.count) {
        map = &ctx->codeLocalsSlotMaps[frame->codeIndex];
    }

    if (map != NULL && map->entries != NULL) {
        uint32_t i;

        usedMap = true;
        for (i = 0; i < map->capacity; i++) {
            IntIntEntry* entry = &map->entries[i];
            uint32_t slot;
            int32_t varId;
            const char* name;
            RValue value;

            if (entry->key == INT_INT_HASHMAP_EMPTY_KEY)
                continue;

            slot = entry->value;
            varId = entry->key;
            name = dbgLocalName(dataWin, codeName, varId);

            JsonWriter_beginObject(writer);
            JsonWriter_propertyInt(writer, "slot", (int64_t) slot);
            JsonWriter_propertyInt(writer, "varId", varId);
            JsonWriter_propertyString(writer, "name", name);
            JsonWriter_key(writer, "value");
            if (frame->localVars != NULL && slot < frame->localVarsCount) {
                value = frame->localVars[slot];
                value.ownsReference = false;
                dbgWriteValue(writer, ctx, value, 0);
            } else {
                JsonWriter_rawValue(writer, "{\"type\":\"undefined\"}");
            }
            JsonWriter_endObject(writer);
        }
    }

    /* BC15/BC16 have no varID->slot map: local slot N is varID N. */
    if (!usedMap) {
        uint32_t slot;

        for (slot = 0; slot < frame->localVarsCount; slot++) {
            const char* name = dbgLocalName(dataWin, codeName, (int32_t) slot);
            RValue value;

            JsonWriter_beginObject(writer);
            JsonWriter_propertyInt(writer, "slot", (int64_t) slot);
            JsonWriter_propertyInt(writer, "varId", (int64_t) slot);
            JsonWriter_propertyString(writer, "name", name);
            JsonWriter_key(writer, "value");
            if (frame->localVars != NULL) {
                value = frame->localVars[slot];
                value.ownsReference = false;
                dbgWriteValue(writer, ctx, value, 0);
            } else {
                JsonWriter_rawValue(writer, "{\"type\":\"undefined\"}");
            }
            JsonWriter_endObject(writer);
        }
    }

    JsonWriter_endArray(writer);
}

char* Bsdbg_BuildCallStackJson(BsdbgControl* c) {
    JsonWriter writer;
    bool paused;
    CallFrame* frame;
    int depth = 0;

    if (c == NULL)
        return NULL;

    Bsdbg_Lock(c);

    writer = JsonWriter_create();
    JsonWriter_beginObject(&writer);
    JsonWriter_key(&writer, "frames");
    JsonWriter_beginArray(&writer);

    paused = c->paused != 0;
    frame = (paused && c->vm != NULL) ? c->vm->currentCallFrame : NULL;

    while (frame != NULL && depth < DBG_MAX_CALL_DEPTH) {
        const char* codeName = dbgCodeName(c->dataWin, frame->codeIndex);

        JsonWriter_beginObject(&writer);
        JsonWriter_propertyInt(&writer, "codeIndex", (int64_t) frame->codeIndex);
        JsonWriter_propertyString(&writer, "codeName", codeName);
        /* Only the topmost frame has a meaningful instruction pointer; the
         * suspended caller frames are parked in their call instruction. */
        JsonWriter_propertyInt(&writer, "ip", depth == 0 ? (int64_t) c->stopIp : 0);
        JsonWriter_propertyInt(&writer, "selfId",
                                frame->self != NULL ? (int64_t) frame->self->instanceId : -1);
        JsonWriter_propertyInt(&writer, "selfObjectIndex",
                                frame->self != NULL ? (int64_t) frame->self->objectIndex : -1);
        JsonWriter_propertyInt(&writer, "eventType", (int64_t) frame->eventType);
        JsonWriter_propertyInt(&writer, "eventSubtype", (int64_t) frame->eventSubtype);
        JsonWriter_propertyInt(&writer, "eventObjectIndex", (int64_t) frame->eventObjectIndex);

        dbgWriteFrameLocals(&writer, c, frame);

        JsonWriter_key(&writer, "args");
        JsonWriter_beginArray(&writer);
        {
            int32_t i;

            for (i = 0; i < frame->scriptArgsCount; i++) {
                RValue argument;

                if (frame->scriptArgs == NULL)
                    break;
                argument = frame->scriptArgs[i];
                argument.ownsReference = false;

                JsonWriter_beginObject(&writer);
                JsonWriter_propertyInt(&writer, "slot", (int64_t) i);
                JsonWriter_key(&writer, "value");
                dbgWriteValue(&writer, c->vm, argument, 0);
                JsonWriter_endObject(&writer);
            }
        }
        JsonWriter_endArray(&writer);

        JsonWriter_endObject(&writer);

        frame = frame->parent;
        depth++;
    }

    JsonWriter_endArray(&writer);
    JsonWriter_endObject(&writer);

    {
        char* result = JsonWriter_copyOutput(&writer);

        JsonWriter_free(&writer);
        Bsdbg_Unlock(c);
        return result;
    }
}

/* ===[ Instances JSON ]=== */

char* Bsdbg_BuildInstancesJson(BsdbgControl* c) {
    JsonWriter writer;
    Runner* runner;
    bool paused;

    if (c == NULL)
        return NULL;

    Bsdbg_Lock(c);

    writer = JsonWriter_create();
    JsonWriter_beginObject(&writer);

    paused = c->paused != 0;
    runner = c->runner;

    JsonWriter_propertyBool(&writer, "paused", paused);
    JsonWriter_key(&writer, "instances");
    JsonWriter_beginArray(&writer);

    if (paused && runner != NULL && runner->instances != NULL) {
        int32_t count = (int32_t) arrlen(runner->instances);
        int32_t i;

        for (i = 0; i < count; i++) {
            Instance* instance = runner->instances[i];
            const char* objectName = NULL;

            if (instance == NULL)
                continue;

            if (c->dataWin != NULL && instance->objectIndex >= 0 &&
                (uint32_t) instance->objectIndex < c->dataWin->objt.count) {
                objectName = c->dataWin->objt.objects[instance->objectIndex].name;
            }

            JsonWriter_beginObject(&writer);
            JsonWriter_propertyInt(&writer, "id", (int64_t) instance->instanceId);
            JsonWriter_propertyInt(&writer, "objectIndex", (int64_t) instance->objectIndex);
            JsonWriter_propertyString(&writer, "objectName", objectName);
            JsonWriter_propertyDouble(&writer, "x", (double) instance->x);
            JsonWriter_propertyDouble(&writer, "y", (double) instance->y);
            JsonWriter_propertyDouble(&writer, "speed", (double) instance->speed);
            JsonWriter_propertyDouble(&writer, "direction", (double) instance->direction);
            JsonWriter_propertyDouble(&writer, "hspeed", (double) instance->hspeed);
            JsonWriter_propertyDouble(&writer, "vspeed", (double) instance->vspeed);
            JsonWriter_propertyInt(&writer, "depth", (int64_t) instance->depth);
            JsonWriter_propertyInt(&writer, "spriteIndex", (int64_t) instance->spriteIndex);
            JsonWriter_propertyDouble(&writer, "imageIndex", (double) instance->imageIndex);
            JsonWriter_propertyDouble(&writer, "imageSpeed", (double) instance->imageSpeed);
            JsonWriter_propertyBool(&writer, "visible", instance->visible);
            JsonWriter_propertyBool(&writer, "active", instance->active);
            JsonWriter_propertyBool(&writer, "persistent", instance->persistent);
            JsonWriter_propertyBool(&writer, "solid", instance->solid);

            dbgWriteSelfVars(&writer, c->vm, &instance->selfVars, "variables");

            JsonWriter_endObject(&writer);
        }
    }

    JsonWriter_endArray(&writer);
    JsonWriter_endObject(&writer);

    {
        char* result = JsonWriter_copyOutput(&writer);

        JsonWriter_free(&writer);
        Bsdbg_Unlock(c);
        return result;
    }
}

/* ===[ Globals JSON ]=== */

char* Bsdbg_BuildGlobalsJson(BsdbgControl* c) {
    JsonWriter writer;
    bool paused;
    Instance* globalScope = NULL;

    if (c == NULL)
        return NULL;

    Bsdbg_Lock(c);

    writer = JsonWriter_create();
    JsonWriter_beginObject(&writer);

    paused = c->paused != 0;
    if (paused && c->vm != NULL)
        globalScope = c->vm->globalScopeInstance;

    JsonWriter_propertyBool(&writer, "paused", paused);

    if (globalScope != NULL)
        dbgWriteSelfVars(&writer, c->vm, &globalScope->selfVars, "globals");
    else {
        JsonWriter_key(&writer, "globals");
        JsonWriter_beginArray(&writer);
        JsonWriter_endArray(&writer);
    }

    JsonWriter_endObject(&writer);

    {
        char* result = JsonWriter_copyOutput(&writer);

        JsonWriter_free(&writer);
        Bsdbg_Unlock(c);
        return result;
    }
}

/* ===[ Set instance variable ]=== */

/* Applies one of the instance built-in fields. Returns true when "name" named a
 * built-in field (and it was written). */
static bool dbgSetBuiltinField(Instance* instance, const char* name, RValue value) {
    if (dbgNamesEqualNoCase(name, "x")) {
        instance->x = (float) RValue_toReal(value);
        return true;
    }
    if (dbgNamesEqualNoCase(name, "y")) {
        instance->y = (float) RValue_toReal(value);
        return true;
    }
    if (dbgNamesEqualNoCase(name, "xstart")) {
        instance->xstart = (float) RValue_toReal(value);
        return true;
    }
    if (dbgNamesEqualNoCase(name, "ystart")) {
        instance->ystart = (float) RValue_toReal(value);
        return true;
    }
    if (dbgNamesEqualNoCase(name, "xprevious")) {
        instance->xprevious = (float) RValue_toReal(value);
        return true;
    }
    if (dbgNamesEqualNoCase(name, "yprevious")) {
        instance->yprevious = (float) RValue_toReal(value);
        return true;
    }
    if (dbgNamesEqualNoCase(name, "speed")) {
        instance->speed = (float) RValue_toReal(value);
        Instance_computeComponentsFromSpeed(instance);
        return true;
    }
    if (dbgNamesEqualNoCase(name, "direction")) {
        GMLReal direction = (GMLReal) fmod((double) RValue_toReal(value), 360.0);

        if (direction < (GMLReal) 0.0)
            direction += (GMLReal) 360.0;
        instance->direction = (float) direction;
        Instance_computeComponentsFromSpeed(instance);
        return true;
    }
    if (dbgNamesEqualNoCase(name, "hspeed")) {
        instance->hspeed = (float) RValue_toReal(value);
        Instance_computeSpeedFromComponents(instance);
        return true;
    }
    if (dbgNamesEqualNoCase(name, "vspeed")) {
        instance->vspeed = (float) RValue_toReal(value);
        Instance_computeSpeedFromComponents(instance);
        return true;
    }
    if (dbgNamesEqualNoCase(name, "friction")) {
        instance->friction = (float) RValue_toReal(value);
        return true;
    }
    if (dbgNamesEqualNoCase(name, "gravity")) {
        instance->gravity = (float) RValue_toReal(value);
        return true;
    }
    if (dbgNamesEqualNoCase(name, "gravity_direction")) {
        instance->gravityDirection = (float) RValue_toReal(value);
        return true;
    }
    if (dbgNamesEqualNoCase(name, "image_speed")) {
        instance->imageSpeed = (float) RValue_toReal(value);
        return true;
    }
    if (dbgNamesEqualNoCase(name, "image_index")) {
        instance->imageIndex = (float) RValue_toReal(value);
        return true;
    }
    if (dbgNamesEqualNoCase(name, "image_xscale")) {
        instance->imageXscale = (float) RValue_toReal(value);
        return true;
    }
    if (dbgNamesEqualNoCase(name, "image_yscale")) {
        instance->imageYscale = (float) RValue_toReal(value);
        return true;
    }
    if (dbgNamesEqualNoCase(name, "image_alpha")) {
        instance->imageAlpha = (float) RValue_toReal(value);
        return true;
    }
    if (dbgNamesEqualNoCase(name, "image_angle")) {
        instance->imageAngle = (GMLReal) RValue_toReal(value);
        return true;
    }
    if (dbgNamesEqualNoCase(name, "image_blend")) {
        instance->imageBlend = RValue_toColour(value);
        return true;
    }
    if (dbgNamesEqualNoCase(name, "depth")) {
        instance->depth = RValue_toInt32(value);
        return true;
    }
    if (dbgNamesEqualNoCase(name, "visible")) {
        instance->visible = RValue_toBool(value);
        return true;
    }
    if (dbgNamesEqualNoCase(name, "active")) {
        instance->active = RValue_toBool(value);
        return true;
    }
    if (dbgNamesEqualNoCase(name, "solid")) {
        instance->solid = RValue_toBool(value);
        return true;
    }
    if (dbgNamesEqualNoCase(name, "persistent")) {
        instance->persistent = RValue_toBool(value);
        return true;
    }
    if (dbgNamesEqualNoCase(name, "sprite_index")) {
        instance->spriteIndex = RValue_toInt32(value);
        return true;
    }
    return false;
}

/* Parses a single value JSON object into an RValue. On success *out owns any
 * string reference; the caller must RValue_free it. */
static int32_t dbgParseValueJson(JsonValue* root, RValue* out, char** errorText) {
    JsonValue* typeValue;
    JsonValue* valueValue;
    const char* type;
    bool hasValue;

    *out = RValue_makeUndefined();

    if (root == NULL || !JsonReader_isObject(root)) {
        dbgSetError(errorText, "value JSON must be an object");
        return -1;
    }

    typeValue = JsonReader_getJsonValueByKey(root, "type");
    if (typeValue == NULL || !JsonReader_isString(typeValue) || JsonReader_getString(typeValue) == NULL) {
        dbgSetError(errorText, "value JSON is missing a string \"type\"");
        return -1;
    }
    type = JsonReader_getString(typeValue);

    valueValue = JsonReader_getJsonValueByKey(root, "value");
    hasValue = valueValue != NULL;

    if (strcmp(type, "undefined") == 0) {
        *out = RValue_makeUndefined();
        return 0;
    }

    if (!hasValue) {
        dbgSetError(errorText, "value JSON of type '%s' is missing \"value\"", type);
        return -1;
    }

    if (strcmp(type, "real") == 0) {
        double number;

        if (JsonReader_isNumber(valueValue))
            number = JsonReader_getDouble(valueValue);
        else if (JsonReader_isString(valueValue))
            number = atof(JsonReader_getString(valueValue));
        else {
            dbgSetError(errorText, "real value must be a number");
            return -1;
        }
        *out = RValue_makeReal((GMLReal) number);
        return 0;
    }

    if (strcmp(type, "int32") == 0) {
        int64_t number;

        if (JsonReader_isNumber(valueValue))
            number = JsonReader_getInt(valueValue);
        else if (JsonReader_isString(valueValue))
            number = (int64_t) strtoll(JsonReader_getString(valueValue), NULL, 10);
        else {
            dbgSetError(errorText, "int32 value must be a number");
            return -1;
        }
        *out = RValue_makeInt32((int32_t) number);
        return 0;
    }

    if (strcmp(type, "int64") == 0) {
        int64_t number;

        if (JsonReader_isNumber(valueValue))
            number = JsonReader_getInt(valueValue);
        else if (JsonReader_isString(valueValue))
            number = (int64_t) strtoll(JsonReader_getString(valueValue), NULL, 10);
        else {
            dbgSetError(errorText, "int64 value must be a number or a decimal string");
            return -1;
        }
        *out = RValue_makeInt64(number);
        return 0;
    }

    if (strcmp(type, "bool") == 0) {
        bool boolean;

        if (JsonReader_isBool(valueValue))
            boolean = JsonReader_getBool(valueValue);
        else if (JsonReader_isNumber(valueValue))
            boolean = JsonReader_getDouble(valueValue) != 0.0;
        else if (JsonReader_isString(valueValue))
            boolean = dbgNamesEqualNoCase(JsonReader_getString(valueValue), "true");
        else {
            dbgSetError(errorText, "bool value must be true or false");
            return -1;
        }
        *out = RValue_makeBool(boolean);
        return 0;
    }

    if (strcmp(type, "string") == 0) {
        const char* text;

        if (!JsonReader_isString(valueValue)) {
            dbgSetError(errorText, "string value must be a string");
            return -1;
        }
        text = JsonReader_getString(valueValue);
        *out = RValue_makeOwnedString(safeStrdup(text != NULL ? text : ""));
        return 0;
    }

    dbgSetError(errorText, "unsupported value type '%s' for assignment", type);
    return -1;
}

int32_t Bsdbg_SetInstanceVariableInternal(BsdbgControl* c, int32_t instanceId,
                                         const char* name, const char* valueJson,
                                         char** errorText) {
    JsonValue* root;
    RValue value;
    Runner* runner;
    Instance* target = NULL;
    int32_t varId;
    int32_t result = 0;

    if (errorText != NULL)
        *errorText = NULL;

    if (c == NULL || name == NULL || name[0] == '\0' || valueJson == NULL) {
        dbgSetError(errorText, "invalid arguments");
        return -1;
    }

    root = JsonReader_parse(valueJson);
    if (root == NULL) {
        dbgSetError(errorText, "invalid value JSON");
        return -1;
    }

    if (dbgParseValueJson(root, &value, errorText) != 0) {
        JsonReader_free(root);
        return -1;
    }
    JsonReader_free(root);

    Bsdbg_Lock(c);

    if (c->paused == 0) {
        Bsdbg_Unlock(c);
        RValue_free(&value);
        dbgSetError(errorText, "Runner must be paused");
        return -1;
    }

    runner = c->runner;
    if (runner != NULL && runner->instances != NULL) {
        int32_t count = (int32_t) arrlen(runner->instances);
        int32_t i;

        for (i = 0; i < count; i++) {
            Instance* instance = runner->instances[i];

            if (instance != NULL && instance->instanceId == (uint32_t) instanceId) {
                target = instance;
                break;
            }
        }
    }

    if (target == NULL) {
        Bsdbg_Unlock(c);
        RValue_free(&value);
        dbgSetError(errorText, "instance %d was not found", instanceId);
        return -1;
    }

    varId = dbgFindVarIdByName(c->vm, name);

    if (dbgSetBuiltinField(target, name, value)) {
        /* Keep a same-named self variable coherent too, when one exists. GML
         * resolves built-ins through the builtin table, but debugger-created
         * aliases and struct-like views read selfVars. */
        if (varId >= 0)
            Instance_setSelfVar(target, varId, value);
    } else {
        if (varId < 0) {
            Bsdbg_Unlock(c);
            RValue_free(&value);
            dbgSetError(errorText, "unknown variable '%s' on instance %d", name, instanceId);
            return -1;
        }
        Instance_setSelfVar(target, varId, value);
    }

    Bsdbg_Unlock(c);
    RValue_free(&value);
    return result;
}

int32_t Bsdbg_SetGlobalVariableInternal(BsdbgControl* c, const char* name,
                                        const char* valueJson, char** errorText) {
    JsonValue* root;
    RValue value;
    Instance* globalScope;
    int32_t varId;

    if (errorText != NULL)
        *errorText = NULL;

    if (c == NULL || name == NULL || name[0] == '\0' || valueJson == NULL) {
        dbgSetError(errorText, "invalid arguments");
        return -1;
    }

    root = JsonReader_parse(valueJson);
    if (root == NULL) {
        dbgSetError(errorText, "invalid value JSON");
        return -1;
    }
    if (dbgParseValueJson(root, &value, errorText) != 0) {
        JsonReader_free(root);
        return -1;
    }
    JsonReader_free(root);

    Bsdbg_Lock(c);

    if (c->paused == 0) {
        Bsdbg_Unlock(c);
        RValue_free(&value);
        dbgSetError(errorText, "Runner must be paused");
        return -1;
    }

    globalScope = c->vm != NULL ? c->vm->globalScopeInstance : NULL;
    if (globalScope == NULL) {
        Bsdbg_Unlock(c);
        RValue_free(&value);
        dbgSetError(errorText, "global scope is not available");
        return -1;
    }

    varId = dbgFindVarIdByName(c->vm, name);
    if (varId < 0) {
        Bsdbg_Unlock(c);
        RValue_free(&value);
        dbgSetError(errorText, "unknown global variable '%s'", name);
        return -1;
    }

    Instance_setSelfVar(globalScope, varId, value);

    Bsdbg_Unlock(c);
    RValue_free(&value);
    return 0;
}

int32_t Bsdbg_SetFrameVariableInternal(BsdbgControl* c, int32_t frameIndex,
                                       int32_t isArgument, int32_t slot,
                                       const char* valueJson, char** errorText) {
    JsonValue* root;
    RValue value;
    CallFrame* frame;
    int32_t depth;
    RValue* target;

    if (errorText != NULL)
        *errorText = NULL;

    if (c == NULL || valueJson == NULL || frameIndex < 0 || slot < 0) {
        dbgSetError(errorText, "invalid arguments");
        return -1;
    }

    root = JsonReader_parse(valueJson);
    if (root == NULL) {
        dbgSetError(errorText, "invalid value JSON");
        return -1;
    }
    if (dbgParseValueJson(root, &value, errorText) != 0) {
        JsonReader_free(root);
        return -1;
    }
    JsonReader_free(root);

    Bsdbg_Lock(c);

    if (c->paused == 0) {
        Bsdbg_Unlock(c);
        RValue_free(&value);
        dbgSetError(errorText, "Runner must be paused");
        return -1;
    }

    frame = c->vm != NULL ? c->vm->currentCallFrame : NULL;
    for (depth = 0; frame != NULL && depth < frameIndex; depth++)
        frame = frame->parent;

    if (frame == NULL) {
        Bsdbg_Unlock(c);
        RValue_free(&value);
        dbgSetError(errorText, "call frame %d was not found", frameIndex);
        return -1;
    }

    if (isArgument != 0) {
        if (frame->scriptArgs == NULL || slot >= frame->scriptArgsCount) {
            Bsdbg_Unlock(c);
            RValue_free(&value);
            dbgSetError(errorText, "argument slot %d is out of range", slot);
            return -1;
        }
        target = &frame->scriptArgs[slot];
    } else {
        if (frame->localVars == NULL || (uint32_t) slot >= frame->localVarsCount) {
            Bsdbg_Unlock(c);
            RValue_free(&value);
            dbgSetError(errorText, "local slot %d is out of range", slot);
            return -1;
        }
        target = &frame->localVars[slot];
    }

    RValue_free(target);
    *target = value;

    Bsdbg_Unlock(c);
    return 0;
}

/* ===[ VM tick ]=== */

void Bsdbg_OnVmInstruction(VMContext* ctx, uint32_t ip) {
    BsdbgControl* c = g_bsdbg;
    uint32_t codeIndex;
    int depth;
    int reason = -1;
    int i;
    char* stateJson;

    if (c == NULL)
        return;
    /* Hot path: one volatile flag read. */
    if (!c->active)
        return;

    Bsdbg_Lock(c);

    if (c->exitRequested) {
        Bsdbg_Unlock(c);
        return;
    }

    codeIndex = (ctx != NULL && ctx->currentCallFrame != NULL)
                    ? ctx->currentCallFrame->codeIndex
                    : 0;
    depth = dbgFrameDepth(ctx);

    if (c->stopOnEntryPending) {
        c->stopOnEntryPending = 0;
        reason = BSDBG_STOP_ENTRY;
    } else if (c->interruptRequested) {
        /* External pause request: consume it here so the next instruction
         * does not stop again for the same request. */
        reason = BSDBG_STOP_PAUSE;
    } else {
        bool breakpointHit = false;

        for (i = 0; i < c->bpCount; i++) {
            BsdbgBreakpoint* breakpoint = &c->breakpoints[i];

            if (!breakpoint->enabled)
                continue;
            if (breakpoint->codeIndex < 0 || (uint32_t) breakpoint->codeIndex != codeIndex)
                continue;
            if (breakpoint->offset != ip)
                continue;
            /* Skip the stop point we just reported (resume/step re-check). */
            if (c->suppressValid && c->suppressCodeIndex == codeIndex && c->suppressIp == ip)
                continue;
            breakpointHit = true;
            break;
        }

        if (!breakpointHit && c->suppressValid &&
            (c->suppressCodeIndex != codeIndex || c->suppressIp != ip)) {
            /* The instruction pointer moved: re-arm breakpoint suppression. */
            c->suppressValid = 0;
        }

        if (breakpointHit) {
            reason = BSDBG_STOP_BREAKPOINT;
        } else if (c->stepping) {
            /* The VM hook runs *before* the instruction at `ip` executes. A step
             * request made while paused therefore resumes by executing that
             * instruction, and the very next hook call is the first candidate.
             * stepSkipOnce only exists to swallow a re-check of the exact stop
             * point (e.g. a step issued from a boundary stop). */
            if (c->stepSkipOnce && c->suppressValid &&
                c->suppressCodeIndex == codeIndex && c->suppressIp == ip) {
                c->stepSkipOnce = 0;
            } else {
                int mode = c->stepMode;
                bool stop = false;

                if (mode == BSDBG_STEP_INTO) {
                    stop = true;
                } else if (mode == BSDBG_STEP_OVER) {
                    stop = depth <= c->stopDepth;
                } else if (mode == BSDBG_STEP_OUT) {
                    /* A top-level frame has no caller to return to; stop at the
                     * next instruction instead of running forever. */
                    stop = (c->stopDepth <= 0) ? (depth <= 0) : (depth < c->stopDepth);
                }

                if (stop)
                    reason = BSDBG_STOP_STEP;
            }
        }
    }

    if (reason < 0) {
        Bsdbg_Unlock(c);
        return;
    }

    /* A stop ends any in-flight step operation. */
    c->stepping = 0;
    c->stepMode = BSDBG_STEP_NONE;
    c->stepSkipOnce = 0;
    /* VM stop path: record the stop point, keep the instruction we stopped on
     * suppressed for breakpoint checks, then park the game thread until the
     * next Resume/Step call or exit. The state JSON is built while the state
     * is quiescent (under the lock); the BREAK event is emitted without it. */
    c->stopReason = reason;
    c->stopLocation = BSDBG_STOPLOC_VM;
    c->stopCodeIndex = codeIndex;
    c->stopIp = ip;
    c->stopDepth = depth;
    c->suppressValid = 1;
    c->suppressCodeIndex = codeIndex;
    c->suppressIp = ip;
    c->paused = 1;
    Bsdbg_UpdateActive(c);

    stateJson = dbgBuildStateJsonLocked(c);
    Bsdbg_Unlock(c);

    Bsdbg_EmitEvent(c, BSDBG_EVENT_BREAK, stateJson != NULL ? stateJson : "null");
    free(stateJson);

    Bsdbg_Lock(c);
    while (c->paused && !c->exitRequested)
        Bsdbg_Wait(c);
    Bsdbg_Unlock(c);
}
