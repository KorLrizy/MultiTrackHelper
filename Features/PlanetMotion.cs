using ADOFAI;
using System;
using System.Collections.Generic;
using UnityEngine;
using TrackData = MultiTrackHelper.LevelAnalysis.TrackData;

namespace MultiTrackHelper.Features
{
    /// <summary>星球角色。蓝/红只决定标签与落点偏移，运动规则完全相同。</summary>
    internal enum PlanetOrbitRole
    {
        Blue,
        Red
    }

    /// <summary>
    /// 一条待输出的星球事件（纯运动规格，不持有 LevelEvent）。
    /// 同时保存两套口径：事件口径（砖号 / angleOffset / duration，写 LevelEvent 用）与
    /// 时间口径（startTime / durationSeconds，烘焙与曲线用）。两套由同一次规划算出，
    /// 因此"输出事件"与"烘焙曲线"不会再出现两套时间。
    /// </summary>
    internal sealed class PlanetMotionDraft
    {
        public PlanetOrbitRole role;
        /// <summary>这是"调整星球视差"的两标签事件（不参与星球曲线）。</summary>
        public bool isParallaxAdjust;
        public int eventFloor;
        public string tag;
        public bool hasAngleOffset;
        public float angleOffset;
        /// <summary>事件时长（拍，事件所在砖的 crotchet 口径）。</summary>
        public float duration;
        /// <summary>绝对起点（秒，scrFloor.entryTime 口径）。数值不可用时为 NaN。</summary>
        public double startTime;
        /// <summary>绝对时长（秒）。数值不可用时为 NaN。</summary>
        public double durationSeconds;
        /// <summary>旧实现里没写该属性时保持 false；写事件时就不写这个字段（事件用默认值）。</summary>
        public bool hasPosition;
        public Vector2 position;
        public bool hasRotation;
        public float rotation;
        /// <summary>仅视差事件：目标大小与视差（100 - 视差 / 视差值）。</summary>
        public float scale;
        public float parallax;
    }

    /// <summary>
    /// 一块砖的星球运动规格：到达/离开时刻、两颗星球的落点与旋转端点，以及
    /// sweep/hold/pause 三段秒数。注意：这三个秒数只是按拍数切分出的**诊断元数据**，
    /// 不是实际输出的运动分段——本轮仍然是一砖一条补间事件（旋转数值沿用旧实现，
    /// 含暂停奇偶判断），游戏里的 tween 并没有按 sweep/hold/pause 拆开。
    /// 它们留给后续真机核实后的精确暂停修复使用，不能拿来声称视觉上已分段。
    /// </summary>
    internal sealed class PlanetFloorMotion
    {
        public int floor;
        public int nextFloor;
        public double arrivalTime;
        public double departureTime;
        public double sweepSeconds;
        public double holdSeconds;
        public double pauseSeconds;
        public bool midSpin;
        public bool leaderIsRed;
        public float startRotation;
        public float endRotation;
        public Vector2 leaderPosition;
        public Vector2 followerPosition;
        public Vector2 leaderHoldPosition;
        public Vector2 followerHoldPosition;
        public bool hasHold;
        public int holdBeats;
        public bool hasPause;
        public float pauseBeats;
    }

    /// <summary>一次生成的星球运动规划结果：每砖的运动规格 + 按输出顺序排好的事件规格。</summary>
    internal sealed class PlanetMotionPlan
    {
        public readonly List<PlanetFloorMotion> floors = new List<PlanetFloorMotion>();
        public readonly List<PlanetMotionDraft> drafts = new List<PlanetMotionDraft>();

        /// <summary>本批是否有真正的星球运动事件（只有视差调整不算）。</summary>
        public bool hasMoveEvents
        {
            get
            {
                foreach (PlanetMotionDraft draft in drafts)
                {
                    if (draft != null && !draft.isParallaxAdjust)
                        return true;
                }
                return false;
            }
        }
    }

    /// <summary>星球动画参数快照（面板值，读一次，规划不再摸 LevelEvent）。</summary>
    internal sealed class PlanetMotionSettings
    {
        public TrackDistribution distribution;
        public bool changeParallax;
        public bool affectPlanet;
        public float parallaxFrom;
        public float parallaxTo;
        public string bluePlanetTag;
        public string redPlanetTag;

        public bool distributed => distribution == TrackDistribution.Distributed;

        public static PlanetMotionSettings Read(LevelEvent dataPanel, string bluePlanetTag, string redPlanetTag)
        {
            Tuple<float, float> parallaxChange = dataPanel.Get<Tuple<float, float>>("parallaxChange");
            PlanetMotionSettings settings = new PlanetMotionSettings();
            settings.distribution = dataPanel.Get<TrackDistribution>("planetAnimationDistribution");
            settings.changeParallax = dataPanel.Get<bool>("changeParallax");
            settings.affectPlanet = dataPanel.Get<bool>("affectPlanet");
            settings.parallaxFrom = parallaxChange.Item1;
            settings.parallaxTo = parallaxChange.Item2;
            settings.bluePlanetTag = bluePlanetTag;
            settings.redPlanetTag = redPlanetTag;
            return settings;
        }
    }

    /// <summary>
    /// 星球运动的规划与输出（T6）。旧实现先把 MoveDecorations 事件写进 editor.events，
    /// BakePlanetTrackMotion 再把它们读回来当"基础曲线"；现在只有一份运动规格：
    /// 规划产出每砖姿态与事件规格，输出事件与烘焙曲线都吃同一份数据。
    /// 事件字段、添加顺序、同刻先后、负 angleOffset 全部按旧实现保留。
    /// </summary>
    internal static class PlanetMotionPlanner
    {
        /// <summary>
        /// 规划整批星球运动。不写编辑器、不读编辑器事件；只依赖砖数据与楼层时间。
        /// pivotTrackOffset 与旧实现相同，用于把落点折算到假星球的轴心坐标系。
        /// </summary>
        internal static PlanetMotionPlan Build(PlanetMotionSettings settings, TrackData[] trackDatas,
            List<scrFloor> listFloors, float levelBpm, int from, int to, float pivotTrackOffset)
        {
            PlanetMotionPlan plan = new PlanetMotionPlan();
            if (settings == null || trackDatas == null || listFloors == null)
                return plan;
            if (from < 0 || to < from || to >= trackDatas.Length || to >= listFloors.Count)
                return plan;

            bool distributed = settings.distributed;
            float startBpm = trackDatas[from].bpm;
            // 时间轴一律用游戏的双精度时刻（含倒计时偏移，但本批全部取差值，抵消常数项），
            // 不再用 TrackData 的 float arrivalTime/departureTime 相减
            double baseGameEntry = trackDatas[from].gameEntryTime;
            int trackCount = to - from + 1;

            // 旧实现用 moveRedPlanet 逐砖交替"领先星球"，第 1 块砖蓝领先。
            // 交替与是否 midSpin 无关：midSpin 砖只产出视差事件、不产出运动事件，但角色仍要翻转。
            bool moveRedPlanet = false;

            for (int floor = from, i = 1; floor <= to; floor++, i++)
            {
                TrackData data = trackDatas[floor];
                scrFloor tile = listFloors[floor];
                if (data == null || tile == null)
                {
                    moveRedPlanet = !moveRedPlanet;
                    continue;
                }

                int nextFloor = floor + 1 >= trackDatas.Length ? floor : floor + 1;
                Vector2 curFloorPos = data.position - trackDatas[from].position;
                Vector2 nextFloorPos = trackDatas[nextFloor].position - trackDatas[from].position;
                bool distributedEvent = distributed;

                int eventFloor = distributedEvent ? floor : from;
                float angleOffset = 0f;
                bool hasAngleOffset = false;
                if (!distributedEvent && !tile.midSpin)
                {
                    // 集中式：把"距锚点砖的时间差"折成锚点砖上的角度偏移。
                    // 时间差用双精度 gameEntryTime；旧实现用 float arrivalTime 相减，这里只提升精度，不改单位。
                    double time = data.gameEntryTime - baseGameEntry;
                    angleOffset = (float)(time / (60.0 / startBpm) * 180.0);
                    hasAngleOffset = true;
                }

                float durationBeats = BuildDuration(data, listFloors, levelBpm, eventFloor);
                double eventStart = ResolveStartSeconds(listFloors, levelBpm, eventFloor, angleOffset);
                double eventSeconds = ResolveDurationSeconds(listFloors, levelBpm, eventFloor, durationBeats);

                PlanetFloorMotion motion = BuildFloorMotion(data, trackDatas[nextFloor], floor, nextFloor,
                    baseGameEntry, moveRedPlanet, pivotTrackOffset, curFloorPos, nextFloorPos, tile);
                plan.floors.Add(motion);

                // 视差调整事件：与旧实现一样先于四类运动事件输出，标签是"红 蓝"两个
                if (settings.changeParallax && settings.affectPlanet)
                {
                    float t = trackCount > 1 ? (i - 1) / (float)(trackCount - 1) : 0f;
                    float parallaxValue = Mathf.Lerp(settings.parallaxFrom, settings.parallaxTo, t);
                    plan.drafts.Add(new PlanetMotionDraft
                    {
                        isParallaxAdjust = true,
                        eventFloor = eventFloor,
                        tag = $"{settings.redPlanetTag} {settings.bluePlanetTag}",
                        hasAngleOffset = hasAngleOffset,
                        angleOffset = angleOffset,
                        duration = 0f,
                        startTime = eventStart,
                        durationSeconds = 0.0,
                        scale = 100f - parallaxValue,
                        parallax = parallaxValue
                    });
                }

                if (!tile.midSpin)
                {
                    string leaderTag = moveRedPlanet ? settings.redPlanetTag : settings.bluePlanetTag;
                    string followerTag = moveRedPlanet ? settings.bluePlanetTag : settings.redPlanetTag;
                    PlanetOrbitRole leaderRole = moveRedPlanet ? PlanetOrbitRole.Red : PlanetOrbitRole.Blue;
                    PlanetOrbitRole followerRole = moveRedPlanet ? PlanetOrbitRole.Blue : PlanetOrbitRole.Red;

                    // 输出顺序与旧实现一致：跟随星球吸附 → 领先星球吸附 → 领先星球补间 →（有 hold 时）跟随星球补间
                    plan.drafts.Add(MakeSnap(hasAngleOffset, angleOffset, eventFloor, followerTag, followerRole,
                        true, motion.followerPosition, false, 0f, eventStart));
                    plan.drafts.Add(MakeSnap(hasAngleOffset, angleOffset, eventFloor, leaderTag, leaderRole,
                        true, motion.leaderPosition, true, motion.startRotation, eventStart));
                    plan.drafts.Add(MakeTween(hasAngleOffset, angleOffset, eventFloor, leaderTag, leaderRole,
                        durationBeats, eventStart, eventSeconds, true, motion.endRotation,
                        motion.hasHold, motion.leaderHoldPosition));
                    if (motion.hasHold)
                    {
                        plan.drafts.Add(MakeTween(hasAngleOffset, angleOffset, eventFloor, followerTag, followerRole,
                            durationBeats, eventStart, eventSeconds, false, 0f,
                            true, motion.followerHoldPosition));
                    }
                }

                moveRedPlanet = !moveRedPlanet;
            }

            return plan;
        }

        /// <summary>
        /// 把规划结果写成 MoveDecorations 事件。这是唯一接触编辑器的一步。
        /// skipMoveTags 里的星球（已被粘性方块烘焙接管的）只跳过它的运动事件，
        /// 视差调整事件照旧输出——与旧实现"先写基础事件、烘焙成功后删掉被接管的那部分"结果一致。
        /// </summary>
        internal static void Emit(scnEditor editor, PlanetMotionPlan plan, ICollection<string> skipMoveTags)
        {
            if (editor?.events == null || plan == null)
                return;

            foreach (PlanetMotionDraft draft in plan.drafts)
            {
                if (draft == null)
                    continue;
                if (draft.isParallaxAdjust)
                {
                    LevelEvent adjust = new LevelEvent(draft.eventFloor, LevelEventType.MoveDecorations);
                    if (draft.hasAngleOffset)
                        adjust["angleOffset"] = draft.angleOffset;
                    adjust.disabled["positionOffset"] = true;
                    adjust.disabled["scale"] = false;
                    adjust.disabled["parallax"] = false;
                    adjust["duration"] = draft.duration;
                    adjust["tag"] = draft.tag;
                    adjust["scale"] = new Vector2(draft.scale, draft.scale);
                    adjust["parallax"] = new Vector2(draft.parallax, draft.parallax);
                    editor.events.Add(adjust);
                    continue;
                }

                if (skipMoveTags != null && skipMoveTags.Contains(draft.tag))
                    continue;

                LevelEvent levelEvent = new LevelEvent(draft.eventFloor, LevelEventType.MoveDecorations);
                if (draft.hasAngleOffset)
                    levelEvent["angleOffset"] = draft.angleOffset;
                levelEvent.disabled["positionOffset"] = false;
                levelEvent.disabled["rotationOffset"] = false;
                levelEvent["tag"] = draft.tag;
                levelEvent["duration"] = draft.duration;
                if (draft.hasPosition)
                    levelEvent["positionOffset"] = draft.position;
                if (draft.hasRotation)
                    levelEvent["rotationOffset"] = draft.rotation;
                editor.events.Add(levelEvent);
            }
        }

        /// <summary>
        /// 事件时长（拍）：用游戏的双精度真实停留时间换算，不再用 angle/180+pause+2×hold 的旧公式。
        /// 秒数 = gameExitTime - gameEntryTime（已含扫角、hold、pause 以及 FreeRoam 等只记在 extraBeats
        /// 里、没有 Pause 事件的额外拍）；再按事件所在砖的 BPM（关卡 BPM × 该砖 speed，即该砖 crotchet 的倒数）
        /// 换成拍数：durationBeats = seconds × 事件砖BPM / 60。
        /// 为什么集中式/分布式可以共用一个公式：集中式的事件挂在锚点砖上，旧实现本来就用
        /// startBpm/bpm 把"本砖拍数"折成"锚点砖拍数"，等价于用锚点砖的真实 crotchet 换算；这里直接取事件砖。
        /// 与旧实现的预期差异：真实时间进 double 运算后再转 float，末位可能不同；有 FreeRoam/extraBeats 的砖
        /// 会因为补上旧公式漏掉的拍数而与旧值不同（这是有意的修正）。时间不可用时退回 0。
        /// </summary>
        private static float BuildDuration(TrackData data, List<scrFloor> listFloors, float levelBpm, int eventFloor)
        {
            double seconds = data.gameExitTime - data.gameEntryTime;
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0.0)
                return 0f;

            scrFloor eventTile = listFloors[eventFloor];
            if (eventTile == null)
                return 0f;
            float eventBpm = levelBpm * eventTile.speed;
            if (!IsFinite(eventBpm) || eventBpm <= 0f)
                return 0f;

            return (float)(seconds * (double)eventBpm / 60.0);
        }

        /// <summary>一块砖的到达/离开姿态与三段（扫过 / hold / 暂停）时间边界。</summary>
        private static PlanetFloorMotion BuildFloorMotion(TrackData data, TrackData nextData, int floor, int nextFloor,
            double baseGameEntry, bool moveRedPlanet, float pivotTrackOffset, Vector2 curFloorPos, Vector2 nextFloorPos,
            scrFloor tile)
        {
            float direction = tile.isCCW ? 1f : -1f;
            PlanetFloorMotion motion = new PlanetFloorMotion
            {
                floor = floor,
                nextFloor = nextFloor,
                arrivalTime = data.gameEntryTime - baseGameEntry,
                departureTime = data.gameExitTime - baseGameEntry,
                midSpin = tile.midSpin,
                leaderIsRed = moveRedPlanet,
                hasHold = data.isHold,
                holdBeats = data.isHold ? data.holdDuration * 2 : 0,
                hasPause = data.isPause,
                pauseBeats = data.isPause ? data.pauseDuration : 0f
            };

            // 旋转端点沿用旧实现：起点 tail-180，终点加扫角 + hold 整圈 + 暂停（旧奇偶判断，偶数拍才加）
            float startRotation = data.tail - 180f;
            // 装饰旋转用几何扫角（head/tail 夹角）；data.angle 是每拍角，只用于把时间换成拍数
            float endRotation = startRotation + direction * data.geometricAngle;
            if (data.isPause && data.pauseDuration % 2 == 0)
                endRotation = endRotation + direction * data.pauseDuration * 180f;
            if (data.isHold)
                endRotation = endRotation + direction * data.holdDuration * 360f;
            motion.startRotation = startRotation;
            motion.endRotation = endRotation;

            // 落点：领先星球与跟随星球在本砖的吸附位置（红领先时两者相对砖心各偏 1 格，蓝领先时都在砖心）
            if (moveRedPlanet)
            {
                motion.leaderPosition = PivotOffset(curFloorPos - new Vector2(1f, 0f), pivotTrackOffset);
                motion.followerPosition = PivotOffset(curFloorPos + new Vector2(1f, 0f), pivotTrackOffset);
            }
            else
            {
                motion.leaderPosition = PivotOffset(curFloorPos, pivotTrackOffset);
                motion.followerPosition = PivotOffset(curFloorPos, pivotTrackOffset);
            }

            // hold 结束落点：沿下一砖 tail 方向再进 1 格，红领先分支仍带 ±1 的偏移
            Vector2 holdBase = nextFloorPos + new Vector2(
                Mathf.Cos(nextData.tail * Mathf.Deg2Rad), Mathf.Sin(nextData.tail * Mathf.Deg2Rad));
            if (moveRedPlanet)
            {
                motion.leaderHoldPosition = PivotOffset(holdBase - new Vector2(1f, 0f), pivotTrackOffset);
                motion.followerHoldPosition = PivotOffset(holdBase + new Vector2(1f, 0f), pivotTrackOffset);
            }
            else
            {
                motion.leaderHoldPosition = PivotOffset(holdBase, pivotTrackOffset);
                motion.followerHoldPosition = PivotOffset(holdBase, pivotTrackOffset);
            }

            // 三段秒数（仅诊断元数据，不是实际运动分段）：同一砖里扫角 / hold / 暂停共用同一个
            // "拍→秒"比例，用到达→离开的真实总时长按拍数切分；本轮旋转与输出仍是整砖一条补间。
            if (!tile.midSpin)
            {
                double totalBeats = data.angle / 180.0 + (data.isPause ? data.pauseDuration : 0.0)
                                    + (data.isHold ? data.holdDuration * 2.0 : 0.0);
                double totalSeconds = motion.departureTime - motion.arrivalTime;
                if (totalBeats > 0.0 && totalSeconds > 0.0 && IsFinite((float)totalBeats) && IsFinite((float)totalSeconds))
                {
                    double secondsPerBeat = totalSeconds / totalBeats;
                    motion.sweepSeconds = data.angle / 180.0 * secondsPerBeat;
                    motion.holdSeconds = (data.isHold ? data.holdDuration * 2.0 : 0.0) * secondsPerBeat;
                    motion.pauseSeconds = (data.isPause ? data.pauseDuration : 0.0) * secondsPerBeat;
                }
            }

            return motion;
        }

        private static PlanetMotionDraft MakeSnap(bool hasAngleOffset, float angleOffset, int eventFloor,
            string tag, PlanetOrbitRole role, bool hasPosition, Vector2 position, bool hasRotation, float rotation,
            double startTime)
        {
            return new PlanetMotionDraft
            {
                role = role,
                eventFloor = eventFloor,
                tag = tag,
                hasAngleOffset = hasAngleOffset,
                angleOffset = angleOffset,
                duration = 0f,
                startTime = startTime,
                durationSeconds = 0.0,
                hasPosition = hasPosition,
                position = position,
                hasRotation = hasRotation,
                rotation = rotation
            };
        }

        private static PlanetMotionDraft MakeTween(bool hasAngleOffset, float angleOffset, int eventFloor,
            string tag, PlanetOrbitRole role, float durationBeats, double startTime, double durationSeconds,
            bool hasRotation, float rotation, bool hasPosition, Vector2 position)
        {
            return new PlanetMotionDraft
            {
                role = role,
                eventFloor = eventFloor,
                tag = tag,
                hasAngleOffset = hasAngleOffset,
                angleOffset = angleOffset,
                duration = durationBeats,
                startTime = startTime,
                durationSeconds = durationSeconds,
                hasPosition = hasPosition,
                position = position,
                hasRotation = hasRotation,
                rotation = rotation
            };
        }

        /// <summary>与旧 BakePlanetTrackMotion 相同的时间换算：entryTime + angleOffset/(3·有效BPM)。</summary>
        private static double ResolveStartSeconds(List<scrFloor> listFloors, float levelBpm, int eventFloor, float angleOffset)
        {
            scrFloor tile = listFloors[eventFloor];
            if (tile == null)
                return double.NaN;
            float effBpm = levelBpm * tile.speed;
            if (!IsFinite(effBpm) || effBpm <= 0f)
                return double.NaN;
            return tile.entryTime + (double)angleOffset / (3.0 * (double)effBpm);
        }

        /// <summary>与旧 BakePlanetTrackMotion 相同的时间换算：duration·60/有效BPM。</summary>
        private static double ResolveDurationSeconds(List<scrFloor> listFloors, float levelBpm, int eventFloor, float durationBeats)
        {
            scrFloor tile = listFloors[eventFloor];
            if (tile == null)
                return double.NaN;
            float effBpm = levelBpm * tile.speed;
            if (!IsFinite(effBpm) || effBpm <= 0f)
                return double.NaN;
            return (double)durationBeats * 60.0 / (double)effBpm;
        }

        private static Vector2 PivotOffset(Vector2 offset, float pivotTrackOffset)
        {
            return LevelAnalysis.GetPivotOffset(offset, -pivotTrackOffset);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
