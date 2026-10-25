using System;
using System.Collections.Generic;

namespace MultiTrackHelper.PropertyCollection
{
    /// <summary>控件种类，只决定 ToData() 写哪些键，本身不参与序列化。</summary>
    internal enum PropertyKind
    {
        Bool,
        Button,
        Color,
        Ease,
        Enum,
        FloatPair,
        InputField,
        Note,
        Tile,
    }

    /// <summary>
    /// 一个面板控件的完整描述。
    /// 游戏侧（ADOFAI.PropertyInfo 的构造器）只认 ToData() 产出的字典，
    /// 所以这里不再"一种控件一个子类"：公共头在本文件，
    /// 各控件特有的键由 Property_Bool.cs 等文件的 WriteXxx 按原顺序写出。
    ///
    /// 硬约束：ToData() 的键名、键序、值类型必须与旧实现逐字一致。
    /// 游戏的 PropertyInfo 按这些键解析（缺键 KeyNotFound、错类型 InvalidCast），
    /// 旧的用户面板设置也是按这些键存的。
    /// </summary>
    public sealed partial class Property
    {
        /// <summary>Tile 控件左侧三个快捷按钮的位掩码，用于 hideButtons 列表。</summary>
        public const int THIS_TILE = 0x001;
        public const int START = 0x010;
        public const int END = 0x100;

        private readonly PropertyKind kind;
        private readonly string name;
        private readonly string key;
        private readonly bool canBeDisabled;
        private readonly bool startEnabled;
        private readonly List<object> enableIf;
        private readonly List<object> disableIf;

        private Property(PropertyKind kind, string name, string key, bool canBeDisabled, bool startEnabled,
            Dictionary<string, string> enableIf, Dictionary<string, string> disableIf)
        {
            this.kind = kind;
            this.name = name;
            this.key = key;
            this.canBeDisabled = canBeDisabled;
            this.startEnabled = startEnabled;
            this.enableIf = Flatten(enableIf);
            this.disableIf = Flatten(disableIf);
        }

        /// <summary>写进 data["type"] 的字符串，决定游戏用哪个控件 prefab。</summary>
        internal string TypeName { get; set; }

        /// <summary>data["default"]。各控件自己解释它的类型，这里原样写回。</summary>
        internal object DefaultValue { get; set; }

        /// <summary>data["min"]（输入框、FloatPair、Tile 用）。</summary>
        internal object MinValue { get; set; }

        /// <summary>data["max"]。</summary>
        internal object MaxValue { get; set; }

        /// <summary>data["unit"]：单位本地化键的后半段。</summary>
        internal string Unit { get; set; }

        /// <summary>data["noteKey"]：Note 控件指向的说明文本键。</summary>
        internal string NoteKey { get; set; }

        /// <summary>data["slider"]：Float/Int 输入框是否用滑条样式。</summary>
        internal bool Slider { get; set; }

        /// <summary>data["isRange"]：FloatPair 是"范围"还是"单值"。</summary>
        internal bool IsRange { get; set; } = true;

        /// <summary>data["usesAlpha"]：颜色是否带透明度。</summary>
        internal bool UsesAlpha { get; set; } = true;

        /// <summary>data["hideButtons"]：Tile 控件要隐藏哪几个快捷按钮。</summary>
        internal List<int> HideButtons { get; set; }

        /// <summary>Tile 默认值的砖块偏移量。</summary>
        internal int TileOffset { get; set; }

        /// <summary>Tile 默认值的相对基准。</summary>
        internal TileRelativeTo TileRelative { get; set; } = TileRelativeTo.ThisTile;

        /// <summary>输入框的取值类型（决定 data["type"] 与 min/max 的形态）。</summary>
        internal InputKind FieldKind { get; set; }

        /// <summary>面板属性名，同时也是 LevelEvent 里的数据键。</summary>
        public string Name { get { return name; } }

        /// <summary>持久化/本地化键；null 表示用游戏默认的 editor.事件名.属性名。</summary>
        public string Key { get { return key; } }

        /// <summary>
        /// 交给 ADOFAI.PropertyInfo 的字典。键序固定为
        /// name → canBeDisabled → startEnabled → enableIf → disableIf → key → type → 各控件自己的键。
        /// </summary>
        public Dictionary<string, object> ToData()
        {
            var data = new Dictionary<string, object>(8);
            data.Add("name", name);
            data.Add("canBeDisabled", canBeDisabled);
            data.Add("startEnabled", startEnabled);
            data.Add("enableIf", enableIf);
            data.Add("disableIf", disableIf);
            data.Add("key", key);
            data.Add("type", TypeName);
            WritePayload(data);
            return data;
        }

        private void WritePayload(Dictionary<string, object> data)
        {
            switch (kind)
            {
                case PropertyKind.Bool:
                    WriteBool(data);
                    return;
                case PropertyKind.Button:
                    WriteButton(data);
                    return;
                case PropertyKind.Color:
                    WriteColor(data);
                    return;
                case PropertyKind.Ease:
                    WriteEase(data);
                    return;
                case PropertyKind.Enum:
                    WriteEnum(data);
                    return;
                case PropertyKind.FloatPair:
                    WriteFloatPair(data);
                    return;
                case PropertyKind.InputField:
                    WriteInputField(data);
                    return;
                case PropertyKind.Note:
                    WriteNote(data);
                    return;
                case PropertyKind.Tile:
                    WriteTile(data);
                    return;
                default:
                    throw new ArgumentOutOfRangeException("kind", kind, "未知控件种类");
            }
        }

        /// <summary>
        /// 把 {键, 值} 条件表摊平成 [键, 值, 键, 值]。
        /// 游戏的 RDEditorUtils.DecodeStringArray 只认 List&lt;object&gt; 里的字符串，
        /// 所以这里不能换成 string[] 或 Dictionary。
        /// </summary>
        private static List<object> Flatten(Dictionary<string, string> conditions)
        {
            var flat = new List<object>();
            if (conditions == null)
                return flat;
            foreach (KeyValuePair<string, string> pair in conditions)
            {
                flat.Add(pair.Key);
                flat.Add(pair.Value);
            }
            return flat;
        }
    }
}
