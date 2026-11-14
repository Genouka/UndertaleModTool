#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Underanalyzer.Decompiler;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;

namespace UndertaleModLib.Diff;

/// <summary>
/// Code comparison: a normalized instruction fingerprint that ignores address/ID renumbering,
/// plus a decompiled GML line diff for the selected code entry.
/// </summary>
public static class CodeFingerprint
{
    public static byte[] Compute(UndertaleCode code, UndertaleData data)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, "code");
        AppendInt32(hash, code.ArgumentsCount);
        AppendInt32(hash, (int)code.LocalsCount);
        AppendInt32(hash, code.WeirdLocalFlag ? 1 : 0);

        (int startIndex, int endIndex) = GetSegmentInstructionRange(code);
        // Anonymous sub-entries share their parent's flat instruction list.
        IReadOnlyList<UndertaleInstruction> instructions = code.ParentEntry?.Instructions ?? code.Instructions;
        Dictionary<uint, int> addressToIndex = BuildAddressMap(instructions, startIndex, endIndex);
        uint segmentWords = 0;
        for (int i = startIndex; i < endIndex; i++)
        {
            segmentWords += GetInstructionSize(instructions[i]);
        }
        uint instructionAddress = 0;
        for (int index = startIndex; index < endIndex; index++)
        {
            UndertaleInstruction instruction = instructions[index];
            AppendString(hash, instruction.Kind.ToString());
            AppendInt32(hash, (int)instruction.Type1);
            AppendInt32(hash, (int)instruction.Type2);
            AppendInt32(hash, (int)instruction.ComparisonKind);
            AppendInt32(hash, instruction.Extra);
            AppendInt32(hash, instruction.ArgumentsCount);
            AppendInt32(hash, instruction.ExtendedKind);

            try
            {
                switch (UndertaleInstruction.GetInstructionType(instruction.Kind))
                {
                    case UndertaleInstruction.InstructionType.GotoInstruction:
                        AppendString(hash, GotoTarget(instruction, segmentWords, instructionAddress, addressToIndex));
                        break;

                    case UndertaleInstruction.InstructionType.PushInstruction:
                        AppendPushValue(hash, instruction, data);
                        break;

                    case UndertaleInstruction.InstructionType.PopInstruction:
                        AppendPopValue(hash, instruction, data);
                        break;

                    case UndertaleInstruction.InstructionType.CallInstruction:
                        AppendString(hash, FunctionName(instruction, data));
                        break;

                    case UndertaleInstruction.InstructionType.BreakInstruction:
                        if (instruction.ValueFunction is not null)
                            AppendString(hash, "function:" + instruction.ValueFunction.Name?.Content);
                        else if (IsAssetReference(instruction.IntArgument))
                            AppendString(hash, AssetReferenceToken(instruction.IntArgument, data));
                        else
                            AppendInt32(hash, instruction.IntArgument);
                        break;
                }
            }
            catch (Exception)
            {
                // Unknown opcode: fall back to the raw packed value so both sides stay comparable.
                AppendInt32(hash, (int)instruction.Kind);
            }

            instructionAddress += GetInstructionSize(instruction);
        }

        return hash.GetHashAndReset();
    }

    private static void AppendPushValue(IncrementalHash hash, UndertaleInstruction instruction, UndertaleData data)
    {
        switch (instruction.Type1)
        {
            case UndertaleInstruction.DataType.Variable:
                AppendString(hash, "variable");
                AppendString(hash, VariableName(instruction, data));
                AppendString(hash, InstanceToken(instruction, data));
                AppendInt32(hash, (int)instruction.ReferenceType);
                break;

            case UndertaleInstruction.DataType.Int32:
                if (instruction.ValueFunction is not null)
                    AppendString(hash, "function:" + instruction.ValueFunction.Name?.Content);
                else if (instruction.ValueVariable is not null)
                    AppendString(hash, "variable:" + instruction.ValueVariable.Name?.Content);
                else if (IsAssetReference(instruction.IntArgument))
                    AppendString(hash, AssetReferenceToken(instruction.IntArgument, data));
                else
                    AppendInt32(hash, instruction.IntArgument);
                break;

            case UndertaleInstruction.DataType.String:
                AppendString(hash, StringValue(instruction, data));
                break;

            case UndertaleInstruction.DataType.Int16:
                AppendInt32(hash, instruction.ValueShort);
                break;

            case UndertaleInstruction.DataType.Double:
                AppendInt64(hash, BitConverter.DoubleToInt64Bits(instruction.ValueDouble));
                break;

            case UndertaleInstruction.DataType.Int64:
                AppendInt64(hash, instruction.ValueLong);
                break;

            default:
                AppendInt32(hash, instruction.IntArgument);
                break;
        }
    }

    private static void AppendPopValue(IncrementalHash hash, UndertaleInstruction instruction, UndertaleData data)
    {
        if (instruction.Type1 == UndertaleInstruction.DataType.Variable)
        {
            AppendString(hash, "variable");
            AppendString(hash, VariableName(instruction, data));
            AppendString(hash, InstanceToken(instruction, data));
            AppendInt32(hash, (int)instruction.ReferenceType);
        }
        else if (instruction.Type1 == UndertaleInstruction.DataType.Int16)
        {
            AppendInt32(hash, instruction.SwapExtra);
        }
    }

    private static string GotoTarget(UndertaleInstruction instruction, uint segmentWords,
                                     uint instructionAddress, Dictionary<uint, int> addressToIndex)
    {
        if (instruction.JumpOffsetPopenvExitMagic)
            return "magic";

        long targetAddress = instructionAddress + instruction.JumpOffset;
        if (targetAddress == segmentWords)
            return "end";
        return targetAddress >= 0 && targetAddress <= uint.MaxValue &&
               addressToIndex.TryGetValue((uint)targetAddress, out int index)
            ? "target:" + index.ToString(CultureInfo.InvariantCulture)
            : "raw:" + instruction.JumpOffset.ToString(CultureInfo.InvariantCulture);
    }

    private static Dictionary<uint, int> BuildAddressMap(IReadOnlyList<UndertaleInstruction> instructions,
                                                         int startIndex, int endIndex)
    {
        Dictionary<uint, int> map = [];
        uint address = 0;
        for (int i = startIndex; i < endIndex; i++)
        {
            map[address] = i - startIndex;
            address += GetInstructionSize(instructions[i]);
        }
        return map;
    }

    /// <summary>
    /// Returns the instruction range belonging to a code entry. Root entries own the blob up to
    /// the first nested (anonymous) function; nested entries own the slice starting at their
    /// <see cref="UndertaleCode.Offset"/> up to the next nested entry.
    /// </summary>
    internal static (int Start, int End) GetSegmentInstructionRange(UndertaleCode code)
    {
        if (code.ParentEntry is null)
        {
            if (code.ChildEntries.Count == 0)
            {
                return (0, code.Instructions.Count);
            }
            uint first = uint.MaxValue;
            foreach (UndertaleCode child in code.ChildEntries)
            {
                if (child.Offset < first)
                {
                    first = child.Offset;
                }
            }
            int firstIndex = IndexAtOffset(code.Instructions, first, out _);
            return firstIndex < 0 ? (0, code.Instructions.Count) : (0, firstIndex);
        }

        UndertaleCode parent = code.ParentEntry;
        uint nextOffset = uint.MaxValue;
        foreach (UndertaleCode sibling in parent.ChildEntries)
        {
            if (sibling.Offset > code.Offset && sibling.Offset < nextOffset)
            {
                nextOffset = sibling.Offset;
            }
        }

        int start = IndexAtOffset(parent.Instructions, code.Offset, out _);
        if (start < 0)
        {
            return (0, parent.Instructions.Count);
        }
        int end = nextOffset == uint.MaxValue
            ? parent.Instructions.Count
            : IndexAtOffset(parent.Instructions, nextOffset, out _);
        if (end < 0)
        {
            end = parent.Instructions.Count;
        }
        return (start, end);
    }

    /// <summary>
    /// Finds the instruction index that starts at the given byte offset inside a blob.
    /// Returns -1 when the offset does not land on an instruction boundary.
    /// </summary>
    internal static int IndexAtOffset(IReadOnlyList<UndertaleInstruction> instructions, uint byteOffset, out uint exactOffset)
    {
        // Instruction sizes are counted in 4-byte words; code offsets are byte offsets.
        if ((byteOffset & 3) != 0)
        {
            exactOffset = 0;
            return -1;
        }
        uint offset = byteOffset / 4;
        uint address = 0;
        for (int i = 0; i < instructions.Count; i++)
        {
            if (address == offset)
            {
                exactOffset = address * 4;
                return i;
            }
            if (address > offset)
            {
                break;
            }
            address += GetInstructionSize(instructions[i]);
        }
        exactOffset = address * 4;
        return address == offset ? instructions.Count : -1;
    }

    internal static uint GetInstructionSize(UndertaleInstruction instruction)
    {
        try
        {
            return instruction.CalculateInstructionSize();
        }
        catch (Exception)
        {
            return 1;
        }
    }

    private static string VariableName(UndertaleInstruction instruction, UndertaleData data)
    {
        string? name = instruction.ValueVariable?.Name?.Content;
        if (name is not null)
            return "name:" + name;

        int stringId = (int)instruction.ReferenceNextOccurrenceOffset;
        if (data.Strings is not null && stringId >= 0 && stringId < data.Strings.Count)
        {
            string? resolved = data.Strings[stringId]?.Content;
            if (resolved is not null)
                return "string:" + resolved;
        }

        return "raw:" + stringId.ToString(CultureInfo.InvariantCulture);
    }

    private static string StringValue(UndertaleInstruction instruction, UndertaleData data)
    {
        string? value = instruction.ValueString?.Resource?.Content;
        if (value is not null)
            return value;

        int stringId = (int)instruction.ReferenceNextOccurrenceOffset;
        if (data.Strings is not null && stringId >= 0 && stringId < data.Strings.Count)
            return data.Strings[stringId]?.Content ?? "";
        return "raw:" + stringId.ToString(CultureInfo.InvariantCulture);
    }

    private static string FunctionName(UndertaleInstruction instruction, UndertaleData data)
    {
        string? name = instruction.ValueFunction?.Name?.Content;
        if (name is not null)
            return name;

        int stringId = (int)instruction.ReferenceNextOccurrenceOffset;
        if (data.Strings is not null && stringId >= 0 && stringId < data.Strings.Count)
            return data.Strings[stringId]?.Content ?? "";
        return "raw:" + stringId.ToString(CultureInfo.InvariantCulture);
    }

    private static string InstanceToken(UndertaleInstruction instruction, UndertaleData data)
    {
        int instanceType = (int)instruction.TypeInst;
        if (instruction.ReferenceType == UndertaleInstruction.VariableType.Instance)
            return "instance:" + instanceType.ToString(CultureInfo.InvariantCulture);

        if (instanceType > 0 && data.GameObjects is not null && instanceType < data.GameObjects.Count)
        {
            string? name = data.GameObjects[instanceType]?.Name?.Content;
            if (!string.IsNullOrEmpty(name))
                return "object:" + name;
        }

        return "type:" + instanceType.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsAssetReference(int intArgument)
    {
        return (int)((uint)intArgument >> 24) != 0;
    }

    private static string AssetReferenceToken(int intArgument, UndertaleData data)
    {
        int type = (int)((uint)intArgument >> 24);
        int id = intArgument & 0xFFFFFF;
        IList? list = GetAssetList(data, type);
        if (list is not null && id >= 0 && id < list.Count)
        {
            if (list[id] is UndertaleNamedResource named && !string.IsNullOrEmpty(named.Name?.Content))
                return $"asset:{type}:{named.Name!.Content}";
        }

        return $"rawref:{type}:{id}";
    }

    private static IList? GetAssetList(UndertaleData data, int type)
    {
        try
        {
            if (data.IsVersionAtLeast(2024, 4))
            {
                return type switch
                {
                    0 => data.GameObjects,
                    1 => data.Sprites,
                    2 => data.Sounds,
                    3 => data.Rooms,
                    4 => data.Paths,
                    5 => data.Scripts,
                    6 => data.Fonts,
                    7 => data.Timelines,
                    8 => data.Shaders,
                    9 => data.Sequences,
                    10 => data.AnimationCurves,
                    11 => data.ParticleSystems,
                    13 => data.Backgrounds,
                    _ => null
                };
            }

            return type switch
            {
                0 => data.GameObjects,
                1 => data.Sprites,
                2 => data.Sounds,
                3 => data.Rooms,
                4 => data.Backgrounds,
                5 => data.Paths,
                6 => data.Scripts,
                7 => data.Fonts,
                8 => data.Timelines,
                10 => data.Shaders,
                11 => data.Sequences,
                12 => data.AnimationCurves,
                13 => data.ParticleSystems,
                _ => null
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        hash.AppendData(buffer);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        hash.AppendData(buffer);
    }
}

/// <summary>
/// Decompiles two matching code entries and produces a unified GML diff.
/// Instances are not thread-safe; calls are serialized internally.
/// </summary>
public sealed class CodeDiffService
{
    private readonly UndertaleData _baseData;
    private readonly UndertaleData _compareData;
    private readonly object _lock = new();

    private GlobalDecompileContext? _baseContext;
    private GlobalDecompileContext? _compareContext;
    private bool _baseContextInitialized;
    private bool _compareContextInitialized;

    public CodeDiffService(UndertaleData baseData, UndertaleData compareData)
    {
        _baseData = baseData;
        _compareData = compareData;
    }

    public CodeDiffResult Compare(UndertaleCode baseCode, UndertaleCode compareCode)
    {
        lock (_lock)
        {
            List<PropertyDiff> metadata = [];
            AddMetadataDifference(metadata, "ArgumentsCount",
                baseCode.ArgumentsCount.ToString(CultureInfo.InvariantCulture),
                compareCode.ArgumentsCount.ToString(CultureInfo.InvariantCulture));
            AddMetadataDifference(metadata, "LocalsCount",
                baseCode.LocalsCount.ToString(CultureInfo.InvariantCulture),
                compareCode.LocalsCount.ToString(CultureInfo.InvariantCulture));
            AddMetadataDifference(metadata, "WeirdLocalFlag",
                baseCode.WeirdLocalFlag.ToString(),
                compareCode.WeirdLocalFlag.ToString());
            AddMetadataDifference(metadata, "Length",
                baseCode.Length.ToString(CultureInfo.InvariantCulture),
                compareCode.Length.ToString(CultureInfo.InvariantCulture));
            AddMetadataDifference(metadata, "ParentEntry",
                baseCode.ParentEntry?.Name?.Content ?? "<none>",
                compareCode.ParentEntry?.Name?.Content ?? "<none>");

            bool baseFailed = false;
            bool compareFailed = false;
            string baseText = Decompile(baseCode, _baseData, ref _baseContext, ref _baseContextInitialized, out baseFailed);
            string compareText = Decompile(compareCode, _compareData, ref _compareContext, ref _compareContextInitialized, out compareFailed);

            IReadOnlyList<TextDiffLine> diffLines = TextLineDiff.Diff(baseText, compareText);
            string diffText = TextLineDiff.FormatUnified(diffLines);

            return new CodeDiffResult
            {
                MetadataDifferences = metadata,
                DiffText = diffText,
                BaseDecompileFailed = baseFailed,
                CompareDecompileFailed = compareFailed
            };
        }
    }

    private static void AddMetadataDifference(List<PropertyDiff> differences, string name, string baseValue, string compareValue)
    {
        if (!string.Equals(baseValue, compareValue, StringComparison.Ordinal))
            differences.Add(new PropertyDiff(name, baseValue, compareValue));
    }

    private static string Decompile(UndertaleCode code, UndertaleData data,
                                    ref GlobalDecompileContext? context, ref bool contextInitialized,
                                    out bool failed)
    {
        failed = false;
        try
        {
            if (!contextInitialized)
            {
                contextInitialized = true;
                try
                {
                    context = new GlobalDecompileContext(data);
                }
                catch (Exception)
                {
                    context = null;
                }
            }

            if (context is not null)
            {
                return new DecompileContext(context, code, data.ToolInfo?.DecompilerSettings ?? new DecompileSettings())
                    .DecompileToString();
            }
        }
        catch (Exception)
        {
            failed = true;
        }

        failed = true;
        return Disassemble(code);
    }

    private static string Disassemble(UndertaleCode code)
    {
        StringBuilder builder = new();
        uint address = 0;
        foreach (UndertaleInstruction instruction in code.Instructions)
        {
            try
            {
                builder.Append(address.ToString("D5", CultureInfo.InvariantCulture))
                       .Append(": ")
                       .AppendLine(instruction.ToString(code, address));
            }
            catch (Exception)
            {
                builder.Append(address.ToString("D5", CultureInfo.InvariantCulture))
                       .AppendLine(": <unreadable instruction>");
            }
            address += CodeFingerprint.GetInstructionSize(instruction);
        }
        return builder.ToString();
    }
}
