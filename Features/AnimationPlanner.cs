using ADOFAI;
using DG.Tweening;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace MultiTrackHelper.Features
{
    /// <summary>
    /// “生成动画”的面板参数快照（T3.1）。把面板读取与事件生成拆开：
    /// 规划函数只吃这份快照 + 每砖时间 + 随机源，不需要 scnEditor / LevelEvent，
    /// 便于离线把同一份参数同时喂给旧实现和新实现做逐字段对拍。
    /// </summary>
    internal sealed class AnimationSettings
    {
        public TrackDistribution distribution;
        public TrackAnimation animation;
        /// <summary>起始砖偏移（面板 startTile 的砖号部分；旧实现只用 Item1）。</summary>
        public int startTileOffset;
        public float duration;
        public float angleOffset;
        public Ease ease;
        public float opacity;
        public float parallaxRevertTo;
        public float scaleRevertTo;
        public float positionXMin;
        public float positionXMax;
        public float positionYMin;
        public float positionYMax;
        public float rotationMin;
        public float rotationMax;
        public float parallaxMin;
        public float parallaxMax;
        public float scaleMin;
        public float scaleMax;
        public bool positionXDisabled;
        public bool positionYDisabled;
        public bool rotationDisabled;
        public bool parallaxDisabled;
        public bool parallaxRevertDisabled;
        public bool scaleDisabled;
        public bool scaleRevertDisabled;
        public bool opacityDisabled;

        public bool distributed => distribution == TrackDistribution.Distributed;

        /// <summary>从面板事件读一份快照。所有键名与旧 CreateAnimation 完全一致。</summary>
        public static AnimationSettings Read(LevelEvent dataPanel)
        {
            Tuple<float, float> xPosOffsetRange = dataPanel.Get<Tuple<float, float>>("xPosOffsetRange");
            Tuple<float, float> yPosOffsetRange = dataPanel.Get<Tuple<float, float>>("yPosOffsetRange");
            Tuple<float, float> rotationOffsetRange = dataPanel.Get<Tuple<float, float>>("rotationOffsetRange");
            Tuple<float, float> scaleRange = dataPanel.Get<Tuple<float, float>>("scaleRange");
            Tuple<float, float> parallaxRange = dataPanel.Get<Tuple<float, float>>("parallaxRange");

            AnimationSettings settings = new AnimationSettings();
            settings.distribution = dataPanel.Get<TrackDistribution>("eventDistribution");
            settings.animation = dataPanel.Get<TrackAnimation>("TrackAnimation");
            settings.startTileOffset = dataPanel.Get<Tuple<int, TileRelativeTo>>("startTile").Item1;
            settings.duration = dataPanel.Get<float>("duration");
            settings.angleOffset = dataPanel.Get<float>("angleOffset");
            settings.ease = dataPanel.Get<Ease>("ease");
            settings.opacity = dataPanel.Get<float>("opacity");
            settings.parallaxRevertTo = dataPanel.Get<float>("parallaxRevertTo");
            settings.scaleRevertTo = dataPanel.Get<float>("scaleRevertTo");
            settings.positionXMin = xPosOffsetRange.Item1;
            settings.positionXMax = xPosOffsetRange.Item2;
            settings.positionYMin = yPosOffsetRange.Item1;
            settings.positionYMax = yPosOffsetRange.Item2;
            settings.rotationMin = rotationOffsetRange.Item1;
            settings.rotationMax = rotationOffsetRange.Item2;
            settings.parallaxMin = parallaxRange.Item1;
            settings.parallaxMax = parallaxRange.Item2;
            settings.scaleMin = scaleRange.Item1;
            settings.scaleMax = scaleRange.Item2;
            settings.positionXDisabled = dataPanel.disabled["xPosOffsetRange"];
            settings.positionYDisabled = dataPanel.disabled["yPosOffsetRange"];
            settings.rotationDisabled = dataPanel.disabled["rotationOffsetRange"];
            settings.parallaxDisabled = dataPanel.disabled["parallaxRange"];
            settings.parallaxRevertDisabled = dataPanel.disabled["parallaxRevertTo"];
            settings.scaleDisabled = dataPanel.disabled["scaleRange"];
            settings.scaleRevertDisabled = dataPanel.disabled["scaleRevertTo"];
            settings.opacityDisabled = dataPanel.disabled["opacity"];
            return settings;
        }
    }

    /// <summary>一条待写入 editor.events 的动画事件（纯数据，不持有 LevelEvent）。</summary>
    internal sealed class AnimationEventData
    {
        public int floor;
        public string tag;
        public float duration;
        public float angleOffset;
        public Vector2 positionOffset;
        public float rotationOffset;
        public Vector2 scale;
        public Vector2 parallax;
        public float opacity;
        public bool hasEase;
        public Ease ease;
        public bool positionEnabled;
        public bool rotationEnabled;
        public bool scaleEnabled;
        public bool parallaxEnabled;
        public bool opacityEnabled;
    }

    /// <summary>
    /// 动画事件规划 + 输出（T3.1）。出现/消失共用同一套“起始值抽样”，
    /// 差异只体现在每条事件写哪些属性、时长与回退值上。
    /// </summary>
    internal static class MultiTrackAnimationPlanner
    {
        /// <summary>
        /// 纯规划入口：不碰编辑器，返回要写入的事件数据列表。
        /// 随机数调用顺序与旧实现对每一块参与动画的砖逐次一致：
        /// 位置 X（启用时）→ 位置 Y（启用时）→ 旋转 → 视差 → 缩放。
        /// 出现动画的第二条（回退）不抽随机；消失动画只有一条并抽上述 5 次。
        /// </summary>
        internal static List<AnimationEventData> BuildAnimationEvents(AnimationSettings settings, string tag,
            float[] arrivalTimes, float[] departureTimes, float fromBpm, int from, int to, RandomSource random)
        {
            List<AnimationEventData> events = new List<AnimationEventData>();
            if (settings == null || random == null || arrivalTimes == null || departureTimes == null)
                return events;
            if (from < 0 || to < from || from >= arrivalTimes.Length || from >= departureTimes.Length)
                return events;

            // 旧实现的调用方已把 to 夹到 trackDatas.Length-1，这里再做一次防御，避免离线桩喂短数组时越界
            to = Math.Min(to, Math.Min(arrivalTimes.Length, departureTimes.Length) - 1);

            for (int floor = from, i = 1; floor <= to; floor++, i++)
            {
                int tileIndex = i + settings.startTileOffset;
                if (settings.animation == TrackAnimation.DisappearAnimation)
                {
                    // 旧条件：i + startTile.Item1 > 0
                    if (tileIndex <= 0)
                        continue;
                    events.Add(BuildStartValues(settings, tag, floor, from, tileIndex,
                        arrivalTimes, departureTimes, fromBpm, random, true));
                    continue;
                }

                if (settings.animation != TrackAnimation.AppearAnimation)
                    continue;
                // 旧条件：floor <= to - (startTile.Item1 >= 0 ? startTile.Item1 : 0)
                if (floor > to - Math.Max(settings.startTileOffset, 0))
                    continue;

                AnimationEventData start = BuildStartValues(settings, tag, floor, from, tileIndex,
                    arrivalTimes, departureTimes, fromBpm, random, false);
                start.duration = 0f;
                start.opacity = 0f;
                start.hasEase = false;
                events.Add(start);

                // 回退事件：位置/旋转归零（面板禁用的轴保持 NaN=不动），缩放与视差回到面板回退值
                events.Add(new AnimationEventData
                {
                    floor = start.floor,
                    tag = start.tag,
                    duration = settings.duration,
                    angleOffset = start.angleOffset,
                    positionOffset = new Vector2(settings.positionXDisabled ? float.NaN : 0f,
                                                 settings.positionYDisabled ? float.NaN : 0f),
                    rotationOffset = 0f,
                    scale = new Vector2(settings.scaleRevertTo, settings.scaleRevertTo),
                    parallax = new Vector2(settings.parallaxRevertTo, settings.parallaxRevertTo),
                    opacity = settings.opacity,
                    hasEase = true,
                    ease = settings.ease,
                    positionEnabled = start.positionEnabled,
                    rotationEnabled = !settings.rotationDisabled,
                    scaleEnabled = !settings.scaleRevertDisabled,
                    parallaxEnabled = !settings.parallaxRevertDisabled,
                    opacityEnabled = !settings.opacityDisabled
                });
            }

            return events;
        }

        /// <summary>
        /// 起始值（消失动画的唯一一条 / 出现动画的瞬时快照）：角度轴、随机抽样与启用标记都在这里定，
        /// 出现与消失因此共用同一段抽样代码，不会因为改其中一支而让另一支的种子序列错位。
        /// </summary>
        private static AnimationEventData BuildStartValues(AnimationSettings settings, string tag, int floor, int from,
            int tileIndex, float[] arrivalTimes, float[] departureTimes, float fromBpm, RandomSource random, bool isDisappear)
        {
            float angleOffset = settings.angleOffset;
            if (!settings.distributed)
            {
                // 集中式：把"距锚点砖的时间差"折成锚点砖上的角度偏移。
                // 消失动画用上一砖的 departure，出现动画用本砖的 arrival，与旧实现一致。
                float time = isDisappear
                    ? departureTimes[Mathf.Max(floor - 1, 0)] - arrivalTimes[from]
                    : arrivalTimes[floor] - arrivalTimes[from];
                angleOffset += time / (60f / fromBpm) * 180;
            }

            Vector2 positionOffset = new Vector2(
                settings.positionXDisabled ? float.NaN : random.Range(settings.positionXMin, settings.positionXMax),
                settings.positionYDisabled ? float.NaN : random.Range(settings.positionYMin, settings.positionYMax));
            float rotationOffset = random.Range(settings.rotationMin, settings.rotationMax);
            float parallax = random.Range(settings.parallaxMin, settings.parallaxMax);
            float scale = random.Range(settings.scaleMin, settings.scaleMax);

            return new AnimationEventData
            {
                floor = settings.distributed ? floor : from,
                tag = $"{tag}{tileIndex}",
                duration = isDisappear ? settings.duration : 0f,
                angleOffset = angleOffset,
                positionOffset = positionOffset,
                rotationOffset = rotationOffset,
                scale = new Vector2(scale, scale),
                parallax = new Vector2(parallax, parallax),
                opacity = isDisappear ? settings.opacity : 0f,
                hasEase = isDisappear,
                ease = settings.ease,
                positionEnabled = !(settings.positionXDisabled && settings.positionYDisabled),
                rotationEnabled = !settings.rotationDisabled,
                scaleEnabled = !settings.scaleDisabled,
                parallaxEnabled = !settings.parallaxDisabled,
                opacityEnabled = !settings.opacityDisabled
            };
        }

        /// <summary>把规划结果写成 MoveDecorations 事件；这是唯一接触编辑器的一步。</summary>
        internal static void Emit(scnEditor editor, List<AnimationEventData> events)
        {
            if (editor?.events == null || events == null)
                return;

            foreach (AnimationEventData data in events)
            {
                if (data == null)
                    continue;

                LevelEvent levelEvent = new LevelEvent(data.floor, LevelEventType.MoveDecorations);
                // 动画事件不改动装饰自身的轴心偏移，否则会把"轴心偏移"生成方式留下的非零轴心清零
                levelEvent.disabled["pivotOffset"] = true;
                levelEvent.disabled["positionOffset"] = !data.positionEnabled;
                levelEvent.disabled["rotationOffset"] = !data.rotationEnabled;
                levelEvent.disabled["scale"] = !data.scaleEnabled;
                levelEvent.disabled["opacity"] = !data.opacityEnabled;
                levelEvent.disabled["parallax"] = !data.parallaxEnabled;
                levelEvent["tag"] = data.tag;
                levelEvent["duration"] = data.duration;
                levelEvent["angleOffset"] = data.angleOffset;
                levelEvent["positionOffset"] = data.positionOffset;
                levelEvent["rotationOffset"] = data.rotationOffset;
                levelEvent["scale"] = data.scale;
                levelEvent["parallax"] = data.parallax;
                levelEvent["opacity"] = data.opacity;
                // 出现动画的瞬时快照不写 ease（沿用事件默认值），只有带时长的两条写
                if (data.hasEase)
                    levelEvent["ease"] = data.ease;

                editor.events.Add(levelEvent);
            }
        }
    }
}
