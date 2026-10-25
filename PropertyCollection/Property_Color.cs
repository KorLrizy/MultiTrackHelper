using System.Collections.Generic;
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

        private static string ToHex(UColor c, bool alpha)
        {
            return (alpha
                ? string.Format("{0:X2}{1:X2}{2:X2}{3:X2}", ToByte(c.r), ToByte(c.g), ToByte(c.b), ToByte(c.a))
                : string.Format("{0:X2}{1:X2}{2:X2}", ToByte(c.r), ToByte(c.g), ToByte(c.b))).ToLower();
        }

        private static byte ToByte(float f)
        {
            return (byte)(Mathf.Clamp01(f) * 255f);
        }
    }
}
