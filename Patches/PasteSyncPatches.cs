using ADOFAI;
using HarmonyLib;
using MultiTrackHelper.Features;
using MultiTrackHelper.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using TrackData = MultiTrackHelper.LevelAnalysis.TrackData;

namespace MultiTrackHelper.Patches
{
    internal static class PasteSyncPatches
    {
        // 本批剪贴板条目的身份列表（复制时按序记下的盒子引用）。只用来认“最近这批是不是我们记的”，
        // 以及逐块追加时保持顺序；**源 BPM 不在这里**，而是记在 ClipboardBpmStore 的弱表里（按盒子/事件列表身份），
        // 否则复制 B 就会把 A 的 BPM 顶掉，撤销回到 A 的剪贴板后重做拿不到 BPM。
        private static readonly List<object> copiedEntries = new List<object>();
        // PACL 粘贴到多选时会在 PasteFloors 内部借 CopyFloor 备份被覆盖的砖；那不是用户的复制，不能动记录
        private static int pasteDepth;

        private static bool ToggleOn()
        {
            LevelEvent panel = Main.GetEvent((LevelEventType)Main.MultiTrack.type);
            return panel != null && panel.Get<bool>("syncSpeedOnPaste");
        }

        private static float GetFloat(LevelEvent e, string key, float defaultValue)
        {
            if (e.TryGet<object>(key, out object o) && o != null)
            {
                try { return Convert.ToSingle(o); } catch { return defaultValue; }
            }
            return defaultValue;
        }

        // 需要按 BPM 等比缩放的计时事件：装饰动画、相机运动与对象事件（SetObject 的 angleOffset/duration 同样是时序值）
        private static bool IsTimedEvent(LevelEvent e)
        {
            return e.eventType == LevelEventType.MoveDecorations
                || e.eventType == LevelEventType.MoveCamera
                || e.eventType == LevelEventType.SetObject;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        // 凡是“剪贴板可能已经换了内容”的分支都先清掉“本批身份”列表。
        // 注意只清列表：源 BPM 在 ClipboardBpmStore 里按身份存，剪贴板换届不能把它清掉——
        // 用户先复制 A、再复制 B，随后撤销/重做把剪贴板换回 A 时，还要靠它做粘贴缩放。
        private static void ResetCopyMetadata()
        {
            copiedEntries.Clear();
        }

        /// <summary>
        /// 关闭模组时连粘贴深度与弱表一起清零。
        /// 刻意不并进 <see cref="ResetCopyMetadata"/>：后者在复制/粘贴半途也会被调用，
        /// 随手改 pasteDepth 会把“粘贴中”的守卫提前解开，也不能把源 BPM 丢掉。
        /// Awake 也不调这个：跨关卡粘贴要求编辑器重载后仍认得那批剪贴板条目。
        /// </summary>
        internal static void ResetAll()
        {
            ResetCopyMetadata();
            ClipboardBpmStore.Clear();
            pasteDepth = 0;
        }

        // 登记一条剪贴板内容的源 BPM：盒子身份 + 它的事件列表身份各记一次。
        // 粘贴多砖拿到的是盒子，单砖粘贴/粘贴事件拿到的是拆箱副本里的列表引用，两种入口都得查得到。
        private static void RecordSourceBpm(object box, float bpm)
        {
            if (box == null || !IsFinite(bpm) || bpm <= 0f)
                return;

            ClipboardBpmStore.StoreBox(box, bpm);
            if (box is scnEditor.FloorData floorData)
                ClipboardBpmStore.StoreEventList(floorData.levelEventData, bpm);
        }

        // 当前剪贴板是不是“最近这批记过的盒子”：内容类型、条数、逐条引用全部一致才算。
        // 只用于复制侧的批次记账（追加复制时判断要不要重记），**不再**当作粘贴的前置条件：
        // 粘贴改按单盒查弱表，所以剪贴板被换回旧批次时也能正常工作。
        private static bool CopiedClipboardMatches(scnEditor editor)
        {
            if (editor == null || editor.clipboard == null)
                return false;
            if (editor.clipboardContent != scnEditor.ClipboardContent.Floors)
                return false;

            int count = editor.clipboard.Count;
            if (count == 0 || count != copiedEntries.Count)
                return false;

            for (int i = 0; i < count; i++)
            {
                if (!ReferenceEquals(editor.clipboard[i], copiedEntries[i]))
                    return false;
            }
            return true;
        }

        // 源有效 BPM = 关卡 BPM × 该砖速度，与 scnGame.ApplyEvent 折算事件时长用的口径一致
        private static float SourceBpm(scnEditor editor, scrFloor floor)
        {
            var data = editor.levelData;
            if (floor == null || data == null)
                return float.NaN;
            float candidate = data.bpm * floor.speed;
            return candidate > 0f && IsFinite(candidate) ? candidate : float.NaN;
        }

        [HarmonyPatch(typeof(scnEditor), "MultiCopyFloors")]
        internal static class MultiCopyFloorsPatch
        {
            internal static void Prefix()
            {
                ResetCopyMetadata();
                try
                {
                    OnCopy();
                }
                catch (Exception e)
                {
                    Main.Logger.Log("PasteSync 复制处理失败: " + e);
                }
            }

            // PACL 把 MultiCopyFloors 整个换掉了，直接逐砖调私有的 CopyOfFloor、不经过 CopyFloor，
            // 逐砖源速度只能在这里按选区补记；原版路径下 CopyFloor 已经逐条记好，这里什么都不做
            internal static void Postfix(scnEditor __instance)
            {
                try
                {
                    if (!ToggleOn())
                        return;
                    if (__instance.clipboardContent != scnEditor.ClipboardContent.Floors || CopiedClipboardMatches(__instance))
                        return;

                    List<scrFloor> floors = __instance.selectedFloors;
                    int count = __instance.clipboard.Count;
                    ResetCopyMetadata();
                    if (floors == null || floors.Count != count)
                    {
                        Main.Logger.Log($"PasteSync: 多砖复制后剪贴板 {count} 条与选区 {floors?.Count ?? 0} 块对不上，本次不记录源速度");
                        return;
                    }
                    for (int i = 0; i < count; i++)
                    {
                        copiedEntries.Add(__instance.clipboard[i]);
                        RecordSourceBpm(__instance.clipboard[i], SourceBpm(__instance, floors[i]));
                    }
                    Main.Logger.Log($"PasteSync: 多砖复制，按选区记录了 {count} 条源速度");
                }
                catch (Exception e)
                {
                    ResetCopyMetadata();
                    Main.Logger.Log("PasteSync 多砖复制后处理失败: " + e);
                }
            }
        }

        [HarmonyPatch(typeof(scnEditor), "CopyFloor", new[] { typeof(scrFloor), typeof(bool), typeof(bool), typeof(bool), typeof(bool) })]
        internal static class CopyFloorPatch
        {
            internal static void Prefix(scnEditor __instance, bool clearClipboard, out int __state)
            {
                __state = clearClipboard ? 0 : __instance.clipboard.Count;
                if (pasteDepth > 0)
                    return;
                try
                {
                    if (!ToggleOn())
                    {
                        ResetCopyMetadata();
                        return;
                    }
                    // 原来的复制要么清空剪贴板、要么往后追加
                    if (clearClipboard || (__state > 0 && !CopiedClipboardMatches(__instance)))
                        ResetCopyMetadata();
                }
                catch (Exception e)
                {
                    ResetCopyMetadata();
                    Main.Logger.Log("PasteSync 复制前处理失败: " + e);
                }
            }

            internal static void Postfix(scnEditor __instance, scrFloor toCopy, int __state)
            {
                if (pasteDepth > 0)
                    return;
                try
                {
                    if (!ToggleOn())
                    {
                        ResetCopyMetadata();
                        return;
                    }
                    if (__instance.clipboardContent != scnEditor.ClipboardContent.Floors)
                        return;
                    // 只认"这次复制确实往尾部加了恰好一个盒子"的情形
                    if (__instance.clipboard.Count != __state + 1 || copiedEntries.Count != __state)
                        return;

                    object box = __instance.clipboard[__instance.clipboard.Count - 1];
                    if (box == null)
                        return;

                    copiedEntries.Add(box);
                    float sourceBpm = SourceBpm(__instance, toCopy);
                    RecordSourceBpm(box, sourceBpm);
                    // 多砖复制在原版里是逐块追加（__state > 0），只给单砖复制打一条，免得刷屏
                    if (__state == 0)
                        Main.Logger.Log($"PasteSync: 复制砖 {toCopy?.seqID}，记录源 BPM {sourceBpm}");
                }
                catch (Exception e)
                {
                    ResetCopyMetadata();
                    Main.Logger.Log("PasteSync 复制后处理失败: " + e);
                }
            }
        }

        [HarmonyPatch(typeof(scnEditor), "PasteFloors")]
        internal static class PasteFloorsPatch
        {
            // 一次粘贴的上下文，随 Harmony __state 走，不留全局状态
            internal sealed class PasteState
            {
                internal HashSet<LevelEvent> existingEvents;
                internal int firstFloor;         // 剪贴板第 0 条内容粘贴后所在砖
                internal int floorCountBefore;   // 粘贴前砖数，用来确认原游戏真的贴完了
                internal float[] sourceBpms;     // 与剪贴板条目一一对应的源有效 BPM
                internal bool enteredDepth;
                internal bool captured;
            }

            internal static void Prefix(scnEditor __instance, out PasteState __state)
            {
                __state = new PasteState();
                try
                {
                    PasteState captured = CapturePasteState(__instance);
                    if (captured != null)
                    {
                        __state = captured;
                        __state.captured = true;
                    }
                }
                catch (Exception e)
                {
                    Main.Logger.Log("PasteSync 粘贴前处理失败: " + e);
                }
                pasteDepth++;
                __state.enteredDepth = true;
            }

            internal static void Finalizer(PasteState __state)
            {
                if (__state != null && __state.enteredDepth && pasteDepth > 0)
                    pasteDepth--;
            }

            internal static void Postfix(scnEditor __instance, PasteState __state)
            {
                try
                {
                    if (__state != null && __state.captured)
                        OnPaste(__instance, __state);
                }
                catch (Exception e)
                {
                    Main.Logger.Log("PasteSync 粘贴处理失败: " + e);
                }
            }
        }

        // 剪贴板只有一块砖时，原版 Ctrl+V 不插砖：PasteFloorEditorAction 改调 PasteEvents，把这块砖的事件覆盖到选中的砖上
        // （多选时逐块覆盖），根本不经过 PasteFloors。"复制事件/粘贴事件"同样走这里。
        [HarmonyPatch(typeof(scnEditor), "PasteEvents")]
        internal static class PasteEventsPatch
        {
            internal sealed class PasteEventsState
            {
                internal HashSet<LevelEvent> existingEvents;
                internal float sourceBpm;
            }

            internal static void Prefix(scnEditor __instance, scnEditor.FloorData floorData, out PasteEventsState __state)
            {
                __state = null;
                try
                {
                    __state = CapturePasteEventsState(__instance, floorData);
                }
                catch (Exception e)
                {
                    Main.Logger.Log("PasteSync 粘贴事件前处理失败: " + e);
                }
            }

            internal static void Postfix(scnEditor __instance, scrFloor targetFloor, bool updateFloors, PasteEventsState __state)
            {
                try
                {
                    OnPasteEvents(__instance, targetFloor, updateFloors, __state);
                }
                catch (Exception e)
                {
                    Main.Logger.Log("PasteSync 粘贴事件处理失败: " + e);
                }
            }
        }

        private static PasteEventsPatch.PasteEventsState CapturePasteEventsState(scnEditor editor, scnEditor.FloorData floorData)
        {
            if (editor == null || !ToggleOn())
                return null;
            List<LevelEvent> source = floorData.levelEventData;
            if (source == null || !source.Any(IsTimedEvent))
                return null;

            // FloorData 是值类型，传进来的是拆箱副本；按它的事件列表引用去弱表里查复制时记下的那条。
            // 不按 tag/内容猜：查不到就是没记录过（复制时开关没开，或内容不是本模组复制的），整次粘贴不同步。
            if (!ClipboardBpmStore.TryGetEventList(source, out float sourceBpm))
            {
                Main.Logger.Log("PasteSync: 粘贴的这块砖没有记录过源速度（复制时开关没开，或该内容不是本模组复制的），本次粘贴不做速度同步");
                return null;
            }

            return new PasteEventsPatch.PasteEventsState
            {
                existingEvents = new HashSet<LevelEvent>(editor.events),
                sourceBpm = sourceBpm
            };
        }

        private static void OnPasteEvents(scnEditor editor, scrFloor targetFloor, bool updateFloors, PasteEventsPatch.PasteEventsState state)
        {
            if (state == null || editor == null || targetFloor == null)
                return;
            if (!ToggleOn())
                return;

            int id = targetFloor.seqID;
            List<LevelEvent> added = editor.events.Where(e => e.floor == id && !state.existingEvents.Contains(e)).ToList();
            if (!added.Any(IsTimedEvent))
                return;

            // 多选逐块粘贴时只有最后一块才刷新砖；贴进来的 SetSpeed 要先生效，目标速度才是贴完后的
            if (!updateFloors && added.Any(e => e.eventType == LevelEventType.SetSpeed))
                editor.ApplyEventsToFloors();

            HashSet<string> manualTags = TagCleanup.CollectHitboxEventTags(editor.levelData.decorations);
            int scaled = 0;
            int unchanged = 0;
            int skipped = 0;
            foreach (LevelEvent e in added)
            {
                if (IsTimedEvent(e))
                    Count(ScaleToDestination(editor, e, state.sourceBpm, manualTags), ref scaled, ref unchanged, ref skipped);
            }

            float destBpm = editor.levelData.bpm * targetFloor.speed;
            Main.Logger.Log($"PasteSync: 事件粘贴到砖 {id}，源 BPM {state.sourceBpm} → 目标 BPM {destBpm}：缩放 {scaled} 个事件，无需缩放 {unchanged} 个，跳过 {skipped} 个");

            if (scaled > 0 && updateFloors)
                editor.ApplyEventsToFloors();
        }

        private enum ScaleResult { Scaled, Unchanged, Skipped }

        private static void Count(ScaleResult result, ref int scaled, ref int unchanged, ref int skipped)
        {
            if (result == ScaleResult.Scaled)
                scaled++;
            else if (result == ScaleResult.Unchanged)
                unchanged++;
            else
                skipped++;
        }

        // 按事件所在砖贴完后的实际速度等比缩放 angleOffset/duration，让它保持在源关卡里的实际时长
        private static ScaleResult ScaleToDestination(scnEditor editor, LevelEvent e, float sourceBpm, HashSet<string> manualTags)
        {
            // WHY：撞击/条件触发的风格事件走 StartEffectWithOffset（延迟随所在砖速度正比），不是时间轴 AO/(BPM×速度)，等比缩放会双重折算
            if (e.eventType == LevelEventType.SetObject && TagCleanup.IsStyleOnlySetObject(e)
                && TagCleanup.HasExternalStyleTrigger(e, editor.events, manualTags))
                return ScaleResult.Skipped;

            if (e.floor < 0 || e.floor >= editor.floors.Count)
                return ScaleResult.Skipped;
            scrFloor tile = editor.floors[e.floor];
            if (sourceBpm <= 0f || !IsFinite(sourceBpm) || tile == null)
                return ScaleResult.Skipped;

            // 事件的实际时长用的是它所在砖的 关卡BPM × 砖速度（见 scnGame.ApplyEvent 的 crotchet）
            float destBpm = editor.levelData.bpm * tile.speed;
            float ratio = destBpm / sourceBpm;
            if (destBpm <= 0f || !IsFinite(destBpm) || ratio <= 0f || !IsFinite(ratio))
                return ScaleResult.Skipped;
            if (Math.Abs(ratio - 1f) < 0.0001f)
                return ScaleResult.Unchanged;

            float ao = GetFloat(e, "angleOffset", 0f);
            float dur = GetFloat(e, "duration", 0f);
            float newAo = ao * ratio;
            float newDur = dur * ratio;
            if (!IsFinite(newAo) || !IsFinite(newDur))
                return ScaleResult.Skipped;
            if (newAo == ao && newDur == dur)
                return ScaleResult.Unchanged;

            e["angleOffset"] = newAo;
            e["duration"] = newDur;
            return ScaleResult.Scaled;
        }

        private static PasteFloorsPatch.PasteState CapturePasteState(scnEditor editor)
        {
            if (!ToggleOn())
                return null;
            if (editor == null)
                return null;

            if (editor.clipboardContent != scnEditor.ClipboardContent.Floors)
                return null;
            int count = editor.clipboard.Count;
            if (count == 0)
                return null;

            if (!editor.SelectionIsSingle())
            {
                Main.Logger.Log("PasteSync: 粘贴时没有恰好选中一块砖，本次粘贴不做速度同步");
                return null;
            }

            // 逐盒查弱表：BPM 跟着盒子身份走，所以不要求剪贴板跟“最近一次复制”完全一致。
            // 复制 B 后撤销/重做把剪贴板换回 A 那批盒子时，A 的 BPM 仍然查得到（旧实现到这里已经丢了）。
            float[] sourceBpms = new float[count];
            for (int i = 0; i < count; i++)
            {
                if (!ClipboardBpmStore.TryGetBox(editor.clipboard[i], out sourceBpms[i]))
                {
                    Main.Logger.Log($"PasteSync: 剪贴板第 {i} 条没有记录过源速度（复制时开关没开，或该内容不是本模组复制的），本次粘贴不做速度同步");
                    return null;
                }
            }

            return new PasteFloorsPatch.PasteState
            {
                existingEvents = new HashSet<LevelEvent>(editor.events),
                firstFloor = editor.selectedFloors[0].seqID + 1,
                floorCountBefore = editor.floors.Count,
                sourceBpms = sourceBpms
            };
        }

        private static void OnCopy()
        {
            if (!ToggleOn())
                return;

            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.selectedFloors == null || editor.selectedFloors.Count == 0)
                return;

            int anchor = editor.selectedFloors[0].seqID;
            TrackData[] tds = LevelAnalysis.GetTrackDatas(LevelAnalysis.GetFloorEvents());
            if (anchor < 0 || anchor >= tds.Length)
            {
                Main.Logger.Log($"PasteSync: 复制锚点砖 {anchor} 超出轨道数据范围（{tds.Length}），跳过分布式→集中式转换");
                return;
            }

            float bpmAnchor = tds[anchor].bpm;
            if (bpmAnchor <= 0f || float.IsNaN(bpmAnchor) || float.IsInfinity(bpmAnchor))
            {
                Main.Logger.Log($"PasteSync: 锚点砖 {anchor} 的 BPM 无效（{bpmAnchor}），跳过同步");
                return;
            }

            // 锚点砖上有批量事件时，尝试把分布在其他砖上的同批事件归并过来
            string batchTag = DeriveTrackBatch(editor, anchor, out HashSet<string> ownedTokens);
            if (batchTag != null)
            {
                ConvertBatchToCentralized(editor, tds, anchor, bpmAnchor, ownedTokens);
            }

            // 这里只负责分布式→集中式的批次归并；逐砖源速度由 CopyFloor 的 Postfix 逐条记录
            Main.Logger.Log($"PasteSync: 复制锚点砖 {anchor}，批次标签 {(batchTag ?? "无")}，剪贴板 {editor.clipboard?.Count ?? 0} 条");
        }

        private static string DeriveTrackBatch(scnEditor editor, int anchor, out HashSet<string> ownedTokens)
        {
            ownedTokens = null;

            if (!LevelAnalysis.GetFloorEvents().TryGetValue(anchor, out Dictionary<LevelEventType, List<LevelEvent>> byType))
                return null;

            // 锚点砖可能只有风格事件而没有动画：动画全取，SetObject 只取 style-only（无关的 SetObject 既不当批次证据也不刷日志）
            byType.TryGetValue(LevelEventType.MoveDecorations, out List<LevelEvent> animations);
            byType.TryGetValue(LevelEventType.SetObject, out List<LevelEvent> objects);
            IEnumerable<LevelEvent> styleEvents = objects == null
                ? Enumerable.Empty<LevelEvent>()
                : objects.Where(TagCleanup.IsStyleOnlySetObject);
            if ((animations == null || animations.Count == 0) && !styleEvents.Any())
                return null;

            IEnumerable<LevelEvent> events = animations == null
                ? styleEvents
                : animations.Concat(styleEvents);

            Dictionary<string, HashSet<string>> owners = TagCleanup.CollectBatchOwners(editor.levelData.decorations, out _, out HashSet<string> uncertain);

            HashSet<string> candidates = new HashSet<string>(StringComparer.Ordinal);
            foreach (LevelEvent e in events)
            {
                if (!e.TryGet<string>("tag", out string tagValue) || string.IsNullOrEmpty(tagValue))
                    continue;
                string[] tokens = tagValue.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Any(t => uncertain.Contains(t)))
                    continue;
                foreach (string token in tokens)
                {
                    if (!owners.TryGetValue(token, out HashSet<string> batches))
                        continue;
                    foreach (string batchTag in batches)
                        candidates.Add(batchTag);
                }
            }

            if (candidates.Count != 1)
            {
                Main.Logger.Log($"PasteSync: 锚点砖 {anchor} 的批量事件无法确定唯一生成批次（候选 {candidates.Count} 个），跳过分布式→集中式转换");
                return null;
            }

            string batch = candidates.First();
            ownedTokens = new HashSet<string>(StringComparer.Ordinal);
            // foo21 既可能是 foo 的第 21 块也可能是 foo2 的第 1 块：被多批声明的令牌身份不明，一个都不归并
            foreach (KeyValuePair<string, HashSet<string>> pair in owners)
            {
                if (pair.Value.Count == 1 && pair.Value.Contains(batch) && !uncertain.Contains(pair.Key))
                    ownedTokens.Add(pair.Key);
            }

            return batch;
        }

        private static void ConvertBatchToCentralized(scnEditor editor, TrackData[] tds, int anchor, float bpmAnchor, HashSet<string> ownedTokens)
        {
            HashSet<string> manualTags = TagCleanup.CollectHitboxEventTags(editor.levelData.decorations);
            List<LevelEvent> batch = new List<LevelEvent>();
            int skipped = 0;
            foreach (LevelEvent e in editor.events)
            {
                // 风格事件与动画同批生成，一起归并；其它 SetObject 归并会搬走无关对象，跳过
                bool styleOnly = e.eventType == LevelEventType.SetObject && TagCleanup.IsStyleOnlySetObject(e);
                if (e.eventType != LevelEventType.MoveDecorations && !styleOnly)
                    continue;
                if (!e.TryGet<string>("tag", out string tag) || string.IsNullOrEmpty(tag))
                    continue;

                string[] tokens = tag.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                // 只归并"每个令牌都属于本批"的事件；混进别批或未知令牌就整条跳过，免得搬走别的目标
                if (tokens.Length == 0 || !tokens.All(ownedTokens.Contains))
                    continue;

                // WHY：撞击/条件触发的事件由游戏按所在砖速度折算实际延迟，换砖或改角度都会改掉玩家看到的触发时机
                if (styleOnly && TagCleanup.HasExternalStyleTrigger(e, editor.events, manualTags))
                {
                    skipped++;
                    continue;
                }

                batch.Add(e);
            }

            if (batch.Count == 0)
            {
                if (skipped > 0)
                    Main.Logger.Log($"PasteSync: 砖 {anchor} 的 {skipped} 个外部触发风格事件不参与归并");
                return;
            }

            int converted = 0;

            using (UndoScope.Begin(editor))
            {
                foreach (LevelEvent e in batch)
                {
                    int f = e.floor;
                    if (f == anchor)
                        continue;
                    if (f < 0 || f >= tds.Length)
                        continue;

                    float bpmSrc = tds[f].bpm;
                    double dt = tds[f].gameEntryTime - tds[anchor].gameEntryTime;
                    float ao = GetFloat(e, "angleOffset", 0f);
                    float dur = GetFloat(e, "duration", 0f);

                    // 事件原本在砖 f 触发：到达(f) + angleOffset(按 bpm_src 折算)
                    // 改挂到砖 anchor 后保持同一绝对时刻、同一段实际时长
                    float newAngleOffset = (float)(dt * bpmAnchor * 3.0 + (double)ao * bpmAnchor / bpmSrc);
                    float newDuration = dur * (bpmAnchor / bpmSrc);

                    // 关卡里可能存在 0/异常速度与越界数值，算不出有限值就跳过这条，不写 NaN/Infinity
                    if (bpmSrc <= 0f || !IsFinite(bpmSrc) || double.IsNaN(dt) || double.IsInfinity(dt) || !IsFinite(ao) || !IsFinite(dur)
                        || !IsFinite(newAngleOffset) || !IsFinite(newDuration))
                        continue;

                    e["angleOffset"] = newAngleOffset;
                    e["duration"] = newDuration;
                    e.floor = anchor;
                    converted++;
                }
            }

            Main.Logger.Log($"PasteSync: 分布式→集中式转换完成，{batch.Count} 个事件中改写 {converted} 个到砖 {anchor}，外部触发风格跳过 {skipped} 个");
        }

        private static void OnPaste(scnEditor editor, PasteFloorsPatch.PasteState state)
        {
            if (state == null || editor == null)
                return;
            if (!ToggleOn())
                return;

            int count = state.sourceBpms.Length;
            if (count == 0)
                return;

            // 原游戏提前 return（剪贴板空/多砖选中/方向朝后）或砖数没按这批剪贴板增长：没贴成功，什么都不改
            if (editor.floors.Count != state.floorCountBefore + count)
            {
                Main.Logger.Log($"PasteSync: 粘贴后砖数 {editor.floors.Count} ≠ 粘贴前 {state.floorCountBefore} + 剪贴板 {count}，视为没贴成功，不做速度同步");
                return;
            }

            var levelData = editor.levelData;
            if (levelData == null)
                return;

            int scaled = 0;
            int unchanged = 0;
            int skipped = 0;
            HashSet<string> manualTags = TagCleanup.CollectHitboxEventTags(levelData.decorations);

            foreach (LevelEvent e in editor.events)
            {
                if (state.existingEvents.Contains(e))
                    continue;
                if (!IsTimedEvent(e))
                    continue;

                // 剪贴板第 i 条的内容一定落在 firstFloor + i 上（不是粘贴后选中的末砖）
                int index = e.floor - state.firstFloor;
                if (index < 0 || index >= count)
                {
                    skipped++;
                    continue;
                }

                Count(ScaleToDestination(editor, e, state.sourceBpms[index], manualTags), ref scaled, ref unchanged, ref skipped);
            }

            if (scaled == 0 && unchanged == 0 && skipped == 0)
                return;

            Main.Logger.Log($"PasteSync: 逐砖源 BPM {count} 条 → 缩放 {scaled} 个事件，无需缩放 {unchanged} 个，跳过 {skipped} 个");

            // 改过数值才需要再刷一次；RemakePath(true, false) 不会 ApplyEventsToFloors，改值要播放一次才生效
            if (scaled > 0)
                editor.ApplyEventsToFloors();
        }
    }
}
