using System;
using System.Collections.Generic;
using System.IO;
using UndertaleModLib.Models;

namespace UndertaleModLib.Compiler;

/// <summary>A variable operand reference inside a serialized code entry.</summary>
public sealed class CodeVarReference
{
    /// <summary>Byte offset of the 4-byte operand word, relative to the code entry start.</summary>
    public int Offset { get; set; }
    public string Name { get; set; }
    public int VarId { get; set; }
    public int InstanceType { get; set; }
    public byte VarType { get; set; }
}

/// <summary>A function operand reference inside a serialized code entry.</summary>
public sealed class CodeFuncReference
{
    public int Offset { get; set; }
    public string Name { get; set; }
    /// <summary>True for BC17 BREAK PUSHREF operands (24-bit function index).</summary>
    public bool PushRef { get; set; }
}

/// <summary>Bytecode and operand references for a single code entry.</summary>
public sealed class SerializedCode
{
    public byte[] Bytecode { get; set; }
    public List<CodeVarReference> VarRefs { get; } = new();
    public List<CodeFuncReference> FuncRefs { get; } = new();
}

/// <summary>
/// Serializes one code entry to standalone VM bytecode, keeping variable and
/// function operands as name string indices (the form the live hot-reload
/// patcher expects). This is intentionally not the full data.win reference-chain
/// form: each operand stays independently resolvable by name.
/// </summary>
public static class CodeBytecodeSerializer
{
    public static SerializedCode Serialize(UndertaleData data, UndertaleCode code)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(code);

        if (data.GeneralInfo is null || data.GeneralInfo.BytecodeVersion <= 14)
            throw new NotSupportedException("Code hot reload requires bytecode version 15 or newer.");

        // UndertaleResourceById serialization looks the resource up in the chunk
        // index dictionary; generate them for this code-only write.
        foreach (UndertaleChunk chunk in data.FORM.Chunks.Values)
        {
            if (chunk is IUndertaleListChunk listChunk)
                listChunk.GenerateIndexDict();
        }

        SerializedCode result = new();
        using MemoryStream stream = new();
        using (UndertaleWriter writer = new(stream))
        {
            writer.undertaleData = data;
            writer.Bytecode14OrLower = false;

            foreach (UndertaleInstruction instr in code.Instructions)
            {
                int start = (int)writer.Position;

                UndertaleVariable variable = instr.ValueVariable;
                if (variable is not null)
                    variable.NameStringID = data.Strings.IndexOf(variable.Name);

                UndertaleFunction function = instr.ValueFunction;
                if (function is not null)
                    function.NameStringID = data.Strings.IndexOf(function.Name);

                instr.Serialize(writer);

                // Every supported variable/function operand is the second word.
                if (variable is not null)
                {
                    result.VarRefs.Add(new CodeVarReference
                    {
                        Offset = start + 4,
                        Name = variable.Name?.Content ?? "",
                        VarId = variable.VarID,
                        InstanceType = (int)variable.InstanceType,
                        VarType = (byte)instr.ReferenceType
                    });
                }

                if (function is not null)
                {
                    result.FuncRefs.Add(new CodeFuncReference
                    {
                        Offset = start + 4,
                        Name = function.Name?.Content ?? "",
                        PushRef = instr.Kind == UndertaleInstruction.Opcode.Break
                                  && instr.Type1 == UndertaleInstruction.DataType.Int32
                    });
                }
            }

            writer.Flush();
        }

        foreach (UndertaleChunk chunk in data.FORM.Chunks.Values)
        {
            if (chunk is IUndertaleListChunk listChunk)
                listChunk.ClearIndexDict();
        }

        result.Bytecode = stream.ToArray();
        return result;
    }
}
