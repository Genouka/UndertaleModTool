using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Xml;

namespace UndertaleModTool
{
    public class GmlSpecParameter
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public bool Optional { get; set; }
        public string Description { get; set; }
    }

    public class GmlSpecFunction
    {
        public string Name { get; set; }
        public string ReturnType { get; set; }
        public bool Deprecated { get; set; }
        public string Description { get; set; }
        public List<GmlSpecParameter> Parameters { get; set; } = new();
    }

    public class GmlSpecVariable
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public bool Deprecated { get; set; }
        public bool CanGet { get; set; }
        public bool CanSet { get; set; }
        public bool IsInstance { get; set; }
        public string Description { get; set; }
    }

    public class GmlSpecConstant
    {
        public string Name { get; set; }
        public string Class { get; set; }
        public string Type { get; set; }
        public bool Deprecated { get; set; }
        public string Description { get; set; }
    }

    public static class GmlSpecLoader
    {
        private static readonly Dictionary<string, GmlSpecFunction> _functionsEn = new();
        private static readonly Dictionary<string, GmlSpecVariable> _variablesEn = new();
        private static readonly Dictionary<string, GmlSpecConstant> _constantsEn = new();

        private static readonly Dictionary<string, GmlSpecFunction> _functionsZh = new();
        private static readonly Dictionary<string, GmlSpecVariable> _variablesZh = new();
        private static readonly Dictionary<string, GmlSpecConstant> _constantsZh = new();

        private static bool _loaded = false;
        private static bool _loadFailed = false;
        private static readonly object _lock = new();

        public static void EnsureLoaded()
        {
            if (_loaded || _loadFailed) return;
            lock (_lock)
            {
                if (_loaded || _loadFailed) return;

                try
                {
                    LoadSpecFromResource("UndertaleModTool.Resources.GmlSpecEnglish", _functionsEn, _variablesEn, _constantsEn);
                    LoadSpecFromResource("UndertaleModTool.Resources.GmlSpecChinese", _functionsZh, _variablesZh, _constantsZh);

                    if (_functionsEn.Count == 0)
                    {
                        LoadSpecFromFile("GmlSpecEnglish.gmlspec", _functionsEn, _variablesEn, _constantsEn);
                        LoadSpecFromFile("GmlSpecChinese.gmlspec", _functionsZh, _variablesZh, _constantsZh);
                    }

                    if (_functionsEn.Count > 0)
                        _loaded = true;
                    else
                        _loadFailed = true;
                }
                catch
                {
                    _loadFailed = true;
                }
            }
        }

        private static void LoadSpecFromResource(string fullResourceName,
            Dictionary<string, GmlSpecFunction> functions,
            Dictionary<string, GmlSpecVariable> variables,
            Dictionary<string, GmlSpecConstant> constants)
        {
            var assembly = typeof(GmlSpecLoader).Assembly;
            using var stream = assembly.GetManifestResourceStream(fullResourceName);
            if (stream == null) return;

            ParseSpecStream(stream, functions, variables, constants);
        }

        private static void LoadSpecFromFile(string fileName,
            Dictionary<string, GmlSpecFunction> functions,
            Dictionary<string, GmlSpecVariable> variables,
            Dictionary<string, GmlSpecConstant> constants)
        {
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string filePath = Path.Combine(exeDir, fileName);
            if (!File.Exists(filePath))
            {
                filePath = Path.Combine(exeDir, "Resources", fileName);
            }
            if (!File.Exists(filePath)) return;

            using var stream = File.OpenRead(filePath);
            ParseSpecStream(stream, functions, variables, constants);
        }

        private static void ParseSpecStream(Stream stream,
            Dictionary<string, GmlSpecFunction> functions,
            Dictionary<string, GmlSpecVariable> variables,
            Dictionary<string, GmlSpecConstant> constants)
        {
            var doc = new XmlDocument();
            doc.Load(stream);

            var root = doc.DocumentElement;
            if (root == null) return;

            var funcNodes = root.SelectNodes("Functions/Function");
            if (funcNodes != null)
            {
                foreach (XmlNode node in funcNodes)
                {
                    var func = new GmlSpecFunction
                    {
                        Name = node.Attributes["Name"]?.Value ?? "",
                        ReturnType = node.Attributes["ReturnType"]?.Value ?? "",
                        Deprecated = node.Attributes["Deprecated"]?.Value == "true"
                    };

                    var descNode = node.SelectSingleNode("Description");
                    func.Description = descNode?.InnerText?.Trim() ?? "";

                    var paramNodes = node.SelectNodes("Parameter");
                    if (paramNodes != null)
                    {
                        foreach (XmlNode pNode in paramNodes)
                        {
                            func.Parameters.Add(new GmlSpecParameter
                            {
                                Name = pNode.Attributes["Name"]?.Value ?? "",
                                Type = pNode.Attributes["Type"]?.Value ?? "",
                                Optional = pNode.Attributes["Optional"]?.Value == "true",
                                Description = pNode.InnerText?.Trim() ?? ""
                            });
                        }
                    }

                    functions[func.Name] = func;
                }
            }

            var varNodes = root.SelectNodes("Variables/Variable");
            if (varNodes != null)
            {
                foreach (XmlNode node in varNodes)
                {
                    var variable = new GmlSpecVariable
                    {
                        Name = node.Attributes["Name"]?.Value ?? "",
                        Type = node.Attributes["Type"]?.Value ?? "",
                        Deprecated = node.Attributes["Deprecated"]?.Value == "true",
                        CanGet = node.Attributes["Get"]?.Value == "true",
                        CanSet = node.Attributes["Set"]?.Value == "true",
                        IsInstance = node.Attributes["Instance"]?.Value == "true",
                        Description = node.InnerText?.Trim() ?? ""
                    };

                    variables[variable.Name] = variable;
                }
            }

            var constNodes = root.SelectNodes("Constants/Constant");
            if (constNodes != null)
            {
                foreach (XmlNode node in constNodes)
                {
                    var constant = new GmlSpecConstant
                    {
                        Name = node.Attributes["Name"]?.Value ?? "",
                        Class = node.Attributes["Class"]?.Value ?? "",
                        Type = node.Attributes["Type"]?.Value ?? "",
                        Deprecated = node.Attributes["Deprecated"]?.Value == "true",
                        Description = node.InnerText?.Trim() ?? ""
                    };

                    constants[constant.Name] = constant;
                }
            }
        }

        private static bool IsZhLocale()
        {
            string lang = Settings.Instance.Language;
            if (!string.IsNullOrEmpty(lang) && lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        public static GmlSpecFunction GetFunction(string name)
        {
            EnsureLoaded();
            if (IsZhLocale())
            {
                if (_functionsZh.TryGetValue(name, out var zhFunc)) return zhFunc;
            }
            if (_functionsEn.TryGetValue(name, out var enFunc)) return enFunc;
            return null;
        }

        public static GmlSpecVariable GetVariable(string name)
        {
            EnsureLoaded();
            if (IsZhLocale())
            {
                if (_variablesZh.TryGetValue(name, out var zhVar)) return zhVar;
            }
            if (_variablesEn.TryGetValue(name, out var enVar)) return enVar;
            return null;
        }

        public static GmlSpecConstant GetConstant(string name)
        {
            EnsureLoaded();
            if (IsZhLocale())
            {
                if (_constantsZh.TryGetValue(name, out var zhConst)) return zhConst;
            }
            if (_constantsEn.TryGetValue(name, out var enConst)) return enConst;
            return null;
        }
    }
}
