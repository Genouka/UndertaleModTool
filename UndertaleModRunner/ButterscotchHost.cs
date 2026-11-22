using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace UndertaleModRunner;

/// <summary>
/// Locates and validates the out-of-process <c>bsdbg-host.exe</c> for an
/// architecture, and decides which architecture a game needs.
/// </summary>
public static class ButterscotchHost
{
    public const string ExecutableName = "bsdbg-host.exe";

    public static string DescribeArchitecture(ButterscotchArchitecture architecture) => architecture switch
    {
        ButterscotchArchitecture.Auto => "auto",
        ButterscotchArchitecture.X64 => "x64",
        ButterscotchArchitecture.X86 => "x86",
        ButterscotchArchitecture.Arm64 => "ARM64",
        _ => "embedded",
    };

    /// <summary>Maps a host architecture to its native folder name.</summary>
    public static string GetNativeFolder(ButterscotchArchitecture architecture) => architecture switch
    {
        ButterscotchArchitecture.X64 => "win-x64",
        ButterscotchArchitecture.X86 => "win-x86",
        ButterscotchArchitecture.Arm64 => "win-arm64",
        _ => null,
    };

    /// <summary>Resolves the POSIX host executable staged next to the app.</summary>
    public static bool TryResolvePosixExecutable(ButterscotchArchitecture architecture,
                                                 string configuredPath,
                                                 out string executablePath, out string error)
    {
        executablePath = null;
        error = null;
        if (OperatingSystem.IsWindows() ||
            (architecture != ButterscotchArchitecture.X64 && architecture != ButterscotchArchitecture.Arm64))
        {
            error = "No POSIX debug host exists for this architecture.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string custom = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath.Trim()));
            if (File.Exists(custom)) { executablePath = custom; return true; }
            error = $"Debug host not found: {custom}";
            return false;
        }

        string rid = (OperatingSystem.IsMacOS(), architecture) switch
        {
            (true, ButterscotchArchitecture.X64) => "osx-x64",
            (true, ButterscotchArchitecture.Arm64) => "osx-arm64",
            (_, ButterscotchArchitecture.X64) => "linux-x64",
            (_, ButterscotchArchitecture.Arm64) => "linux-arm64",
            _ => null,
        };
        if (rid is null)
        {
            error = "No POSIX debug host exists for this architecture.";
            return false;
        }
        string candidate = Path.Combine(AppContext.BaseDirectory, "native", rid, "bsdbg-host");
        if (File.Exists(candidate)) { executablePath = candidate; return true; }
        error = $"No {rid} debug host found. Expected '{candidate}'.";
        return false;
    }

    /// <summary>
    /// Picks the concrete host architecture.  Auto prefers a host matching the
    /// native DLLs shipped with the game (the usual reason a 32-bit host is
    /// needed), then falls back to the current process architecture.
    /// </summary>
    public static ButterscotchArchitecture ResolveArchitecture(ButterscotchArchitecture requested, string dataWinPath)
    {
        if (requested != ButterscotchArchitecture.Auto)
            return requested;

        ButterscotchArchitecture? detected = DetectGameArchitecture(dataWinPath);
        if (detected.HasValue)
            return detected.Value;

        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => ButterscotchArchitecture.X86,
            Architecture.Arm64 => ButterscotchArchitecture.Arm64,
            _ => ButterscotchArchitecture.X64,
        };
    }

    /// <summary>
    /// Scans the game directory for native DLLs.  A directory containing only
    /// x86 DLLs needs the x86 host; only x64 DLLs needs x64.  Mixed or empty
    /// directories are ambiguous and return null.
    /// </summary>
    public static ButterscotchArchitecture? DetectGameArchitecture(string dataWinPath)
    {
        if (string.IsNullOrEmpty(dataWinPath))
            return null;
        string gameDirectory = Path.GetDirectoryName(Path.GetFullPath(dataWinPath));
        if (string.IsNullOrEmpty(gameDirectory) || !Directory.Exists(gameDirectory))
            return null;

        bool x86 = false;
        bool x64 = false;
        bool arm64 = false;
        foreach (string file in EnumerateCandidateDlls(gameDirectory))
        {
            PeMachine machine = ExternalButterscotch.DetectMachine(file, out bool isDll);
            if (!isDll)
                continue;
            switch (machine)
            {
                case PeMachine.X86:
                    x86 = true;
                    break;
                case PeMachine.X64:
                    x64 = true;
                    break;
                case PeMachine.Arm64:
                    arm64 = true;
                    break;
            }
        }

        int kinds = (x86 ? 1 : 0) + (x64 ? 1 : 0) + (arm64 ? 1 : 0);
        if (kinds != 1)
            return null;
        if (x86)
            return ButterscotchArchitecture.X86;
        if (x64)
            return ButterscotchArchitecture.X64;
        return ButterscotchArchitecture.Arm64;
    }

    private static IEnumerable<string> EnumerateCandidateDlls(string gameDirectory)
    {
        IEnumerable<string> topLevel;
        try
        {
            topLevel = Directory.EnumerateFiles(gameDirectory, "*.dll", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (string file in topLevel)
            yield return file;

        string extensions = Path.Combine(gameDirectory, "extensions");
        if (!Directory.Exists(extensions))
            yield break;
        IEnumerable<string> nested;
        try
        {
            nested = Directory.EnumerateFiles(extensions, "*.dll", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (string file in nested)
            yield return file;
    }

    /// <summary>Resolves a host executable, validating that it is a PE of the requested architecture.</summary>
    public static bool TryResolveExecutable(ButterscotchArchitecture architecture, string configuredPath,
                                            out string executablePath, out string error)
    {
        executablePath = null;
        error = null;
        if (architecture == ButterscotchArchitecture.InProcess || architecture == ButterscotchArchitecture.Auto)
        {
            error = "The embedded session does not use an external debug host.";
            return false;
        }

        PeMachine expected = architecture switch
        {
            ButterscotchArchitecture.X64 => PeMachine.X64,
            ButterscotchArchitecture.X86 => PeMachine.X86,
            ButterscotchArchitecture.Arm64 => PeMachine.Arm64,
            _ => PeMachine.Unknown,
        };
        if (expected == PeMachine.Unknown)
        {
            error = "Unsupported debug host architecture.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string path = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
            if (!Path.IsPathRooted(path))
                path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
            if (!File.Exists(path))
            {
                error = $"Debug host not found: {path}";
                return false;
            }
            return ValidateHost(path, expected, out executablePath, out error);
        }

        string folder = GetNativeFolder(architecture);
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "native", folder, ExecutableName),
            Path.Combine(AppContext.BaseDirectory, $"bsdbg-host-{folder}.exe"),
            Path.Combine(AppContext.BaseDirectory, ExecutableName),
        };
        foreach (string candidate in candidates)
        {
            if (!File.Exists(candidate))
                continue;
            if (ValidateHost(candidate, expected, out executablePath, out error))
                return true;
        }

        error ??= $"No {DescribeArchitecture(architecture)} debug host found. Expected " +
                  $"'{Path.Combine(AppContext.BaseDirectory, "native", folder, ExecutableName)}'.";
        return false;
    }

    private static bool ValidateHost(string path, PeMachine expected, out string executablePath, out string error)
    {
        executablePath = null;
        error = null;
        PeMachine found = ExternalButterscotch.DetectMachine(path, out bool isDll);
        if (isDll)
        {
            error = $"'{path}' is a DLL, not a debug host executable.";
            return false;
        }
        if (found == PeMachine.Unknown)
        {
            error = $"'{path}' is not a valid Windows PE executable.";
            return false;
        }
        if (found != expected)
        {
            error = $"'{path}' is {DescribeMachine(found)}, but {DescribeMachine(expected)} was requested.";
            return false;
        }
        executablePath = path;
        return true;
    }

    private static string DescribeMachine(PeMachine machine) => machine switch
    {
        PeMachine.X86 => "32-bit x86",
        PeMachine.X64 => "64-bit x64",
        PeMachine.Arm64 => "64-bit ARM64",
        PeMachine.Arm => "32-bit ARM",
        _ => "unknown architecture",
    };
}
