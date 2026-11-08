/*
 * ButterscotchDbg - hot code reload.
 *
 * A patch is produced by UndertaleModTool and delivered as JSON (see the
 * schema in bsdbg.h).  Patches are queued immediately and applied on the game
 * thread at a frame boundary (or by the session while paused).
 *
 * The important parts are:
 *
 *   - CODE entries are never patched in place.  New bytecode is appended to
 *     DataWin.bytecodeBuffer, then the CodeEntry is pointed at the fresh
 *     copy.  This keeps any currently suspended call frame valid and avoids
 *     overlapping byte ranges between entries.
 *
 *   - Reference-chain operands in the supplied bytecode contain the patch
 *     builder's *names* (encoded as STRG indices) for variables/functions.
 *     At apply time we resolve those names against VARI/FUNC, appending new
 *     entries when necessary, and rewrite the operand to the runtime index
 *     while preserving the upper variable-type bits.
 *
 *   - VMContext caches (codeIndexByName, codeLocalsMap, funcCallCache,
 *     codeLocalsSlotMaps, static arrays and cross references) are rebuilt so
 *     freshly appended code/variables/functions are visible without a restart.
 */

#include "bsdbg_internal.h"

#include "data_win.h"
#include "vm.h"
#include "vm_builtins.h"
#include "json_reader.h"
#include "base64.h"
#include "binary_utils.h"
#include "utils.h"

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>

/* ===[ Patch-owned storage ]===
 *
 * Names/strings added by a patch live for as long as the DataWin references
 * them.  We keep those allocations in a simple arena and expose a cleanup
 * function for session destroy; atexit() is a safety net for hosts that
 * forget to call it.
 */

typedef struct HotPatchBlock {
    struct HotPatchBlock* next;
    char data[1];
} HotPatchBlock;

static HotPatchBlock* g_patchBlocks = NULL;
static int g_patchAtexitRegistered = 0;

static void hot_release_blocks(void) {
    HotPatchBlock* block = g_patchBlocks;
    g_patchBlocks = NULL;
    while (block != NULL) {
        HotPatchBlock* next = block->next;
        free(block);
        block = next;
    }
}

void BsdbgHotreload_FreeOwned(void) {
    hot_release_blocks();
}

static char* hot_strdup(const char* text) {
    if (text == NULL)
        return NULL;

    size_t length = strlen(text) + 1;
    HotPatchBlock* block = (HotPatchBlock*) safeMalloc(sizeof(HotPatchBlock) + length);
    block->next = g_patchBlocks;
    g_patchBlocks = block;
    memcpy(block->data, text, length);
    return block->data;
}

static void hot_register_atexit(void) {
    if (!g_patchAtexitRegistered) {
        g_patchAtexitRegistered = 1;
        atexit(hot_release_blocks);
    }
}

/* ===[ Small utilities ]=== */

static bool name_equal(const char* a, const char* b) {
    return a != NULL && b != NULL && strcmp(a, b) == 0;
}

static bool name_equal_ci(const char* a, const char* b) {
    if (a == NULL || b == NULL)
        return false;

    while (*a != '\0' && *b != '\0') {
        if (tolower((unsigned char) *a) != tolower((unsigned char) *b))
            return false;
        a++;
        b++;
    }
    return *a == '\0' && *b == '\0';
}

static bool starts_with(const char* text, const char* prefix) {
    return text != NULL && strncmp(text, prefix, strlen(prefix)) == 0;
}

static char* alloc_vformat(const char* fmt, va_list args) {
    va_list copy;
    va_copy(copy, args);
    int needed = vsnprintf(NULL, 0, fmt, copy);
    va_end(copy);
    if (needed < 0)
        return NULL;

    char* result = (char*) safeMalloc((size_t) needed + 1);
    vsnprintf(result, (size_t) needed + 1, fmt, args);
    return result;
}

static char* alloc_format(const char* fmt, ...) {
    va_list args;
    va_start(args, fmt);
    char* result = alloc_vformat(fmt, args);
    va_end(args);
    return result;
}

/* ===[ JSON helpers ]=== */

static JsonValue* object_get(JsonValue* object, const char* key) {
    if (object == NULL || !JsonReader_isObject(object))
        return NULL;
    return JsonReader_getJsonValueByKey(object, key);
}

static bool value_to_int(JsonValue* value, int64_t* out) {
    if (value == NULL || !JsonReader_isNumber(value))
        return false;
    *out = JsonReader_getInt(value);
    return true;
}

static bool json_int_field(JsonValue* object, const char* key, bool* present, int64_t* out) {
    JsonValue* value = object_get(object, key);
    if (value == NULL) {
        *present = false;
        return true;
    }
    if (!value_to_int(value, out))
        return false;
    *present = true;
    return true;
}

static const char* json_string_field(JsonValue* object, const char* key) {
    JsonValue* value = object_get(object, key);
    if (value == NULL || !JsonReader_isString(value))
        return NULL;
    return JsonReader_getString(value);
}

static bool json_bool_field(JsonValue* object, const char* key, bool* out) {
    JsonValue* value = object_get(object, key);
    if (value == NULL) {
        *out = false;
        return true;
    }
    if (!JsonReader_isBool(value))
        return false;
    *out = JsonReader_getBool(value);
    return true;
}

static char* make_json_string(const char* text) {
    if (text == NULL)
        text = "";

    size_t extra = 0;
    for (const char* p = text; *p != '\0'; p++) {
        unsigned char ch = (unsigned char) *p;
        if (ch == '"' || ch == '\\' || ch == '\n' || ch == '\r' || ch == '\t')
            extra += 2;
        else if (ch < 0x20)
            extra += 6;
    }

    size_t length = strlen(text);
    char* out = (char*) safeMalloc(length + extra + 1);
    char* dst = out;
    for (const char* p = text; *p != '\0'; p++) {
        unsigned char ch = (unsigned char) *p;
        switch (ch) {
            case '"':  *dst++ = '\\'; *dst++ = '"'; break;
            case '\\': *dst++ = '\\'; *dst++ = '\\'; break;
            case '\n': *dst++ = '\\'; *dst++ = 'n'; break;
            case '\r': *dst++ = '\\'; *dst++ = 'r'; break;
            case '\t': *dst++ = '\\'; *dst++ = 't'; break;
            default:
                if (ch < 0x20) {
                    static const char hex[] = "0123456789abcdef";
                    *dst++ = '\\';
                    *dst++ = 'u';
                    *dst++ = '0';
                    *dst++ = '0';
                    *dst++ = hex[(ch >> 4) & 0xF];
                    *dst++ = hex[ch & 0xF];
                } else {
                    *dst++ = (char) ch;
                }
                break;
        }
    }
    *dst = '\0';
    return out;
}

static void set_error_text(char** errorText, const char* message) {
    if (errorText == NULL)
        return;
    free(*errorText);
    *errorText = message != NULL ? safeStrdup(message) : NULL;
}

/* ===[ Apply context / errors ]=== */

typedef struct HotApplyContext {
    BsdbgControl* control;
    DataWin* dataWin;
    VMContext* vm;
    int applied;
    int skipped;
    char* error;
} HotApplyContext;

static bool hot_fail(HotApplyContext* ctx, const char* fmt, ...) {
    if (ctx->error != NULL)
        return false;

    va_list args;
    va_start(args, fmt);
    ctx->error = alloc_vformat(fmt, args);
    va_end(args);
    return false;
}

/* ===[ DataWin append helpers ]=== */

static bool grow_strg(DataWin* dw, uint32_t count) {
    if (count <= dw->strg.count)
        return true;

    dw->strg.strings = (const char**) safeRealloc(dw->strg.strings, (size_t) count * sizeof(const char*));
    for (uint32_t i = dw->strg.count; i < count; i++)
        dw->strg.strings[i] = "";
    dw->strg.count = count;
    return true;
}

static bool grow_vari(DataWin* dw, uint32_t count) {
    if (count <= dw->vari.variableCount)
        return true;

    dw->vari.variables = (Variable*) safeRealloc(dw->vari.variables, (size_t) count * sizeof(Variable));
    for (uint32_t i = dw->vari.variableCount; i < count; i++) {
        Variable* variable = &dw->vari.variables[i];
        memset(variable, 0, sizeof(*variable));
        variable->name = "";
        variable->instanceType = 0;
        variable->varID = -1;
        variable->builtinVarId = -1;
    }
    dw->vari.variableCount = count;
    return true;
}

static bool grow_func(DataWin* dw, uint32_t count) {
    if (count <= dw->func.functionCount)
        return true;

    dw->func.functions = (Function*) safeRealloc(dw->func.functions, (size_t) count * sizeof(Function));
    for (uint32_t i = dw->func.functionCount; i < count; i++) {
        memset(&dw->func.functions[i], 0, sizeof(Function));
        dw->func.functions[i].name = "";
    }
    dw->func.functionCount = count;
    return true;
}

static bool grow_code(DataWin* dw, uint32_t count) {
    if (count <= dw->code.count)
        return true;

    dw->code.entries = (CodeEntry*) safeRealloc(dw->code.entries, (size_t) count * sizeof(CodeEntry));
    for (uint32_t i = dw->code.count; i < count; i++) {
        memset(&dw->code.entries[i], 0, sizeof(CodeEntry));
        dw->code.entries[i].name = "";
    }
    dw->code.count = count;
    return true;
}

static int32_t next_variable_id(HotApplyContext* ctx) {
    DataWin* dw = ctx->dataWin;
    int32_t next = 0;

    for (uint32_t i = 0; i < dw->vari.variableCount; i++) {
        int32_t id = dw->vari.variables[i].varID;
        if (id >= 0 && id >= next)
            next = id + 1;
    }

    if (ctx->vm != NULL && ctx->vm->nextDynamicVarID > next)
        next = ctx->vm->nextDynamicVarID;

    if (ctx->vm != NULL)
        ctx->vm->nextDynamicVarID = next + 1;
    return next;
}

/* ===[ Name resolution ]=== */

static int32_t find_variable(DataWin* dw, const char* name, bool hasInstanceType, int32_t instanceType) {
    if (name == NULL)
        return -1;

    /* Prefer an exact match.  If an instance type was supplied, honour it. */
    for (int pass = 0; pass < 2; pass++) {
        for (uint32_t i = 0; i < dw->vari.variableCount; i++) {
            Variable* variable = &dw->vari.variables[i];
            bool matches = pass == 0 ? name_equal(variable->name, name) : name_equal_ci(variable->name, name);
            if (!matches)
                continue;
            if (hasInstanceType && variable->instanceType != instanceType)
                continue;
            return (int32_t) i;
        }
    }
    return -1;
}

static int32_t find_variable_by_id(DataWin* dw, int32_t varId) {
    for (uint32_t i = 0; i < dw->vari.variableCount; i++) {
        if (dw->vari.variables[i].varID == varId)
            return (int32_t) i;
    }
    return -1;
}

static int32_t append_variable(HotApplyContext* ctx, const char* name,
                               bool hasInstanceType, int32_t instanceType,
                               bool hasVarId, int32_t varId) {
    DataWin* dw = ctx->dataWin;
    uint32_t index = dw->vari.variableCount;
    if (!grow_vari(dw, index + 1))
        return -1;

    Variable* variable = &dw->vari.variables[index];
    variable->name = hot_strdup(name != NULL ? name : "");
    variable->instanceType = hasInstanceType ? instanceType : INSTANCE_SELF;
    variable->varID = (hasVarId && varId >= 0) ? varId : next_variable_id(ctx);
    variable->occurrences = 0;
    variable->firstAddress = 0;
    if (variable->instanceType == INSTANCE_BUILTIN || variable->varID == VARIABLE_BUILTIN)
        variable->builtinVarId = VMBuiltins_resolveBuiltinVarId(variable->name);
    else
        variable->builtinVarId = -1;

    if (ctx->vm != NULL && variable->varID >= ctx->vm->nextDynamicVarID)
        ctx->vm->nextDynamicVarID = variable->varID + 1;

    return (int32_t) index;
}

static int32_t resolve_or_create_variable(HotApplyContext* ctx, const char* name,
                                          bool hasInstanceType, int32_t instanceType,
                                          bool hasVarId, int32_t varId) {
    int32_t index = find_variable(ctx->dataWin, name, hasInstanceType, instanceType);
    if (index < 0 && hasVarId)
        index = find_variable_by_id(ctx->dataWin, varId);
    if (index >= 0)
        return index;
    return append_variable(ctx, name, hasInstanceType, instanceType, hasVarId, varId);
}

static int32_t find_function(DataWin* dw, const char* name) {
    if (name == NULL)
        return -1;
    for (uint32_t i = 0; i < dw->func.functionCount; i++) {
        if (name_equal(dw->func.functions[i].name, name))
            return (int32_t) i;
    }
    for (uint32_t i = 0; i < dw->func.functionCount; i++) {
        if (name_equal_ci(dw->func.functions[i].name, name))
            return (int32_t) i;
    }
    return -1;
}

static int32_t append_function(HotApplyContext* ctx, const char* name) {
    DataWin* dw = ctx->dataWin;
    uint32_t index = dw->func.functionCount;
    if (!grow_func(dw, index + 1))
        return -1;

    Function* function = &dw->func.functions[index];
    function->name = hot_strdup(name != NULL ? name : "");
    function->occurrences = 0;
    function->firstAddress = 0;
    return (int32_t) index;
}

static int32_t resolve_or_create_function(HotApplyContext* ctx, const char* name) {
    int32_t index = find_function(ctx->dataWin, name);
    if (index >= 0)
        return index;
    return append_function(ctx, name);
}

/* ===[ Code locals ]=== */

static bool code_locals_name_matches(const char* localsName, const char* codeName) {
    if (localsName == NULL || codeName == NULL)
        return false;
    if (name_equal(localsName, codeName))
        return true;

    /* WAD17+ stores script locals under gml_GlobalScript_, while CODE entries
     * use gml_Script_.  Accept either direction. */
    if (starts_with(localsName, "gml_GlobalScript_") && starts_with(codeName, "gml_Script_"))
        return name_equal(localsName + 17, codeName + 11);
    if (starts_with(localsName, "gml_Script_") && starts_with(codeName, "gml_GlobalScript_"))
        return name_equal(localsName + 11, codeName + 17);
    return false;
}

static int32_t find_code_locals(DataWin* dw, const char* codeName) {
    for (uint32_t i = 0; i < dw->func.codeLocalsCount; i++) {
        if (code_locals_name_matches(dw->func.codeLocals[i].name, codeName))
            return (int32_t) i;
    }
    return -1;
}


/* ===[ VM cache rebuild ]=== */

static void free_cross_refs(VMContext* vm) {
    if (vm->crossRefMap == NULL)
        return;

    for (ptrdiff_t i = 0; i < hmlen(vm->crossRefMap); i++)
        arrfree(vm->crossRefMap[i].value);
    hmfree(vm->crossRefMap);
    vm->crossRefMap = NULL;
}

static void code_map_add(CodeIndexByNameEntry** map, const char* name, int32_t codeIndex) {
    if (name == NULL || name[0] == '\0')
        return;
    if (shgeti(*map, (char*) name) < 0)
        shput(*map, (char*) name, codeIndex);
}

static void rebuild_code_index_map(VMContext* vm) {
    DataWin* dw = vm->dataWin;

    shfree(vm->codeIndexByName);
    vm->codeIndexByName = NULL;

    for (uint32_t i = 0; i < dw->scpt.count; i++) {
        Script* script = &dw->scpt.scripts[i];
        if (script->name == NULL || script->codeId < 0 || (uint32_t) script->codeId >= dw->code.count)
            continue;

        const char* codeName = dw->code.entries[script->codeId].name;
        code_map_add(&vm->codeIndexByName, codeName, script->codeId);
        code_map_add(&vm->codeIndexByName, script->name, script->codeId);
    }

    for (uint32_t i = 0; i < dw->code.count; i++) {
        const char* codeName = dw->code.entries[i].name;
        code_map_add(&vm->codeIndexByName, codeName, (int32_t) i);

        /* Make appended code callable by its bare script name even though it
         * has no SCPT entry. */
        if (starts_with(codeName, "gml_Script_"))
            code_map_add(&vm->codeIndexByName, codeName + 11, (int32_t) i);
        else if (starts_with(codeName, "gml_GlobalScript_"))
            code_map_add(&vm->codeIndexByName, codeName + 17, (int32_t) i);
    }
}

static void rebuild_code_locals_map(VMContext* vm) {
    DataWin* dw = vm->dataWin;

    if (vm->codeLocalsMap != NULL) {
        for (ptrdiff_t i = 0; i < shlen(vm->codeLocalsMap); i++)
            free(vm->codeLocalsMap[i].key);
        shfree(vm->codeLocalsMap);
        vm->codeLocalsMap = NULL;
    }

    for (uint32_t i = 0; i < dw->func.codeLocalsCount; i++) {
        CodeLocals* locals = &dw->func.codeLocals[i];
        if (locals->name == NULL || locals->name[0] == '\0')
            continue;

        if (shgeti(vm->codeLocalsMap, (char*) locals->name) < 0)
            shput(vm->codeLocalsMap, safeStrdup(locals->name), locals);

        if (dw->gen8.wadVersion >= 17) {
            char alias[512];
            if (starts_with(locals->name, "gml_GlobalScript_")) {
                snprintf(alias, sizeof(alias), "gml_Script_%s", locals->name + 17);
                if (shgeti(vm->codeLocalsMap, alias) < 0)
                    shput(vm->codeLocalsMap, safeStrdup(alias), locals);
            } else if (starts_with(locals->name, "gml_Script_")) {
                snprintf(alias, sizeof(alias), "gml_GlobalScript_%s", locals->name + 11);
                if (shgeti(vm->codeLocalsMap, alias) < 0)
                    shput(vm->codeLocalsMap, safeStrdup(alias), locals);
            }
        }
    }
}

static void update_var_name_map(VMContext* vm) {
    DataWin* dw = vm->dataWin;

    for (uint32_t i = 0; i < dw->vari.variableCount; i++) {
        Variable* variable = &dw->vari.variables[i];
        if (variable->varID < 0 || variable->name == NULL || variable->name[0] == '\0')
            continue;

        ptrdiff_t slot = shgeti(vm->varNameMap, (char*) variable->name);
        if (slot < 0)
            shput(vm->varNameMap, safeStrdup(variable->name), variable->varID);
        else
            vm->varNameMap[slot].value = variable->varID;
    }

    if (vm->nextDynamicVarID < 1)
        vm->nextDynamicVarID = 1;
}

static void refresh_func_cache(VMContext* vm) {
    DataWin* dw = vm->dataWin;
    uint32_t count = dw->func.functionCount;

    if (count != vm->funcCallCacheCount) {
        vm->funcCallCache = (FuncCallCache*) safeRealloc(vm->funcCallCache, (size_t) count * sizeof(FuncCallCache));
        vm->funcCallCacheCount = count;
    }

    for (uint32_t i = 0; i < count; i++) {
        Function* function = &dw->func.functions[i];
        const char* name = function->name;
        BuiltinFunc builtin = (name != NULL && name[0] != '\0') ? VM_findBuiltin(vm, name) : NULL;
        vm->funcCallCache[i].builtin = (void*) builtin;

        if (builtin != NULL) {
            vm->funcCallCache[i].scriptCodeIndex = -1;
        } else if (name != NULL && name[0] != '\0') {
            ptrdiff_t slot = shgeti(vm->codeIndexByName, (char*) name);
            vm->funcCallCache[i].scriptCodeIndex = slot >= 0 ? vm->codeIndexByName[slot].value : -1;
        } else {
            vm->funcCallCache[i].scriptCodeIndex = -1;
        }
    }
}

static void grow_vm_code_arrays(VMContext* vm, uint32_t oldCount, uint32_t newCount) {
    if (newCount <= oldCount)
        return;

    DataWin* dw = vm->dataWin;
    bool needStatic = dw->gen8.wadVersion >= 17 || vm->staticInitialized != NULL;
    bool needLocals = dw->gen8.wadVersion >= 17 || dw->gen8.wadVersion <= 14 || vm->codeLocalsSlotMaps != NULL;

    if (needStatic) {
        vm->staticInitialized = (bool*) safeRealloc(vm->staticInitialized, (size_t) newCount * sizeof(bool));
        memset(vm->staticInitialized + oldCount, 0, (size_t) (newCount - oldCount) * sizeof(bool));

        vm->staticStructs = (Instance**) safeRealloc(vm->staticStructs, (size_t) newCount * sizeof(Instance*));
        memset(vm->staticStructs + oldCount, 0, (size_t) (newCount - oldCount) * sizeof(Instance*));
    }

    if (needLocals) {
        vm->codeLocalsSlotMaps = (IntIntHashMap*) safeRealloc(vm->codeLocalsSlotMaps,
                                                              (size_t) newCount * sizeof(IntIntHashMap));
        memset(vm->codeLocalsSlotMaps + oldCount, 0,
               (size_t) (newCount - oldCount) * sizeof(IntIntHashMap));
    }
}

static void rebuild_vm_caches(HotApplyContext* ctx, uint32_t oldCodeCount) {
    VMContext* vm = ctx->vm;
    if (vm == NULL)
        return;

    DataWin* dw = ctx->dataWin;
    grow_vm_code_arrays(vm, oldCodeCount, dw->code.count);
    rebuild_code_index_map(vm);
    rebuild_code_locals_map(vm);
    update_var_name_map(vm);
    refresh_func_cache(vm);

    free_cross_refs(vm);
    VM_buildCrossReferences(vm);

    /* executeBytecodeInstructions recomputes this on entry, but a suspended
     * caller may still hold the old base. */
    vm->bytecodeBase = NULL;
}

/* ===[ Patch code item ]=== */

typedef struct PatchCodeItem {
    int32_t index;
    const char* name;
    bool hasLocalsCount;
    uint16_t localsCount;
    bool hasArgumentsCount;
    uint16_t argumentsCount;
    uint8_t* bytecode;
    uint32_t length;
    uint32_t assignedOffset; /* absolute file offset */
    bool identical;
    JsonValue* varRefs;
    JsonValue* funcRefs;
    JsonValue* locals;
} PatchCodeItem;

static bool apply_code_locals(HotApplyContext* ctx, PatchCodeItem* item, uint32_t codeIndex);

/* ===[ Reference resolution ]=== */

static bool patch_variable_ref(HotApplyContext* ctx, PatchCodeItem* item, uint8_t* bytecode,
                               JsonValue* ref) {
    if (!JsonReader_isObject(ref))
        return hot_fail(ctx, "code %d: varRefs entries must be objects", item->index);

    bool hasOffset = false;
    int64_t offsetValue = 0;
    if (!json_int_field(ref, "offset", &hasOffset, &offsetValue))
        return hot_fail(ctx, "code %d: invalid varRef offset", item->index);
    if (!hasOffset && !json_int_field(ref, "operandOffset", &hasOffset, &offsetValue))
        return hot_fail(ctx, "code %d: invalid varRef offset", item->index);

    if (!hasOffset) {
        bool hasInsn = false;
        int64_t insnOffset = 0;
        if (!json_int_field(ref, "instructionOffset", &hasInsn, &insnOffset) || !hasInsn)
            return hot_fail(ctx, "code %d: varRef is missing offset", item->index);
        offsetValue = insnOffset + 4;
    }

    if (offsetValue < 0 || (uint64_t) offsetValue + 4 > (uint64_t) item->length)
        return hot_fail(ctx, "code %d: varRef offset %lld is outside the bytecode",
                        item->index, (long long) offsetValue);

    const char* name = json_string_field(ref, "name");
    bool hasVarId = false;
    int64_t varIdValue = 0;
    if (!json_int_field(ref, "varId", &hasVarId, &varIdValue))
        return hot_fail(ctx, "code %d: invalid varRef varId", item->index);

    bool hasInstanceType = false;
    int64_t instanceTypeValue = 0;
    if (!json_int_field(ref, "instanceType", &hasInstanceType, &instanceTypeValue)) {
        hasInstanceType = false;
        instanceTypeValue = 0;
    }

    int32_t varIndex = -1;
    if (name != NULL && name[0] != '\0') {
        varIndex = resolve_or_create_variable(ctx, name,
                                              hasInstanceType, (int32_t) instanceTypeValue,
                                              hasVarId, (int32_t) varIdValue);
    } else if (hasVarId) {
        varIndex = find_variable_by_id(ctx->dataWin, (int32_t) varIdValue);
    }

    if (varIndex < 0)
        return hot_fail(ctx, "code %d: could not resolve variable reference", item->index);

    if ((uint32_t) varIndex > 0x07FFFFFFu)
        return hot_fail(ctx, "code %d: variable index out of range", item->index);

    uint8_t* operandPtr = bytecode + (size_t) offsetValue;
    uint32_t operand = BinaryUtils_readUint32(operandPtr);
    uint32_t upperBits = operand & 0xF8000000u;

    /* Optional explicit reference type (0x00/0x10/0x80/0x90/0xA0/0xE0). */
    bool hasVarType = false;
    int64_t varTypeValue = 0;
    if (json_int_field(ref, "varType", &hasVarType, &varTypeValue) && hasVarType) {
        uint32_t varType = (uint32_t) varTypeValue & 0xFFu;
        if (varType == 0x00 || varType == 0x10 || varType == 0x80 ||
            varType == 0x90 || varType == 0xA0 || varType == 0xE0) {
            upperBits = (varType & 0xF8u) << 24;
        }
    }

    BinaryUtils_writeUint32(operandPtr, upperBits | ((uint32_t) varIndex & 0x07FFFFFFu));
    return true;
}

static bool patch_function_ref(HotApplyContext* ctx, PatchCodeItem* item, uint8_t* bytecode,
                               JsonValue* ref) {
    if (!JsonReader_isObject(ref))
        return hot_fail(ctx, "code %d: funcRefs entries must be objects", item->index);

    bool hasOffset = false;
    int64_t offsetValue = 0;
    if (!json_int_field(ref, "offset", &hasOffset, &offsetValue))
        return hot_fail(ctx, "code %d: invalid funcRef offset", item->index);
    if (!hasOffset && !json_int_field(ref, "operandOffset", &hasOffset, &offsetValue))
        return hot_fail(ctx, "code %d: invalid funcRef offset", item->index);

    if (!hasOffset) {
        bool hasInsn = false;
        int64_t insnOffset = 0;
        if (!json_int_field(ref, "instructionOffset", &hasInsn, &insnOffset) || !hasInsn)
            return hot_fail(ctx, "code %d: funcRef is missing offset", item->index);
        offsetValue = insnOffset + 4;
    }

    if (offsetValue < 0 || (uint64_t) offsetValue + 4 > (uint64_t) item->length)
        return hot_fail(ctx, "code %d: funcRef offset %lld is outside the bytecode",
                        item->index, (long long) offsetValue);

    bool pushRef = false;
    if (!json_bool_field(ref, "pushRef", &pushRef))
        return hot_fail(ctx, "code %d: invalid funcRef pushRef", item->index);

    const char* name = json_string_field(ref, "name");
    int32_t funcIndex = -1;

    if (name != NULL && name[0] != '\0') {
        funcIndex = resolve_or_create_function(ctx, name);
    } else {
        bool hasIndex = false;
        int64_t indexValue = 0;
        if (!json_int_field(ref, "index", &hasIndex, &indexValue))
            return hot_fail(ctx, "code %d: invalid funcRef index", item->index);
        if (!hasIndex && !json_int_field(ref, "funcIndex", &hasIndex, &indexValue))
            return hot_fail(ctx, "code %d: invalid funcRef index", item->index);
        if (!hasIndex)
            return hot_fail(ctx, "code %d: funcRef is missing name/index", item->index);
        funcIndex = (int32_t) indexValue;
    }

    if (funcIndex < 0)
        return hot_fail(ctx, "code %d: could not resolve function reference", item->index);

    uint8_t* operandPtr = bytecode + (size_t) offsetValue;
    uint32_t value;
    if (pushRef) {
        if ((uint32_t) funcIndex > 0x00FFFFFFu)
            return hot_fail(ctx, "code %d: function index out of range for pushref", item->index);
        value = ((uint32_t) ASSET_TYPE_SCRIPT << 24) | ((uint32_t) funcIndex & 0x00FFFFFFu);
    } else {
        if ((uint32_t) funcIndex > 0x07FFFFFFu)
            return hot_fail(ctx, "code %d: function index out of range", item->index);
        value = (uint32_t) funcIndex;
    }

    BinaryUtils_writeUint32(operandPtr, value);
    return true;
}

static bool patch_code_references(HotApplyContext* ctx, PatchCodeItem* item, uint8_t* bytecode) {
    JsonValue* varRefs = item->varRefs;
    if (varRefs != NULL) {
        if (!JsonReader_isArray(varRefs))
            return hot_fail(ctx, "code %d: varRefs must be an array", item->index);
        int count = JsonReader_arrayLength(varRefs);
        for (int i = 0; i < count; i++)
            if (!patch_variable_ref(ctx, item, bytecode, JsonReader_getArrayElement(varRefs, i)))
                return false;
    }

    JsonValue* funcRefs = item->funcRefs;
    if (funcRefs != NULL) {
        if (!JsonReader_isArray(funcRefs))
            return hot_fail(ctx, "code %d: funcRefs must be an array", item->index);
        int count = JsonReader_arrayLength(funcRefs);
        for (int i = 0; i < count; i++)
            if (!patch_function_ref(ctx, item, bytecode, JsonReader_getArrayElement(funcRefs, i)))
                return false;
    }

    return true;
}


static void free_patch_items(PatchCodeItem* items, int count) {
    if (items == NULL)
        return;
    for (int i = 0; i < count; i++)
        free(items[i].bytecode);
    free(items);
}

static bool decode_bytecode(HotApplyContext* ctx, int32_t codeIndex, const char* encoded,
                            uint8_t** outBytes, uint32_t* outLength) {
    *outBytes = NULL;
    *outLength = 0;

    if (encoded == NULL || encoded[0] == '\0')
        return true;

    size_t inLength = strlen(encoded);
    if (inLength % 4 != 0)
        return hot_fail(ctx, "code %d: bytecode is not valid base64", codeIndex);

    size_t capacity = (inLength / 4) * 3 + 3;
    uint8_t* bytes = (uint8_t*) safeMalloc(capacity);
    unsigned int decoded = base64_decode(encoded, (unsigned int) inLength, bytes);
    if (decoded > 0xFFFFFFFFu) {
        free(bytes);
        return hot_fail(ctx, "code %d: decoded bytecode is too large", codeIndex);
    }

    *outBytes = bytes;
    *outLength = (uint32_t) decoded;
    return true;
}

static bool collect_code_items(HotApplyContext* ctx, JsonValue* array,
                               PatchCodeItem** outItems, int* outCount, size_t* outTotalBytes) {
    *outItems = NULL;
    *outCount = 0;
    *outTotalBytes = 0;

    if (array == NULL)
        return true;
    if (!JsonReader_isArray(array))
        return hot_fail(ctx, "\"codes\" must be an array");

    int count = JsonReader_arrayLength(array);
    if (count <= 0)
        return true;

    PatchCodeItem* items = (PatchCodeItem*) safeCalloc((size_t) count, sizeof(PatchCodeItem));
    uint32_t oldCodeCount = ctx->dataWin->code.count;
    size_t totalBytes = 0;

    for (int i = 0; i < count; i++) {
        JsonValue* value = JsonReader_getArrayElement(array, i);
        if (!JsonReader_isObject(value)) {
            free_patch_items(items, i);
            return hot_fail(ctx, "codes[%d] must be an object", i);
        }

        PatchCodeItem* item = &items[i];
        bool hasIndex = false;
        int64_t indexValue = 0;
        if (!json_int_field(value, "index", &hasIndex, &indexValue) || !hasIndex ||
            indexValue < 0 || indexValue > INT32_MAX) {
            free_patch_items(items, i);
            return hot_fail(ctx, "codes[%d] has an invalid index", i);
        }
        item->index = (int32_t) indexValue;

        item->name = json_string_field(value, "name");
        if ((uint32_t) item->index >= oldCodeCount &&
            (item->name == NULL || item->name[0] == '\0')) {
            free_patch_items(items, i);
            return hot_fail(ctx, "code %d: name is required when appending a new entry", item->index);
        }

        int64_t number = 0;
        bool present = false;
        if (!json_int_field(value, "localsCount", &present, &number) ||
            (present && (number < 0 || number > 0xFFFF))) {
            free_patch_items(items, i);
            return hot_fail(ctx, "code %d: invalid localsCount", item->index);
        }
        item->hasLocalsCount = present;
        item->localsCount = present ? (uint16_t) number : 0;

        present = false;
        if (!json_int_field(value, "argumentsCount", &present, &number) ||
            (present && (number < 0 || number > 0xFFFF))) {
            free_patch_items(items, i);
            return hot_fail(ctx, "code %d: invalid argumentsCount", item->index);
        }
        item->hasArgumentsCount = present;
        item->argumentsCount = present ? (uint16_t) number : 0;

        const char* encoded = json_string_field(value, "bytecode");
        if (!decode_bytecode(ctx, item->index, encoded, &item->bytecode, &item->length)) {
            free_patch_items(items, i + 1);
            return false;
        }

        item->varRefs = object_get(value, "varRefs");
        if (item->varRefs == NULL)
            item->varRefs = object_get(value, "variableRefs");
        if (item->varRefs != NULL && !JsonReader_isArray(item->varRefs)) {
            free_patch_items(items, i + 1);
            return hot_fail(ctx, "code %d: varRefs must be an array", item->index);
        }

        item->funcRefs = object_get(value, "funcRefs");
        if (item->funcRefs == NULL)
            item->funcRefs = object_get(value, "functionRefs");
        if (item->funcRefs != NULL && !JsonReader_isArray(item->funcRefs)) {
            free_patch_items(items, i + 1);
            return hot_fail(ctx, "code %d: funcRefs must be an array", item->index);
        }

        item->locals = object_get(value, "locals");
        if (item->locals != NULL && !JsonReader_isArray(item->locals)) {
            free_patch_items(items, i + 1);
            return hot_fail(ctx, "code %d: locals must be an array", item->index);
        }

        if (item->length > 0) {
            if (totalBytes > (size_t) UINT32_MAX - item->length) {
                free_patch_items(items, i + 1);
                return hot_fail(ctx, "code %d: total bytecode size overflow", item->index);
            }
            totalBytes += item->length;
        }
    }

    *outItems = items;
    *outCount = count;
    *outTotalBytes = totalBytes;
    return true;
}

static void mark_identical_code(DataWin* dw, PatchCodeItem* items, int count, uint32_t oldCodeCount) {
    size_t base = dw->bytecodeBufferBase;

    for (int i = 0; i < count; i++) {
        PatchCodeItem* item = &items[i];
        if ((uint32_t) item->index >= oldCodeCount || item->length == 0)
            continue;

        CodeEntry* entry = &dw->code.entries[item->index];
        if (!entry->present || entry->length != item->length)
            continue;
        if (entry->bytecodeAbsoluteOffset < base)
            continue;

        const uint8_t* oldBytes = dw->bytecodeBuffer + (entry->bytecodeAbsoluteOffset - base);
        if (memcmp(oldBytes, item->bytecode, item->length) == 0)
            item->identical = true;
    }
}

static bool apply_code_items(HotApplyContext* ctx, PatchCodeItem* items, int count,
                             size_t totalBytes, uint32_t oldCodeCount) {
    (void) totalBytes;
    DataWin* dw = ctx->dataWin;

    /* First make sure the entry array is large enough, so failures below leave
     * the DataWin in a consistent (if partially updated) state. */
    for (int i = 0; i < count; i++) {
        uint32_t index = (uint32_t) items[i].index;
        if (index >= dw->code.count)
            grow_code(dw, index + 1);
    }

    size_t oldUsed = 0;
    {
        uint64_t end = 0;
        for (uint32_t i = 0; i < oldCodeCount && i < dw->code.count; i++) {
            CodeEntry* entry = &dw->code.entries[i];
            if (!entry->present)
                continue;
            uint64_t entryEnd = (uint64_t) entry->bytecodeAbsoluteOffset + entry->length;
            if (entryEnd > end)
                end = entryEnd;
        }
        if (end > dw->bytecodeBufferBase)
            oldUsed = (size_t) (end - dw->bytecodeBufferBase);
    }

    size_t totalToAppend = 0;
    for (int i = 0; i < count; i++) {
        if (!items[i].identical)
            totalToAppend += items[i].length;
    }

    /* Give every item a valid (in-bounds) bytecode offset before the copy loop;
     * zero-length entries keep this value. */
    for (int i = 0; i < count; i++) {
        items[i].assignedOffset = (uint32_t) ((uint64_t) dw->bytecodeBufferBase + oldUsed);
    }
    if (totalToAppend > 0) {
        uint64_t finalSize = (uint64_t) dw->bytecodeBufferBase + oldUsed + totalToAppend;
        if (finalSize > 0xFFFFFFFFu)
            return hot_fail(ctx, "patched bytecode would exceed the 32-bit code offset space");

        dw->bytecodeBuffer = (uint8_t*) safeRealloc(dw->bytecodeBuffer, oldUsed + totalToAppend);
        size_t cursor = oldUsed;
        for (int i = 0; i < count; i++) {
            PatchCodeItem* item = &items[i];
            if (item->identical || item->length == 0)
                continue;
            memcpy(dw->bytecodeBuffer + cursor, item->bytecode, item->length);
            item->assignedOffset = (uint32_t) (dw->bytecodeBufferBase + cursor);
            cursor += item->length;
        }
    }

    for (int i = 0; i < count; i++) {
        PatchCodeItem* item = &items[i];
        uint32_t index = (uint32_t) item->index;
        CodeEntry* entry = &dw->code.entries[index];
        bool replacing = index < oldCodeCount;

        if (item->identical || totalToAppend == 0) {
            if (item->identical) {
                ctx->skipped++;
                continue;
            }
            if (item->length != 0) {
                /* totalToAppend == 0 while length != 0 should be impossible. */
                return hot_fail(ctx, "code %d: internal bytecode append error", item->index);
            }
        }

        if (replacing) {
            entry->present = true;
            if ((entry->name == NULL || entry->name[0] == '\0') &&
                item->name != NULL && item->name[0] != '\0')
                entry->name = hot_strdup(item->name);
            if (item->hasLocalsCount)
                entry->localsCount = item->localsCount;
            if (item->hasArgumentsCount)
                entry->argumentsCount = item->argumentsCount;
        } else {
            entry->present = true;
            entry->name = hot_strdup(item->name);
            entry->localsCount = item->hasLocalsCount ? item->localsCount : 0;
            entry->argumentsCount = item->hasArgumentsCount ? item->argumentsCount : 0;
        }

        entry->length = item->length;
        entry->offset = 0;
        entry->bytecodeAbsoluteOffset = item->assignedOffset;

        if (entry->length > 0) {
            uint8_t* codeBytes = dw->bytecodeBuffer +
                                 (size_t) (entry->bytecodeAbsoluteOffset - dw->bytecodeBufferBase);
            if (!patch_code_references(ctx, item, codeBytes))
                return false;
        }

        if (!apply_code_locals(ctx, item, index))
            return false;

        ctx->applied++;
    }

    return true;
}

/* ===[ Locals application ]=== */

static bool apply_code_locals(HotApplyContext* ctx, PatchCodeItem* item, uint32_t codeIndex) {
    if (item->locals == NULL)
        return true;

    DataWin* dw = ctx->dataWin;
    if (!JsonReader_isArray(item->locals))
        return hot_fail(ctx, "code %d: locals must be an array", item->index);

    int count = JsonReader_arrayLength(item->locals);
    if (count < 0)
        count = 0;

    const char* codeName = item->name;
    if (codeIndex < dw->code.count && dw->code.entries[codeIndex].name != NULL)
        codeName = dw->code.entries[codeIndex].name;
    if (codeName == NULL) {
        codeName = item->name;
    }
    if (codeName == NULL)
        return hot_fail(ctx, "code %d: cannot attach locals without a code name", item->index);

    LocalVar* locals = NULL;
    if (count > 0) {
        locals = (LocalVar*) safeCalloc((size_t) count, sizeof(LocalVar));
        for (int i = 0; i < count; i++) {
            JsonValue* value = JsonReader_getArrayElement(item->locals, i);
            if (!JsonReader_isObject(value)) {
                free(locals);
                return hot_fail(ctx, "code %d: locals[%d] must be an object", item->index, i);
            }

            bool hasVarId = false;
            int64_t varId = i;
            if (!json_int_field(value, "varId", &hasVarId, &varId) ||
                (hasVarId && (varId < 0 || varId > UINT32_MAX))) {
                free(locals);
                return hot_fail(ctx, "code %d: locals[%d] has an invalid varId", item->index, i);
            }

            const char* name = json_string_field(value, "name");
            if (name == NULL) {
                free(locals);
                return hot_fail(ctx, "code %d: locals[%d] is missing a name", item->index, i);
            }

            locals[i].varID = hasVarId ? (uint32_t) varId : (uint32_t) i;
            locals[i].name = hot_strdup(name);
        }
    }

    int32_t existing = find_code_locals(dw, codeName);
    if (existing >= 0) {
        CodeLocals* target = &dw->func.codeLocals[existing];
        free(target->locals);
        target->locals = locals;
        target->localVarCount = (uint32_t) count;
    } else {
        uint32_t newIndex = dw->func.codeLocalsCount;
        dw->func.codeLocals = (CodeLocals*) safeRealloc(dw->func.codeLocals,
                                                        (size_t) (newIndex + 1) * sizeof(CodeLocals));
        memset(&dw->func.codeLocals[newIndex], 0, sizeof(CodeLocals));
        dw->func.codeLocals[newIndex].name = hot_strdup(codeName);
        dw->func.codeLocals[newIndex].locals = locals;
        dw->func.codeLocals[newIndex].localVarCount = (uint32_t) count;
        dw->func.codeLocalsCount = newIndex + 1;
    }

    if (!item->hasLocalsCount && codeIndex < dw->code.count && dw->code.entries[codeIndex].localsCount == 0)
        dw->code.entries[codeIndex].localsCount = (uint16_t) (count > 0xFFFF ? 0xFFFF : count);

    /* The slot map is keyed by varID; a replaced local set may have changed it.
     * Rebuild lazily unless a frame is currently inside this code. */
    if (ctx->vm != NULL && ctx->vm->codeLocalsSlotMaps != NULL && codeIndex < dw->code.count) {
        bool active = ctx->vm->currentCallFrame != NULL &&
                      ctx->vm->currentCallFrame->codeIndex == codeIndex;
        if (!active) {
            IntIntHashMap_free(&ctx->vm->codeLocalsSlotMaps[codeIndex]);
            memset(&ctx->vm->codeLocalsSlotMaps[codeIndex], 0, sizeof(IntIntHashMap));
        }
    }

    return true;
}

/* ===[ Patch sections ]=== */

static bool apply_strings(HotApplyContext* ctx, JsonValue* array) {
    if (array == NULL)
        return true;
    if (!JsonReader_isArray(array))
        return hot_fail(ctx, "\"strings\" must be an array");

    DataWin* dw = ctx->dataWin;
    int count = JsonReader_arrayLength(array);

    for (int i = 0; i < count; i++) {
        JsonValue* value = JsonReader_getArrayElement(array, i);
        if (!JsonReader_isObject(value))
            return hot_fail(ctx, "strings[%d] must be an object", i);

        bool hasIndex = false;
        int64_t indexValue = 0;
        if (!json_int_field(value, "index", &hasIndex, &indexValue) ||
            !hasIndex || indexValue < 0 || indexValue > UINT32_MAX)
            return hot_fail(ctx, "strings[%d] has an invalid index", i);

        const char* text = json_string_field(value, "value");
        if (text == NULL)
            return hot_fail(ctx, "strings[%d] is missing its value", i);

        uint32_t index = (uint32_t) indexValue;
        if (index < dw->strg.count) {
            if (name_equal(dw->strg.strings[index], text)) {
                ctx->skipped++;
                continue;
            }
            return hot_fail(ctx, "string index %u already exists with a different value", index);
        }

        if (!grow_strg(dw, index + 1))
            return false;

        dw->strg.strings[index] = hot_strdup(text);
        ctx->applied++;
    }

    return true;
}

static bool apply_variables(HotApplyContext* ctx, JsonValue* array) {
    if (array == NULL)
        return true;
    if (!JsonReader_isArray(array))
        return hot_fail(ctx, "\"variables\" must be an array");

    DataWin* dw = ctx->dataWin;
    int count = JsonReader_arrayLength(array);

    for (int i = 0; i < count; i++) {
        JsonValue* value = JsonReader_getArrayElement(array, i);
        if (!JsonReader_isObject(value))
            return hot_fail(ctx, "variables[%d] must be an object", i);

        bool hasIndex = false;
        int64_t indexValue = 0;
        if (!json_int_field(value, "index", &hasIndex, &indexValue) ||
            !hasIndex || indexValue < 0 || indexValue > UINT32_MAX)
            return hot_fail(ctx, "variables[%d] has an invalid index", i);

        const char* name = json_string_field(value, "name");
        if (name == NULL)
            return hot_fail(ctx, "variables[%d] is missing a name", i);

        bool hasInstanceType = false;
        int64_t instanceTypeValue = 0;
        if (!json_int_field(value, "instanceType", &hasInstanceType, &instanceTypeValue)) {
            hasInstanceType = false;
            instanceTypeValue = 0;
        }

        bool hasVarId = false;
        int64_t varIdValue = -1;
        if (!json_int_field(value, "varId", &hasVarId, &varIdValue)) {
            hasVarId = false;
            varIdValue = -1;
        }

        uint32_t index = (uint32_t) indexValue;
        if (index < dw->vari.variableCount) {
            Variable* existing = &dw->vari.variables[index];
            bool same = name_equal(existing->name, name) &&
                        (!hasVarId || existing->varID == (int32_t) varIdValue) &&
                        (!hasInstanceType || existing->instanceType == (int32_t) instanceTypeValue);
            if (same) {
                ctx->skipped++;
                continue;
            }
            return hot_fail(ctx, "variable index %u already exists with a different definition", index);
        }

        if (!grow_vari(dw, index + 1))
            return false;

        Variable* variable = &dw->vari.variables[index];
        variable->name = hot_strdup(name);
        variable->instanceType = hasInstanceType ? (int32_t) instanceTypeValue : INSTANCE_SELF;
        variable->varID = (hasVarId && varIdValue >= 0) ? (int32_t) varIdValue : next_variable_id(ctx);
        variable->occurrences = 0;
        variable->firstAddress = 0;
        if (variable->instanceType == INSTANCE_BUILTIN || variable->varID == VARIABLE_BUILTIN)
            variable->builtinVarId = VMBuiltins_resolveBuiltinVarId(name);
        else
            variable->builtinVarId = -1;

        if (ctx->vm != NULL && variable->varID >= ctx->vm->nextDynamicVarID)
            ctx->vm->nextDynamicVarID = variable->varID + 1;

        ctx->applied++;
    }

    return true;
}

static bool apply_functions(HotApplyContext* ctx, JsonValue* array) {
    if (array == NULL)
        return true;
    if (!JsonReader_isArray(array))
        return hot_fail(ctx, "\"functions\" must be an array");

    DataWin* dw = ctx->dataWin;
    int count = JsonReader_arrayLength(array);

    for (int i = 0; i < count; i++) {
        JsonValue* value = JsonReader_getArrayElement(array, i);
        if (!JsonReader_isObject(value))
            return hot_fail(ctx, "functions[%d] must be an object", i);

        bool hasIndex = false;
        int64_t indexValue = 0;
        if (!json_int_field(value, "index", &hasIndex, &indexValue) ||
            !hasIndex || indexValue < 0 || indexValue > UINT32_MAX)
            return hot_fail(ctx, "functions[%d] has an invalid index", i);

        const char* name = json_string_field(value, "name");
        if (name == NULL)
            return hot_fail(ctx, "functions[%d] is missing a name", i);

        uint32_t index = (uint32_t) indexValue;
        if (index < dw->func.functionCount) {
            if (name_equal(dw->func.functions[index].name, name)) {
                ctx->skipped++;
                continue;
            }
            return hot_fail(ctx, "function index %u already exists with a different name", index);
        }

        if (!grow_func(dw, index + 1))
            return false;

        dw->func.functions[index].name = hot_strdup(name);
        dw->func.functions[index].occurrences = 0;
        dw->func.functions[index].firstAddress = 0;
        ctx->applied++;
    }

    return true;
}

/* ===[ Root application ]=== */

static bool apply_patch_object(HotApplyContext* ctx, JsonValue* root) {
    if (!JsonReader_isObject(root))
        return hot_fail(ctx, "patch root must be an object");

    uint32_t oldCodeCount = ctx->dataWin->code.count;

    /* New resources must exist before code references are resolved. */
    if (!apply_strings(ctx, object_get(root, "strings")))
        return false;
    if (!apply_variables(ctx, object_get(root, "variables")))
        return false;
    if (!apply_functions(ctx, object_get(root, "functions")))
        return false;

    JsonValue* codes = object_get(root, "codes");
    PatchCodeItem* items = NULL;
    int itemCount = 0;
    size_t totalBytes = 0;
    if (!collect_code_items(ctx, codes, &items, &itemCount, &totalBytes))
        return false;

    if (itemCount > 0)
        mark_identical_code(ctx->dataWin, items, itemCount, oldCodeCount);

    bool ok = apply_code_items(ctx, items, itemCount, totalBytes, oldCodeCount);
    free_patch_items(items, itemCount);
    return ok;
}

static bool apply_patch_root(HotApplyContext* ctx, JsonValue* root) {
    if (JsonReader_isArray(root)) {
        int count = JsonReader_arrayLength(root);
        for (int i = 0; i < count; i++) {
            if (!apply_patch_root(ctx, JsonReader_getArrayElement(root, i)))
                return false;
        }
        return true;
    }
    return apply_patch_object(ctx, root);
}

/* ===[ Queue validation ]=== */

static bool validate_patch_section(JsonValue* section, const char* name, char** errorText) {
    if (section == NULL)
        return true;
    if (!JsonReader_isArray(section)) {
        char* message = alloc_format("\"%s\" must be an array", name);
        set_error_text(errorText, message);
        free(message);
        return false;
    }

    int count = JsonReader_arrayLength(section);
    for (int i = 0; i < count; i++) {
        if (!JsonReader_isObject(JsonReader_getArrayElement(section, i))) {
            char* message = alloc_format("%s[%d] must be an object", name, i);
            set_error_text(errorText, message);
            free(message);
            return false;
        }
    }
    return true;
}

static bool validate_patch_json(JsonValue* root, char** errorText) {
    if (JsonReader_isArray(root)) {
        int count = JsonReader_arrayLength(root);
        for (int i = 0; i < count; i++) {
            if (!validate_patch_json(JsonReader_getArrayElement(root, i), errorText))
                return false;
        }
        return true;
    }

    if (!JsonReader_isObject(root)) {
        set_error_text(errorText, "patch root must be an object");
        return false;
    }

    return validate_patch_section(object_get(root, "codes"), "codes", errorText) &&
           validate_patch_section(object_get(root, "strings"), "strings", errorText) &&
           validate_patch_section(object_get(root, "variables"), "variables", errorText) &&
           validate_patch_section(object_get(root, "functions"), "functions", errorText);
}

/* ===[ Public API ]=== */

int32_t BsdbgHotreload_Queue(BsdbgControl* c, const char* json, char** errorText) {
    if (errorText != NULL)
        *errorText = NULL;

    if (c == NULL) {
        set_error_text(errorText, "no session");
        return -1;
    }
    if (json == NULL || json[0] == '\0') {
        set_error_text(errorText, "patch JSON is empty");
        return -1;
    }

    JsonValue* parsed = JsonReader_parse(json);
    if (parsed == NULL) {
        set_error_text(errorText, "patch JSON is malformed");
        return -1;
    }

    bool valid = validate_patch_json(parsed, errorText);
    JsonReader_free(parsed);
    if (!valid)
        return -1;

    hot_register_atexit();

    Bsdbg_Lock(c);
    char* existing = c->pendingPatchJson;
    char* copy = safeStrdup(json);
    if (existing != NULL) {
        size_t existingLength = strlen(existing);
        size_t copyLength = strlen(copy);
        if (existingLength > SIZE_MAX - copyLength - 4) {
            free(copy);
            Bsdbg_Unlock(c);
            set_error_text(errorText, "patch queue is too large");
            return -1;
        }
        char* merged = (char*) safeMalloc(existingLength + copyLength + 4);
        memcpy(merged, "[", 1);
        memcpy(merged + 1, existing, existingLength);
        merged[existingLength + 1] = ',';
        memcpy(merged + existingLength + 2, copy, copyLength);
        merged[existingLength + copyLength + 2] = ']';
        merged[existingLength + copyLength + 3] = '\0';
        free(existing);
        free(copy);
        c->pendingPatchJson = merged;
    } else {
        c->pendingPatchJson = copy;
    }
    Bsdbg_Unlock(c);
    return 0;
}

static void store_last_error(BsdbgControl* c, const char* message) {
    if (c == NULL)
        return;
    Bsdbg_Lock(c);
    free(c->lastPatchError);
    c->lastPatchError = message != NULL ? safeStrdup(message) : NULL;
    Bsdbg_Unlock(c);
}

void BsdbgHotreload_ApplyPending(BsdbgControl* c) {
    if (c == NULL)
        return;

    Bsdbg_Lock(c);
    char* json = c->pendingPatchJson;
    c->pendingPatchJson = NULL;
    DataWin* dataWin = c->dataWin;
    VMContext* vm = c->vm;
    Bsdbg_Unlock(c);

    if (json == NULL)
        return;

    char* error = NULL;
    HotApplyContext ctx;
    memset(&ctx, 0, sizeof(ctx));
    ctx.control = c;
    ctx.dataWin = dataWin;
    ctx.vm = vm;

    if (dataWin == NULL) {
        error = safeStrdup("the game is not running");
    } else {
        JsonValue* root = JsonReader_parse(json);
        if (root == NULL) {
            error = safeStrdup("patch JSON is malformed");
        } else {
            uint32_t oldCodeCount = dataWin->code.count;
            if (!apply_patch_root(&ctx, root)) {
                error = ctx.error != NULL ? ctx.error : safeStrdup("patch application failed");
                ctx.error = NULL;
            }
            /* Even on failure some earlier sections may have been applied. */
            rebuild_vm_caches(&ctx, oldCodeCount);
            JsonReader_free(root);
        }
    }

    free(json);

    if (error != NULL) {
        store_last_error(c, error);
        char* escaped = make_json_string(error);
        char* payload = alloc_format("{\"message\":\"%s\"}", escaped);
        Bsdbg_EmitEvent(c, BSDBG_EVENT_ERROR, payload);
        free(payload);
        free(escaped);
        free(error);
        return;
    }

    store_last_error(c, NULL);
    char* payload = alloc_format("{\"applied\":%d,\"skipped\":%d}", ctx.applied, ctx.skipped);
    Bsdbg_EmitEvent(c, BSDBG_EVENT_RELOADED, payload);
    free(payload);
}
