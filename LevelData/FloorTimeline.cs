using System;
using System.Collections.Generic;
using UnityEngine;

namespace MultiTrackHelper
{
    /// <summary>
    /// 角度归一化、有向角差、中点方向、轴心旋转的统一实现。
    /// 三种归一化顺序都保留旧写法，只是收进一个入口：调用方只需说明"要不要把 0 当成一整圈"、
    /// "要不要先补一整圈再取模"，不再各处手抄 % 与补 360 的组合。
    /// 结果与旧实现逐位一致（同表达式、同运算顺序），因此可以用它替换原来的散装实现。
    /// </summary>
    internal static class AngleMath
    {
        /// <summary>
        /// 通用归一化：先加 <paramref name="shift"/> 再对 360 取模。
        /// shift = 0 是"先取模、再把负数补成 360 的补角"；shift = 360 是"先补一整圈再取模"（旧 GetMidDirection 的顺序）。
        /// <paramref name="zeroCountsAsFullTurn"/> 为 true 时结果落在 (0,360]（0/360/720 都得到 360），否则落在 [0,360)。
        /// </summary>
        internal static float Normalize360(float degrees, float shift, bool zeroCountsAsFullTurn)
        {
            float wrapped = (degrees + shift) % 360f;
            if (zeroCountsAsFullTurn)
            {
                if (wrapped <= 0f) wrapped += 360f;
            }
            else if (wrapped < 0f)
            {
                wrapped += 360f;
            }
            return wrapped;
        }

        /// <summary>方向值：落在 [0,360)，360 的整数倍给 0。</summary>
        internal static float Direction(float degrees)
        {
            return Normalize360(degrees, 0f, false);
        }

        /// <summary>扫角值：落在 (0,360]，0 与 360 的整数倍给 360。</summary>
        internal static float Sweep(float degrees)
        {
            return Normalize360(degrees, 0f, true);
        }

        /// <summary>
        /// 先把角度收进 [0,360) 的标准区间；对任意量级都先取模再补圈，与旧参数归一化顺序一致。
        /// </summary>
        internal static float NormalizeSigned(float degrees)
        {
            return (degrees % 360f + 360f) % 360f;
        }

        /// <summary>
        /// 从 <paramref name="from"/> 转到 <paramref name="to"/> 的有向角度差（度），落在 [0,360)。
        /// 与旧写法一致：先补一整圈再取模（两个入参都在 [0,360) 时两者逐位相同）。
        /// </summary>
        internal static float Delta(float from, float to, bool isCCW)
        {
            return Normalize360(isCCW ? to - from : from - to, 360f, false);
        }

        /// <summary>两方向的中点方向；两侧相等且 <paramref name="goFullCircleIfEqual"/> 时按一整圈折半。</summary>
        internal static float MidDirection(float startDirection, float endDirection, bool isCCW, bool goFullCircleIfEqual)
        {
            startDirection = NormalizeSigned(startDirection);
            endDirection = NormalizeSigned(endDirection);

            float delta = Delta(startDirection, endDirection, isCCW);
            if (delta == 0f && goFullCircleIfEqual)
            {
                delta = 360f;
            }

            // 逆时针取正向一半，顺时针取反向一半；顺时针那侧先补一整圈再取模，避免负数
            return isCCW
                ? Normalize360(startDirection + delta / 2f, 0f, false)
                : Normalize360(startDirection - delta / 2f, 360f, false);
        }

        /// <summary>
        /// 把偏移按 -<paramref name="rotation"/> 旋转（游戏里轴心偏移用的方向）。
        /// 表达式与旧实现一致。
        /// </summary>
        internal static Vector2 PivotOffset(Vector2 offset, float rotation)
        {
            float radians = rotation * Mathf.Deg2Rad;
            float cosine = Mathf.Cos(radians);
            float sine = Mathf.Sin(radians);

            return new Vector2(cosine * offset.x + sine * offset.y, -sine * offset.x + cosine * offset.y);
        }
    }

    /// <summary>
    /// 逐砖时间线：直接读游戏已经算好的 scrFloor 字段（entryTime / angleLength / speed /
    /// holdLength / extraBeats / numPlanets / entryangle / exitangle），不再用 BPM 公式自己估。
    ///
    /// 只读：不调用任何会改游戏状态的方法，也不回写 scrFloor。
    /// 时间口径见 <see cref="Entry.arrivalTime"/>：与 scrFloor.entryTime 使用同一条原始时间轴，
    /// 所以"相邻两砖的到达时刻之差"就是游戏里行星走这一步的时间。
    /// </summary>
    internal static class FloorTimeline
    {
        /// <summary>midspin 砖的角度哨兵，沿用旧契约（调用方靠 &gt;= 900 识别）。</summary>
        internal const float MidSpinAngle = 999f;

        /// <summary>扫角只在这个容差内做边界归整（浮点尾巴收回 0 或 360），不对任意角度取舍。</summary>
        private const float BoundaryTolerance = (float)(1e-6 * 180.0 / Math.PI);

        /// <summary>
        /// 诊断开关，默认关闭。打开后只做新旧双算并写 MTTiming: 日志，不参与结果、不改游戏状态。
        /// 除了代码里直接置 true，也可以在启动游戏前设环境变量 <see cref="DiagnosticsEnvironmentVariable"/>。
        /// </summary>
        internal static bool EnableDiagnostics = ReadDiagnosticsSwitch();

        /// <summary>打开诊断日志的环境变量名（值为空表示关闭）。</summary>
        internal const string DiagnosticsEnvironmentVariable = "MT_TIMING_DIAG";

        private static bool ReadDiagnosticsSwitch()
        {
            try
            {
                return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DiagnosticsEnvironmentVariable));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>最近一次 <see cref="Build"/> 读到的 scrFloor 时间数据疑似未刷新或异常。</summary>
        internal static bool LastTimelineSuspectedStale { get; private set; }

        private static bool loggedStaleHint;

        /// <summary>单块砖的时间线数据。</summary>
        internal struct Entry
        {
            /// <summary>纯扫角（度）：已扣掉 hold 贡献，midspin 为 <see cref="MidSpinAngle"/>。</summary>
            public float sweepAngle;

            /// <summary>几何扫角（度）：按 head/tail 的夹角算，不带多星球修正，midspin 为 <see cref="MidSpinAngle"/>。</summary>
            public float geometricAngle;

            /// <summary>出砖方向（度）。</summary>
            public float head;

            /// <summary>入砖方向（度）。</summary>
            public float tail;

            public bool midSpin;
            public float speed;
            public int numPlanets;

            /// <summary>该砖的额外拍（pause / FreeRoam 等，来自 scrFloor.extraBeats）。</summary>
            public float extraBeats;

            /// <summary>到达时刻（秒）：游戏原始 entryTime 的 float 兼容值。</summary>
            public float arrivalTime;

            /// <summary>离开时刻（秒）：下一砖到达时刻（含 hold、extraBeats 的停留）；末砖按本砖推算。</summary>
            public float departureTime;

            /// <summary>游戏原始到达时刻（scrFloor.entryTime，含开局倒计时）。</summary>
            public double gameEntryTime;

            /// <summary>游戏原始离开时刻（下一砖 entryTime，末砖为本砖推算，均含倒计时）。</summary>
            public double gameExitTime;
        }

        /// <summary>
        /// 读 <paramref name="floorCount"/> + 1 块砖的时间线。floorCount 会被收缩到实际地砖范围内。
        /// </summary>
        internal static Entry[] Build(int floorCount)
        {
            scrLevelMaker maker = scrLevelMaker.instance;
            List<scrFloor> floors = maker == null ? null : maker.listFloors;
            if (floors == null || floors.Count == 0)
            {
                LastTimelineSuspectedStale = false;
                return new Entry[0];
            }

            if (floorCount > floors.Count - 1) floorCount = floors.Count - 1;
            if (floorCount < 0) floorCount = 0;

            Entry[] entries = new Entry[floorCount + 1];
            double countdown = CountdownSeconds();
            float baseBpm = BaseBpm();
            float previousHead = 180f;
            bool sawZeroAngle = false;

            for (int i = 0; i <= floorCount; i++)
            {
                scrFloor floor = floors[i];
                Entry entry = new Entry();
                entry.speed = floor.speed > 0f ? floor.speed : 1f;
                entry.midSpin = floor.midSpin;
                entry.numPlanets = Mathf.Max(floor.numPlanets, 2);
                entry.extraBeats = floor.extraBeats;
                entry.sweepAngle = ReadSweepAngle(floor);
                if (!floor.midSpin && floor.angleLength <= 1e-6)
                {
                    // 非 midspin 砖的扫角不可能为 0：说明 angleLength 还没被 ApplyEventsToFloors 算过
                    sawZeroAngle = true;
                }

                // 游戏用 entryangle / exitangle（弧度）表示方向：exit=(-angleData+90)°，
                // 因此 head = 90 - exit、tail = 90 - entry，与旧实现的 head/tail 值相同。
                float exitDegrees = 90f - RadiansToDegrees(floor.exitangle);
                entry.tail = AngleMath.Direction(90f - RadiansToDegrees(floor.entryangle));
                if (floor.midSpin)
                {
                    // midspin 砖的 exitangle 等于 entryangle，直接换算会把 head 翻 180 度；
                    // 旧实现沿用上一块非 midspin 砖的 head，假轨道朝向依赖它，这里保持一致。
                    entry.head = previousHead;
                }
                else
                {
                    entry.head = AngleMath.Direction(exitDegrees);
                    previousHead = entry.head;
                }

                // 几何扫角：旧实现就是 head/tail 的夹角（0 记 360），这里照算一份，
                // 供"画轨道 / 装饰旋转"这类要的是视觉角度、而不是每拍角度的调用方使用。
                if (floor.midSpin)
                {
                    entry.geometricAngle = MidSpinAngle;
                }
                else
                {
                    double turn = (floor.isCCW ? floor.entryangle - floor.exitangle : floor.exitangle - floor.entryangle) % (2.0 * Math.PI);
                    if (turn < 0.0) turn += 2.0 * Math.PI;
                    float geometric = (float)(turn * (180.0 / Math.PI));
                    // U 型砖（进出方向相同）在浮点上会算成 0 或 359.99999，游戏自己也是把 <=1e-6 与 >=2π
                    // 一律当整圈处理，这里只在这两个边界上归整，不动中间的角度。
                    if (geometric < BoundaryTolerance || geometric > 360f - BoundaryTolerance)
                    {
                        geometric = 360f;
                    }
                    entry.geometricAngle = geometric;
                }

                // 首砖 angleLength 可能含播放留下的倒计时；首步没有多星球修正，直接用方向差。
                if (i == 0 && !floor.midSpin) entry.sweepAngle = entry.geometricAngle;
                if (floors.Count == 1 && !floor.midSpin)
                {
                    entry.head = 180f;
                    entry.tail = 180f;
                    entry.geometricAngle = 180f;
                    entry.sweepAngle = 180f;
                }

                // 非 midspin 砖的扫角不可能为 0；若 angleLength 还没算过（上面已标记疑似未刷新），
                // 退回几何角——这正是改动前的取值，比拿 0 去生成安全。
                if (!floor.midSpin && entry.sweepAngle <= 0f)
                {
                    entry.sweepAngle = entry.geometricAngle;
                }

                entry.gameEntryTime = floor.entryTime;
                entry.arrivalTime = (float)floor.entryTime;
                entries[i] = entry;
            }

            // 离开时刻：下一砖的到达时刻就是本砖的离开时刻（含停留），末砖自己推算
            for (int i = 0; i <= floorCount; i++)
            {
                if (i < floorCount)
                {
                    entries[i].departureTime = entries[i + 1].arrivalTime;
                    entries[i].gameExitTime = entries[i + 1].gameEntryTime;
                }
                else
                {
                    float step = i == 0 && baseBpm > 0f
                        ? (entries[i].midSpin ? 0f : entries[i].sweepAngle / 180f) * 60f / (baseBpm * entries[i].speed)
                        : StepSeconds(floors[i], baseBpm);
                    entries[i].departureTime = entries[i].arrivalTime + step;
                    entries[i].gameExitTime = entries[i].gameEntryTime + step;
                }
            }

            LastTimelineSuspectedStale = sawZeroAngle || DetectSuspicious(entries, floorCount);
            if (LastTimelineSuspectedStale && EnableDiagnostics)
            {
                HintOnce(entries, floorCount);
            }

            if (EnableDiagnostics)
            {
                Main.Logger?.Log($"MTTiming: 时间线来自 scrFloor（砖数={floorCount + 1}，倒计时参考={countdown:0.####}s（未扣减），baseBpm={baseBpm:0.###}，疑似未刷新={LastTimelineSuspectedStale}）");
            }

            return entries;
        }

        /// <summary>
        /// 诊断用：把新时间线与旧公式的累计值并列写日志，只在 <see cref="EnableDiagnostics"/> 打开时动作。
        /// 旧公式一侧复用同一扫角，因此差异只反映时间模型本身（speed、多星球、倒计时、extraBeats）。
        /// </summary>
        internal static void LogComparison(int floor, Entry entry, float legacyArrival, float legacyDeparture)
        {
            if (!EnableDiagnostics)
            {
                return;
            }

            float arrivalDelta = entry.arrivalTime - legacyArrival;
            float departureDelta = entry.departureTime - legacyDeparture;
            if (Mathf.Abs(arrivalDelta) < 1e-3f && Mathf.Abs(departureDelta) < 1e-3f)
            {
                return;
            }

            Main.Logger?.Log($"MTTiming: tile={floor} speed={entry.speed:0.###} planets={entry.numPlanets} extraBeats={entry.extraBeats:0.###} sweep={entry.sweepAngle:0.###} "
                + $"arrival={entry.arrivalTime:0.####}(旧={legacyArrival:0.####}, Δ={arrivalDelta:+0.####;-0.####}) "
                + $"departure={entry.departureTime:0.####}(旧={legacyDeparture:0.####}, Δ={departureDelta:+0.####;-0.####})");
        }

        /// <summary>本砖走完所需的秒数（扫角 + hold + 额外拍），只在推算末砖离开时刻时用。</summary>
        internal static float StepSeconds(scrFloor floor, float baseBpm)
        {
            if (floor == null || baseBpm <= 0f)
            {
                return 0f;
            }

            double speed = floor.speed > 0f ? floor.speed : 1f;
            double crotchet = 60.0 / (baseBpm * speed);
            // angleLength 已经含 hold 的贡献（holdLength*2 拍），extraBeats 在外层另算
            double angle = floor.angleLength > 0.0 ? floor.angleLength : 0.0;
            return (float)(angle / Math.PI * crotchet + floor.extraBeats * crotchet);
        }

        /// <summary>扫角（度）：angleLength 是弧度且把 hold 的 2 拍/格也算进去了，这里减掉。</summary>
        private static float ReadSweepAngle(scrFloor floor)
        {
            if (floor.midSpin)
            {
                return MidSpinAngle;
            }

            double length = floor.angleLength;
            int hold = floor.holdLength;
            if (hold > 0)
            {
                // 游戏加法是 num2 += (double)((float)(holdLength * 2) * MathF.PI)（float 里的 π 乘法），
                // 这里用同一表达式扣回去，否则长 hold 会残留一点，把 360 变成 359.99x。
                length -= (double)((float)(hold * 2) * (float)Math.PI);
            }
            if (length < 0.0)
            {
                length = 0.0;
            }

            float degrees = (float)(length * (180.0 / Math.PI));
            // 只做边界容差：超过 360 的（浮点尾巴）收回 360，接近 0 的收回 0（真正的 0 由几何角兜底）
            if (degrees > 360f - BoundaryTolerance)
            {
                degrees = 360f;
            }
            else if (degrees < BoundaryTolerance)
            {
                degrees = 0f;
            }
            return degrees;
        }

        private static float RadiansToDegrees(double radians)
        {
            return (float)(radians * (180.0 / Math.PI));
        }

        /// <summary>开局倒计时占用的秒数；游戏算 entryTime 时就是 crotchetAtStart * (adjustedCountdownTicks - 1)。</summary>
        private static double CountdownSeconds()
        {
            scrConductor conductor = scrConductor.instance;
            if (conductor == null || conductor.crotchetAtStart <= 0.0)
            {
                return 0.0;
            }
            return conductor.crotchetAtStart * (conductor.adjustedCountdownTicks - 1.0);
        }

        private static float BaseBpm()
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.levelData == null)
            {
                return 0f;
            }
            return editor.levelData.bpm > 0f ? editor.levelData.bpm : 0f;
        }

        /// <summary>
        /// 只判断"数据看起来不对"，**不重算**。依据是结构的硬性约束：时刻必须是有限、逐砖不减；
        /// 另外未刷新的砖 entryTime 会被 scrFloor.Reset 归零，所以末砖为 0 时按"疑似未刷新"提示。
        /// 注意：全 midspin、零步长这类关卡末砖合法地可能是 0，所以这里只报疑似，不下结论、不改数据。
        /// </summary>
        private static bool DetectSuspicious(Entry[] entries, int floorCount)
        {
            double previous = double.NegativeInfinity;
            for (int i = 0; i <= floorCount; i++)
            {
                double time = entries[i].gameEntryTime;
                if (double.IsNaN(time) || double.IsInfinity(time))
                {
                    return true;
                }
                if (time < previous)
                {
                    return true;
                }
                previous = time;
            }

            return floorCount >= 1 && entries[floorCount].gameEntryTime <= 0.0;
        }

        private static void HintOnce(Entry[] entries, int floorCount)
        {
            if (loggedStaleHint)
            {
                return;
            }
            loggedStaleHint = true;

            string reason;
            if (double.IsNaN(entries[floorCount].gameEntryTime) || double.IsInfinity(entries[floorCount].gameEntryTime))
            {
                reason = $"末砖 entryTime 非有限值（{entries[floorCount].gameEntryTime}）";
            }
            else if (entries[floorCount].gameEntryTime <= 0.0)
            {
                reason = $"末砖 entryTime={entries[floorCount].gameEntryTime:0.####}";
            }
            else if (double.IsNaN(entries[floorCount].sweepAngle))
            {
                reason = "扫角非有限值";
            }
            else
            {
                reason = "逐砖时刻出现倒退或非 midspin 砖扫角为 0";
            }
            Main.Logger?.Log($"MTTiming: scrFloor 的时间数据疑似未刷新（{reason}），本次生成的时刻可能偏差；"
                + "如确认异常，请先让编辑器执行一次 ApplyEventsToFloors 再生成。");
        }
    }
}
