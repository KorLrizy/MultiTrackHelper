using System;
using System.Collections.Generic;
using UnityEngine;

namespace MultiTrackHelper.PropertyCollection
{
    public sealed partial class Property
    {
        /// <summary>
        /// 一对浮点控件（"范围"或"单值对"）。游戏把 default/min/max 都按 Vector2 的 x/y 拆开读。
        /// data 键序：type → default → min → max → isRange。
        /// </summary>
        public static Property FloatPair(string name, object value_default = null, object min = null,
            object max = null, string key = null, bool isRange = true, bool canBeDisabled = false,
            bool startEnabled = false, Dictionary<string, string> enableIf = null,
            Dictionary<string, string> disableIf = null)
        {
            return new Property(PropertyKind.FloatPair, name, key, canBeDisabled, startEnabled, enableIf, disableIf)
            {
                TypeName = "FloatPair",
                DefaultValue = value_default,
                MinValue = min,
                MaxValue = max,
                IsRange = isRange
            };
        }

        private void WriteFloatPair(Dictionary<string, object> data)
        {
            if (DefaultValue == null)
            {
                // 旧实现这里给的是整数 0，不是 0f；JSON 里会写成 0 而不是 0.0，保持一致
                data["default"] = new List<object> { 0, 0 };
            }
            else
            {
                Vector2 vector = (Vector2)DefaultValue;
                data["default"] = new List<object> { vector.x, vector.y };
            }
            data["min"] = MinValue != null ? Convert.ToSingle(MinValue) : float.NegativeInfinity;
            data["max"] = MaxValue != null ? Convert.ToSingle(MaxValue) : float.PositiveInfinity;
            data["isRange"] = IsRange;
        }
    }
}
