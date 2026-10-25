using ADOFAI;
using System.Collections.Generic;

namespace MultiTrackHelper.Features
{
    /// <summary>
    /// 真地砖图标 → 假轨道图标 的静态映射（T1.3）。
    /// 原实现是 25 个 case 的 switch，游戏新增/改名枚举时新增分支很容易漏；
    /// 这里按 FloorIcon 的全部成员列成一张显式表，未知成员统一回落 None，
    /// 语义与旧 switch 的 default 一致。
    /// </summary>
    internal static class TrackIconMapping
    {
        private static readonly Dictionary<FloorIcon, CustomFloorIcon> Table = new Dictionary<FloorIcon, CustomFloorIcon>
        {
            { FloorIcon.None, CustomFloorIcon.None },
            { FloorIcon.Snail, CustomFloorIcon.Snail },
            { FloorIcon.AnimatedSnail, CustomFloorIcon.Snail },
            { FloorIcon.DoubleSnail, CustomFloorIcon.DoubleSnail },
            { FloorIcon.AnimatedDoubleSnail, CustomFloorIcon.DoubleSnail },
            { FloorIcon.Rabbit, CustomFloorIcon.Rabbit },
            { FloorIcon.AnimatedRabbit, CustomFloorIcon.Rabbit },
            { FloorIcon.DoubleRabbit, CustomFloorIcon.DoubleRabbit },
            { FloorIcon.AnimatedDoubleRabbit, CustomFloorIcon.DoubleRabbit },
            { FloorIcon.Swirl, CustomFloorIcon.Swirl },
            { FloorIcon.Checkpoint, CustomFloorIcon.Checkpoint },
            { FloorIcon.HoldArrowShort, CustomFloorIcon.HoldArrowShort },
            { FloorIcon.HoldArrowLong, CustomFloorIcon.HoldArrowLong },
            { FloorIcon.HoldReleaseShort, CustomFloorIcon.HoldReleaseShort },
            { FloorIcon.HoldReleaseLong, CustomFloorIcon.HoldReleaseLong },
            { FloorIcon.MultiPlanetTwo, CustomFloorIcon.MultiPlanetTwo },
            { FloorIcon.MultiPlanetThreeMore, CustomFloorIcon.MultiPlanetThreeMore },
            { FloorIcon.Portal, CustomFloorIcon.Portal },
            // 以下图标没有对应的假轨道图标：旧 switch 与 default 一起落到 None
            { FloorIcon.ForceNone, CustomFloorIcon.None },
            { FloorIcon.Decoration, CustomFloorIcon.None },
            { FloorIcon.SwirlCW, CustomFloorIcon.None },
            { FloorIcon.SameSpeed, CustomFloorIcon.None },
            { FloorIcon.OneVfx, CustomFloorIcon.None },
            { FloorIcon.Vfx, CustomFloorIcon.None },
            { FloorIcon.MultiPlanetThreeLess, CustomFloorIcon.None }
        };

        /// <summary>映射表覆盖的 FloorIcon 成员数（离线对拍用：应与 Enum.GetValues(FloorIcon).Length 相等）。</summary>
        internal static int Count => Table.Count;

        /// <summary>
        /// 取假轨道图标。所有已收录成员都返回与旧 switch 相同的结果；
        /// 游戏将来新增而本表未收录的值返回 None（旧 switch 的 default 行为）。
        /// </summary>
        internal static CustomFloorIcon Resolve(FloorIcon icon)
        {
            return Table.TryGetValue(icon, out CustomFloorIcon mapped) ? mapped : CustomFloorIcon.None;
        }
    }
}
