using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Serialization;

namespace UndertaleModRunner;

/// <summary>
/// Which Butterscotch runtime UndertaleModTool should use to run a game.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ButterscotchArchitecture>))]
public enum ButterscotchArchitecture
{
    /// <summary>Embedded bsdbg session inside the current UndertaleModTool process (supports debugging).</summary>
    InProcess,
    /// <summary>External 64-bit Butterscotch executable (run only).</summary>
    X64,
    /// <summary>External 32-bit x86 Butterscotch executable (run only).</summary>
    X86,
    /// <summary>External ARM64 Butterscotch executable (run only).</summary>
    Arm64,
}

/// <summary>Machine type read from a PE file header.</summary>
public enum PeMachine
{
    Unknown,
    X86,
    X64,
    Arm64,
    Arm,
}

/// <summary>
/// Locates, validates and launches standalone Butterscotch executables of a requested
/// architecture. Used for "Run with Butterscotch" when the embedded (in-process, x64)
/// debugger cannot be used, for example to load 32-bit extension DLLs.
/// </summary>
public static class ExternalButterscotch
{

    /// <summary>
    /// The embedded bsdbg session is a native x64 DLL, so it can only be loaded by an x64 host.
    /// </summary>
    public static bool CanUseEmbeddedSession =>
        Environment.Is64BitProcess &&
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.X64;

    /// <summary>Returns the display name of an architecture.</summary>
    public static string DescribeArchitecture(ButterscotchArchitecture architecture) => architecture switch
    {
        ButterscotchArchitecture.X64 => "x64",
        ButterscotchArchitecture.X86 => "x86",
        ButterscotchArchitecture.Arm64 => "ARM64",
        _ => "embedded",
    };

    /// <summary>Reads the COFF machine type of a PE file. Returns Unknown for non-PE files.</summary>
    public static PeMachine DetectMachine(string path, out bool isDll)
    {
        isDll = false;
        try
        {
            using FileStream stream = File.OpenRead(path);
            using BinaryReader reader = new(stream);
            if (stream.Length < 0x40)
                return PeMachine.Unknown;

            if (reader.ReadByte() != 'M' || reader.ReadByte() != 'Z')
                return PeMachine.Unknown;

            stream.Position = 0x3C;
            int peOffset = reader.ReadInt32();
            if (peOffset < 0x40 || peOffset + 24L > stream.Length)
                return PeMachine.Unknown;

            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) // "PE\0\0"
                return PeMachine.Unknown;

            ushort machine = reader.ReadUInt16();
            stream.Position = peOffset + 22; // IMAGE_FILE_HEADER.Characteristics
            ushort characteristics = reader.ReadUInt16();
            isDll = (characteristics & 0x2000) != 0; // IMAGE_FILE_DLL

            return machine switch
            {
                0x014C => PeMachine.X86,
                0x8664 => PeMachine.X64,
                0xAA64 => PeMachine.Arm64,
                0x01C0 or 0x01C4 => PeMachine.Arm,
                _ => PeMachine.Unknown,
            };
        }
        catch (IOException)
        {
            return PeMachine.Unknown;
        }
        catch (UnauthorizedAccessException)
        {
            return PeMachine.Unknown;
        }
    }

    /// <summary>
    /// Resolves the executable for the requested architecture. When <paramref name="configuredPath"/>
    /// is empty, probes the app directory, native/win-&lt;arch&gt; and the game directory for the
    /// conventional Butterscotch executable names.
    /// </summary>
    public static bool TryResolveExecutable(ButterscotchArchitecture architecture, string configuredPath,
                                            string dataWinPath, out string executablePath, out string error)
    {
        executablePath = null;
        error = null;

        PeMachine expected = architecture switch
        {
            ButterscotchArchitecture.X64 => PeMachine.X64,
            ButterscotchArchitecture.X86 => PeMachine.X86,
            ButterscotchArchitecture.Arm64 => PeMachine.Arm64,
            _ => PeMachine.Unknown,
        };
        if (architecture == ButterscotchArchitecture.InProcess || expected == PeMachine.Unknown)
        {
            error = "Embedded Butterscotch sessions do not use an external executable.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string path = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
            if (!Path.IsPathRooted(path))
                path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
            if (!File.Exists(path))
            {
                error = $"Butterscotch executable not found: {path}";
                return false;
            }
            return ValidateExecutable(path, expected, out executablePath, out error);
        }

        List<string> directories = new();
        foreach (string directory in CandidateDirectories(architecture, dataWinPath))
        {
            if (!directories.Contains(directory))
                directories.Add(directory);
        }
        List<string> fileNames = new(CandidateFileNames(architecture));
        List<string> rejected = new();
        foreach (string directory in directories)
        {
            foreach (string fileName in fileNames)
            {
                string candidate = Path.Combine(directory, fileName);
                if (!File.Exists(candidate))
                    continue;

                PeMachine found = DetectMachine(candidate, out bool isDll);
                if (isDll || found == PeMachine.Unknown || found != expected)
                {
                    rejected.Add($"{candidate} ({DescribeMachine(found, isDll)})");
                    continue;
                }

                executablePath = candidate;
                return true;
            }
        }

        error = $"No {DescribeArchitecture(architecture)} Butterscotch executable found. " +
                $"Looked for {string.Join(", ", fileNames)} in:\n" +
                string.Join("\n", directories);
        if (rejected.Count > 0)
            error += "\nRejected candidates with a different architecture:\n" + string.Join("\n", rejected);
        return false;
    }

    /// <summary>
    /// Starts a standalone Butterscotch executable with the given data file. Returns false and
    /// sets <paramref name="error"/> when the executable is missing, is the wrong architecture, or
    /// cannot be started.
    /// </summary>
    public static bool TryStart(ButterscotchArchitecture architecture, string configuredPath, string dataWinPath,
                                out Process process, out string error, IEnumerable<string> extraArguments = null)
    {
        process = null;
        if (string.IsNullOrEmpty(dataWinPath) || !File.Exists(dataWinPath))
        {
            error = $"Data file not found: {dataWinPath}";
            return false;
        }

        if (!TryResolveExecutable(architecture, configuredPath, dataWinPath, out string executable, out error))
            return false;

        try
        {
            string fullDataWinPath = Path.GetFullPath(dataWinPath);
            ProcessStartInfo startInfo = new(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(fullDataWinPath),
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(fullDataWinPath);
            if (extraArguments is not null)
            {
                foreach (string argument in extraArguments)
                    startInfo.ArgumentList.Add(argument);
            }

            process = Process.Start(startInfo);
            if (process is null)
            {
                error = $"Failed to start '{executable}'.";
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            error = $"Failed to start '{executable}': {ex.Message}";
            return false;
        }
    }

    private static bool ValidateExecutable(string path, PeMachine expected, out string executablePath, out string error)
    {
        executablePath = null;
        error = null;
        PeMachine found = DetectMachine(path, out bool isDll);
        if (isDll)
        {
            error = $"'{path}' is a DLL, not a Butterscotch executable.";
            return false;
        }
        if (found == PeMachine.Unknown)
        {
            error = $"'{path}' is not a valid Windows PE executable.";
            return false;
        }
        if (found != expected)
        {
            error = $"'{path}' is {DescribeMachine(found, false)}, but {DescribeMachine(expected, false)} was requested.";
            return false;
        }
        executablePath = path;
        return true;
    }

    private static string DescribeMachine(PeMachine machine, bool isDll)
    {
        string kind = isDll ? "DLL" : "executable";
        return machine switch
        {
            PeMachine.X86 => $"32-bit x86 {kind}",
            PeMachine.X64 => $"64-bit x64 {kind}",
            PeMachine.Arm64 => $"64-bit ARM64 {kind}",
            PeMachine.Arm => $"32-bit ARM {kind}",
            _ => $"unknown {kind}",
        };
    }

    private static IEnumerable<string> CandidateDirectories(ButterscotchArchitecture architecture, string dataWinPath)
    {
        string nativeFolder = architecture switch
        {
            ButterscotchArchitecture.X64 => "win-x64",
            ButterscotchArchitecture.X86 => "win-x86",
            ButterscotchArchitecture.Arm64 => "win-arm64",
            _ => "",
        };

        yield return AppContext.BaseDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "native", nativeFolder);
        yield return Path.Combine(AppContext.BaseDirectory, "butterscotch", nativeFolder);
        yield return Path.Combine(AppContext.BaseDirectory, "butterscotch");

        if (!string.IsNullOrEmpty(dataWinPath))
        {
            string gameDirectory = Path.GetDirectoryName(Path.GetFullPath(dataWinPath));
            if (!string.IsNullOrEmpty(gameDirectory))
            {
                yield return gameDirectory;
                yield return Path.Combine(gameDirectory, "butterscotch", nativeFolder);
                yield return Path.Combine(gameDirectory, "butterscotch");
            }
        }
    }

    private static IEnumerable<string> CandidateFileNames(ButterscotchArchitecture architecture) => architecture switch
    {
        ButterscotchArchitecture.X64 => new[] { "butterscotch.exe", "butterscotch-x64.exe", "butterscotch-x86_64.exe" },
        ButterscotchArchitecture.X86 => new[] { "butterscotch-x86.exe", "butterscotch-i486.exe", "butterscotch-32.exe", "butterscotch.exe" },
        ButterscotchArchitecture.Arm64 => new[] { "butterscotch-arm64.exe", "butterscotch-aarch64.exe", "butterscotch.exe" },
        _ => Array.Empty<string>(),
    };
}
