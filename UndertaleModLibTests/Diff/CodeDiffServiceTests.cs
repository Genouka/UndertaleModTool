using System;
using System.Linq;
using UndertaleModLib;
using UndertaleModLib.Diff;
using UndertaleModLib.Models;
using Xunit;

namespace UndertaleModLibTests.Diff;

public class CodeDiffServiceTests
{
    [Fact]
    public void CompareSegments_ShowsDisassemblyDifference_ForAnonymousSubEntries()
    {
        (UndertaleData baseData, UndertaleCode baseChild) = BuildParentWithChild(constant: 2);
        (UndertaleData compareData, UndertaleCode compareChild) = BuildParentWithChild(constant: 3);

        // Sanity: the segment fingerprints differ, so the entry shows up as changed.
        Assert.NotEqual(
            Convert.ToBase64String(CodeFingerprint.Compute(baseChild, baseData)),
            Convert.ToBase64String(CodeFingerprint.Compute(compareChild, compareData)));

        CodeDiffService service = new(baseData, compareData);
        CodeDiffResult result = service.CompareSegments(baseChild, compareChild);

        Assert.False(result.BaseDecompileFailed);
        Assert.False(result.CompareDecompileFailed);
        Assert.True(result.HasTextDifferences, "sub-entry segments must produce a disassembly diff");
        Assert.Contains("push", result.DiffText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-", result.DiffText, StringComparison.Ordinal);
        Assert.Contains("+", result.DiffText, StringComparison.Ordinal);
    }

    [Fact]
    public void CompareSegments_ReturnsNoTextDiff_ForIdenticalSubEntries()
    {
        (UndertaleData baseData, UndertaleCode baseChild) = BuildParentWithChild(constant: 2);
        (UndertaleData compareData, UndertaleCode compareChild) = BuildParentWithChild(constant: 2);

        CodeDiffService service = new(baseData, compareData);
        CodeDiffResult result = service.CompareSegments(baseChild, compareChild);
        Assert.False(result.HasTextDifferences);
    }

    private static (UndertaleData Data, UndertaleCode Child) BuildParentWithChild(int constant)
    {
        UndertaleData data = UndertaleData.CreateNew();
        data.GeneralInfo.Major = 2024;
        data.GeneralInfo.Minor = 8;

        UndertaleInstruction MakeConstant(int value) => new()
        {
            Kind = UndertaleInstruction.Opcode.Push,
            Type1 = UndertaleInstruction.DataType.Int32,
            IntArgument = value,
        };
        UndertaleInstruction MakePopz() => new() { Kind = UndertaleInstruction.Opcode.Popz };

        UndertaleCode parent = new() { Name = data.Strings.MakeString("gml_Object_obj_test_Create_0") };
        parent.Instructions.Add(MakeConstant(1));
        parent.Instructions.Add(MakePopz());

        UndertaleCode child = new() { Name = data.Strings.MakeString("gml_Script_anon@1@gml_Object_obj_test_Create_0") };
        child.ParentEntry = parent;
        child.Offset = (uint)(parent.Instructions.Sum(i => i.CalculateInstructionSize()) * 4);
        parent.ChildEntries.Add(child);
        parent.Instructions.Add(MakeConstant(constant));
        parent.Instructions.Add(MakePopz());

        data.Code.Add(parent);
        data.Code.Add(child);
        return (data, child);
    }
}
