// ==UndertaleModTool==
// @name        Export All Strings
// @name:zh-Hans 导出所有字符串
// @name:zh-CN  导出所有字符串
// ==/UndertaleModTool==
using System.Text;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UndertaleModLib.Util;

EnsureDataLoaded();

string stringsPath = PromptSaveFile(".txt", "TXT files (*.txt)|*.txt|All files (*.*)|*.*");
if (string.IsNullOrWhiteSpace(stringsPath))
{
    return;
}

bool promptedForNewlines = false;
bool skipNewlines = false;
using (StreamWriter writer = new StreamWriter(stringsPath))
{
    foreach (var str in Data.Strings)
    {
        if (str.Content.Contains('\n') || str.Content.Contains('\r'))
        {
            if (!promptedForNewlines)
            {
                promptedForNewlines = true;
                skipNewlines = ScriptQuestion("Export strings containing newlines? Doing so will break reimporting.");
            }
            if (skipNewlines)
            {
                continue;
            }
        }
        writer.WriteLine(str.Content);
    }
}
