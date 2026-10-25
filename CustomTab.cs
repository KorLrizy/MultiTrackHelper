using ADOFAI;
using MultiTrackHelper.Utils;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace MultiTrackHelper
{
    /// <summary>
    /// 本模组注入编辑器的那一个自定义标签页（事件类型 810）。
    /// 面板数据（properties）是 PropertyCollection.ToData() 的结果；
    /// 控件值变化后的联动逻辑也收在这里，Main 只负责创建、显隐与数据存取。
    /// </summary>
    internal sealed class CustomTab
    {
        /// <summary>标签页图标。</summary>
        internal Sprite icon;

        /// <summary>游戏的 LevelEventType 数值（810）。</summary>
        internal int type;

        /// <summary>事件类型名：GCS 字典的键、面板对象名、标签页标题本地化键的组成部分。</summary>
        internal string name;

        /// <summary>ToData() 的结果，交给游戏构建 PropertyInfo。</summary>
        internal List<Dictionary<string, object>> properties;

        /// <summary>切换面板后是否保留用户已填的值（本模组为 true）。</summary>
        internal bool saveSetting;

        internal Action onFocused;
        internal Action onUnFocused;

        internal CustomTab(int type, string name, Sprite icon, bool saveSetting, List<Dictionary<string, object>> properties)
        {
            this.type = type;
            this.name = name;
            this.icon = icon;
            this.saveSetting = saveSetting;
            this.properties = properties;
            onFocused = delegate { };
            onUnFocused = delegate { };
        }

        /// <summary>同一数值的类型化写法，省掉各处强转。</summary>
        internal LevelEventType EventType { get { return (LevelEventType)type; } }

        /// <summary>标签页被选中时调用。</summary>
        internal void Focused()
        {
            onFocused?.Invoke();
        }

        /// <summary>标签页被切走时调用。</summary>
        internal void Unfocused()
        {
            onUnFocused?.Invoke();
        }

        /// <summary>
        /// 面板任意控件值变化后的总入口。
        /// 返回值 false 表示"不要采纳这个新值"，调用方会把值回滚成旧值并刷新面板。
        /// 本模组所有联动都只在内存里改事件数据，因此恒返回 true。
        /// </summary>
        internal bool OnValueChanged(LevelEvent levelEvent, string key, object oldValue, object newValue)
        {
            if (levelEvent == null)
                return true;

            // 两个值都在本次变化之前读，后面的分支不依赖它们被改写
            TrackFeatures trackFeatures = levelEvent.Get<TrackFeatures>("TrackFeatures");
            TrackAnimation trackAnimation = levelEvent.Get<TrackAnimation>("TrackAnimation");

            // 开启“粘贴时同步速度”后，三种分布方式都强制集中式（面板上对应项也会被置灰）
            if (key == "syncSpeedOnPaste" && levelEvent.Get<bool>("syncSpeedOnPaste"))
                ForceCentralizedDistributions(levelEvent);

            if (key == "syncSpeedOnPaste" || key == "TrackDistribution"
                || key == "trackGenerationMode" || key == "syncMoveTrack")
            {
                Features.MultiTrackFeature.ApplyGenerationModeConstraints(levelEvent);
                levelEvent.UpdatePanel();
            }

            switch (key)
            {
                case "TrackFeatures":
                    ApplyFeatureVisibility(trackFeatures, trackAnimation);
                    Features.InsertBpmMultiplierFeature.ResetComputed();
                    if (trackFeatures == TrackFeatures.InsertBpmMultiplier
                        && Features.InsertBpmMultiplierFeature.CaptureEnabled(levelEvent))
                    {
                        Features.InsertBpmMultiplierFeature.SyncFromSelection(levelEvent, true);
                    }
                    break;
                case "TrackAnimation":
                    if (trackFeatures == TrackFeatures.CreateAnimation)
                        ApplyAnimationVisibility(trackAnimation);
                    break;
                case "realtimeCapture":
                    if (trackFeatures == TrackFeatures.InsertBpmMultiplier
                        && Features.InsertBpmMultiplierFeature.CaptureEnabled(levelEvent))
                    {
                        Features.InsertBpmMultiplierFeature.SyncFromSelection(levelEvent, true);
                    }
                    break;
            }

            if (Features.InsertBpmMultiplierFeature.IsInsertKey(key))
                Features.InsertBpmMultiplierFeature.OnChanged(levelEvent, key);

            Main.activeChilden();
            return true;
        }

        private static void ForceCentralizedDistributions(LevelEvent levelEvent)
        {
            levelEvent["TrackDistribution"] = TrackDistribution.Centralized;
            levelEvent["planetAnimationDistribution"] = TrackDistribution.Centralized;
            levelEvent["eventDistribution"] = TrackDistribution.Centralized;
        }

        /// <summary>生成类型切换后，决定面板上显示哪一组控件。</summary>
        private static void ApplyFeatureVisibility(TrackFeatures trackFeatures, TrackAnimation trackAnimation)
        {
            switch (trackFeatures)
            {
                case TrackFeatures.CreateTrack:
                    Main.SetActiveProperties(PrefabProperties.CreateTrackToActive);
                    break;
                case TrackFeatures.CreateAnimation:
                    ApplyAnimationVisibility(trackAnimation);
                    break;
                case TrackFeatures.TrackSizeChange:
                    Main.SetActiveProperties(PrefabProperties.TrackSizeChangeToActive);
                    break;
            }
        }

        /// <summary>出现/消失动画的控件组不同（消失组没有 revertTo），切过去时还要重置起始砖与透明度。</summary>
        private static void ApplyAnimationVisibility(TrackAnimation trackAnimation)
        {
            switch (trackAnimation)
            {
                case TrackAnimation.DisappearAnimation:
                    Main.SetActiveProperties(PrefabProperties.DisappearAnimationToActive);
                    Main.ApplyAnimationDefaults(trackAnimation);
                    break;
                case TrackAnimation.AppearAnimation:
                    Main.SetActiveProperties(PrefabProperties.AppearAnimationToActive);
                    Main.ApplyAnimationDefaults(trackAnimation);
                    break;
            }
        }
    }
}
