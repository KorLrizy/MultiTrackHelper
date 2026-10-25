using UnityEngine;

namespace MultiTrackHelper
{
    /// <summary>
    /// 关卡几何的唯一换算入口：世界坐标 ↔ tile 单位，以及"真地砖朝向 → 假轨道装饰 rotation"的推导。
    ///
    /// 单位口径：游戏里所有"格子"位移都乘/除 scrController.tileSize——scrDecoration.SetPlacementType
    /// 把 position 乘 tileSize、pivotOffset 也乘 tileSize；scnGame 处理 PositionTrack 的位移同样乘
    /// ADOBase.controller.tileSize。旧实现把 1.5 写死在两处（<see cref="LevelAnalysis"/> 的基础位置、
    /// MultiTrackFeature 的烘焙），关卡改用别的 tileSize 时两边会算出不同的格子数；现在统一读同一个来源。
    /// </summary>
    internal static class TrackGeometry
    {
        /// <summary>
        /// 旧实现硬编码 1.5f 的兼容退化值。它只是“读不到 tileSize 时保持旧行为”的回退值，
        /// 不声称是游戏的唯一默认值（实际值一律以 scrController.tileSize 为准）。
        /// </summary>
        internal const float FallbackTileSize = 1.5f;

        /// <summary>
        /// 一格的边长（世界单位）。直接读 scrController.tileSize；非有限值或非正值一律退回
        /// <see cref="FallbackTileSize"/>，避免除零把整批位置算成 Infinity。
        /// </summary>
        internal static float TileSize
        {
            get
            {
                scrController controller = ADOBase.controller;
                if (controller != null)
                {
                    float size = controller.tileSize;
                    if (IsFinite(size) && size > 0f)
                    {
                        return size;
                    }
                }
                return FallbackTileSize;
            }
        }

        /// <summary>世界坐标 → tile 单位（相对世界原点），只取 x/y。</summary>
        internal static Vector2 WorldToTile(Vector3 worldPosition)
        {
            float tileSize = TileSize;
            return new Vector2(worldPosition.x / tileSize, worldPosition.y / tileSize);
        }

        /// <summary>
        /// 真地砖 → 假轨道装饰的 rotation（度）。midspin 砖不走这里：游戏按 trackType = Midspin 画整块，
        /// rotation 直接取 head。
        ///
        /// 推导（依据 v3 反编译源码，ignore_verify_build/real_game_research_unique/full）：
        /// 1. 真地砖的轨道弧在**地砖局部角度空间**里的两个端点就是 (tail, head)：
        ///    scrFloor.UpdateAngle 把 (π/2 − entryangle, π/2 − exitangle) 交给 FloorMeshRenderer.SetAngle，
        ///    即 _angle0 = 90 − entry = tail、_angle1 = 90 − exit = head；FloorMesh 再以地砖局部原点为圆心、
        ///    以 scrController.baseFloorDimensions（跟随 tileSize）为半径把这段弧画出来。半径不影响角度端点，
        ///    所以只要角度端点对上，两条弧的方向就一致。
        /// 2. 假轨道（AddObject 的 ObjectDecorationType.Floor）的弧在**装饰局部角度空间**里固定占
        ///    (−180, 180 − angle)：scrObjectDecoration.SetFloorAngle 写 _angle0 = −180、_angle1 = 180 − angle，
        ///    angle 就是要写的 trackAngle，即本砖的扫角 g（= geometricAngle）。装饰整体再按 rotation 旋转
        ///    （scrDecoration.SetRotation：pivotTrans.rotation = Euler(0, 0, startRot + rotAngle)）。
        ///    FloorMesh 只取两端点之间 ≤180° 的那一侧，所以只要两个端点集合对上，两条弧就重合。
        /// 3. 令 {R − 180, R + 180 − g} = {tail, head}，代入 scrMisc.GetAngleMoved 的符号约定
        ///    （isCW 时 exit − entry > 0，即 head = tail − g；isCCW 时 head = tail + g）解得
        ///        R ≡ tail − 180 + (isCCW ? g : 0)   (mod 360)。
        ///    这也解释了 PlanetMotion 里星球基础姿态为什么从 tail − 180 起算：它们是同一套局部角度。
        /// 4. 第 3 步的 R 换写成"弧平分线 − 装饰参考姿态"就是本函数：真弧的平分线是 tail→head 沿 isCCW 的
        ///    中点（旧实现算的就是这个），而装饰在 rotation = 0 时的参考姿态有闭式 180 − g/2
        ///    （见 <see cref="LocalReferencePose"/>）。两条式子在 g ∈ [0, 360] 上逐值相等，R 允许差 360 的整数倍。
        ///    平分线仍用 MidDirection：g = 360 时它按"补一整圈再折半"给 tail + 180，与旧实现一致。
        /// </summary>
        internal static float TrackRotation(float tail, float head, bool isCCW, float geometricAngle)
        {
            return AngleMath.MidDirection(tail, head, isCCW, true) - LocalReferencePose(geometricAngle);
        }

        /// <summary>
        /// 假轨道装饰的参考姿态（度）：rotation = 0 时装饰弧的两个端点在 (−180, 180 − g)，
        /// 相对真弧平分线的固定偏移就是 180 − g/2。
        ///
        /// 旧实现是"再调一次 MidDirection(180, (360 − ((g + 180) % 360)) % 360, false, true)"把它算出来，
        /// 本函数是同一个量的闭式化简：镜像那一步等价于取 (180 − g) mod 360 再走半程，结果恒为 180 − g/2
        /// （g = 360 时得 0、g = 0 时也得 0，与旧实现相同）。
        /// 这是纯代数结论，本轮未做任何数值或游戏内验证：两者只应差浮点舍入，具体误差由主代理实测确认。
        /// </summary>
        internal static float LocalReferencePose(float geometricAngle)
        {
            // Sweep 把扫角收进 (0, 360]：360 与 0 都按整圈处理，与 FloorTimeline 的扫角口径一致
            return 180f - AngleMath.Sweep(geometricAngle) * 0.5f;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
