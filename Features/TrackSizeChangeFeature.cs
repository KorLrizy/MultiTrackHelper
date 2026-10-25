using ADOFAI;
using DG.Tweening;
using MultiTrackHelper.Utils;
using System;
using System.Collections.Generic;
using UnityEngine;
using TrackData = MultiTrackHelper.LevelAnalysis.TrackData;

namespace MultiTrackHelper.Features
{
    /// <summary>
    /// "轨道尺寸变化"（移植自 MappingHelper 的 TrackSizeChange 分支）：
    /// 在作用范围内逐砖按缓速曲线在"起始值→结束值"之间插值，写入对应的轨道设置事件——
    /// 位置轨道的大小(PositionTrack.scale)、位置轨道的透明度(PositionTrack.opacity)、
    /// 星球半径大小(ScaleRadius.scale)、缩放行星(ScalePlanets.scale)。
    /// 与上游一致：每块砖先清掉该砖上同类型的事件再重建，因此重复创建幂等；
    /// 被禁用的范围不写事件。
    /// </summary>
    internal static class TrackSizeChangeFeature
    {
        public static void Create(LevelEvent dataPanel)
        {
            try
            {
                CreateInternal(dataPanel);
            }
            catch (Exception e)
            {
                // 按钮点击里的异常不会在界面上有任何提示，这里显式弹出并记录
                Main.Logger.Log("轨道尺寸变化创建失败: " + e);
                Popup.ShowMessage(Main.Localizations.GetValue("mth.createFailed") + "\n" + e.Message);
            }
        }

        private static void CreateInternal(LevelEvent dataPanel)
        {
            if (ADOBase.lm.isOldLevel)
            {
                Popup.ShowMessage(Main.Localizations.GetValue("mth.theLevelVersionIsTooOld"));
                return;
            }

            bool applyPositionScale = IsEnabled(dataPanel, "positionTrackScale");
            bool applyPositionOpacity = IsEnabled(dataPanel, "positionTrackOpacity");
            bool applyRadiusScale = IsEnabled(dataPanel, "scaleRadiusScale");
            bool applyPlanetsScale = IsEnabled(dataPanel, "scalePlanetsScale");
            if (!applyPositionScale && !applyPositionOpacity && !applyRadiusScale && !applyPlanetsScale)
            {
                Popup.ShowMessage(Main.Localizations.GetValue("mth.nothingToChange"));
                return;
            }

            using (UndoScope.Begin(scnEditor.instance))
            {
                scnEditor editor = scnEditor.instance;
                if (editor == null) return;

                Tuple<int, int> range = LevelAnalysis.GetAffectedRange();
                int from = range.Item1;
                int to = range.Item2;
                if (from == -1 || to == -1) return;

                Dictionary<int, Dictionary<LevelEventType, List<LevelEvent>>> floorEvents = LevelAnalysis.GetFloorEvents();
                TrackData[] trackDatas = LevelAnalysis.GetTrackDatas(floorEvents);
                List<scrFloor> listFloors = scrLevelMaker.instance.listFloors;

                to = Mathf.Min(to, trackDatas.Length - 1);
                if (from > to) return;

                Tuple<float, float> positionScaleRange = GetRange(dataPanel, "positionTrackScale");
                Tuple<float, float> opacityRange = GetRange(dataPanel, "positionTrackOpacity");
                Tuple<float, float> radiusRange = GetRange(dataPanel, "scaleRadiusScale");
                Tuple<float, float> planetsRange = GetRange(dataPanel, "scalePlanetsScale");
                Ease ease = dataPanel.Get<Ease>("ease");

                // 位置轨道的大小与透明度共用一条事件：同一块砖挂两条 PositionTrack 会互相覆盖
                bool writePositionTrack = applyPositionScale || applyPositionOpacity;
                int span = to - from;

                for (int floor = from; floor <= to; floor++)
                {
                    float t = span > 0 ? (floor - from) / (float)span : 0f;
                    t = DOVirtual.EasedValue(0f, 1f, t, ease);

                    if (writePositionTrack)
                    {
                        RemoveEvents(editor, floorEvents, floor, LevelEventType.PositionTrack);
                        LevelEvent levelEvent = new LevelEvent(floor, LevelEventType.PositionTrack);
                        if (applyPositionScale)
                        {
                            levelEvent.disabled["scale"] = false;
                            levelEvent["scale"] = Mathf.LerpUnclamped(positionScaleRange.Item1, positionScaleRange.Item2, t);
                        }
                        if (applyPositionOpacity)
                        {
                            levelEvent.disabled["opacity"] = false;
                            levelEvent["opacity"] = Mathf.LerpUnclamped(opacityRange.Item1, opacityRange.Item2, t);
                        }
                        editor.events.Add(levelEvent);
                    }

                    if (applyRadiusScale)
                    {
                        RemoveEvents(editor, floorEvents, floor, LevelEventType.ScaleRadius);
                        LevelEvent levelEvent = new LevelEvent(floor, LevelEventType.ScaleRadius);
                        levelEvent["scale"] = Mathf.LerpUnclamped(radiusRange.Item1, radiusRange.Item2, t);
                        editor.events.Add(levelEvent);
                    }

                    if (applyPlanetsScale)
                    {
                        RemoveEvents(editor, floorEvents, floor, LevelEventType.ScalePlanets);
                        LevelEvent levelEvent = new LevelEvent(floor, LevelEventType.ScalePlanets);
                        levelEvent["duration"] = 0f;
                        levelEvent["targetPlanet"] = TargetPlanet.All;
                        levelEvent["scale"] = Mathf.LerpUnclamped(planetsRange.Item1, planetsRange.Item2, t);
                        editor.events.Add(levelEvent);
                    }
                }

                // 与原版 PasteTrackColor 一致：加完事件直接 ApplyEventsToFloors。
                // 不能用 RemakePath(true, false)：remakeLevel=false 时它连 ApplyEventsToFloors 都不调，要播放一次才刷新
                MultiTrackHelper.Utils.EditorRefresh.AfterWrite(editor);
                editor.DeselectFloors();
                editor.SelectFloor(listFloors[from]);
            }
        }

        /// <summary>
        /// 面板上该范围是否处于启用状态。游戏用 disabled 字典的反值表示启用
        /// （与游戏启用按钮的约定一致），键缺失时按未启用来处理。
        /// </summary>
        private static bool IsEnabled(LevelEvent dataPanel, string key)
        {
            return dataPanel.disabled.TryGetValue(key, out bool isDisabled) && !isDisabled;
        }

        private static Tuple<float, float> GetRange(LevelEvent dataPanel, string key)
        {
            return dataPanel.Get<Tuple<float, float>>(key) ?? Tuple.Create(100f, 100f);
        }

        /// <summary>清掉该砖上同类型的事件，保证重复创建不会叠加（与上游 removeEvents 等价）。</summary>
        private static void RemoveEvents(scnEditor editor, Dictionary<int, Dictionary<LevelEventType, List<LevelEvent>>> floorEvents, int floor, LevelEventType type)
        {
            if (!floorEvents.TryGetValue(floor, out Dictionary<LevelEventType, List<LevelEvent>> events)) return;
            if (!events.TryGetValue(type, out List<LevelEvent> targets)) return;

            foreach (LevelEvent target in targets)
                editor.events.Remove(target);
        }
    }
}
