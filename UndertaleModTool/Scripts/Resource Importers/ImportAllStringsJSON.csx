// ==UndertaleModTool==
// @name        Import All Strings from JSON
// @name:zh-Hans 从 JSON 导入所有字符串
// @name:zh-CN  从 JSON 导入所有字符串
// ==/UndertaleModTool==
using System.Text.Json;

EnsureDataLoaded();

string path = PromptLoadFile("", "JSON files (*.json)|*.json|TXT files (*.txt)|*.txt|All files (*.*)|*.*");
if (string.IsNullOrWhiteSpace(path))
{
    throw new ScriptCancelledException("The import file was not set.");
}

string file = File.ReadAllText(path);
JsonElement json = JsonSerializer.Deserialize<JsonElement>(file);
JsonElement.ArrayEnumerator array = json.GetProperty("Strings").EnumerateArray();
int i = 0;
foreach (JsonElement elmnt in array)
    Data.Strings[i++].Content = elmnt.ToString();
ScriptMessage("Successfully imported");
