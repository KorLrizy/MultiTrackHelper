using System.Runtime.CompilerServices;

namespace MultiTrackHelper.Utils
{
    /// <summary>
    /// 剪贴板条目的"源有效 BPM"登记表：纯内存、弱引用键，按内容身份存而不是按下标存。
    ///
    /// 为什么不能再用"全局两条列表"：游戏只有一个剪贴板槽，而撤销/重做会把槽里的内容换回旧的那批盒子。
    /// 旧实现（copiedEntries + copiedBpms 按下标一一对应）一旦被"复制 B"顶掉，A 的源 BPM 就没了，
    /// 撤销回到 A 的剪贴板后重做粘贴拿不到 BPM；BPM 本质上是"这条剪贴板内容"的属性，所以按身份存。
    ///
    /// 双身份（同一块砖从两条路进来拿到的是不同的对象，都要能查到）：
    /// · FloorData 盒子的引用：scnEditor.clipboard 里的装箱条目，粘贴多砖（PasteFloors）按它查；
    /// · FloorData.levelEventData 列表的引用：单砖粘贴与 PasteEvents 拿到的是 FloorData 的拆箱副本，
    ///   只有里面的列表引用还是同一个，所以同一个 BPM 再按列表登记一次。
    ///
    /// 生命周期：只在关闭模组时清（Main.StopMod → PasteSyncPatches.ResetAll）。编辑器重载、切换关卡都不清，
    /// 否则跨关卡粘贴拿不到源 BPM。键是弱引用，盒子被回收后条目自动消失，不会留下泄漏。
    ///
    /// 只做"存/查一个有限正 BPM"，不持有 LevelEvent、不做解析，也不判断该不该缩放（那些留在调用方）。
    /// </summary>
    internal static class ClipboardBpmStore
    {
        private sealed class BpmHolder
        {
            internal float bpm;
        }

        // 用可替换的字段而不是 ConditionalWeakTable.Clear()：net48 的 BCL 没有 Clear。
        private static ConditionalWeakTable<object, BpmHolder> byBox =
            new ConditionalWeakTable<object, BpmHolder>();

        private static ConditionalWeakTable<object, BpmHolder> byEventList =
            new ConditionalWeakTable<object, BpmHolder>();

        /// <summary>登记一个剪贴板盒子的源 BPM；非有限值或非正值不登记（宁可不缩放，也不写坏数据）。</summary>
        internal static void StoreBox(object box, float bpm)
        {
            Store(byBox, box, bpm);
        }

        /// <summary>按 FloorData.levelEventData 的列表身份登记同一个源 BPM。</summary>
        internal static void StoreEventList(object eventList, float bpm)
        {
            Store(byEventList, eventList, bpm);
        }

        /// <summary>按剪贴板盒子查源 BPM。</summary>
        internal static bool TryGetBox(object box, out float bpm)
        {
            return TryGet(byBox, box, out bpm);
        }

        /// <summary>按 FloorData.levelEventData 的列表身份查源 BPM。</summary>
        internal static bool TryGetEventList(object eventList, out float bpm)
        {
            return TryGet(byEventList, eventList, out bpm);
        }

        /// <summary>关闭模组时清空。不要放进 Awake：跨关卡粘贴要仍然认得那批盒子。</summary>
        internal static void Clear()
        {
            byBox = new ConditionalWeakTable<object, BpmHolder>();
            byEventList = new ConditionalWeakTable<object, BpmHolder>();
        }

        private static void Store(ConditionalWeakTable<object, BpmHolder> table, object key, float bpm)
        {
            if (key == null || float.IsNaN(bpm) || float.IsInfinity(bpm) || bpm <= 0f)
                return;

            // 同一个盒子被重新登记时以最新一次为准（ConditionalWeakTable.Add 不允许重复键）
            table.Remove(key);
            table.Add(key, new BpmHolder { bpm = bpm });
        }

        private static bool TryGet(ConditionalWeakTable<object, BpmHolder> table, object key, out float bpm)
        {
            bpm = 0f;
            if (key == null)
                return false;

            BpmHolder holder;
            if (!table.TryGetValue(key, out holder) || holder == null)
                return false;
            if (float.IsNaN(holder.bpm) || float.IsInfinity(holder.bpm) || holder.bpm <= 0f)
                return false;

            bpm = holder.bpm;
            return true;
        }
    }
}
