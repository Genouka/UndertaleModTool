// ==UndertaleModTool==
// @name        Get All Chunk Names
// @name:zh-Hans 获取所有块名称
// @name:zh-CN  获取所有块名称
// ==/UndertaleModTool==
EnsureDataLoaded();

string x = "String[] order = {\"";
foreach (string key in Data.FORM.Chunks.Keys)  
{
    if (key != "AUDO")
        x += (key + "\", \"");
    else
        x += (key + "\"};");
}
return x;
