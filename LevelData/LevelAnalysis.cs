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
    /// </summary>
    public static class LevelAnalysis
    {
        public static int GetIndex(object data)
        {
            Tuple<int, TileRelativeTo> tuple = (Tuple<int, TileRelativeTo>)data;
            // 作用范围按地砖编号算：最后一块有效砖的编号 = 地砖数 - 1
            int lastTile = LastTileIndex();
            return Mathf.Clamp(tuple.Item1 + (tuple.Item2 == TileRelativeTo.End ? lastTile : 0), 0, lastTile);
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

                // 第 0 块砖上的事件不参与半径 / 透明度 / pause 的累计（与旧实现一致）；
                // BPM 不受这条影响：它现在直接取关卡 BPM × 该砖速度
                if (floor > 0 && floorEvents != null
                    && floorEvents.TryGetValue(floor, out Dictionary<LevelEventType, List<LevelEvent>> events))
                {
                    if (events.TryGetValue(LevelEventType.Hold, out List<LevelEvent> holdEvents))
                    {
                        holdDistance = (int)holdEvents[0]["distanceMultiplier"];
                    }

                    if (events.TryGetValue(LevelEventType.Pause, out List<LevelEvent> pauseEvents))
                    {
                        isPause = true;
                        pauseDuration = (float)pauseEvents[0]["duration"];
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

                result[floor] = new TrackData(
                    timing.sweepAngle,
                    timing.head,
                    timing.tail,
                    hasTwirl,
                    floor == 0 ? Vector2.zero : (Vector2)(currentFloor.transform.position / 1.5f),
                    floor == 0 ? 0f : currentFloor.transform.rotation.eulerAngles.z,
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
                    trackOpacity = floorOpacity,
                    geometricAngle = timing.geometricAngle,
                    speed = timing.speed,
                    numPlanets = timing.numPlanets,
                    extraBeats = timing.extraBeats,
                    gameEntryTime = timing.gameEntryTime,
                    gameExitTime = timing.gameExitTime
                };
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

        public static Dictionary<int, Dictionary<LevelEventType, List<LevelEvent>>> GetFloorEvents()
        {
            int tileCount = LastTileIndex() + 1;
            Dictionary<int, Dictionary<LevelEventType, List<LevelEvent>>> floorEvents = new Dictionary<int, Dictionary<LevelEventType, List<LevelEvent>>>();
            foreach (LevelEvent levelEvent in scnEditor.instance.events)
            {
                if (levelEvent.floor <= 0 || levelEvent.floor >= tileCount)
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

        public static Tuple<int, int> GetAffectedRange()
        {
            LevelEvent levelEvent = Main.GetEvent((LevelEventType)Main.MultiTrack.type);
            int v1 = 0;
            int v2 = 0;
            AffectAt affect = (AffectAt)levelEvent["affectAt"];
            switch (affect)
            {
                case AffectAt.SpecificRange:
                    v1 = GetIndex(levelEvent["affectTileRangeFrom"]);
                    v2 = GetIndex(levelEvent["affectTileRangeTo"]);
                    break;
                case AffectAt.SelectedTiles:
                    if (scnEditor.instance.selectedFloors != null && scnEditor.instance.selectedFloors.Count > 0)
                    {
                        v1 = scnEditor.instance.selectedFloors[0].seqID;
                        v2 = scnEditor.instance.selectedFloors[scnEditor.instance.selectedFloors.Count - 1].seqID;
                    }
                    else
                    {
                        v1 = -1;
                        v2 = -1;
                    }
                    break;
            }

            return Tuple.Create(v1, v2);
        }

        public static float GetMidDirection(float startDirection, float endDirection, bool isCCW, bool goFullCircleIfEqual)
        {
            return AngleMath.MidDirection(startDirection, endDirection, isCCW, goFullCircleIfEqual);
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
            public bool hasTwirl;
            public Vector2 position;
            public float rotation;
            public bool isPause;
            public float pauseDuration;
            public bool isHold;
            public int holdDuration;
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
        }
    }
}
