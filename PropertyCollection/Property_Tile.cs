using System.Collections.Generic;

namespace MultiTrackHelper.PropertyCollection
{
    public sealed partial class Property
    {
        /// <summary>
        /// 砖块定位控件（偏移量 + 相对基准）。游戏把 default 读成 [数量, 基准名]：
        /// 第二个元素用 Convert.ToString + Enum.Parse 还原成 TileRelativeTo，
        /// 所以这里写枚举本身即可（和旧的 List&lt;object&gt;{int, enum} 序列化结果一致）。
        /// data 键序：type → default → unit → hideButtons → min → max。
        /// </summary>
        public static Property Tile(string name, (int, TileRelativeTo)? value_default = null,
            List<int> hideButtons = null, int min = int.MinValue, int max = int.MaxValue,
            string key = null, bool canBeDisabled = false, bool startEnabled = false,
            Dictionary<string, string> enableIf = null, Dictionary<string, string> disableIf = null)
        {
            Property property = new Property(PropertyKind.Tile, name, key, canBeDisabled, startEnabled, enableIf, disableIf);
            property.TypeName = "Tile";
            property.MinValue = min;
            property.MaxValue = max;
            property.HideButtons = hideButtons ?? new List<int>();
            if (value_default.HasValue)
            {
                property.TileOffset = value_default.Value.Item1;
                property.TileRelative = value_default.Value.Item2;
            }
            return property;
        }

        private void WriteTile(Dictionary<string, object> data)
        {
            data["default"] = new List<object> { TileOffset, TileRelative };
            data["unit"] = "tilesFrom";
            data["hideButtons"] = HideButtons;
            data["min"] = MinValue;
            data["max"] = MaxValue;
        }
    }
}
