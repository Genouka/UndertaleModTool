using System;
using System.Text.RegularExpressions;

namespace UndertaleModTool.Update
{
    /// <summary>
    /// Renders release-note Markdown as readable plain text for the update dialog. GitHub release
    /// notes mix Markdown and HTML (tables inside &lt;details&gt; blocks), which does not read well
    /// in a plain text view without this clean-up.
    /// </summary>
    internal static class ChangelogFormatter
    {
        static readonly Regex htmlComment = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
        static readonly Regex markdownLink = new(@"\[([^\]]+)\]\((https?://[^)\s]+)\)", RegexOptions.Compiled);
        static readonly Regex autoLink = new(@"<(https?://[^>\s]+)>", RegexOptions.Compiled);
        static readonly Regex htmlTag = new(@"<[^>]+>", RegexOptions.Compiled);
        static readonly Regex heading = new(@"^\s{0,3}#{1,6}\s*", RegexOptions.Multiline | RegexOptions.Compiled);
        static readonly Regex bullet = new(@"^(\s*)[-*+]\s+", RegexOptions.Multiline | RegexOptions.Compiled);
        static readonly Regex tableSeparator = new(@"^\s*\|?[\s:|-]+\|?\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
        static readonly Regex trailingSpaces = new(@"[ \t]+$", RegexOptions.Multiline | RegexOptions.Compiled);
        static readonly Regex blankLines = new(@"\n{3,}", RegexOptions.Compiled);

        /// <summary>Converts Markdown release notes into readable plain text.</summary>
        public static string ToPlainText(string changelog)
        {
            if (string.IsNullOrWhiteSpace(changelog))
                return "";

            string text = changelog.Replace("\r\n", "\n").Replace('\r', '\n');
            text = htmlComment.Replace(text, "");
            text = markdownLink.Replace(text, match =>
            {
                string label = match.Groups[1].Value;
                string url = match.Groups[2].Value;
                return string.Equals(label, url, StringComparison.OrdinalIgnoreCase)
                    ? label
                    : $"{label} ({url})";
            });
            text = autoLink.Replace(text, "$1");
            text = heading.Replace(text, "");
            text = bullet.Replace(text, "$1• ");
            text = text.Replace("**", "").Replace("`", "");
            text = htmlTag.Replace(text, "");
            text = tableSeparator.Replace(text, "");
            text = trailingSpaces.Replace(text, "");
            text = blankLines.Replace(text, "\n\n");
            return text.Trim();
        }
    }
}
