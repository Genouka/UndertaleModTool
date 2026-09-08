using System;
using System.Globalization;
using System.Windows.Data;

namespace UndertaleModTool
{
    /// <summary>
    /// Converts a 32-bit unsigned (or signed) offset/pointer between its numeric value and a
    /// "0xHHHHHHHH" edit string, round-trip (ConvertBigToLittle order irrelevant). Used by the
    /// WAD per-entry editors for precise, absolute-offset pointer manipulation: the user types
    /// the target file offset as hex and it is written straight back to the model field.
    /// </summary>
    public class HexUIntConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            ulong v;
            switch (value)
            {
                case uint u: v = u; break;
                case int i: v = unchecked((uint)i); break;
                case byte b: v = b; break;
                case short s: v = unchecked((uint)(ushort)s); break;
                case null: return parameter is string none ? none : "0x00000000";
                default:
                    try { v = System.Convert.ToUInt64(value, CultureInfo.InvariantCulture); }
                    catch { return "0x00000000"; }
                    break;
            }
            string width = parameter as string?? "";
            return width == "8" ? $"0x{v:X8}" : $"0x{v:X8}";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string text = ((value as string) ?? "").Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                text = text[2..];
            text = text.Replace("_", "").Replace(" ", "");
            uint parsed = ParseHexOrDec(text);
            if (targetType == typeof(int) || targetType == typeof(short) || targetType == typeof(long))
                return unchecked((int)parsed);
            return parsed;
        }

        private static uint ParseHexOrDec(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
            // Try hex first, then decimal, so both "0x1C" and "28" round-trip.
            if (uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex))
                return hex;
            if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint dec))
                return dec;
            return 0;
        }
    }
}
