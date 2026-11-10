using System;
using System.Globalization;
using Newtonsoft.Json;
using UndertaleModRunner;

namespace UndertaleModTool.Debugging
{
    /// <summary>A single row in the debugger variable/instance inspectors.</summary>
    public sealed class VariableRow
    {
        public string Source { get; set; }
        public string Name { get; set; }
        public string Value { get; set; }
        public ValueInfo ValueInfo { get; set; }
        public int VarId { get; set; }
        public int Slot { get; set; }
        public int FrameIndex { get; set; }
        public int InstanceId { get; set; }
        /// <summary>Original variable name (empty names cannot be resolved by the native setter).</summary>
        public string EditName { get; set; }

        public bool CanEdit => DebugVariableEditing.IsEditable(this);
    }

    /// <summary>
    /// Converts a text value back into the scalar value JSON accepted by the
    /// native setters, and dispatches the write to the right debugger scope.
    /// </summary>
    public static class DebugVariableEditing
    {
        public static bool IsEditable(VariableRow row)
        {
            ValueInfo value = row?.ValueInfo;
            if (value is null)
                return false;
            return value.Type is "real" or "int32" or "int64" or "bool" or "string" or "undefined";
        }

        public static string FormatValue(ValueInfo value)
        {
            if (value is null || value.Type == "undefined")
                return "undefined";
            if (value.Type == "bool" && value.Value is bool boolean)
                return boolean ? "true" : "false";
            if (value.Type is "real" or "int32" or "int64" && value.Value is IFormattable number)
                return number.ToString(null, CultureInfo.InvariantCulture);
            if (value.Type == "string")
                return value.Value?.ToString() ?? "";
            return value.ToString();
        }

        public static bool TryBuildValueJson(ValueInfo original, string text, out string json, out string error)
        {
            json = null;
            error = null;
            if (original is null)
            {
                error = "The value type is unavailable.";
                return false;
            }

            text ??= "";
            switch (original.Type)
            {
                case "undefined":
                    json = "{\"type\":\"undefined\"}";
                    return true;

                case "real":
                    if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double real))
                    {
                        error = "Enter a number.";
                        return false;
                    }
                    json = JsonConvert.SerializeObject(new { type = "real", value = real });
                    return true;

                case "int32":
                    if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int int32))
                    {
                        error = "Enter a 32-bit integer.";
                        return false;
                    }
                    json = JsonConvert.SerializeObject(new { type = "int32", value = int32 });
                    return true;

                case "int64":
                    if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long int64))
                    {
                        error = "Enter a 64-bit integer.";
                        return false;
                    }
                    json = JsonConvert.SerializeObject(new { type = "int64", value = int64 });
                    return true;

                case "bool":
                    string boolText = text.Trim();
                    if (bool.TryParse(boolText, out bool boolean))
                    {
                        json = JsonConvert.SerializeObject(new { type = "bool", value = boolean });
                        return true;
                    }
                    if (boolText == "1" || boolText == "0")
                    {
                        json = JsonConvert.SerializeObject(new { type = "bool", value = boolText == "1" });
                        return true;
                    }
                    error = "Enter true or false.";
                    return false;

                case "string":
                    json = JsonConvert.SerializeObject(new { type = "string", value = text });
                    return true;

                default:
                    error = $"Editing {original.Type} values is not supported.";
                    return false;
            }
        }

        public static bool Commit(DebugSessionService service, VariableRow row, string text, out string error)
        {
            error = "No active debug session.";
            if (service is null || row is null || !service.IsPaused)
                return false;

            if (!TryBuildValueJson(row.ValueInfo, text, out string json, out error))
                return false;

            switch (row.Source)
            {
                case "local":
                    return service.SetFrameVariable(row.FrameIndex, false, row.Slot, json, out error);
                case "arg":
                    return service.SetFrameVariable(row.FrameIndex, true, row.Slot, json, out error);
                case "global":
                    return service.SetGlobalVariable(row.EditName, json, out error);
                case "instance":
                    return service.SetInstanceVariable(row.InstanceId, row.EditName, json, out error);
                default:
                    error = "Unknown variable scope.";
                    return false;
            }
        }
    }
}
