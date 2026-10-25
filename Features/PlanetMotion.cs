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
        public bool linearEase;
        public bool outSineEase;
        public bool inBackEase;
        /// <summary>仅视差事件：目标大小与视差（100 - 视差 / 视差值）。</summary>
        public float scale;
        public float parallax;
    }

    /// <summary>
    /// 一块砖的星球运动规格：到达/离开时刻、两颗星球的落点与旋转端点，以及
    /// sweep/hold/pause 的诊断秒数。它们按本砖 BPM 换算，不是实际输出的运动分段。
    /// 仍然一砖一条补间；暂停与首砖倒计时通过起点相位表达，不改变轨道几何角。
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
        public double countdownSeconds;
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
        public readonly HashSet<string> approximations = new HashSet<string>(StringComparer.Ordinal);

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
        public float firstFloorCountdownBeats;
        public MotionOutputMode outputMode = MotionOutputMode.FrameByFrame;

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
            scrConductor conductor = scrConductor.instance;
            float countdown = conductor != null ? conductor.adjustedCountdownTicks - 1f : 0f;
            settings.firstFloorCountdownBeats = PlanetRotationPlanner.NonNegative(countdown);
            return settings;
        }
    }

    /// <summary>双星球的装饰旋转相位；纯数学，不读编辑器，也不推算事件时间。</summary>
    internal static class PlanetRotationPlanner
    {
        internal static float NonNegative(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) || value < 0f ? 0f : value;
        }

        internal static void Resolve(float baseRotation, float geometricSweep, int holdTurns,
            bool isCCW, float phaseDelayBeats, out float start, out float end)
        {
            // 游戏使用顺时针弧度，Unity 的 rotationOffset 使用逆时针度，符号相反。
            // scrPlanet.HandlePause 重锚 theta_entry - extraBeats*pi；随后每帧
            // Update_RefreshAngles 仍用完整 pi/拍。出口保持几何方向，时长由真实 entryTime 决定。
            float visualDirection = isCCW ? 1f : -1f;
            float origin = float.IsNaN(baseRotation) || float.IsInfinity(baseRotation) ? 0f : baseRotation;
            start = origin - NonNegative(phaseDelayBeats) * 180f * visualDirection;
            end = origin + (NonNegative(geometricSweep) + Math.Max(0, holdTurns) * 360f) * visualDirection;
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

            if (trackDatas[from] == null || listFloors[from] == null)
                return plan;
            bool distributed = settings.distributed;
            float startBpm = trackDatas[from].bpm;
            if (!IsFinite(startBpm) || startBpm <= 0f)
                return plan;
            // 时间轴一律用游戏的双精度时刻（含倒计时偏移，但本批全部取差值，抵消常数项），
            // 不再用 TrackData 的 float arrivalTime/departureTime 相减
            double baseGameEntry = trackDatas[from].gameEntryTime;
            int trackCount = to - from + 1;

            // chosen(0) 是红，运动者是它的 next（蓝）；选区不能重新从蓝开始。
            // 双星球 prev == next，因此 midspin 同样换到另一颗，逐砖翻转。
            bool moveRedPlanet = (from & 1) != 0;

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
                if (trackDatas[nextFloor] == null)
                {
                    moveRedPlanet = !moveRedPlanet;
                    plan.approximations.Add("缺失后继砖快照，已跳过运动");
                    continue;
                }
                RecordApproximations(plan, tile, data);
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
                    baseGameEntry, moveRedPlanet, pivotTrackOffset, curFloorPos, nextFloorPos, tile,
                    settings.firstFloorCountdownBeats, floor + 1 < listFloors.Count);
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
                        true, motion.followerPosition, true, 0f, eventStart));
                    float correction = floor > 0 && data.numPlanets == 2 && motion.hasPause
                        ? ResolvePauseCorrection(tile, listFloors[nextFloor]) : 0f;
                    plan.drafts.Add(MakeSnap(hasAngleOffset, angleOffset, eventFloor, leaderTag, leaderRole,
                        true, motion.leaderPosition, true, motion.startRotation + correction, eventStart));
                    if (correction != 0f && eventSeconds > 0 && IsFinite(data.bpm) && data.bpm > 0)
                    {
                        if (motion.hasHold)
                            plan.drafts.Add(MakeTween(hasAngleOffset, angleOffset, eventFloor, leaderTag, leaderRole,
                                durationBeats, eventStart, eventSeconds, false, 0f, true, motion.leaderHoldPosition));
                        if (settings.outputMode == MotionOutputMode.Simple)
                        {
                            // Simple mode deliberately approximates the whole pause-floor dwell with one ease.
                            PlanetMotionDraft approximate = MakeTween(true, angleOffset, eventFloor, leaderTag, leaderRole,
                                durationBeats, eventStart, eventSeconds, true, motion.endRotation, false, default(Vector2));
                            approximate.inBackEase = tile.angleCorrectionType < 0f;
                            approximate.outSineEase = !approximate.inBackEase;
                            plan.drafts.Add(approximate);
                            plan.approximations.Add("简单模式暂停角度校准使用前进OutSine/后退InBack近似");
                        }
                        else AppendPauseRotation(plan, motion, eventFloor, angleOffset, leaderTag, leaderRole,
                            durationBeats, eventStart, eventSeconds, correction, 60.0 / data.bpm);
                    }
                    else
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
                    LevelEvent adjust = NewMotionEvent(draft.eventFloor);
                    if (draft.hasAngleOffset)
                        adjust["angleOffset"] = draft.angleOffset;
                    adjust.disabled["positionOffset"] = true;
                    adjust.disabled["scale"] = false;
                    adjust.disabled["parallax"] = false;
                    SetGeneratedNote(adjust, "移动星球");
                    adjust["duration"] = draft.duration;
                    adjust["tag"] = draft.tag;
                    adjust["scale"] = new Vector2(draft.scale, draft.scale);
                    adjust["parallax"] = new Vector2(draft.parallax, draft.parallax);
                    editor.events.Add(adjust);
                    continue;
                }

                if (skipMoveTags != null && skipMoveTags.Contains(draft.tag))
                    continue;

                LevelEvent levelEvent = NewMotionEvent(draft.eventFloor);
                if (draft.hasAngleOffset)
                    levelEvent["angleOffset"] = draft.angleOffset;
                levelEvent.disabled["positionOffset"] = !draft.hasPosition;
                levelEvent.disabled["rotationOffset"] = !draft.hasRotation;
                SetGeneratedNote(levelEvent, "移动星球");
                levelEvent["tag"] = draft.tag;
                levelEvent["duration"] = draft.duration;
                if (draft.linearEase) levelEvent["ease"] = DG.Tweening.Ease.Linear;
                if (draft.outSineEase) levelEvent["ease"] = DG.Tweening.Ease.OutSine;
                if (draft.inBackEase) levelEvent["ease"] = DG.Tweening.Ease.InBack;
                if (draft.hasPosition)
                    levelEvent["positionOffset"] = draft.position;
                if (draft.hasRotation)
                    levelEvent["rotationOffset"] = draft.rotation;
                editor.events.Add(levelEvent);
            }
        }

        internal static void SetGeneratedNote(LevelEvent levelEvent, string note)
        {
            // AEE 为事件注册 aeeNote；未安装/未注册时不写未知键，避免原生面板与保存错误。
            // 只对本次新建事件调用，不改用户的源事件备注或触发标签。
            if (levelEvent?.info?.propertiesInfo != null && levelEvent.info.propertiesInfo.ContainsKey("aeeNote"))
                levelEvent["aeeNote"] = note;
        }

        private static LevelEvent NewMotionEvent(int floor)
        {
            LevelEvent levelEvent = new LevelEvent(floor, LevelEventType.MoveDecorations);
            // 星球基础事件只修改本 draft 声明的属性，不能依赖游戏版本的可禁用属性默认值。
            foreach (KeyValuePair<string, PropertyInfo> property in levelEvent.info.propertiesInfo)
            {
                if (property.Value.canBeDisabled)
                    levelEvent.disabled[property.Key] = true;
            }
            return levelEvent;
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
            scrFloor tile, float firstFloorCountdownBeats, bool hasNextFloor)
        {
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
                hasPause = hasNextFloor && !tile.midSpin && !tile.freeroam && PlanetRotationPlanner.NonNegative(data.extraBeats) > 0f,
                pauseBeats = hasNextFloor && !tile.midSpin && !tile.freeroam ? PlanetRotationPlanner.NonNegative(data.extraBeats) : 0f
            };

            // 首砖 angleLength 可能混入播放历史；倒计时从 settings 快照独立取得。
            // 几何角仍供假轨道使用，不注入倒计时。暂停只后移本砖起点相位，出口不漂移。
            float phaseDelay = floor == 0 ? PlanetRotationPlanner.NonNegative(firstFloorCountdownBeats) : motion.pauseBeats;
            PlanetRotationPlanner.Resolve(data.tail - 180f, data.geometricAngle,
                data.isHold ? data.holdDuration : 0, tile.isCCW, phaseDelay,
                out motion.startRotation, out motion.endRotation);

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

            // 仅诊断：按本砖真实有效 BPM 换算，不把含 extraBeats/倒计时的总秒数重新比例摊回旧公式。
            if (!tile.midSpin && IsFinite(data.bpm) && data.bpm > 0f)
            {
                double secondsPerBeat = 60.0 / data.bpm;
                motion.sweepSeconds = PlanetRotationPlanner.NonNegative(data.angle) / 180.0 * secondsPerBeat;
                motion.holdSeconds = (data.isHold ? Math.Max(0, data.holdDuration) * 2.0 : 0.0) * secondsPerBeat;
                motion.pauseSeconds = motion.pauseBeats * secondsPerBeat;
                motion.countdownSeconds = floor == 0 ? phaseDelay * secondsPerBeat : 0.0;
            }

            return motion;
        }

        // Ideal, on-time two-planet path. Use the game's raw modulo difference, NOT a shortest arc.
        internal static float ResolvePauseCorrection(scrFloor tile, scrFloor next)
        {
            if (tile == null || next == null || !IsFinite(tile.angleCorrectionType)) return 0f;
            double direction = tile.isCCW ? -1.0 : 1.0;
            double before = Mod(tile.entryangle);
            double anchor = next.entryangle - Math.PI * direction
                - Math.PI * (double)(tile.extraBeats + 1f) * direction
                + (Math.PI - tile.angleLength) * direction;
            double correction = tile.angleCorrectionType * Math.Abs(before - Mod(anchor)) * direction * 180.0 / Math.PI;
            return double.IsNaN(correction) || double.IsInfinity(correction) ? 0f : (float)correction;
        }

        private static double Mod(double angle)
        {
            double circle = Math.PI * 2.0;
            return (angle % circle + circle) % circle;
        }

        private static void AppendPauseRotation(PlanetMotionPlan plan, PlanetFloorMotion motion,
            int eventFloor, float angleOffset, string tag, PlanetOrbitRole role, float beats,
            double start, double seconds, float correction, double correctionSeconds)
        {
            // Linear interpolation error <= max|f''| h²/8. OutSine bias is added to linear orbit.
            // Bound the angular error by 0.005 degrees; the next landing clears a truncated bias.
            double active = Math.Min(seconds, correctionSeconds);
            int required = Math.Max(1, (int)Math.Ceiling(active / correctionSeconds
                * Math.Sqrt(Math.Abs(correction) * Math.PI * Math.PI / (8.0 * 0.005))));
            int count = Math.Min(2048, required);
            if (required > count) plan.approximations.Add("暂停校准采样达到安全上限");
            for (int i = 1; i <= count; i++)
            {
                double left = active * (i - 1) / count, right = active * i / count;
                Add(left, right, Rotation(right));
            }
            if (active < seconds) Add(active, seconds, motion.endRotation);

            float Rotation(double time)
            {
                return (float)(motion.startRotation + (motion.endRotation - motion.startRotation) * time / seconds
                    + correction * (1.0 - Math.Sin(Math.PI * 0.5 * Math.Min(1.0, time / correctionSeconds))));
            }
            void Add(double left, double right, float rotation)
            {
                PlanetMotionDraft draft = MakeTween(true, angleOffset + (float)(left * beats / seconds * 180.0),
                    eventFloor, tag, role, (float)((right - left) * beats / seconds), start + left, right - left,
                    true, rotation, false, default(Vector2));
                draft.linearEase = true;
                plan.drafts.Add(draft);
            }
        }

        private static void RecordApproximations(PlanetMotionPlan plan, scrFloor tile, TrackData data)
        {
            if (data.numPlanets != 2) plan.approximations.Add("多星球仍使用蓝红双星球示意");
            if (Math.Abs(data.radiusScale - 100f) > 0.001f) plan.approximations.Add("非默认半径的公转布局为近似");
            if (tile.freeroam) plan.approximations.Add("自由漫游运行期轨迹不完全复刻");
            if (tile.planetEase != DG.Tweening.Ease.Linear) plan.approximations.Add("星球非线性缓动为线性近似");
            if (!tile.midSpin && !tile.freeroam && PlanetRotationPlanner.NonNegative(data.extraBeats) > 0f
                && tile.angleCorrectionType != 0f && (data.numPlanets != 2 || tile.seqID == 0))
                plan.approximations.Add("暂停的视觉角度修正补间未复刻");
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
