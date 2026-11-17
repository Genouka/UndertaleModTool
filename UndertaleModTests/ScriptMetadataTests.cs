using System.Globalization;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UndertaleModLib.Scripting;

namespace UndertaleModTests;

[TestClass]
public class ScriptMetadataTests
{
    private const string Header =
        "// ==UndertaleModTool==\n" +
        "// @name        Import GML\n" +
        "// @name:zh-Hans 导入 GML\n" +
        "// @name:zh-CN  导入 GML\n" +
        "// @description Imports .gml files.\n" +
        "// @description:zh-Hans 导入 .gml 文件。\n" +
        "// @author      Someone\n" +
        "// ==/UndertaleModTool==\n";

    [TestMethod]
    public void ParsesMetadataBlock()
    {
        ScriptMetadata metadata = ScriptMetadata.Parse(Header);

        Assert.IsTrue(metadata.HasMetadata);
        Assert.AreEqual("Import GML", metadata.Name);
        Assert.AreEqual("Imports .gml files.", metadata.Description);
        Assert.AreEqual("Someone", metadata.GetValue("author"));
    }

    [TestMethod]
    public void NoMetadataWhenHeaderIsNotFirstContent()
    {
        ScriptMetadata metadata = ScriptMetadata.Parse("EnsureDataLoaded();\n// ==UndertaleModTool==\n// @name Fake\n");

        Assert.IsFalse(metadata.HasMetadata);
        Assert.IsNull(metadata.Name);
    }

    [TestMethod]
    public void AcceptsUserScriptHeaderAlias()
    {
        ScriptMetadata metadata = ScriptMetadata.Parse(
            "// ==UserScript==\n" +
            "// @name My Script\n" +
            "// ==/UserScript==\n");

        Assert.IsTrue(metadata.HasMetadata);
        Assert.AreEqual("My Script", metadata.Name);
    }

    [TestMethod]
    public void LocalizedNameFallsBackThroughCultures()
    {
        ScriptMetadata metadata = ScriptMetadata.Parse(Header);

        Assert.AreEqual("导入 GML", metadata.GetLocalizedName(CultureInfo.GetCultureInfo("zh-CN")));
        Assert.AreEqual("导入 GML", metadata.GetLocalizedName(CultureInfo.GetCultureInfo("zh-Hans")));
        Assert.AreEqual("Import GML", metadata.GetLocalizedName(CultureInfo.GetCultureInfo("en-US")));
    }

    [TestMethod]
    public void DisplayNameHonorsModes()
    {
        string path = Path.Combine("Scripts", "ImportGML.csx");
        ScriptMetadata metadata = ScriptMetadata.Parse(Header);

        Assert.AreEqual("ImportGML.csx", metadata.GetDisplayName(path, ScriptNameDisplayMode.FileName));
        Assert.AreEqual("Import GML", metadata.GetDisplayName(path, ScriptNameDisplayMode.EnglishName));
        Assert.AreEqual("导入 GML", metadata.GetDisplayName(path, ScriptNameDisplayMode.LocalizedName,
                                                        CultureInfo.GetCultureInfo("zh-Hans")));
    }

    [TestMethod]
    public void DisplayNameFallsBackToFileNameWithoutMetadata()
    {
        ScriptMetadata metadata = ScriptMetadata.Parse("// just a comment\n");

        Assert.IsFalse(metadata.HasMetadata);
        Assert.AreEqual("NoMetadata.csx", metadata.GetDisplayName(Path.Combine("Scripts", "NoMetadata.csx"),
                                                                  ScriptNameDisplayMode.LocalizedName));
    }

    [TestMethod]
    public void LastValueOfSameKeyAndLocaleWins()
    {
        ScriptMetadata metadata = ScriptMetadata.Parse(
            "// ==UndertaleModTool==\n" +
            "// @name First\n" +
            "// @name Second\n" +
            "// ==/UndertaleModTool==\n");

        Assert.AreEqual("Second", metadata.Name);
    }
}
