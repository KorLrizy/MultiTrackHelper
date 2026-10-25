using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UColor = UnityEngine.Color;

namespace MultiTrackHelper.PropertyCollection
{
    public sealed partial class Property
    {
        /// <summary>
        /// 颜色控件。游戏要的 default 是十六进制字符串（带透明度 8 位，不带 6 位，小写）。
        /// data 键序：type → default → usesAlpha。
        /// 本文件用 UColor 指代 UnityEngine.Color，避免与工厂方法名 Color 混淆。
        /// </summary>
        public static Property Color(string name, UColor? value_default = null, bool usesAlpha = true,
            string key = null, bool canBeDisabled = false, bool startEnabled = false,
            Dictionary<string, string> enableIf = null, Dictionary<string, string> disableIf = null)
        {
            return new Property(PropertyKind.Color, name, key, canBeDisabled, startEnabled, enableIf, disableIf)
            {
                TypeName = "Color",
                UsesAlpha = usesAlpha,
                DefaultValue = ToHex(value_default ?? UColor.white, usesAlpha)
            };
        }

        private void WriteColor(Dictionary<string, object> data)
        {
            data["default"] = DefaultValue;
            data["usesAlpha"] = UsesAlpha;
        }

        /// <summary>小写十六进制字符表：直接按两字符输出，免得走 ToUpper/Lower 与格式化（不受当前区域影响）。</summary>
        private const string HexDigits = "0123456789abcdef";

        /// <summary>
        /// 颜色 → 十六进制字符串。逐字节用 <see cref="HexDigits"/> 拼，结果与旧实现
        /// （string.Format("{0:X2}...").ToLower()）逐字符相同：同样是先 Clamp01 再 ×255 截断取整（不是四舍五入）。
        /// </summary>
        private static string ToHex(UColor c, bool alpha)
        {
            var builder = new StringBuilder(alpha ? 8 : 6);
            AppendHex(builder, ToByte(c.r));
            AppendHex(builder, ToByte(c.g));
            AppendHex(builder, ToByte(c.b));
            if (alpha)
                AppendHex(builder, ToByte(c.a));
            return builder.ToString();
        }

        private static void AppendHex(StringBuilder builder, byte value)
        {
            builder.Append(HexDigits[value >> 4]);
            builder.Append(HexDigits[value & 0x0F]);
        }

        /// <summary>通道值 → 字节：Clamp01 后乘 255 再截断（与旧实现一致，刻意不用 Mathf.RoundToInt）。</summary>
        private static byte ToByte(float f)
        {
            return (byte)(Mathf.Clamp01(f) * 255f);
        }
    }
}
