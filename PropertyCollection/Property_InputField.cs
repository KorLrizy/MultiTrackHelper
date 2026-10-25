using System;
using System.Collections.Generic;
using UnityEngine;

namespace MultiTrackHelper.PropertyCollection
{
    /// <summary>
    /// 输入框的取值类型。成员名会原样写进 data["type"]（游戏按 "Float"/"Int"/"String"/"Vector2" 分支），
    /// 改名等于换控件，不能动。
    /// </summary>
    public enum InputKind
    {
        Float,
        Int,
        String,
        Vector2,
    }

    public sealed partial class Property
    {
        /// <summary>
        /// 输入框（可带滑条）。min/max 的形态随取值类型变化：
        /// Float/Int 是标量，Vector2 是 [x, y]。String 没有滑条键。
        /// data 键序：type → unit → default → min → max →（Float/Int 才有的）slider。
        /// </summary>
        public static Property Input(string name, InputKind type, object value_default = null,
            object min = null, object max = null, string unit = null, string key = null,
            bool canBeDisabled = false, bool startEnabled = false,
            Dictionary<string, string> enableIf = null, Dictionary<string, string> disableIf = null,
            bool slider = false)
        {
            return new Property(PropertyKind.InputField, name, key, canBeDisabled, startEnabled, enableIf, disableIf)
            {
                TypeName = Enum.GetName(typeof(InputKind), type),
                FieldKind = type,
                DefaultValue = value_default,
                MinValue = min,
                MaxValue = max,
                Unit = unit,
                Slider = slider
            };
        }

        private void WriteInputField(Dictionary<string, object> data)
        {
            // unit 在 default 之前，保持旧的键序
            data["unit"] = Unit;
            switch (FieldKind)
            {
                case InputKind.Float:
                    data["default"] = Convert.ToSingle(DefaultValue);
                    data["min"] = MinValue != null ? Convert.ToSingle(MinValue) : float.NegativeInfinity;
                    data["max"] = MaxValue != null ? Convert.ToSingle(MaxValue) : float.PositiveInfinity;
                    data["slider"] = Slider;
                    break;
                case InputKind.Int:
                    data["default"] = Convert.ToInt32(DefaultValue);
                    data["min"] = MinValue != null ? Convert.ToInt32(MinValue) : int.MinValue;
                    data["max"] = MaxValue != null ? Convert.ToInt32(MaxValue) : int.MaxValue;
                    data["slider"] = Slider;
                    break;
                case InputKind.String:
                    data["default"] = (DefaultValue as string) ?? string.Empty;
                    data["min"] = MinValue != null ? Convert.ToInt32(MinValue) : int.MinValue;
                    data["max"] = MaxValue != null ? Convert.ToInt32(MaxValue) : int.MaxValue;
                    break;
                case InputKind.Vector2:
                    data["default"] = VectorPair(DefaultValue, 0, 0);
                    data["min"] = VectorPair(MinValue, float.NegativeInfinity, float.NegativeInfinity);
                    data["max"] = VectorPair(MaxValue, float.PositiveInfinity, float.PositiveInfinity);
                    break;
                default:
                    throw new ArgumentOutOfRangeException("FieldKind", FieldKind, "未知输入框类型");
            }
        }

        /// <summary>
        /// 把 Vector2 拆成 [x, y]；为空时用给定的一对缺省值。
        /// 缺省值故意用两个独立参数，好让"全是整数 0"和"全是 ±Infinity"两种旧形态都原样保留。
        /// </summary>
        private static List<object> VectorPair(object value, object emptyX, object emptyY)
        {
            if (value == null)
                return new List<object> { emptyX, emptyY };
            Vector2 vector = (Vector2)value;
            return new List<object> { vector.x, vector.y };
        }
    }
}
