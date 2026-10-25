using System;
using System.Collections.Generic;

namespace MultiTrackHelper.PropertyCollection
{
    public sealed partial class Property
    {
        /// <summary>
        /// 枚举下拉框。游戏按 "Enum:程序集限定名" 反查枚举类型，
        /// 所以 type 里必须带完整限定名，只写类型名会解析不到。
        /// data 键序：type → default（default 是成员名文本，不是数值）。
        /// </summary>
        public static Property EnumOf<T>(string name, T value_default = default(T), string key = null,
            bool canBeDisabled = false, bool startEnabled = false,
            Dictionary<string, string> enableIf = null, Dictionary<string, string> disableIf = null) where T : Enum
        {
            return new Property(PropertyKind.Enum, name, key, canBeDisabled, startEnabled, enableIf, disableIf)
            {
                TypeName = "Enum:" + typeof(T).AssemblyQualifiedName,
                DefaultValue = value_default.ToString()
            };
        }

        private void WriteEnum(Dictionary<string, object> data)
        {
            data["default"] = DefaultValue;
        }
    }
}
