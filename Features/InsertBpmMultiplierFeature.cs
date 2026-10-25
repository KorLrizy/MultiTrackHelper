using ADOFAI;
using MultiTrackHelper.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace MultiTrackHelper.Features
{
    /// <summary>
    /// “插入 BPM 倍频”功能。
    ///
    /// 读取选中轨道区间内已有的 SetSpeed(×倍率) 事件并同步到面板；面板上的每一行代表
    /// 一个倍频器，位置可在“度 / 砖块”两种表示间互解，数值可在“bpm / 倍”两种表示间互解。
    /// “目标总角度”是用户给定的目标参考：当且仅当有一行的数值留空(0)时，用它反解该行
    /// 应有的倍率，使整段轨道的等效总角度等于目标值。
    /// 点击创建时按行写入/新建 SetSpeed 事件。
    /// </summary>
    internal static class InsertBpmMultiplierFeature
    {
        /// <summary>选中轨道上允许的倍频器数量上限，超过则不自动同步面板。</summary>
        public const int MaxMultipliers = 25;

        /// <summary>当前面板显示的行数（不含隐藏的空行）。</summary>
        public static int RowCount { get; private set; } = 1;

        /// <summary>面板中通过“添加倍频器”新增、尚未被删除的行数（删除按钮只删这些行）。</summary>
        public static int AddedRows { get; private set; }

        /// <summary>是否已完成一次“计算”。为 true 时主按钮显示“创建”，否则显示“计算”。</summary>
        public static bool Computed { get; private set; }

        public static void ResetComputed() => Computed = false;

        /// <summary>关卡（重新）加载时清空全部静态状态，避免上一关的区间/行数据泄漏到新关卡。</summary>
        public static void Reset()
        {
            hasRange = false;
            rangeFrom = 0;
            rangeTo = 0;
            floorAngles = null;
            bpmAfter = null;
            cumulative = null;
            baseBpm = 100f;
            RowCount = 1;
            AddedRows = 0;
            Computed = false;
            pendingSyncFrame = -1;
            pendingEvent = null;
            solveFailure = null;
        }

        public static void ResetForLevel()
        {
            Reset();

            LevelEvent panel = Main.MultiTrackLevelEvent;
            LevelEvent saved = null;
            // Main.MultiTrack 在 mod 初始化的极早期可能还没建好，此时跳过按类型的字典访问
            if (Main.MultiTrack != null)
                MultiTrackHelper.Patches.InspectorPanelShowPanelPatch.saves.TryGetValue((LevelEventType)Main.MultiTrack.type, out saved);

            ClearRows(panel, true);
            if (!ReferenceEquals(saved, panel))
                ClearRows(saved, true);

            Main.MultiTrackLevelEvent = null;
        }

        private static bool ClearRows(LevelEvent levelEvent, bool clearTargets = false)
        {
            if (levelEvent == null)
                return false;

            bool changed = false;
            for (int i = 1; i <= MaxMultipliers; i++)
            {
                changed |= Assign(levelEvent, PosDegName(i), 0f);
                changed |= Assign(levelEvent, PosTileName(i), 0);
                changed |= Assign(levelEvent, ValueBpmName(i), 0f);
                changed |= Assign(levelEvent, ValueMultName(i), 0f);
                changed |= Assign(levelEvent, LockName(i), false);
            }

            if (clearTargets)
            {
                changed |= Assign(levelEvent, "targetAngleDeg", 0f);
                changed |= Assign(levelEvent, "targetAngleBeats", 0f);
            }
            return changed;
        }

        /// <summary>当前固定下来的区间，形如 "6..20"（用于在面板/日志里显示）。</summary>
        public static string RangeText => hasRange ? rangeFrom + ".." + rangeTo : "-";

        private static int rangeFrom;
        private static int rangeTo;

        // 是否已经确定过区间。捕获后固定下来，之后不再随编辑器选中变化而改变，
        // 避免"添加/编辑某一行时点了一下砖块导致区间缩小、其它行被判为越界"。
        private static bool hasRange;

        // 只读的轻量缓存：每砖扫过角度、每砖之后的生效 BPM。
        // 不做 TrackData[] 那种“每砖一个对象 + 嵌套事件字典”的重建，避免选中砖块时卡顿。
        private static float[] floorAngles;
        private static float[] bpmAfter;
        private static float baseBpm = 100f;

        // cumulative[f] = 从区间第一块砖到第 f 块砖“进入时”的累计几何角度；cumulative[to + 1] 为区间总角度
        private static float[] cumulative;

        private static int pendingSyncFrame = -1;
        private static LevelEvent pendingEvent;

        // 上一次求解失败的具体原因（含数值），失败时随弹窗一起显示，便于直接定位
        private static string solveFailure;

        public static string PosDegName(int index) => "bpPosDeg" + index;
        public static string PosTileName(int index) => "bpPosTile" + index;
        public static string ValueBpmName(int index) => "bpValueBpm" + index;
        public static string ValueMultName(int index) => "bpValueMult" + index;
        public static string LockName(int index) => "bpLock" + index;

        /// <summary>按当前行数生成需要显示的属性名列表（供 Main.activeChilden 使用）。</summary>
        public static List<string> BuildActiveProperties()
        {
            var list = new List<string> { "TrackFeatures", "realtimeCapture", "targetAngleDeg", "targetAngleBeats" };
            for (int i = 1; i <= RowCount; i++)
            {
                list.Add(PosDegName(i));
                list.Add(PosTileName(i));
                list.Add(ValueBpmName(i));
                list.Add(ValueMultName(i));
                list.Add(LockName(i));
            }
            list.Add("deleteMultiplierButton");
            list.Add("addMultiplierButton");
            list.Add(Computed ? "createButton" : "calculateButton");
            return list;
        }

        /// <summary>“计算”按钮：按目标总角度求解未锁定的行；成功后主按钮切换为“创建”。</summary>
        public static void Compute()
        {
            LevelEvent levelEvent = Main.MultiTrackLevelEvent;
            if (levelEvent == null)
                return;
            if (!RefreshTrackData(false))
            {
                Popup.ShowMessage(Message("mth.needSelectTrack"));
                return;
            }
            if (ToFloat(levelEvent["targetAngleDeg"]) <= 0f)
            {
                Popup.ShowMessage(Message("mth.needTarget"));
                return;
            }

            // 区间可能刚被扩展过，重算各行的"度"显示，保持一致
            ResyncRowDegrees(levelEvent);
            LogSolverState(levelEvent, "compute");

            if (!AutoSolve(levelEvent))
            {
                string reason = string.IsNullOrEmpty(solveFailure) ? "数值无效或无法应用解" : solveFailure;
                Popup.ShowMessage($"{Message("mth.nothingToCompute")}\n{reason}\n{DescribeRows(levelEvent)}\n[{RangeText}]");
                return;
            }

            LogSolverState(levelEvent, "after-compute");
            Computed = true;
            levelEvent.UpdatePanel();
            Main.activeChilden();
        }

        /// <summary>无条件把面板刷新一遍（用于状态变化后强制同步按钮/可见性）。</summary>
        private static void RefreshPanel(LevelEvent levelEvent)
        {
            if (levelEvent == null)
                return;
            levelEvent.UpdatePanel();
            Main.activeChilden();
        }

        /// <summary>把各行的位置/数值/锁定状态拼成一行文本，失败弹窗里显示，便于直接定位。</summary>
        private static string DescribeRows(LevelEvent levelEvent)
        {
            var builder = new StringBuilder();
            for (int i = 1; i <= RowCount; i++)
            {
                int tile = ToInt(levelEvent[PosTileName(i)]);
                if (tile < rangeFrom || tile > rangeTo)
                    continue;
                builder.Append("t").Append(tile)
                    .Append("(x").Append(ToFloat(levelEvent[ValueMultName(i)]).ToString("0.####"))
                    .Append(ToBool(levelEvent[LockName(i)]) ? ",锁" : ",未锁")
                    .Append(tile == rangeFrom ? ",基准" : "")
                    .Append(") ");
            }
            return builder.Length > 0 ? builder.ToString() : "（无有效行）";
        }

        private static void ResyncRowDegrees(LevelEvent levelEvent)
        {
            for (int i = 1; i <= RowCount; i++)
            {
                int tile = ToInt(levelEvent[PosTileName(i)]);
                if (tile < rangeFrom || tile > rangeTo)
                    continue;
                Assign(levelEvent, PosDegName(i), TileToDegrees(tile));
            }
        }

        /// <summary>把求解用到的关键数值写进 UMM 日志，便于对照游戏内显示的总角度排查。</summary>
        private static void LogSolverState(LevelEvent levelEvent, string tag)
        {
            if (Main.Logger == null || cumulative == null)
                return;

            var builder = new StringBuilder();
            builder.Append("InsertBpm[").Append(tag).Append("] target=")
                .Append(ToFloat(levelEvent["targetAngleDeg"]))
                .Append(" range=").Append(rangeFrom).Append("..").Append(rangeTo)
                .Append(" totalGeom=").Append(TotalAngle.ToString("0.###"))
                .Append(" rows=");

            var listed = new List<SolveRow>();
            for (int i = 1; i <= RowCount; i++)
            {
                int tile = ToInt(levelEvent[PosTileName(i)]);
                if (tile < rangeFrom || tile > rangeTo)
                    continue;
                listed.Add(new SolveRow
                {
                    tile = tile,
                    value = ToFloat(levelEvent[ValueMultName(i)]),
                    locked = ToBool(levelEvent[LockName(i)]),
                    index = i,
                });
            }
            listed.Sort((a, b) => a.tile.CompareTo(b.tile));

            double total = 0d, product = 1d;
            for (int k = 0; k < listed.Count; k++)
            {
                SolveRow row = listed[k];
                double previous = k == 0 ? 0d : cumulative[listed[k - 1].tile];
                total += (cumulative[row.tile] - previous) / product;

                builder.Append("{t=").Append(row.tile)
                    .Append(",d=").Append(TileToDegrees(row.tile).ToString("0.###"))
                    .Append(",x=").Append(row.value.ToString("0.#####"))
                    .Append(row.locked ? ",L" : "")
                    .Append(row.tile == rangeFrom ? ",基准" : "")
                    .Append("} ");

                double m = row.tile == rangeFrom ? 1d : row.value;
                if (m <= 0d) m = 1d;
                product *= m;
            }
            if (listed.Count > 0)
                total += (TotalAngle - cumulative[listed[listed.Count - 1].tile]) / product;

            builder.Append(" => total=").Append(total.ToString("0.####"))
                .Append("(").Append((total / 180d).ToString("0.####")).Append("拍)");
            Main.Logger.Log(builder.ToString());
        }

        /// <summary>
        /// 若把所有有效行都锁定就没有可求解的行了，此时自动解开“最后面”那一行的锁定。
        /// 优先解开非基准行（区间第一块砖上的行不参与求解，解开它也没用）。
        /// </summary>
        private static void EnsureNotAllLocked(LevelEvent levelEvent)
        {
            int validCount = 0, lockedCount = 0, lastValid = -1, lastComputable = -1;
            for (int i = 1; i <= RowCount; i++)
            {
                int tile = ToInt(levelEvent[PosTileName(i)]);
                if (tile < rangeFrom || tile > rangeTo)
                    continue;
                validCount++;
                lastValid = i;
                if (tile != rangeFrom)
                    lastComputable = i;
                if (ToBool(levelEvent[LockName(i)]))
                    lockedCount++;
            }
            if (validCount > 0 && lockedCount == validCount)
            {
                int row = lastComputable > 0 ? lastComputable : lastValid;
                if (row > 0)
                    levelEvent[LockName(row)] = false;
            }
        }

        public static void AddMultiplier()
        {
            if (RowCount >= MaxMultipliers)
            {
                Popup.ShowMessage(Message("mth.tooManyRows"));
                return;
            }

            RowCount++;
            AddedRows++;
            Computed = false;

            // 新行默认落在区间末尾，避免默认 tile=0 在区间起点为 0 时变成"基准行"（不参与计算、输入无反馈）
            LevelEvent levelEvent = Main.MultiTrackLevelEvent;
            if (levelEvent != null && hasRange)
            {
                int row = RowCount;
                levelEvent[PosTileName(row)] = rangeTo;
                levelEvent[PosDegName(row)] = TileToDegrees(rangeTo);
                levelEvent[ValueBpmName(row)] = 0f;
                levelEvent[ValueMultName(row)] = 0f;
                levelEvent[LockName(row)] = false;
                levelEvent.UpdatePanel();
            }
            Main.activeChilden();
        }

        /// <summary>删除面板中最新添加的倍频器行（只删“添加倍频器”加出来的行，不动捕获自轨道的行）。</summary>
        public static void DeleteMultiplier()
        {
            LevelEvent levelEvent = Main.MultiTrackLevelEvent;
            if (levelEvent == null || AddedRows <= 0)
            {
                Popup.ShowMessage(Message("mth.nothingToDelete"));
                return;
            }

            int row = RowCount;
            RowCount--;
            AddedRows--;
            Computed = false;

            levelEvent[PosDegName(row)] = 0f;
            levelEvent[PosTileName(row)] = 0;
            levelEvent[ValueBpmName(row)] = 0f;
            levelEvent[ValueMultName(row)] = 0f;
            levelEvent[LockName(row)] = false;

            levelEvent.UpdatePanel();
            Main.activeChilden();
        }

        private static string Message(string key) => Main.Localizations.GetValue(key) ?? key;

        private static void Log(string message) => Main.Logger?.Log("InsertBpm " + message);

        // ---------------- 选中轨道数据 ----------------

        /// <summary>
        /// 刷新角度/BPM 缓存。fromSelection=true 时用当前编辑器选中重新确定区间（捕获用）；
        /// 否则沿用已固定的区间，只在还没有区间时才回退到当前选中。
        /// </summary>
        private static bool RefreshTrackData(bool fromSelection)
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null)
                return false;

            floorAngles = LevelAnalysis.GetFloorAngles();
            if (floorAngles == null || floorAngles.Length == 0)
                return false;

            int last = floorAngles.Length - 1;
            BuildBpmAfter(editor, last);

            bool hasSelection = editor.selectedFloors != null && editor.selectedFloors.Count > 0;
            int selectedFrom = hasSelection ? editor.selectedFloors.Min(f => f.seqID) : 0;
            int selectedTo = hasSelection ? editor.selectedFloors.Max(f => f.seqID) : 0;

            if (fromSelection)
            {
                // 捕获：区间完全由当前选中决定（可大可小）
                if (hasSelection)
                {
                    rangeFrom = selectedFrom;
                    rangeTo = selectedTo;
                    hasRange = true;
                }
            }
            else if (!hasRange && hasSelection)
            {
                // 还没有区间时用当前选中确定一次；一旦确定，编辑器选中变化不再影响它
                // （要改区间请用“实时捕获”重新选一次，标签上会显示当前区间）。
                rangeFrom = selectedFrom;
                rangeTo = selectedTo;
                hasRange = true;
            }
            else if (!hasRange)
            {
                return false;
            }

            rangeFrom = Mathf.Clamp(rangeFrom, 0, last);
            rangeTo = Mathf.Clamp(rangeTo, 0, last);
            if (rangeFrom > rangeTo)
                return false;

            cumulative = new float[rangeTo + 2];
            cumulative[rangeFrom] = 0f;
            for (int f = rangeFrom; f <= rangeTo; f++)
                cumulative[f + 1] = cumulative[f] + AngleOf(f);
            return true;
        }

        private static float AngleOf(int floor)
        {
            if (floorAngles == null || floor < 0 || floor >= floorAngles.Length)
                return 0f;
            float angle = floorAngles[floor];
            if (float.IsNaN(angle) || angle >= 900f)
                return 0f; // midspin 等哨兵值不产生角度
            return angle;
        }

        /// <summary>
        /// 求解器专用：按待编辑的 SetSpeed 事件算出每砖之后的 BPM，不是游戏砖块平均速度：
        /// 绝对 BPM 事件直接赋值，×倍率事件累乘；第 0 块砖上的事件被忽略。
        /// </summary>
        private static void BuildBpmAfter(scnEditor editor, int last)
        {
            baseBpm = editor != null && editor.levelData != null ? editor.levelData.bpm : 100f;
            bpmAfter = new float[last + 1];
            if (bpmAfter.Length == 0)
                return;
            bpmAfter[0] = baseBpm;

            Dictionary<int, List<LevelEvent>> setSpeeds = null;
            foreach (LevelEvent ev in editor.events)
            {
                if (ev == null || !ev.active || ev.eventType != LevelEventType.SetSpeed)
                    continue;
                if (ev.floor < 1 || ev.floor > last)
                    continue;
                if (setSpeeds == null)
                    setSpeeds = new Dictionary<int, List<LevelEvent>>();
                if (!setSpeeds.TryGetValue(ev.floor, out List<LevelEvent> list))
                    setSpeeds[ev.floor] = list = new List<LevelEvent>();
                list.Add(ev);
            }

            for (int f = 1; f <= last; f++)
            {
                float bpm = bpmAfter[f - 1];
                if (setSpeeds != null && setSpeeds.TryGetValue(f, out List<LevelEvent> list))
                {
                    foreach (LevelEvent ev in list)
                    {
                        switch (ev.Get<SpeedType>("speedType"))
                        {
                            case SpeedType.Bpm:
                                bpm = ev.Get<float>("beatsPerMinute");
                                break;
                            case SpeedType.Multiplier:
                                bpm *= ev.Get<float>("bpmMultiplier");
                                break;
                        }
                    }
                }
                bpmAfter[f] = bpm;
            }
        }

        private static float TotalAngle => cumulative != null ? cumulative[rangeTo + 1] : 0f;

        /// <summary>区间第一块砖到第 floor 块砖“进入时”的累计角度。</summary>
        public static float TileToDegrees(int floor)
        {
            if (cumulative == null)
                return 0f;
            floor = Mathf.Clamp(floor, rangeFrom, rangeTo);
            return cumulative[floor];
        }

        /// <summary>把累计角度吸附到最近的砖块（进入角度最接近的那一块）。</summary>
        public static int DegreesToTile(float degrees)
        {
            if (cumulative == null)
                return rangeFrom;
            if (degrees <= 0f)
                return rangeFrom;

            int best = rangeFrom;
            float bestDelta = float.MaxValue;
            for (int f = rangeFrom; f <= rangeTo; f++)
            {
                float delta = Mathf.Abs(cumulative[f] - degrees);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = f;
                }
            }
            return best;
        }

        /// <summary>某一砖之前一行生效的 BPM（已计入该砖之前的所有 SetSpeed 事件）。</summary>
        private static float RunningBpmBefore(int floor)
        {
            int index = floor - 1;
            if (bpmAfter == null || index < 0 || index >= bpmAfter.Length)
                return baseBpm;
            float bpm = bpmAfter[index];
            return bpm > 0f ? bpm : baseBpm;
        }

        /// <summary>区间内所有生效的 SetSpeed 事件（绝对 BPM 型与 ×倍率型都算“倍频器”）。</summary>
        private static List<LevelEvent> GetExistingSetSpeeds(scnEditor editor)
        {
            var result = new List<LevelEvent>();
            foreach (LevelEvent ev in editor.events)
            {
                if (ev == null || !ev.active || ev.eventType != LevelEventType.SetSpeed)
                    continue;
                if (ev.floor < rangeFrom || ev.floor > rangeTo)
                    continue;
                result.Add(ev);
            }
            result.Sort((a, b) => a.floor.CompareTo(b.floor));
            return result;
        }

        // ---------------- 同步 / 互解 ----------------

        public static void RequestSync(LevelEvent levelEvent)
        {
            if (levelEvent == null)
                return;
            pendingEvent = levelEvent;
            pendingSyncFrame = Time.frameCount + 1;
        }

        public static void ProcessPending()
        {
            if (pendingSyncFrame < 0 || Time.frameCount < pendingSyncFrame)
                return;
            pendingSyncFrame = -1;
            SyncFromSelection(pendingEvent, false);
        }

        /// <summary>
        /// 读取选中轨道上的倍频器并写入面板；超过上限时不更新面板。
        /// notify=true 时弹窗提示（用户主动操作触发），否则只写日志（自动同步触发，避免刷屏）。
        /// </summary>
        public static void SyncFromSelection(LevelEvent levelEvent, bool notify = false)
        {
            if (levelEvent == null)
                return;

            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.selectedFloors == null || editor.selectedFloors.Count == 0)
                return;
            if (!RefreshTrackData(true))
                return;

            List<LevelEvent> existing = GetExistingSetSpeeds(editor);

            if (existing.Count > MaxMultipliers)
            {
                if (notify)
                    Popup.ShowMessage(Message("mth.tooManyMultipliers"));
                else
                    Log($"sync skipped: 区间内倍频器 {existing.Count} 个，超过上限 {MaxMultipliers}");
                return; // 超过限制：不自动更新面板
            }

            AddedRows = 0;
            bool wasComputed = Computed;
            Computed = false;
            int previousRowCount = RowCount;
            RowCount = Mathf.Max(existing.Count, 1);
            bool changed = previousRowCount != RowCount || wasComputed;

            // 先清空所有行，避免上一次选择的数据残留
            changed |= ClearRows(levelEvent);

            for (int i = 0; i < existing.Count; i++)
            {
                LevelEvent ev = existing[i];
                int row = i + 1;
                float before = RunningBpmBefore(ev.floor);

                float bpm;
                float multiplier;
                if (ev.Get<SpeedType>("speedType") == SpeedType.Bpm)
                {
                    // 绝对 BPM 型：bpm 是权威值，“倍”按当前 BPM 反推
                    bpm = ev.Get<float>("beatsPerMinute");
                    multiplier = before > 1e-6f ? bpm / before : 0f;
                }
                else
                {
                    multiplier = ev.Get<float>("bpmMultiplier");
                    bpm = before * multiplier;
                }

                changed |= Assign(levelEvent, PosTileName(row), ev.floor);
                changed |= Assign(levelEvent, PosDegName(row), TileToDegrees(ev.floor));
                changed |= Assign(levelEvent, ValueMultName(row), multiplier);
                changed |= Assign(levelEvent, ValueBpmName(row), bpm);
            }

            // 选中同一段轨道反复点击时值不变，跳过整面板刷新，避免每点一下都重建 100 多个输入框文本
            if (changed)
            {
                levelEvent.UpdatePanel();
                Main.activeChilden();
            }
        }

        /// <summary>仅当值确实变化时写入并返回 true（数值按数值比较，忽略 int/float 装箱差异）。</summary>
        private static bool Assign(LevelEvent levelEvent, string key, object value)
        {
            object current = levelEvent[key];
            if (current != null && IsSameNumber(current, value))
                return false;
            levelEvent[key] = value;
            return true;
        }

        private static bool IsSameNumber(object a, object b)
        {
            try
            {
                return Math.Abs(Convert.ToDouble(a) - Convert.ToDouble(b)) < 1e-9;
            }
            catch
            {
                return Equals(a, b);
            }
        }

        public static bool IsInsertKey(string key)
        {
            if (key == "targetAngleDeg" || key == "targetAngleBeats")
                return true;
            return TryParseField(key, out _, out _);
        }

        /// <summary>“实时捕获”开关：开启后才会读取选中轨道并随选中变化同步。</summary>
        public static bool CaptureEnabled(LevelEvent levelEvent)
            => levelEvent != null && levelEvent.Get<bool>("realtimeCapture");

        /// <summary>
        /// 面板任一相关字段变化后：先做同行的互解换算，再按需用目标总角度求解未锁定的行。
        /// 编辑“未锁定行”的数值时不求解，留给玩家填好后去锁定（避免刚输入就被覆盖）。
        /// </summary>
        public static void OnChanged(LevelEvent levelEvent, string key)
        {
            if (levelEvent == null)
                return;

            bool hasTrack = RefreshTrackData(false);

            if (key == "targetAngleDeg")
            {
                levelEvent["targetAngleBeats"] = ToFloat(levelEvent["targetAngleDeg"]) / 180f;
            }
            else if (key == "targetAngleBeats")
            {
                levelEvent["targetAngleDeg"] = ToFloat(levelEvent["targetAngleBeats"]) * 180f;
            }
            else if (TryParseField(key, out InsertField field, out int index))
            {
                switch (field)
                {
                    case InsertField.PosDeg:
                        if (hasTrack)
                        {
                            int tile = DegreesToTile(ToFloat(levelEvent[PosDegName(index)]));
                            levelEvent[PosTileName(index)] = tile;
                            levelEvent[PosDegName(index)] = TileToDegrees(tile);
                        }
                        break;
                    case InsertField.PosTile:
                        if (hasTrack)
                        {
                            int tile = Mathf.Clamp(ToInt(levelEvent[PosTileName(index)]), rangeFrom, rangeTo);
                            levelEvent[PosTileName(index)] = tile;
                            levelEvent[PosDegName(index)] = TileToDegrees(tile);
                        }
                        break;
                    case InsertField.ValueMult:
                        if (hasTrack)
                        {
                            float run = RunningBpmBefore(ToInt(levelEvent[PosTileName(index)]));
                            levelEvent[ValueBpmName(index)] = run * ToFloat(levelEvent[ValueMultName(index)]);
                        }
                        break;
                    case InsertField.ValueBpm:
                        if (hasTrack)
                        {
                            float run = RunningBpmBefore(ToInt(levelEvent[PosTileName(index)]));
                            float bpm = ToFloat(levelEvent[ValueBpmName(index)]);
                            levelEvent[ValueMultName(index)] = run > 1e-6f ? bpm / run : 0f;
                        }
                        break;
                    case InsertField.Lock:
                        if (hasTrack)
                            EnsureNotAllLocked(levelEvent);
                        break;
                }
            }

            // 任何输入变化都会让上一次“计算”的结果失效，主按钮回到“计算”
            Computed = false;
            levelEvent.UpdatePanel();
        }

        /// <summary>
        /// 按目标总角度求解：锁定的行视为已知，未锁定的行参与计算。
        /// 恰好一个未锁定 → 闭式精确反解；多个未锁定 → 保持它们当前值的相对比例，
        /// 整体乘以公共系数 s，二分求解 A(s) = Σ seg_k / (m_1…m_k) = 目标。
        /// </summary>
        private static bool AutoSolve(LevelEvent levelEvent)
        {
            solveFailure = null;
            if (cumulative == null)
            {
                solveFailure = "还没有确定区间/角度数据";
                return false;
            }

            float target = ToFloat(levelEvent["targetAngleDeg"]);
            if (target <= 0f)
                return false;

            var rows = new List<SolveRow>();
            for (int i = 1; i <= RowCount; i++)
            {
                int tile = ToInt(levelEvent[PosTileName(i)]);
                if (tile < rangeFrom || tile > rangeTo)
                    continue;
                rows.Add(new SolveRow
                {
                    tile = tile,
                    value = ToFloat(levelEvent[ValueMultName(i)]),
                    locked = ToBool(levelEvent[LockName(i)]),
                    index = i,
                });
            }
            if (rows.Count == 0)
            {
                solveFailure = $"区间 {rangeFrom}..{rangeTo} 内没有有效的倍频器行";
                Log($"solve: 区间 {rangeFrom}..{rangeTo} 内没有有效的倍频器行（面板里的位置可能落在区间外）");
                return false;
            }

            rows.Sort((a, b) => a.tile.CompareTo(b.tile));

            int freeCount = 0;
            int freePos = -1;
            for (int k = 0; k < rows.Count; k++)
            {
                // 位于区间第一块砖上的倍频器不参与计算：它的效果已经包含在"基准速度"里
                // （总角度以第一块选中砖的速度为基准），把它计入会整体多除一次。
                if (!rows[k].locked && rows[k].tile != rangeFrom)
                {
                    freeCount++;
                    freePos = k;
                }
            }

            if (freeCount == 0)
            {
                solveFailure = $"没有可计算的未锁定行：共 {rows.Count} 行，全部已锁定，或位于区间第一块砖 {rangeFrom} 上";
                Log($"solve: 没有可计算的未锁定行（区间 {rangeFrom}..{rangeTo}，{rows.Count} 行全部已锁定或位于第一块砖 {rangeFrom} 上）");
                return false;
            }
            if (freeCount == 1)
                return SolveSingle(levelEvent, rows, freePos, target);
            return SolveProportional(levelEvent, rows, target);
        }

        private sealed class SolveRow
        {
            public int tile;
            public float value;
            public bool locked;
            public int index;
        }

        /// <summary>只有一个未锁定行：闭式反解。</summary>
        private static bool SolveSingle(LevelEvent levelEvent, List<SolveRow> rows, int freePos, float target)
        {
            int count = rows.Count;
            int unknown = freePos + 1;

            double[] m = new double[count + 1];
            double[] q = new double[count + 2];
            q[0] = 0d;
            for (int k = 0; k < count; k++)
            {
                q[k + 1] = cumulative[rows[k].tile];
                if (k != freePos)
                    m[k + 1] = (rows[k].tile == rangeFrom || rows[k].value <= 0f) ? 1d : rows[k].value;
            }
            q[count + 1] = TotalAngle;

            // 未知行之前的已知贡献 L 与累计倍率 P = m_1…m_{j-1}
            double before = 0d, productBefore = 1d;
            for (int k = 0; k <= unknown - 1; k++)
            {
                before += (q[k + 1] - q[k]) / productBefore;
                if (k < unknown - 1)
                    productBefore *= m[k + 1];
            }

            // 未知行及其之后、但去掉未知倍率后的等效尾长 T
            double tail = q[unknown + 1] - q[unknown];
            double productTail = 1d;
            for (int k = unknown + 1; k <= count; k++)
            {
                productTail *= m[k];
                tail += (q[k + 1] - q[k]) / productTail;
            }

            double denominator = productBefore * (target - before);
            if (tail <= 0d || denominator <= 1e-9)
            {
                solveFailure = $"单行无解：前段已占 {before:0.###}°、尾段 {tail:0.###}°、目标 {target:0.###}°";
                Log($"solve-single failed: target={target:0.###} before(前段)={before:0.###} tail={tail:0.###} P={productBefore:0.####} (目标低于前段贡献或无解)");
                return false;
            }

            double solved = tail / denominator;
            return Apply(levelEvent, rows[freePos], (float)solved);
        }

        /// <summary>多个未锁定行：保持相对比例，共同乘以 s，二分求解。</summary>
        private static bool SolveProportional(LevelEvent levelEvent, List<SolveRow> rows, float target)
        {
            // 未锁定行的基准值：已填值就用它，否则用 1（这样空行也会被赋一个解）
            double[] bases = new double[rows.Count];
            for (int k = 0; k < rows.Count; k++)
                bases[k] = !rows[k].locked && rows[k].tile != rangeFrom && rows[k].value > 0f ? rows[k].value : 1d;

            double lo = 1e-6, hi = 1e2;
            double atLo = TotalWithScale(rows, bases, lo);
            double atHi = TotalWithScale(rows, bases, hi);

            // 目标很接近下界时需要很大的 scale；逐步扩大上界再判定，避免误报无解
            for (int expand = 0; expand < 14 && !(atHi < target) && hi < 1e15; expand++)
            {
                hi *= 10d;
                atHi = TotalWithScale(rows, bases, hi);
            }

            if (double.IsNaN(atLo) || double.IsNaN(atHi) || !(atLo > target) || !(atHi < target))
            {
                solveFailure = $"多行无解：前段已占 {atHi:0.###}°、目标 {target:0.###}°（需要更靠前的未锁定行，或调低锁定值）";
                Log($"solve-proportional failed: target={target:0.###} atLo={atLo:0.###} atHi={atHi:0.###} (目标低于“第一个未锁定倍频器之前那段”的贡献，需更靠前的未锁定行或调整锁定值)");
                return false; // 目标低于“第一个未锁定倍频器之前那段”的贡献时无解
            }

            for (int iteration = 0; iteration < 80; iteration++)
            {
                double mid = (lo + hi) * 0.5;
                if (TotalWithScale(rows, bases, mid) > target)
                    lo = mid;
                else
                    hi = mid;
            }

            double scale = (lo + hi) * 0.5;
            bool applied = false;
            for (int k = 0; k < rows.Count; k++)
                if (!rows[k].locked && rows[k].tile != rangeFrom)
                    applied |= Apply(levelEvent, rows[k], (float)(bases[k] * scale));
            return applied;
        }

        private static double TotalWithScale(List<SolveRow> rows, double[] bases, double scale)
        {
            double total = 0d, product = 1d;
            for (int k = 0; k < rows.Count; k++)
            {
                double previous = k == 0 ? 0d : cumulative[rows[k - 1].tile];
                total += (cumulative[rows[k].tile] - previous) / product;

                double m;
                if (rows[k].tile == rangeFrom)
                    m = 1d; // 基准行：不参与归一化
                else if (rows[k].locked)
                    m = rows[k].value > 0f ? rows[k].value : 1d;
                else
                    m = bases[k] * scale;
                if (m <= 0d)
                    return double.PositiveInfinity;
                product *= m;
            }
            total += (TotalAngle - cumulative[rows[rows.Count - 1].tile]) / product;
            return total;
        }

        private static bool Apply(LevelEvent levelEvent, SolveRow row, float multiplier)
        {
            if (multiplier <= 0f || float.IsNaN(multiplier) || float.IsInfinity(multiplier))
                return false;
            levelEvent[ValueMultName(row.index)] = multiplier;
            levelEvent[ValueBpmName(row.index)] = RunningBpmBefore(row.tile) * multiplier;
            return true;
        }

        private static bool ToBool(object value)
        {
            if (value == null)
                return false;
            try
            {
                return Convert.ToBoolean(value);
            }
            catch
            {
                return false;
            }
        }

        // ---------------- 创建 ----------------

        public static void Create(LevelEvent levelEvent)
        {
            if (levelEvent == null)
                return;
            if (!RefreshTrackData(false))
            {
                Popup.ShowMessage(Message("mth.needSelectTrack"));
                return;
            }

            scnEditor editor = scnEditor.instance;
            int applied = 0;

            using (UndoScope.Begin(editor))
            {
                List<LevelEvent> existing = GetExistingSetSpeeds(editor);

                // 按砖去重（同一砖多行时后者覆盖前者，明确 last-wins）；
                // 值 <=0：锁定行按“不变(×1)”处理，未锁定行视为未填写 → 不写入。
                var byTile = new Dictionary<int, SolveRow>();
                for (int i = 1; i <= RowCount; i++)
                {
                    int tile = ToInt(levelEvent[PosTileName(i)]);
                    if (tile < rangeFrom || tile > rangeTo)
                        continue;

                    float multiplier = ToFloat(levelEvent[ValueMultName(i)]);
                    bool locked = ToBool(levelEvent[LockName(i)]);
                    if (multiplier <= 0f)
                    {
                        if (!locked)
                            continue;
                        multiplier = 1f;
                    }

                    byTile[tile] = new SolveRow { tile = tile, value = multiplier, locked = locked, index = i };
                }

                // 按砖排序，边走边更新上游 BPM：绝对 BPM 型事件写入 "最新上游 × 倍"，
                // 否则本轮改动过的上游会让它下次读取时反推出的倍率发生漂移。
                var rows = new List<SolveRow>(byTile.Values);
                rows.Sort((a, b) => a.tile.CompareTo(b.tile));

                float running = RunningBpmBefore(rangeFrom);

                foreach (SolveRow row in rows)
                {
                    float bpm = running * row.value;

                    LevelEvent ev = existing.FirstOrDefault(x => x.floor == row.tile);
                    bool keepBpmType;
                    if (ev == null)
                    {
                        ev = new LevelEvent(row.tile, LevelEventType.SetSpeed);
                        editor.events.Add(ev);
                        existing.Add(ev);
                        keepBpmType = false;
                    }
                    else
                    {
                        // 保留事件原本的类型，避免把绝对 BPM 型改成倍率型
                        keepBpmType = ev.Get<SpeedType>("speedType") == SpeedType.Bpm;
                    }

                    if (!ev.ContainsKey("angleOffset"))
                        ev["angleOffset"] = 0f;

                    if (keepBpmType)
                    {
                        ev["speedType"] = SpeedType.Bpm;
                        ev["beatsPerMinute"] = bpm;
                        ev["bpmMultiplier"] = row.value;
                    }
                    else
                    {
                        ev["speedType"] = SpeedType.Multiplier;
                        ev["bpmMultiplier"] = row.value;
                        ev["beatsPerMinute"] = bpm;
                    }

                    running = bpm;
                    applied++;
                }

                // RemakePath(true, false) 不会 ApplyEventsToFloors，新 SetSpeed 要播放一次才生效；ApplyEventsToFloors 会重算到达时间
                editor.ApplyEventsToFloors();
            }

            if (applied == 0)
                Popup.ShowMessage(Message("mth.noMultiplierToCreate"));

            SyncFromSelection(levelEvent, false);
            RefreshPanel(levelEvent);
        }

        // ---------------- 辅助 ----------------

        private enum InsertField
        {
            PosDeg,
            PosTile,
            ValueBpm,
            ValueMult,
            Lock,
        }

        private static bool TryParseField(string key, out InsertField field, out int index)
        {
            field = default;
            index = 0;
            if (string.IsNullOrEmpty(key))
                return false;

            int digitsStart = key.Length;
            while (digitsStart > 0 && char.IsDigit(key[digitsStart - 1]))
                digitsStart--;
            if (digitsStart == key.Length)
                return false;
            if (!int.TryParse(key.Substring(digitsStart), out index))
                return false;

            switch (key.Substring(0, digitsStart))
            {
                case "bpPosDeg":
                    field = InsertField.PosDeg;
                    return true;
                case "bpPosTile":
                    field = InsertField.PosTile;
                    return true;
                case "bpValueBpm":
                    field = InsertField.ValueBpm;
                    return true;
                case "bpValueMult":
                    field = InsertField.ValueMult;
                    return true;
                case "bpLock":
                    field = InsertField.Lock;
                    return true;
                default:
                    return false;
            }
        }

        private static float ToFloat(object value)
        {
            if (value == null)
                return 0f;
            try
            {
                return Convert.ToSingle(value);
            }
            catch
            {
                return 0f;
            }
        }

        private static int ToInt(object value)
        {
            if (value == null)
                return 0;
            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return 0;
            }
        }
    }
}
