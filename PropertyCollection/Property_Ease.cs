using System.Collections.Generic;

namespace MultiTrackHelper.PropertyCollection
{
    public sealed partial class Property
    {
        /// <summary>
        /// 缓动曲线下拉框。游戏的 PropertyInfo 把 "Enum:Ease" 当特例，
        /// 直接映射到 DG.Tweening.Ease，所以这里不能按普通枚举写程序集限定名。
        /// data 键序：type → default。
        /// </summary>
        public static Property Ease(string name, string key = null, bool canBeDisabled = false,
            bool startEnabled = false, Dictionary<string, string> enableIf = null,
            Dictionary<string, string> disableIf = null)
        {
            return new Property(PropertyKind.Ease, name, key, canBeDisabled, startEnabled, enableIf, disableIf)
            {
                TypeName = "Enum:Ease",
                DefaultValue = "Linear"
            };
        }

        private void WriteEase(Dictionary<string, object> data)
        {
            data["default"] = DefaultValue;
        }
    }
}
