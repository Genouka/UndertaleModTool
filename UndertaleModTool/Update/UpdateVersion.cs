using System;

namespace UndertaleModTool.Update
{
    /// <summary>
    /// Compares release version strings such as <c>0.9.2.0</c>, <c>v0.9.2.0</c>,
    /// <c>0.9.2.0-qiu.4</c> or <c>0.9.2.0+abcdef</c>.
    /// </summary>
    public static class UpdateVersion
    {
        /// <summary>
        /// Compares two version strings. Returns a negative value when <paramref name="remote"/> is
        /// newer than <paramref name="local"/>, zero when they are equal, and a positive value when
        /// the local version is newer.
        /// </summary>
        public static int Compare(string local, string remote)
        {
            string localCore = SplitCoreAndSuffix(local, out string localSuffix);
            string remoteCore = SplitCoreAndSuffix(remote, out string remoteSuffix);

            int coreComparison = CompareCores(localCore, remoteCore);
            if (coreComparison != 0)
                return coreComparison;

            if (localSuffix.Length == 0 && remoteSuffix.Length == 0)
                return 0;
            if (localSuffix.Length == 0)
                return -1;
            if (remoteSuffix.Length == 0)
                return 1;

            return NaturalCompare(localSuffix, remoteSuffix);
        }

        /// <summary>
        /// Compares the installed version with a release tag for an update check. When the local
        /// version carries no pre-release suffix (the exact installed tag is unknown, only the
        /// numeric core is available), a release with the same numeric core is treated as the same
        /// build; otherwise every check would offer the release the user installed manually.
        /// Versions recorded by the updater keep their suffix and are compared exactly.
        /// </summary>
        public static int CompareForUpdate(string local, string remote)
        {
            int comparison = Compare(local, remote);
            if (comparison >= 0)
                return comparison;

            string localCore = SplitCoreAndSuffix(local, out string localSuffix);
            if (localSuffix.Length != 0)
                return comparison;

            string remoteCore = SplitCoreAndSuffix(remote, out _);
            return CompareCores(localCore, remoteCore) == 0 ? 0 : comparison;
        }

        /// <summary>Removes a trailing <c>+buildmetadata</c> part from a version string.</summary>
        public static string StripBuildMetadata(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return "";

            string result = version.Trim();
            int plus = result.IndexOf('+');
            if (plus >= 0)
                result = result[..plus];
            return result.Trim();
        }

        /// <summary>
        /// Returns only the numeric core of a version string (no build metadata, no
        /// <c>v</c> prefix, no pre-release suffix and no trailing extra text).
        /// </summary>
        /// <returns>The numeric version core, for example <c>0.9.2.0</c>.</returns>
        public static string GetVersionCore(string version)
            => SplitCoreAndSuffix(version, out _);

        static string SplitCoreAndSuffix(string version, out string suffix)
        {
            string normalized = StripBuildMetadata(version);

            int space = normalized.IndexOf(' ');
            if (space >= 0)
                normalized = normalized[..space];

            if (normalized.Length > 1 && (normalized[0] == 'v' || normalized[0] == 'V') && char.IsDigit(normalized[1]))
                normalized = normalized[1..];

            int dash = normalized.IndexOf('-');
            if (dash < 0)
            {
                suffix = "";
                return normalized;
            }

            suffix = normalized[(dash + 1)..];
            return normalized[..dash];
        }

        static int CompareCores(string local, string remote)
        {
            ReadOnlySpan<char> localCore = local.AsSpan();
            ReadOnlySpan<char> remoteCore = remote.AsSpan();

            for (int i = 0; i < 4; i++)
            {
                uint localPart = NextComponent(ref localCore);
                uint remotePart = NextComponent(ref remoteCore);
                if (localPart != remotePart)
                    return localPart < remotePart ? -1 : 1;
            }

            return 0;
        }

        static uint NextComponent(ref ReadOnlySpan<char> core)
        {
            if (core.IsEmpty)
                return 0;

            int separator = core.IndexOf('.');
            ReadOnlySpan<char> part = separator < 0 ? core : core[..separator];
            core = separator < 0 ? default : core[(separator + 1)..];

            uint value = 0;
            foreach (char c in part)
            {
                if (!char.IsAsciiDigit(c))
                    return 0;
                value = unchecked(value * 10 + (uint)(c - '0'));
            }
            return value;
        }

        /// <summary>Case-insensitive comparison where runs of digits compare by numeric value.</summary>
        static int NaturalCompare(string local, string remote)
        {
            int i = 0;
            int j = 0;

            while (i < local.Length && j < remote.Length)
            {
                if (char.IsAsciiDigit(local[i]) && char.IsAsciiDigit(remote[j]))
                {
                    int localStart = i;
                    while (i < local.Length && char.IsAsciiDigit(local[i]))
                        i++;
                    int remoteStart = j;
                    while (j < remote.Length && char.IsAsciiDigit(remote[j]))
                        j++;

                    ReadOnlySpan<char> localNumber = local.AsSpan(localStart, i - localStart).TrimStart('0');
                    ReadOnlySpan<char> remoteNumber = remote.AsSpan(remoteStart, j - remoteStart).TrimStart('0');

                    if (localNumber.Length != remoteNumber.Length)
                        return localNumber.Length < remoteNumber.Length ? -1 : 1;

                    int numberComparison = localNumber.SequenceCompareTo(remoteNumber);
                    if (numberComparison != 0)
                        return numberComparison < 0 ? -1 : 1;

                    continue;
                }

                char localChar = char.ToLowerInvariant(local[i]);
                char remoteChar = char.ToLowerInvariant(remote[j]);
                if (localChar != remoteChar)
                    return localChar < remoteChar ? -1 : 1;

                i++;
                j++;
            }

            int localRemaining = local.Length - i;
            int remoteRemaining = remote.Length - j;
            if (localRemaining == remoteRemaining)
                return 0;
            return localRemaining < remoteRemaining ? -1 : 1;
        }
    }
}
