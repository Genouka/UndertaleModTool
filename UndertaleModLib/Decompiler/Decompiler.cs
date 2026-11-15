using System;
using System.Collections.Generic;
using Underanalyzer.Decompiler;
using UndertaleModLib.Models;

namespace UndertaleModLib.Decompiler;

/// <summary>
/// Deprecated decompiler entry points, kept for compatibility with existing scripts.
/// </summary>
/// <remarks>
/// Use <see cref="DecompileContext"/> and <see cref="GlobalDecompileContext"/> directly instead.
/// </remarks>
[Obsolete("Use Underanalyzer.Decompiler.DecompileContext instead.")]
public static class Decompiler
{
    /// <summary>
    /// Decompiles a single code entry.
    /// </summary>
    /// <param name="code">Code entry to decompile.</param>
    /// <param name="globalContext">Global context to use for decompilation.</param>
    /// <param name="msgDelegate">Optional callback for progress messages.</param>
    /// <returns>Decompiled GML source.</returns>
    [Obsolete("Use Underanalyzer.Decompiler.DecompileContext instead.")]
    public static string Decompile(UndertaleCode code, GlobalDecompileContext globalContext, Action<string> msgDelegate = null)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(globalContext);

        msgDelegate?.Invoke("Building the cache of all sub-functions...");
        if (globalContext.Data is not null && globalContext.Data.GlobalFunctions is null)
            BuildSubFunctionCache(globalContext.Data);
        msgDelegate?.Invoke("Decompiling, please wait... This can take a while on complex scripts.");

        return new DecompileContext(globalContext, code, globalContext.Data?.ToolInfo.DecompilerSettings).DecompileToString();
    }

    /// <summary>
    /// Builds the deprecated sub-function cache, which is now backed by <see cref="UndertaleData.GlobalFunctions"/>.
    /// </summary>
    /// <param name="data">Data to build the cache for.</param>
    [Obsolete("Use GlobalDecompileContext.BuildGlobalFunctionCache instead.")]
    public static void BuildSubFunctionCache(UndertaleData data)
    {
        if (data is null)
            return;

        GlobalDecompileContext.BuildGlobalFunctionCache(data);

        // Old scripts only check this for null before building the cache; populate it so such
        // checks keep working. The lookup itself is superseded by GlobalFunctions.
        if (data.KnownSubFunctions is null && data.Functions is not null)
        {
            const string scriptPrefix = "gml_Script_";
            Dictionary<string, UndertaleFunction> cache = new(data.Functions.Count);
            foreach (UndertaleFunction function in data.Functions)
            {
                string name = function?.Name?.Content;
                if (name is null)
                    continue;
                if (name.StartsWith(scriptPrefix, StringComparison.Ordinal))
                    cache.TryAdd(name[scriptPrefix.Length..], function);
                else
                    cache.TryAdd(name, function);
            }
            data.KnownSubFunctions = cache;
        }
    }
}
