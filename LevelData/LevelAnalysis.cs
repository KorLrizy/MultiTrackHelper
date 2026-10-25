using ADOFAI;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace MultiTrackHelper
{
    /// <summary>
    /// 从当前编辑器关卡读取每块地砖的几何 / BPM / 时间数据，以及作用范围换算。
    ///
    /// 角度与时间一律取游戏已经算好的 scrFloor 数据（见 <see cref="FloorTimeline"/>）：
    /// entryTime（真实到达时刻）、angleLength（真实扫角）、speed（变速）、holdLength、extraBeats。
    /// 不在这里用 "60 / bpm × 角度 / 180" 自己估时间——那条公式漏了多星球、变速、turnaround、
    /// 倒计时与 extraBeats。只有 ScaleRadius 与轨道透明度需要按砖累计事件。
    /// 世界坐标一律经 <see cref="TrackGeometry"/> 换算成 tile 单位，方向/朝向的数学也在那里，这里只取数据。
    /// </summary>
    public static class LevelAnalysis
    {
        /// <summary>
        /// 把面板里的"砖号 + 相对位置"解析成实际砖号。兼容保留的公共入口。
        /// 旧调用方只要求"拿一个可用砖号"，所以解析失败时退回首砖 0；内部逻辑一律用 <see cref="ResolveTileIndex"/>。
        /// </summary>
        public static int GetIndex(object data)
        {
            int index = ResolveTileIndex(data, LastTileIndex());
            return index < 0 ? 0 : index;
        }

        /// <summary>
        /// "砖号 + 相对位置" → 砖号（纯函数，不读游戏状态）。
        ///
        /// 返回值约定：
        /// · 能解析 → 钳到 [0, lastTile] 的砖号；加法在 long 里做，砖号接近 int.MaxValue 时不会溢出回绕成首砖；
        /// · 解析不了（data 不是本类型的 Tuple、lastTile &lt; 0、相对位置是未定义枚举值）→ -1，
        ///   由调用方当作"无范围"，绝不悄悄退化成首砖。
        /// ThisTile 与 Start 等价（范围面板不提供 ThisTile，外部旧调用可能传，旧实现也是按 Start 处理）。
        /// </summary>
        internal static int ResolveTileIndex(object data, int lastTile)
        {
            if (lastTile < 0 || !(data is Tuple<int, TileRelativeTo> tuple))
                return -1;

            long offset;
            switch (tuple.Item2)
            {
                case TileRelativeTo.Start:
                case TileRelativeTo.ThisTile:
                    offset = 0L;
                    break;
                case TileRelativeTo.End:
                    offset = lastTile;
                    break;
                default:
                    // 未定义的枚举值：当成非法数据，不当成 0
                    return -1;
            }

            // 作用范围按地砖编号算：最后一块有效砖的编号 = 地砖数 - 1
            long index = (long)tuple.Item1 + offset;
            if (index < 0L)
                index = 0L;
            else if (index > lastTile)
                index = lastTile;
            return (int)index;
        }

        public static TrackData[] GetTrackDatas(Dictionary<int, Dictionary<LevelEventType, List<LevelEvent>>> floorEvents = null)
        {
            scrLevelMaker maker = scrLevelMaker.instance;
            if (maker == null || maker.listFloors == null || maker.listFloors.Count == 0)
            {
                Main.Logger?.Log("GetTrackDatas: 当前没有地砖数据，返回空结果");
                return new TrackData[0];
            }

            List<scrFloor> listFloors = maker.listFloors;
            int floorCount = ResolveFloorCount(listFloors);
            FloorTimeline.Entry[] timeline = FloorTimeline.Build(floorCount);
            if (timeline.Length == 0)
            {
                return new TrackData[0];
            }
            floorCount = timeline.Length - 1;

            TrackData[] result = new TrackData[floorCount + 1];
            scnEditor editor = scnEditor.instance;
            float baseBpm = editor != null && editor.levelData != null ? editor.levelData.bpm : 0f;
            // 星球半径大小(ScaleRadius)是百分比且按砖生效，后续砖沿用最近一次事件的取值
            float radiusScalePercent = 100f;
            // 真轨道透明度：由 PositionTrack.opacity 决定，同样按砖生效
            float trackOpacityPercent = 100f;

            // 诊断开关打开时才额外维护旧公式的累计，默认不重算
            bool diagnostics = FloorTimeline.EnableDiagnostics;
            double legacyArrival = 0.0;
            double legacyDeparture = 0.0;

            for (int floor = 0; floor <= floorCount; floor++)
            {
                FloorTimeline.Entry timing = timeline[floor];
                scrFloor currentFloor = listFloors[floor];

                bool isPause = false;
                float pauseDuration = 0f;
                int holdDistance = 0;
                bool hasTwirl = false;
                // 本砖实际生效的透明度：justThisTile 的 PositionTrack 只改该砖，不改后续砖
                float floorOpacity = trackOpacityPercent;

                // 第 0 块砖上的事件同样生效：scnGame.ApplyEventsToFloors / ApplyCoreEventsToFloors 都是
                // "从 floors[0] 起逐砖累计"（target-game/scnGame.decompiled.cs:279-285 按 floor 入桶、
                // :366 与 :1126 两个 foreach 都从 floors[0] 开始）。ScaleRadius 与 PositionTrack.opacity
                // 是改一次沿用到后续砖，Pause 只要求本砖不是 midspin 且有后继砖。
                // 旧实现对 0 做了特殊处理，导致挂在首砖上的半径/透明度/暂停全部丢失。
                if (floorEvents != null
                    && floorEvents.TryGetValue(floor, out Dictionary<LevelEventType, List<LevelEvent>> events))
                {
                    // Hold 的 distanceMultiplier 只填 TrackData.holdDistance（兼容字段），真正的 hold 拍数
                    // 取 scrFloor.holdLength，两者不要混用
                    if (events.TryGetValue(LevelEventType.Hold, out List<LevelEvent> holdEvents))
                    {
                        holdEvents[0].TryGet("distanceMultiplier", out holdDistance);
                    }

                    // Pause 的生效条件与游戏一致（target-game/scnGame.decompiled.cs:1144-1145）：
                    // 本砖不是 midspin 且有后继砖。
                    // 游戏把同砖所有 Pause 的 duration 累加进 extraBeats，这里也只累计有效值；
                    // duration 非正或非有限的事件不产生停留，不能当成 pause（extraBeats 还会被 FreeRoam 等改写）。
                    if (!currentFloor.midSpin && floor + 1 < listFloors.Count
                        && events.TryGetValue(LevelEventType.Pause, out List<LevelEvent> pauseEvents))
                    {
                        foreach (LevelEvent pauseEvent in pauseEvents)
                        {
                            float eventDuration = pauseEvent.GetFloat("duration");
                            if (!IsFinite(eventDuration) || eventDuration <= 0f)
                            {
                                continue;
                            }

                            pauseDuration += eventDuration;
                        }

                        isPause = pauseDuration > 0f;
                    }

                    if (events.TryGetValue(LevelEventType.ScaleRadius, out List<LevelEvent> radiusEvents))
                    {
                        foreach (LevelEvent radiusEvent in radiusEvents)
                        {
                            // GetFloat 在缺 key 时返回 0，这里忽略非正值，避免把假轨道大小写成 0
                            float eventScale = radiusEvent.GetFloat("scale");
                            if (eventScale > 0f)
                            {
                                radiusScalePercent = eventScale;
                            }
                        }
                    }

                    if (events.TryGetValue(LevelEventType.PositionTrack, out List<LevelEvent> positionEvents))
                    {
                        foreach (LevelEvent positionEvent in positionEvents)
                        {
                            // 透明度只在该属性被启用时生效（与游戏 TryGetAndSet(onlyIfEnabled) 一致）
                            if (!positionEvent.disabled.TryGetValue("opacity", out bool opacityDisabled) || opacityDisabled)
                            {
                                continue;
                            }

                            float eventOpacity = positionEvent.GetFloat("opacity");
                            if (positionEvent.GetBool("justThisTile"))
                            {
                                floorOpacity = eventOpacity;
                            }
                            else
                            {
                                trackOpacityPercent = eventOpacity;
                                floorOpacity = eventOpacity;
                            }
                        }
                    }

                    // hasTwirl 是公开返回字段的一部分，即使生成路径不读它也照游戏语义填写
                    if (events.ContainsKey(LevelEventType.Twirl))
                    {
                        hasTwirl = true;
                    }
                }

                // hold 以游戏字段为准：holdLength 是真正生效的拍数（无后继砖或 duration < 0 时游戏置 -1）
                bool isHold = currentFloor.holdLength > 0;
                int holdDuration = isHold ? currentFloor.holdLength : 0;

                // 每砖 BPM 取游戏口径：关卡 BPM × 该砖速度。scrFloor.speed 已经含 SetSpeed 的
                // BPM / 倍率，angleOffset 分段变速时游戏也会折算成这一块砖的平均速度，不用再自己扫事件。
                float bpm = baseBpm > 0f ? baseBpm * timing.speed : 0f;

                if (diagnostics && bpm > 0f)
                {
                    // 旧公式（T5 之前的口径）：60/bpm × (几何角/180 + pause + 2×hold)，不含 speed 与多星球修正。
                    // 旧模型用的是几何角，不是 angleLength 反算出来的每拍角，所以这里用 geometricAngle。
                    double legacyDuration = timing.midSpin
                        ? 0.0
                        : (60.0 / bpm) * (timing.geometricAngle / 180.0 + pauseDuration + 2.0 * holdDuration);
                    if (floor == 0)
                    {
                        legacyDeparture = timing.geometricAngle / (3.0 * bpm);
                    }
                    else
                    {
                        legacyDeparture = legacyArrival + legacyDuration;
                    }
                    FloorTimeline.LogComparison(floor, timing, (float)legacyArrival, (float)legacyDeparture);
                    legacyArrival = legacyDeparture;
                }

                // 快照统一由 TrackData.Snapshot 构建：时间/角度全部取 FloorTimeline，基础位置按 TrackGeometry
                // 的同一套 tile 单位换算（不再把 1.5 写死在这里）；hasTwirl / holdDistance 是公开字段，照旧填
                result[floor] = TrackData.Snapshot(
                    timing,
                    floor == 0 ? Vector2.zero : TrackGeometry.WorldToTile(currentFloor.transform.position),
                    floor == 0 ? 0f : currentFloor.transform.rotation.eulerAngles.z,
                    isPause,
                    pauseDuration,
                    isHold,
                    holdDuration,
                    bpm,
                    radiusScalePercent,
                    floorOpacity,
                    hasTwirl: hasTwirl,
                    holdDistance: holdDistance);
            }

            return result;
        }

        /// <summary>
        /// 只返回每块地砖的扫过角度(度)，用于高频读取（如选中砖块时刷新面板）。
        /// 与 GetAnglesData 数值等价，但不分配 AngleData 对象。midspin 记 999 哨兵。
        /// </summary>
        public static float[] GetFloorAngles()
        {
            scrLevelMaker maker = scrLevelMaker.instance;
            if (maker == null || maker.listFloors == null || maker.listFloors.Count == 0)
            {
                return new float[0];
            }

            FloorTimeline.Entry[] timeline = FloorTimeline.Build(ResolveFloorCount(maker.listFloors));
            float[] result = new float[timeline.Length];
            for (int i = 0; i < timeline.Length; i++)
            {
                result[i] = timeline[i].sweepAngle;
            }
            return result;
        }

        public static AngleData[] GetAnglesData()
        {
            scrLevelMaker maker = scrLevelMaker.instance;
            if (maker == null || maker.listFloors == null || maker.listFloors.Count == 0)
            {
                return new AngleData[0];
            }

            FloorTimeline.Entry[] timeline = FloorTimeline.Build(ResolveFloorCount(maker.listFloors));
            AngleData[] result = new AngleData[timeline.Length];
            for (int i = 0; i < timeline.Length; i++)
            {
                result[i] = new AngleData(timeline[i].sweepAngle, timeline[i].head, timeline[i].tail);
            }
            return result;
        }

        /// <summary>
        /// 按砖号归集关卡事件。包含第 0 块砖：游戏自身（scnGame.ApplyEventsToFloors 等）就是逐砖从
        /// floors[0] 开始应用事件，挂在首砖上的 MoveTrack / ScaleRadius / PositionTrack / Pause 都生效。
        /// 旧实现把 floor &lt;= 0 一律丢弃，调用方靠另走一遍 editor.events 绕开，现在两边口径一致。
        /// 调用方需要注意：拿到的事件表现在可能包含键 0。
        /// </summary>
        public static Dictionary<int, Dictionary<LevelEventType, List<LevelEvent>>> GetFloorEvents()
        {
            int tileCount = LastTileIndex() + 1;
            Dictionary<int, Dictionary<LevelEventType, List<LevelEvent>>> floorEvents = new Dictionary<int, Dictionary<LevelEventType, List<LevelEvent>>>();
            foreach (LevelEvent levelEvent in scnEditor.instance.events)
            {
                if (levelEvent.floor < 0 || levelEvent.floor >= tileCount)
                {
                    continue;
                }

                if (!floorEvents.TryGetValue(levelEvent.floor, out Dictionary<LevelEventType, List<LevelEvent>> byType))
                {
                    byType = new Dictionary<LevelEventType, List<LevelEvent>>();
                    floorEvents[levelEvent.floor] = byType;
                }
                if (!byType.TryGetValue(levelEvent.eventType, out List<LevelEvent> list))
                {
                    list = new List<LevelEvent>();
                    byType[levelEvent.eventType] = list;
                }
                list.Add(levelEvent);
            }

            return floorEvents;
        }

        public static Vector2 GetPivotOffset(Vector2 offset, float rotation)
        {
            return AngleMath.PivotOffset(offset, rotation);
        }

        /// <summary>
        /// 把"由地砖世界坐标换算出的方块单位偏移"折算回星球半径 100% 下的数值。
        /// 半径被 ScaleRadius 放大后地砖世界间距同步放大，不折算 pivotOffset 会成倍偏移。
        /// </summary>
        public static Vector2 NormalizeToRadius100(Vector2 offset, float radiusScalePercent)
        {
            float factor = radiusScalePercent / 100f;
            return factor > 0f ? offset / factor : offset;
        }

        public static float CorrectDirection(float direction)
        {
            return AngleMath.Direction(direction);
        }

        public static float CorrectAngle(float direction)
        {
            return AngleMath.Sweep(direction);
        }

        /// <summary>没有任何可作用的地砖 / 面板不可用时的统一返回值，调用方靠 -1 短路。</summary>
        private static readonly Tuple<int, int> NoRange = Tuple.Create(-1, -1);

        /// <summary>
        /// 当前面板的作用范围（闭区间）。解析失败（面板事件不存在、作用方式不可识别、没有选中砖、
        /// 关卡里没有地砖）一律返回 (-1, -1)，由调用方短路。
        /// 仅负责从游戏里取原始数据，判定规则全在纯函数 <see cref="ResolveAffectedRange"/> 里。
        /// </summary>
        public static Tuple<int, int> GetAffectedRange()
        {
            LevelEvent levelEvent = Main.MultiTrack == null ? null : Main.GetEvent((LevelEventType)Main.MultiTrack.type);
            if (levelEvent == null)
            {
                Main.Logger?.Log("GetAffectedRange: 找不到面板事件，按无作用范围处理");
                return NoRange;
            }

            if (!levelEvent.TryGet("affectAt", out AffectAt affect))
            {
                Main.Logger?.Log("GetAffectedRange: 面板的作用方式(affectAt)不可识别，按无作用范围处理");
                return NoRange;
            }

            // 上界必须来自实际地砖：关卡里没有砖时没有可作用的范围。
            // 这里不用 LastTileIndex()：它在没有地砖时会退回角度数据长度，用来算范围会把范围凭空扩到空关卡上。
            List<scrFloor> floors = RealFloors();
            if (floors == null)
            {
                Main.Logger?.Log("GetAffectedRange: 当前没有实际地砖，按无作用范围处理");
                return NoRange;
            }

            levelEvent.TryGet("affectTileRangeFrom", out Tuple<int, TileRelativeTo> rangeFrom);
            levelEvent.TryGet("affectTileRangeTo", out Tuple<int, TileRelativeTo> rangeTo);
            scnEditor editor = scnEditor.instance;
            List<scrFloor> selected = editor == null ? null : editor.selectedFloors;
            return ResolveAffectedRange(affect, rangeFrom, rangeTo, selected, floors.Count - 1);
        }

        /// <summary>
        /// 当前关卡实际存在的地砖列表；没有就返回 null。
        /// 与 <see cref="LastTileIndex"/> 的区别：最后找不到地砖时**不**退回到角度数据长度。
        /// </summary>
        private static List<scrFloor> RealFloors()
        {
            scrLevelMaker maker = scrLevelMaker.instance;
            List<scrFloor> floors = maker == null ? null : maker.listFloors;
            return floors == null || floors.Count == 0 ? null : floors;
        }

        /// <summary>
        /// 作用范围判定（纯函数）：不读游戏状态、不改任何东西。
        /// 先判 lastTile：小于 0（没有实际地砖）直接 (-1, -1)。
        /// - <see cref="AffectAt.SpecificRange"/>：两端各自钳位到真实范围，再规范成正序；
        ///   任一端解析不出来（<see cref="ResolveTileIndex"/> 返回 -1）整体算无范围；
        /// - <see cref="AffectAt.SelectedTiles"/>：选中砖取 min/max 砖号（见 <see cref="ResolveSelectedRange"/>）；
        /// - 其它取值（未知枚举）：(-1, -1)。
        /// </summary>
        internal static Tuple<int, int> ResolveAffectedRange(AffectAt affect, object rangeFrom, object rangeTo,
            IList<scrFloor> selected, int lastTile)
        {
            if (lastTile < 0)
                return NoRange;

            switch (affect)
            {
                case AffectAt.SpecificRange:
                {
                    int from = ResolveTileIndex(rangeFrom, lastTile);
                    int to = ResolveTileIndex(rangeTo, lastTile);
                    // 任一端解析失败都算"没有范围"：不能拿 -1 参与比较，更不能退化成首砖静默生成
                    if (from < 0 || to < 0)
                        return NoRange;

                    // 两端各自钳位后统一成正序：反向填写（to < from）与游戏里"拖反"的选区语义等价
                    return from <= to ? Tuple.Create(from, to) : Tuple.Create(to, from);
                }
                case AffectAt.SelectedTiles:
                    return ResolveSelectedRange(selected, lastTile);
                default:
                    return NoRange;
            }
        }

        /// <summary>
        /// 选中砖范围：取所有选中砖 seqID 的最小/最大值（忽略 null 元素），再钳到 [0, lastTile]。
        /// 用 min/max 而不是首尾元素，是因为 selectedFloors 的顺序取决于选择方式，不一定递增。
        /// 没有选中砖（或全是 null）时返回 (-1, -1)。
        /// </summary>
        internal static Tuple<int, int> ResolveSelectedRange(IList<scrFloor> selected, int lastTile)
        {
            if (selected == null || selected.Count == 0 || lastTile < 0)
            {
                return NoRange;
            }

            int min = int.MaxValue;
            int max = int.MinValue;
            for (int i = 0; i < selected.Count; i++)
            {
                scrFloor floor = selected[i];
                if (floor == null)
                {
                    continue;
                }

                int id = floor.seqID;
                if (id < min) min = id;
                if (id > max) max = id;
            }

            if (min == int.MaxValue)
            {
                return NoRange;
            }

            return Tuple.Create(Mathf.Clamp(min, 0, lastTile), Mathf.Clamp(max, 0, lastTile));
        }

        /// <summary>
        /// 两方向的中点方向。保留的公共入口（外部兼容）：假轨道朝向已改为
        /// <see cref="TrackGeometry.TrackRotation"/>：它在真弧平分线之外还扣掉了装饰自身的参考姿态。
        /// </summary>
        public static float GetMidDirection(float startDirection, float endDirection, bool isCCW, bool goFullCircleIfEqual)
        {
            return AngleMath.MidDirection(startDirection, endDirection, isCCW, goFullCircleIfEqual);
        }

        /// <summary>有限值判断：NaN / ±Infinity 一律视为不可用。</summary>
        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>最后一块有效砖的编号；地砖列表不可用时退回关卡角度数据长度。</summary>
        private static int LastTileIndex()
        {
            scrLevelMaker maker = scrLevelMaker.instance;
            if (maker != null && maker.listFloors != null && maker.listFloors.Count > 0)
            {
                return maker.listFloors.Count - 1;
            }

            scnEditor editor = scnEditor.instance;
            if (editor != null && editor.levelData != null)
            {
                return editor.levelData.angleData.Count;
            }
            return 0;
        }

        /// <summary>
        /// 地砖数与关卡角度数的口径对齐：两者不一致时（重铺/裁剪的过渡状态）取小者，
        /// 保证不越界，并留下一条日志。
        /// </summary>
        private static int ResolveFloorCount(List<scrFloor> listFloors)
        {
            int lastTile = listFloors.Count - 1;
            scnEditor editor = scnEditor.instance;
            int angleCount = editor != null && editor.levelData != null ? editor.levelData.angleData.Count : lastTile;
            int floorCount = Mathf.Min(angleCount, lastTile);
            if (floorCount < 0)
            {
                floorCount = 0;
            }

            if (floorCount != lastTile)
            {
                Main.Logger?.Log($"LevelAnalysis: 地砖数({listFloors.Count})与角度数({angleCount})不一致，按 {floorCount} 块地砖处理");
            }
            return floorCount;
        }

        public class AngleData
        {
            public float angle;
            public float head;
            public float tail;
            public AngleData(float angle, float head, float tail)
            {
                this.angle = angle;
                this.head = head;
                this.tail = tail;
            }
        }

        public class TrackData
        {
            public float angle;
            public float head;
            public float tail;
            /// <summary>该砖上是否有 Twirl 事件（公开字段，按砖扫描填入；生成路径不消费）。</summary>
            public bool hasTwirl;
            public Vector2 position;
            public float rotation;
            public bool isPause;
            public float pauseDuration;
            public bool isHold;
            public int holdDuration;
            /// <summary>
            /// 该砖首条 Hold 事件的 distanceMultiplier（原始百分比整数，公开字段；生成路径不消费）。
            /// 注意它与 scrFloor.holdDistance 的"格数"口径不同，真正生效的 hold 拍数是 <see cref="holdDuration"/>。
            /// </summary>
            public int holdDistance;
            public float bpm;
            public float arrivalTime;
            public float departureTime;
            /// <summary>该砖生效的"星球半径大小"(ScaleRadius)百分比，默认 100。</summary>
            public float radiusScale = 100f;
            /// <summary>该砖生效的真轨道透明度（PositionTrack.opacity，百分比），默认 100。</summary>
            public float trackOpacity = 100f;
            /// <summary>该砖的几何扫角（度，midspin = 999）：head/tail 的夹角，不带多星球修正。</summary>
            public float geometricAngle;
            /// <summary>该砖的游戏速度倍率（scrFloor.speed），1 表示原速。</summary>
            public float speed = 1f;
            /// <summary>该砖的星球数量（scrFloor.numPlanets），默认 2。</summary>
            public int numPlanets = 2;
            /// <summary>该砖的额外拍数（scrFloor.extraBeats，pause / FreeRoam 等已按砖归集）。</summary>
            public float extraBeats;
            /// <summary>游戏原始到达时刻（scrFloor.entryTime，含开局倒计时）。</summary>
            public double gameEntryTime;
            /// <summary>游戏原始离开时刻（下一砖 entryTime，末砖为推算值，同样含倒计时）。</summary>
            public double gameExitTime;

            /// <summary>
            /// 兼容构造器：只为旧调用方保留原签名与字段语义，数据层已不再用它（改为 <see cref="Snapshot"/>），
            /// 避免 14 个位置参数的调用表继续扩散。
            /// </summary>
            public TrackData(float angle, float head, float tail, bool hasTwirl, Vector2 position, float rotation, bool isPause, float pauseDuration, bool isHold, int holdDuration, int holdDistance, float bpm, float arrivalTime, float departureTime)
            {
                this.angle = angle;
                this.head = head;
                this.tail = tail;
                this.hasTwirl = hasTwirl;
                this.position = position;
                this.rotation = rotation;
                this.isPause = isPause;
                this.isHold = isHold;
                this.pauseDuration = isPause ? pauseDuration : 0;
                this.holdDuration = isHold ? holdDuration : 0;
                this.holdDistance = isHold ? holdDistance : 0;
                this.bpm = bpm;
                this.arrivalTime = arrivalTime;
                this.departureTime = departureTime;
            }

            /// <summary>
            /// 从时间线快照 + 本砖状态构造一块砖的轨道数据（内部构建入口，取代 14 参数构造调用）。
            /// 时间/角度类字段全部来自 <see cref="FloorTimeline.Entry"/>，
            /// 只剩"这块砖此刻的游戏状态"作为参数：位置、自身旋转、暂停、hold、BPM、半径、透明度。
            /// hasTwirl / holdDistance 是公开字段，照旧由数据层扫描填入，但生成路径不消费它们。
            /// </summary>
            internal static TrackData Snapshot(FloorTimeline.Entry timing, Vector2 position, float rotation,
                bool isPause, float pauseDuration, bool isHold, int holdDuration, float bpm,
                float radiusScalePercent, float trackOpacityPercent, bool hasTwirl = false, int holdDistance = 0)
            {
                return new TrackData(
                    timing.sweepAngle,
                    timing.head,
                    timing.tail,
                    hasTwirl,
                    position,
                    rotation,
                    isPause,
                    pauseDuration,
                    isHold,
                    holdDuration,
                    holdDistance,
                    bpm,
                    timing.arrivalTime,
                    timing.departureTime)
                {
                    radiusScale = radiusScalePercent,
                    trackOpacity = trackOpacityPercent,
                    geometricAngle = timing.geometricAngle,
                    speed = timing.speed,
                    numPlanets = timing.numPlanets,
                    extraBeats = timing.extraBeats,
                    gameEntryTime = timing.gameEntryTime,
                    gameExitTime = timing.gameExitTime
                };
            }
        }
    }
}
