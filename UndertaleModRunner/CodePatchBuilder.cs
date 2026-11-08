using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UndertaleModLib;
using UndertaleModLib.Compiler;
using UndertaleModLib.Models;

namespace UndertaleModRunner;

/// <summary>
/// Builds the JSON payload consumed by <c>Bsdbg_ApplyCodePatch</c>.
/// The patch carries standalone bytecode plus name-based variable/function
/// references; the native runner resolves them against the live game and
/// rebuilds its VM caches.
/// </summary>
public static class CodePatchBuilder
{
    private sealed class PatchRoot
    {
        [JsonProperty("codes")] public List<PatchCode> Codes { get; } = new();
        [JsonProperty("strings")] public List<PatchString> Strings { get; } = new();
    }

    private sealed class PatchCode
    {
        [JsonProperty("index")] public int Index { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("localsCount")] public uint LocalsCount { get; set; }
        [JsonProperty("argumentsCount")] public ushort ArgumentsCount { get; set; }
        [JsonProperty("bytecode")] public string Bytecode { get; set; }
        [JsonProperty("varRefs")] public List<PatchVarRef> VarRefs { get; } = new();
        [JsonProperty("funcRefs")] public List<PatchFuncRef> FuncRefs { get; } = new();
        [JsonProperty("locals")] public List<PatchLocal> Locals { get; } = new();
    }

    private sealed class PatchVarRef
    {
        [JsonProperty("offset")] public int Offset { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("varId")] public int VarId { get; set; }
        [JsonProperty("instanceType")] public int InstanceType { get; set; }
        [JsonProperty("varType")] public int VarType { get; set; }
    }

    private sealed class PatchFuncRef
    {
        [JsonProperty("offset")] public int Offset { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("pushRef")] public bool PushRef { get; set; }
    }

    private sealed class PatchLocal
    {
        [JsonProperty("varId")] public int VarId { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
    }

    private sealed class PatchString
    {
        [JsonProperty("index")] public int Index { get; set; }
        [JsonProperty("value")] public string Value { get; set; }
    }

    /// <summary>
    /// Builds a patch for the supplied code entries. Only code content and
    /// string literals are carried; object/event/resource changes are out of
    /// scope for hot reload (save and restart for those).
    /// </summary>
    public static string Build(UndertaleData data, IEnumerable<UndertaleCode> changedCodes)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(changedCodes);

        PatchRoot root = new();
        HashSet<int> seenCodeIndices = new();
        Dictionary<int, string> strings = new();

        foreach (UndertaleCode code in changedCodes.Distinct())
        {
            int index = data.Code.IndexOf(code);
            if (index < 0)
                throw new InvalidOperationException($"Code entry \"{code.Name?.Content}\" is not part of the loaded data.");

            if (!seenCodeIndices.Add(index))
                continue;

            SerializedCode serialized = CodeBytecodeSerializer.Serialize(data, code);

            PatchCode patch = new()
            {
                Index = index,
                Name = code.Name?.Content ?? "",
                LocalsCount = code.LocalsCount,
                ArgumentsCount = code.ArgumentsCount,
                Bytecode = Convert.ToBase64String(serialized.Bytecode)
            };

            foreach (CodeVarReference varRef in serialized.VarRefs)
            {
                patch.VarRefs.Add(new PatchVarRef
                {
                    Offset = varRef.Offset,
                    Name = varRef.Name,
                    VarId = varRef.VarId,
                    InstanceType = varRef.InstanceType,
                    VarType = varRef.VarType
                });
            }

            foreach (CodeFuncReference funcRef in serialized.FuncRefs)
            {
                patch.FuncRefs.Add(new PatchFuncRef
                {
                    Offset = funcRef.Offset,
                    Name = funcRef.Name,
                    PushRef = funcRef.PushRef
                });
            }

            // Locals are indexed parallel to Code in UndertaleModLib.
            if (data.CodeLocals is not null && index < data.CodeLocals.Count)
            {
                UndertaleCodeLocals locals = data.CodeLocals[index];
                foreach (UndertaleCodeLocals.LocalVar local in locals.Locals)
                {
                    patch.Locals.Add(new PatchLocal
                    {
                        VarId = (int)local.Index,
                        Name = local.Name?.Content ?? ""
                    });
                }
            }

            // String literals used by Push.s instructions. The runner appends
            // any index that does not exist yet; existing entries must match.
            foreach (UndertaleInstruction instr in code.Instructions)
            {
                if (instr.Type1 != UndertaleInstruction.DataType.String)
                    continue;
                UndertaleString value = instr.ValueString?.Resource;
                if (value is null)
                    continue;

                int stringIndex = data.Strings.IndexOf(value);
                if (stringIndex < 0)
                    throw new InvalidOperationException(
                        $"String literal used by \"{code.Name?.Content}\" is not registered in the STRG chunk.");
                strings[stringIndex] = value.Content;
            }

            root.Codes.Add(patch);
        }

        foreach (KeyValuePair<int, string> pair in strings.OrderBy(p => p.Key))
            root.Strings.Add(new PatchString { Index = pair.Key, Value = pair.Value });

        return JsonConvert.SerializeObject(root, Formatting.None);
    }
}
