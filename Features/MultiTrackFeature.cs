using ADOFAI;
using DG.Tweening;
using MultiTrackHelper.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TrackData = MultiTrackHelper.LevelAnalysis.TrackData;

namespace MultiTrackHelper.Features
{
    /// <summary>
    /// 多轨道功能入口与实现（移植自 MappingHelper 的 MultipleTracks 分支）。
    /// CreateTrack：把作用范围内的每块地砖转成 AddObject 轨道装饰，
    /// 可选集中/分散布局、蓝红星球、深度递增、视差渐变。
    /// CreateAnimation：对带 tag 的轨道装饰批量生成 MoveDecorations 出现/消失动画。
    /// </summary>
    internal static class MultiTrackFeature
    {
        public static void Create()
        {
            try
            {
                CreateInternal();
            }
            catch (Exception e)
            {
                // 按钮点击里的异常不会在界面上有任何提示，这里显式弹出并记录
                Main.Logger.Log("创建失败: " + e);
                Popup.ShowMessage(Main.Localizations.GetValue("mth.createFailed") + "\n" + e.Message);
            }
        }

        private static void CreateInternal()
        {
            if (ADOBase.lm.isOldLevel)
            {
                Popup.ShowMessage(Main.Localizations.GetValue("mth.theLevelVersionIsTooOld"));
                return;
            }

            // “插入 BPM 倍频”有独立的选择范围与撤销作用域，单独处理
            if (scnEditor.instance != null)
            {
                LevelEvent insertPanel = Main.GetEvent((LevelEventType)Main.MultiTrack.type);
                if (insertPanel != null
                    && insertPanel.Get<TrackFeatures>("TrackFeatures") == TrackFeatures.InsertBpmMultiplier)
                {
                    InsertBpmMultiplierFeature.Create(insertPanel);
                    return;
                }

                // “轨道尺寸变化”有独立的参数与撤销作用域，单独处理
                if (insertPanel != null
                    && insertPanel.Get<TrackFeatures>("TrackFeatures") == TrackFeatures.TrackSizeChange)
                {
                    TrackSizeChangeFeature.Create(insertPanel);
                    return;
                }
            }

            scnEditor editor = scnEditor.instance;
            if (editor == null) return;

            LevelEvent dataPanel = Main.GetEvent((LevelEventType)Main.MultiTrack.type);
            if (dataPanel == null) return;

            // 标签按空格分词做匹配，含空白的标签无法还原，因此拒绝而不是静默 Trim
            string tag = dataPanel.Get<string>("tag");
            if (!string.IsNullOrEmpty(tag) && tag.Any(char.IsWhiteSpace))
            {
                Popup.ShowMessage(Main.Localizations.GetValue("mth.tagCannotContainWhitespace"));
                return;
            }

            using (UndoScope.Begin(editor))
            {
                // 粘贴时同步速度开启时，分布方式一律按集中式处理
                if (dataPanel.Get<bool>("syncSpeedOnPaste"))
                {
                    dataPanel["TrackDistribution"] = TrackDistribution.Centralized;
                    dataPanel["planetAnimationDistribution"] = TrackDistribution.Centralized;
                    dataPanel["eventDistribution"] = TrackDistribution.Centralized;
                }

                // 上面刚改成集中式、或用户没动过面板（onChange 从未触发）时，这里补一次收敛，避免绕过互斥
                if (ApplyGenerationModeConstraints(dataPanel))
                    dataPanel.UpdatePanel();

                Tuple<int, int> range = LevelAnalysis.GetAffectedRange();
                int affectTileRangeFrom = range.Item1;
                int affectTileRangeTo = range.Item2;
                if (affectTileRangeFrom == -1 || affectTileRangeTo == -1) return;

                TrackFeatures trackFeatures = dataPanel.Get<TrackFeatures>("TrackFeatures");
                bool eventsOnly = trackFeatures == TrackFeatures.CreateAnimation;

                // 生成动画允许 tag 留空：自动作用于"_MultiTrack"自动标签的假轨道
                if (eventsOnly && string.IsNullOrEmpty(tag))
                    tag = TagCleanup.ReservedPrefix;

                // 星球标签：输入非空时用输入值，否则沿用默认（主 tag 非空时保持 {tag}_BluePlanet 命名空间，避免多批星球互相干扰）
                string bluePlanetTag = ResolvePlanetTag(dataPanel.Get<string>("planetBlueTag"), tag, "_BluePlanet");
                string redPlanetTag = ResolvePlanetTag(dataPanel.Get<string>("planetRedTag"), tag, "_RedPlanet");

                if (dataPanel.Get<bool>("replaceSameTag"))
                    TagCleanup.RemovePrevious(editor, tag, bluePlanetTag, redPlanetTag,
                        eventsOnly ? CleanupScope.EventsOnly : CleanupScope.TracksAndEvents);

                Dictionary<int, Dictionary<LevelEventType, List<LevelEvent>>> floorEvents = LevelAnalysis.GetFloorEvents();
                TrackData[] trackDatas = LevelAnalysis.GetTrackDatas(floorEvents);
                List<scrFloor> listFloors = scrLevelMaker.instance.listFloors;
                RandomSource random = RandomSource.From(dataPanel);

                // 作用范围上界对齐实际可用的地砖数据
                affectTileRangeTo = Mathf.Min(affectTileRangeTo, trackDatas.Length - 1);
                if (affectTileRangeFrom > affectTileRangeTo) return;

                if (trackFeatures == TrackFeatures.CreateTrack)
                    CreateTrack(editor, dataPanel, floorEvents, trackDatas, listFloors, random, affectTileRangeFrom, affectTileRangeTo, tag, bluePlanetTag, redPlanetTag);
                else
                    CreateAnimation(editor, dataPanel, trackDatas, random, affectTileRangeFrom, affectTileRangeTo, tag);

                // RemakePath(true, false) skips ApplyEventsToFloors inside the game.
                // Apply directly: rebuild event buckets/icons and decoration preview without rebuilding geometry.
                MultiTrackHelper.Utils.EditorRefresh.AfterWrite(editor);
                if (trackFeatures == TrackFeatures.CreateAnimation)
                {
                    editor.DeselectFloors();
                    editor.SelectFloor(listFloors[affectTileRangeFrom]);
                }
            }
        }

        internal static bool ApplyGenerationModeConstraints(LevelEvent panel)
        {
            if (panel == null)
                return false;

            if (panel.Get<TrackDistribution>("TrackDistribution") != TrackDistribution.Centralized)
                return false;
            if (panel.Get<TrackGenerationMode>("trackGenerationMode") != TrackGenerationMode.PivotOffset)
                return false;
            if (!panel.Get<bool>("syncMoveTrack"))
                return false;

            panel["syncMoveTrack"] = false;
            return true;
        }

        private static string ResolvePlanetTag(string input, string tag, string defaultSuffix)
        {
            string trimmed = input?.Trim();
            if (!string.IsNullOrEmpty(trimmed))
                return trimmed;
            return string.IsNullOrEmpty(tag) ? defaultSuffix : $"{tag}{defaultSuffix}";
        }

        private static void CreateTrack(scnEditor editor, LevelEvent dataPanel, Dictionary<int, Dictionary<LevelEventType, List<LevelEvent>>> floorEvents, TrackData[] trackDatas, List<scrFloor> listFloors, RandomSource random, int from, int to, string tag, string bluePlanetTag, string redPlanetTag)
        {
            TrackDistribution trackDistribution = dataPanel.Get<TrackDistribution>("TrackDistribution");
            bool isCentralized = trackDistribution == TrackDistribution.Centralized;
            TrackGenerationMode trackGenerationMode = dataPanel.Get<TrackGenerationMode>("trackGenerationMode");
            MotionOutputMode outputMode = ReadMotionOutputMode(dataPanel);
            float trackRotationValue = dataPanel.Get<float>("trackRotation");
            float pivotTrackOffset = isCentralized ? trackRotationValue : 0;
            // "同步大小"：假轨道的 scale 跟随真轨道的星球半径大小(ScaleRadius)，
            // 位置则按 100% 半径来读，由装饰自身的 scale 再放大回去。
            bool syncSize = dataPanel.Get<bool>("syncSize");
            // "同步透明度"：把真轨道透明度(PositionTrack.opacity)写到假轨道装饰上——改 trackColor 的
            // A 通道，或写 AddObject 自带的 trackOpacity。
            bool syncOpacity = dataPanel.Get<bool>("syncOpacity");
            OpacitySyncType opacitySyncType = dataPanel.Get<OpacitySyncType>("opacitySyncType");
            // "同步移动轨道"：把真轨道上的 MoveTrack 事件转成假轨道的 MoveDecorations
            bool syncMoveTrack = dataPanel.Get<bool>("syncMoveTrack");
            // "同步轨道风格"：假轨道初值取本砖的 initialTrackStyle，真轨道上的 RecolorTrack 风格另补 SetObject
            bool syncStyle = dataPanel.Get<bool>("syncStyle");
            int trackCount = to - from + 1;

            bool usePlanet = dataPanel.Get<bool>("usePlanet");
            bool changeParallax = dataPanel.Get<bool>("changeParallax");
            Tuple<float, float> parallaxChange = dataPanel.Get<Tuple<float, float>>("parallaxChange");

            // 深度：开启深度递增时按初始深度/递增值逐块递增；否则以自定义深度为基准
            // 沿路径逐块 +1（后砖压前砖，与真轨道一致），星球置于整批轨道之上
            bool useIncreasingDepth = dataPanel.Get<bool>("useIncreasingDepth");
            int baseDepth = dataPanel.Get<int>("depth");
            Vector2 genOffset = dataPanel.Get<Vector2>("generationPosition");
            int planetDepth = useIncreasingDepth ? dataPanel.Get<int>("initialDepth") - 1 : baseDepth;

            // 星球粘性跟随与轨道副本同步独立。轴心模式禁止复制轨道移动，不能连带停用星球跟随。
            // 两条链路仍共用一次派发快照；是否烘焙由关卡/逐砖 stickToFloors 决定。
            List<MoveTrackDispatch> moveTrackDispatches = (syncMoveTrack || usePlanet)
                ? CollectMoveTrackDispatches(editor, listFloors, from, to, isCentralized)
                : null;

            // 星球运动只规划一次：每砖两颗星球的到达/离开姿态与时间轴都在 PlanetMotionPlan 里，
            // 输出事件与粘性方块烘焙都吃这一份规格，不再"先写事件再读回来当曲线"
            PlanetMotionPlan planetMotion = null;
            if (usePlanet)
            {
                CreatePlanets(editor, dataPanel, from, pivotTrackOffset, tag, bluePlanetTag, redPlanetTag, planetDepth, genOffset);
                PlanetMotionSettings planetMotionSettings = PlanetMotionSettings.Read(dataPanel, bluePlanetTag, redPlanetTag);
                planetMotionSettings.outputMode = outputMode;
                planetMotion = PlanetMotionPlanner.Build(planetMotionSettings, trackDatas, listFloors,
                    editor.levelData.bpm, from, to, pivotTrackOffset);
                if (planetMotion.approximations.Count > 0)
                    Main.Logger?.Log("PlanetMotion: " + string.Join("；", planetMotion.approximations.OrderBy(value => value)));
                if (from == 0 && planetMotionSettings.firstFloorCountdownBeats > 0f)
                    Main.Logger?.Log("PlanetMotion: 首砖星球相位含倒计时，轨道几何角保持不变；请真机检查开局视觉");
            }

            int trackDepth = dataPanel.Get<int>("initialDepth");
            for (int floor = from, i = 1; floor <= to; floor++, i++)
            {
                Vector2 curFloorPos = trackDatas[floor].position - trackDatas[from].position;

                LevelEvent levelEvent = new LevelEvent(isCentralized ? from : floor, LevelEventType.AddObject);
                levelEvent["relativeTo"] = DecPlacementType.Tile;
                levelEvent["position"] = genOffset;
                if (listFloors[floor].midSpin)
                {
                    levelEvent["trackType"] = FloorDecorationType.Midspin;
                }
                else
                {
                    levelEvent["trackAngle"] = trackDatas[floor].geometricAngle;
                }

                float trackRotation = listFloors[floor].midSpin
                    ? trackDatas[floor].head
                    : TrackGeometry.TrackRotation(trackDatas[floor].tail, trackDatas[floor].head, listFloors[floor].isCCW, trackDatas[floor].geometricAngle);
                levelEvent["rotation"] = trackRotation + trackDatas[floor].rotation;

                // 单砖标签：MoveDecorations 靠它定位这一块假轨道
                string tileTag = ResolveTileTag(tag, i);
                if (!string.IsNullOrEmpty(tag))
                {
                    levelEvent["tag"] = $"{tag} {tileTag}";
                }
                else
                {
                    // 主标签留空时打保留前缀的自动标签，幂等重建才能找到上一批轨道
                    levelEvent["tag"] = tileTag;
                }

                // 深度：面板"深度"输入（默认 0），本批所有轨道与星球统一取该值。
                // 游戏语义：正值在真轨道之后，负值在前。开启深度递增时由初始深度/递增值决定。
                if (useIncreasingDepth)
                {
                    levelEvent["depth"] = trackDepth;
                    trackDepth += dataPanel.Get<int>("increasingValue");
                }
                else
                {
                    levelEvent["depth"] = baseDepth;
                }

                if (isCentralized)
                {
                    // 半径放大后地砖世界间距同步放大，这里折回 100% 半径的数值，
                    // 再由下面的 scale 放大回去，否则 pivotOffset 会随半径成倍偏移
                    Vector2 pivotPos = syncSize
                        ? LevelAnalysis.NormalizeToRadius100(curFloorPos, trackDatas[floor].radiusScale)
                        : curFloorPos;
                    levelEvent["pivotOffset"] = LevelAnalysis.GetPivotOffset(pivotPos, trackRotation + trackDatas[floor].rotation);
                }

                ApplyTrackIcon(levelEvent, listFloors[floor], trackRotation);

                if (isCentralized) levelEvent["rotation"] = (float)levelEvent["rotation"] + trackRotationValue;

                if (changeParallax)
                {
                    // 修复：只有一块轨道时插值系数恒为 0，避免除零得到 NaN
                    float t = trackCount > 1 ? (i - 1) / (float)(trackCount - 1) : 0f;
                    float parallaxValue = Mathf.Lerp(parallaxChange.Item1, parallaxChange.Item2, t);
                    // 同步大小时以真轨道大小为基准做视差缩小，否则维持原来的 100%
                    float baseScale = syncSize ? trackDatas[floor].radiusScale : 100f;
                    float scaleValue = baseScale - parallaxValue;
                    levelEvent["scale"] = new Vector2(scaleValue, scaleValue);
                    levelEvent["parallax"] = new Vector2(parallaxValue, parallaxValue);
                }
                else if (syncSize)
                {
                    float trackScale = trackDatas[floor].radiusScale;
                    levelEvent["scale"] = new Vector2(trackScale, trackScale);
                }

                if (syncOpacity)
                {
                    float trackOpacity = trackDatas[floor].trackOpacity;
                    if (opacitySyncType == OpacitySyncType.Decoration)
                    {
                        // "对象装饰本身的透明度"：AddObject 的 trackOpacity（对象面板里的"透明度"字段，单位 %）
                        levelEvent["trackOpacity"] = trackOpacity;
                    }
                    else
                    {
                        // "轨道主色调的透明度"：改轨道主色 trackColor 的 A 通道（#RRGGBBAA）
                        levelEvent["trackColor"] = WithAlpha(levelEvent.GetString("trackColor"), trackOpacity);
                    }
                }

                if (syncStyle)
                    levelEvent["trackStyle"] = listFloors[floor].initialTrackStyle;

                // 折入位置后旋转和缩放围绕本砖中心；保留偏移时则围绕共用轴心。
                if (isCentralized && trackGenerationMode == TrackGenerationMode.Position)
                {
                    float decorationRotation = levelEvent.Get<float>("rotation");
                    Vector2 decorationScale = levelEvent.Get<Vector2>("scale") / 100f;
                    Vector2 pivotOffset = levelEvent.Get<Vector2>("pivotOffset");
                    if (pivotOffset != Vector2.zero
                        && IsUsable(pivotOffset.x) && IsUsable(pivotOffset.y) && IsUsable(decorationRotation))
                    {
                        // GetPivotOffset 做的是 -angle 的旋转，传 -rotation 即得 +rotation 的旋转
                        Vector2 baked = LevelAnalysis.GetPivotOffset(
                            new Vector2(pivotOffset.x * decorationScale.x, pivotOffset.y * decorationScale.y), -decorationRotation);
                        if (IsUsable(baked.x) && IsUsable(baked.y))
                        {
                            levelEvent["position"] = levelEvent.Get<Vector2>("position") + baked;
                            levelEvent["pivotOffset"] = Vector2.zero;
                        }
                    }
                }

                editor.levelData.decorations.Add(levelEvent);
                scrDecorationManager.instance.CreateDecoration(levelEvent, out _, -1);
            }

            // 粘性方块下真轨道移动会带着星球走：把"基础星球曲线 + 相位砖位移/旋转"复合成刚体姿态，
            // 烘成逐段 Linear 的 MoveDecorations 并替换掉本轮生成的星球位置/旋转事件。
            // 烘焙只产出事件列表、不直接写编辑器；写入顺序仍排在基础星球事件之后，保持旧的登记顺序。
            // 放在假轨道副本之前：两者写的是不同对象的属性，运行期游戏会按触发时刻重排，这里只保证登记顺序稳定
            HashSet<string> bakedPlanetTags = null;
            List<LevelEvent> bakedPlanetEvents = null;
            if (usePlanet && planetMotion != null)
            {
                bakedPlanetEvents = BakePlanetTrackMotion(editor, listFloors, from, to, pivotTrackOffset, genOffset,
                    bluePlanetTag, redPlanetTag, planetMotion, moveTrackDispatches, out bakedPlanetTags, outputMode);
            }

            if (planetMotion != null)
                PlanetMotionPlanner.Emit(editor, planetMotion, bakedPlanetTags);

            if (bakedPlanetEvents != null)
            {
                foreach (LevelEvent bakedEvent in bakedPlanetEvents)
                    editor.events.Add(bakedEvent);
            }

            if (syncMoveTrack)
            {
                int before = editor.events.Count;
                CopyMoveTracks(editor, moveTrackDispatches, from, tag);
                if (outputMode == MotionOutputMode.Simple)
                {
                    List<LevelEvent> copied = editor.events.Skip(before).ToList();
                    MergeEquivalentMotionTargets(copied);
                    editor.events.RemoveRange(before, editor.events.Count - before);
                    editor.events.AddRange(copied);
                }
            }

            // 与"同步移动轨道"各自独立：不跟随移动时风格变化照样要搬
            if (syncStyle)
                CopyTrackStyles(editor, listFloors, from, to, tag, isCentralized);
        }

        /// <summary>单砖标签：假轨道装饰自身带的 tag，也是动画事件定位用的 tag。</summary>
        private static string ResolveTileTag(string tag, int index)
        {
            return string.IsNullOrEmpty(tag) ? $"{TagCleanup.ReservedPrefix}{index}" : $"{tag}{index}";
        }

        /// <summary>
        /// 把作用范围内真轨道上的 MoveTrack 转成假轨道的 MoveDecorations：调用方先用 CollectMoveTrackDispatches
        /// 按原游戏口径把 RepeatEvents/条件/撞击展开成一份"派发计划"（每次展开的承载砖、触发时刻、
        /// 换算到目标砖后的 angleOffset/duration、受影响砖集合都算好，并与星球跟随烘焙共用同一份），这里逐砖落成事件。
        /// 集中式布局的自动副本统一挂在锚点砖上：运行期游戏按 (触发时刻, 砖) 给效果列表排序登记，
        /// 同刻的先后仍按登记顺序，所以这里要按计划排序后再追加；外部触发（撞击/条件）的副本猜不到触发时刻，
        /// 留在源砖/承载砖上沿用原标签，由游戏自己的条件与 Repeat 逻辑展开，不参与排序。
        /// 位移/旋转/大小/透明度都按"绝对目标"原样搬（游戏里都是 startPos+offset 的重扫语义），不做逐时刻累加；
        /// NaN 轴表示该轴不动，原样保留，非有限值（Infinity）才丢掉该轴/该属性。
        /// </summary>
        private static void CopyMoveTracks(scnEditor editor, List<MoveTrackDispatch> dispatches, int from, string tag)
        {
            if (dispatches == null || dispatches.Count == 0)
                return;

            // 顺序必须还原原版的 (触发时刻, 原承载砖, 原事件序号, Repeat 第几次)：
            // 同属性上一条会被下一条 Kill(complete:true)，先后不能乱
            foreach (MoveTrackDispatch dispatch in dispatches.Where(d => !d.external)
                         .OrderBy(d => d.startTime)
                         .ThenBy(d => d.originFloor)
                         .ThenBy(d => d.sourceIndex)
                         .ThenBy(d => d.repeatIndex))
            {
                EmitMoveTrackCopy(editor, dispatch, from, tag);
            }

            int externalCount = 0;
            // 外部触发：按源砖 + 原事件顺序 + Repeat 次数追加，与猜测的触发时刻无关
            foreach (MoveTrackDispatch dispatch in dispatches.Where(d => d.external)
                         .OrderBy(d => d.sourceFloor)
                         .ThenBy(d => d.sourceIndex)
                         .ThenBy(d => d.repeatIndex))
            {
                EmitMoveTrackCopy(editor, dispatch, from, tag);
                externalCount++;
            }

            if (externalCount > 0)
                Main.Logger.Log($"CopyMoveTracks: {externalCount} 条外部触发（撞击/条件）的 MoveTrack 副本已按原标签生成，" +
                                "其触发时刻无法静态预判（星球跟随只能覆盖自动触发的部分）");
        }

        /// <summary>一条 MoveTrack（含 RepeatEvents 展开后的每一次）落到某个承载砖上的生成计划。</summary>
        private sealed class MoveTrackDispatch
        {
            public LevelEvent source;
            /// <summary>源 MoveTrack 在原 editor.events 里的下标，用于还原原相对顺序。</summary>
            public int sourceIndex;
            /// <summary>RepeatEvents 展开的第几次（0 = 本体）。</summary>
            public int repeatIndex;
            /// <summary>原 MoveTrack 这一次实际作用/挂接的砖（executeOnCurrentFloor 时是重复落点砖）。</summary>
            public int originFloor;
            /// <summary>MoveTrack 自身所在的砖。</summary>
            public int sourceFloor;
            /// <summary>生成事件挂接的砖：集中式=锚点砖，外部触发=源砖/承载砖。</summary>
            public int eventFloor;
            /// <summary>原事件的实际触发时刻（秒，按砖的 entryTime 口径）。外部触发的只做记录、不参与排序。</summary>
            public double startTime;
            /// <summary>原事件的绝对时长（秒）。</summary>
            public double durationSeconds;
            /// <summary>换算到 eventFloor 的 angleOffset。</summary>
            public float angleOffset;
            /// <summary>换算到 eventFloor 的 duration。</summary>
            public float duration;
            public string eventTag;
            /// <summary>撞击/条件等外部触发，不能按 startTime 静态排进自动序列。</summary>
            public bool external;
            /// <summary>本次展开受影响的砖（已夹在 from..to、升序去重），逐砖各挂一条副本。</summary>
            public int[] targetFloors;
        }

        /// <summary>
        /// 扫描原事件表，按游戏口径建出 RepeatEvents（按砖×标签，9 槽条件表）两张表与保留标签集合。
        /// "同步轨道风格"与"同步移动轨道"共用同一份展开判定，避免两条链路对同一个 Repeat 理解不一致。
        /// </summary>
        private static void BuildEventTriggers(LevelEvent[] snapshot,
            out Dictionary<int, Dictionary<string, Tuple<int, float, bool, int>>> repeats,
            out Dictionary<int, string[]> conditionalTags,
            out HashSet<string> reservedTags)
        {
            char[] space = { ' ' };
            repeats = new Dictionary<int, Dictionary<string, Tuple<int, float, bool, int>>>();
            conditionalTags = new Dictionary<int, string[]>();
            reservedTags = new HashSet<string>(StringComparer.Ordinal);
            foreach (LevelEvent levelEvent in snapshot)
            {
                if (!levelEvent.active)
                    continue;

                if (levelEvent.eventType == LevelEventType.RepeatEvents)
                {
                    if (!repeats.TryGetValue(levelEvent.floor, out Dictionary<string, Tuple<int, float, bool, int>> tokens))
                    {
                        tokens = new Dictionary<string, Tuple<int, float, bool, int>>();
                        repeats[levelEvent.floor] = tokens;
                    }

                    bool isBeat = levelEvent.Get<RepeatType>("repeatType") == RepeatType.Beat;
                    Tuple<int, float, bool, int> setting = Tuple.Create(
                        levelEvent.GetInt(isBeat ? "repetitions" : "floorCount"),
                        isBeat ? levelEvent.GetFloat("interval") : -1f,
                        levelEvent.GetBool("executeOnCurrentFloor"),
                        levelEvent.GetInt("gapLength"));
                    foreach (string token in (levelEvent.GetString("tag") ?? "").Split(space, StringSplitOptions.None))
                    {
                        tokens[token] = setting;
                        reservedTags.Add(token);
                    }
                    continue;
                }

                if (levelEvent.eventType != LevelEventType.SetConditionalEvents)
                    continue;

                conditionalTags[levelEvent.floor] = new string[9]
                {
                    levelEvent.GetString("perfectTag"),
                    levelEvent.GetString("earlyPerfectTag").NullIfEmptyConditionalTag() ?? levelEvent.GetString("hitTag"),
                    levelEvent.GetString("latePerfectTag").NullIfEmptyConditionalTag() ?? levelEvent.GetString("hitTag"),
                    levelEvent.GetString("veryEarlyTag").NullIfEmptyConditionalTag() ?? levelEvent.GetString("barelyTag"),
                    levelEvent.GetString("veryLateTag").NullIfEmptyConditionalTag() ?? levelEvent.GetString("barelyTag"),
                    levelEvent.GetString("tooEarlyTag").NullIfEmptyConditionalTag() ?? levelEvent.GetString("missTag"),
                    levelEvent.GetString("tooLateTag").NullIfEmptyConditionalTag() ?? levelEvent.GetString("missTag"),
                    levelEvent.GetString("lossTag"),
                    levelEvent.GetString("onCheckpointTag")
                };
            }

            foreach (string[] conditionalSlots in conditionalTags.Values)
            {
                foreach (string slot in conditionalSlots)
                {
                    if (!string.IsNullOrEmpty(slot) && !slot.IsNoneConditionalTag())
                        reservedTags.Add(slot);
                }
            }
        }

        /// <summary>
        /// 收集本批 from..to 内所有真轨道 MoveTrack 的生成计划（只收会动的、砖号合法的活跃事件）。
        /// 口径与 scnGame 的展开一致：RepeatEvents 按 eventTag 在同砖查表，n=0..repetitions，
        /// 间隔型用 interval·n·180° 的角度偏移、其余用落点砖与原砖的 entryBeat 差；
        /// executeOnCurrentFloor 时承载砖（以及按 ThisTile 解析的受影响范围）随之平移。
        /// </summary>
        private static List<MoveTrackDispatch> CollectMoveTrackDispatches(scnEditor editor, List<scrFloor> listFloors,
            int from, int to, bool isCentralized)
        {
            List<MoveTrackDispatch> dispatches = new List<MoveTrackDispatch>();
            int lastFloor = listFloors.Count - 1;
            if (editor?.levelData == null || from < 0 || to < from || to > lastFloor)
                return dispatches;

            float levelBpm = editor.levelData.bpm;
            // 快照保留原始下标：原游戏对同砖事件就是按 events 列表顺序登记的
            LevelEvent[] snapshot = editor.events.ToArray();
            BuildEventTriggers(snapshot, out Dictionary<int, Dictionary<string, Tuple<int, float, bool, int>>> repeats,
                out Dictionary<int, string[]> conditionalTags, out HashSet<string> reservedTags);

            HashSet<string> manualTags = TagCleanup.CollectHitboxEventTags(editor.levelData.decorations);
            reservedTags.UnionWith(manualTags);
            // WHY：自动副本需要一个没人占用、也不会被空标签 Repeat/条件二次展开的新事件标签；
            // 外部触发的副本反过来必须沿用原标签，才保住"撞击/判定才生效"的语义。
            string generatedEventTag = NewGeneratedEventTag(reservedTags);
            char[] space = { ' ' };

            for (int sourceIndex = 0; sourceIndex < snapshot.Length; sourceIndex++)
            {
                LevelEvent source = snapshot[sourceIndex];
                // 直接扫 editor.events 而不是 GetFloorEvents：这里要保留原始下标（事件顺序影响同砖登记顺序），
                // 并且要保留挂在第 0 块砖上的 MoveTrack
                if (source == null || !source.active || source.eventType != LevelEventType.MoveTrack)
                    continue;

                int sourceFloor = source.floor;
                if (sourceFloor < 0 || sourceFloor > lastFloor)
                    continue;
                if (!HasMoveTrackPayload(source))
                    continue;

                float sourceAngleOffset = source.GetFloat("angleOffset");
                float sourceDuration = source.GetFloat("duration");
                if (!IsUsable(sourceAngleOffset) || !IsUsable(sourceDuration))
                    continue;

                string sourceEventTag = source.GetString("eventTag");
                string[] sourceTokens = (sourceEventTag ?? "").Split(space, StringSplitOptions.None);
                bool isManual = TagCleanup.HasManualHitboxTag(source, manualTags);
                bool isConditional = IsConditionalSource(source, conditionalTags);

                int repetitions = 0;
                float interval = 0f;
                bool executeOnCurrentFloor = false;
                int repeatGap = 1;
                bool matchedRepeat = false;
                if (sourceEventTag != null
                    && repeats.TryGetValue(sourceFloor, out Dictionary<string, Tuple<int, float, bool, int>> sourceRepeats))
                {
                    foreach (string token in sourceTokens)
                    {
                        if (!sourceRepeats.TryGetValue(token, out Tuple<int, float, bool, int> matched))
                            continue;
                        matchedRepeat = true;
                        repetitions = matched.Item1;
                        interval = matched.Item2;
                        executeOnCurrentFloor = matched.Item3;
                        repeatGap = matched.Item4;
                        break;
                    }
                }

                if (isConditional || (isManual && !matchedRepeat))
                {
                    // 条件/纯撞击：副本挂回源砖、沿用源 angleOffset/duration/原 eventTag，
                    // 同砖的 Repeat 与条件展开由游戏自己再做一遍，这里不 flatten 也不改标签。
                    AddDispatchAtSourceFloor(dispatches, listFloors, source, sourceIndex, sourceFloor, lastFloor, from, to,
                        levelBpm, sourceEventTag);
                    continue;
                }

                int manualCarrierFloor = -1;
                if (isManual)
                {
                    manualCarrierFloor = FindManualEventCarrier(listFloors, from, sourceFloor, sourceTokens,
                        sourceEventTag, repeats, conditionalTags);
                    if (manualCarrierFloor < 0)
                    {
                        Main.Logger.Log($"CollectMoveTrackDispatches: 砖 {sourceFloor} 的手动触发移动含重复且无安全承载砖，未同步");
                        continue;
                    }
                }

                scrFloor srcFloor = listFloors[sourceFloor];
                bool intervalBased = interval > 0f;
                for (long n = 0; n <= repetitions; n++)
                {
                    long repeatFloorIndex = intervalBased ? srcFloor.seqID : (long)srcFloor.seqID + n * repeatGap;
                    if (repeatFloorIndex < 0 || repeatFloorIndex >= listFloors.Count)
                        break;
                    scrFloor repeatFloor = listFloors[(int)repeatFloorIndex];
                    int originFloor = executeOnCurrentFloor ? (int)repeatFloorIndex : sourceFloor;
                    scrFloor origin = listFloors[originFloor];

                    double repeatOffset = intervalBased
                        ? (double)(interval * (float)n * 180f)
                        : (executeOnCurrentFloor ? 0.0 : (repeatFloor.entryBeat - srcFloor.entryBeat) * 180.0);
                    double triggeredAngle = (double)sourceAngleOffset + repeatOffset;

                    // 原事件的实际时长/触发时刻都用原承载砖的 crotchet 口径算
                    float originEffBpm = levelBpm * origin.speed;
                    if (!IsUsable(originEffBpm) || originEffBpm <= 0f)
                        continue;
                    double startTime = origin.entryTime + triggeredAngle / (3.0 * (double)originEffBpm);
                    double durationSeconds = (double)sourceDuration * 60.0 / (double)originEffBpm;
                    if (double.IsNaN(startTime) || double.IsInfinity(startTime)
                        || double.IsNaN(durationSeconds) || double.IsInfinity(durationSeconds))
                        continue;

                    int[] targetFloors = ResolveDispatchTargets(source, originFloor, lastFloor, from, to);
                    if (targetFloors == null)
                        continue;

                    if (manualCarrierFloor >= 0)
                    {
                        // 撞击延迟是 60/关卡BPM × 承载砖 speed × AO/180（与自动的除以 speed 相反），
                        // 换砖只能按速度反比折算角度，时长则按正比折算才能保住绝对秒数
                        scrFloor carrier = listFloors[manualCarrierFloor];
                        float carrierEffBpm = levelBpm * carrier.speed;
                        if (!IsUsable(carrierEffBpm) || carrierEffBpm <= 0f)
                            continue;
                        double manualAngle = triggeredAngle * ((double)origin.speed / (double)carrier.speed);
                        double manualDuration = (double)sourceDuration * (double)carrierEffBpm / (double)originEffBpm;
                        if (double.IsNaN(manualAngle) || double.IsInfinity(manualAngle)
                            || double.IsNaN(manualDuration) || double.IsInfinity(manualDuration))
                            continue;
                        float manualAngleOffset = (float)manualAngle;
                        float manualDurationValue = (float)manualDuration;
                        if (!IsUsable(manualAngleOffset) || !IsUsable(manualDurationValue))
                            continue;

                        dispatches.Add(new MoveTrackDispatch
                        {
                            source = source,
                            sourceIndex = sourceIndex,
                            repeatIndex = (int)n,
                            originFloor = originFloor,
                            sourceFloor = sourceFloor,
                            eventFloor = manualCarrierFloor,
                            startTime = startTime,
                            durationSeconds = durationSeconds,
                            angleOffset = manualAngleOffset,
                            duration = manualDurationValue,
                            eventTag = sourceEventTag,
                            external = true,
                            targetFloors = targetFloors
                        });
                        continue;
                    }

                    int eventFloor = isCentralized ? from : originFloor;
                    float newAngleOffset = sourceAngleOffset;
                    float newDuration = sourceDuration;
                    if (eventFloor != originFloor)
                    {
                        // 换砖：时刻按目标砖的 crotchet 反算成 angleOffset，时长按目标砖 crotchet 反算成时长单位，
                        // 绝对秒数不变（集中在锚点砖上时走的就是这一支）
                        scrFloor dest = listFloors[eventFloor];
                        float destEffBpm = levelBpm * dest.speed;
                        if (!IsUsable(destEffBpm) || destEffBpm <= 0f)
                            continue;
                        double convertedAngle = (startTime - dest.entryTime) * (double)destEffBpm * 3.0;
                        double convertedDuration = durationSeconds * (double)destEffBpm / 60.0;
                        if (double.IsNaN(convertedAngle) || double.IsInfinity(convertedAngle)
                            || double.IsNaN(convertedDuration) || double.IsInfinity(convertedDuration))
                            continue;
                        newAngleOffset = (float)convertedAngle;
                        newDuration = (float)convertedDuration;
                        if (!IsUsable(newAngleOffset) || !IsUsable(newDuration))
                            continue;
                    }

                    dispatches.Add(new MoveTrackDispatch
                    {
                        source = source,
                        sourceIndex = sourceIndex,
                        repeatIndex = (int)n,
                        originFloor = originFloor,
                        sourceFloor = sourceFloor,
                        eventFloor = eventFloor,
                        startTime = startTime,
                        durationSeconds = durationSeconds,
                        angleOffset = newAngleOffset,
                        duration = newDuration,
                        eventTag = generatedEventTag,
                        external = false,
                        targetFloors = targetFloors
                    });
                }

                // WHY 只在收集阶段提示：原 MoveTrack 会连自由漫游区里生成的格子一起移动，
                // 那些格子由 scrLevelMaker.MakeFreeroamGrid 在游戏开始时才创建、不在 listFloors 里，
                // 编辑器阶段 scrFloor.freeroamArea 为 null，这里没有可靠 API 可枚举，只能如实提示。
                if (TouchesFreeroam(listFloors, source, sourceFloor, lastFloor, from, to))
                    Main.Logger.Log($"CollectMoveTrackDispatches: 砖 {sourceFloor} 的 MoveTrack 覆盖自由漫游区，" +
                                    "编辑器内拿不到该区域生成的格子，这部分移动未同步");
            }

            return dispatches;
        }

        /// <summary>条件/纯撞击触发的 MoveTrack：原样留在源砖，沿用源 angleOffset 与源时长（同砖同 crotchet，无需换算）。</summary>
        private static void AddDispatchAtSourceFloor(List<MoveTrackDispatch> dispatches, List<scrFloor> listFloors,
            LevelEvent source, int sourceIndex, int sourceFloor, int lastFloor, int from, int to, float levelBpm, string sourceEventTag)
        {
            int[] targetFloors = ResolveDispatchTargets(source, sourceFloor, lastFloor, from, to);
            if (targetFloors == null)
                return;

            float sourceAngleOffset = source.GetFloat("angleOffset");
            float sourceDuration = source.GetFloat("duration");
            scrFloor origin = listFloors[sourceFloor];

            // startTime/durationSeconds 只作记录：外部触发由游戏按撞击/条件时刻执行，排序也不看它们
            double startTime = double.NaN;
            double durationSeconds = double.NaN;
            float originEffBpm = levelBpm * origin.speed;
            if (IsUsable(originEffBpm) && originEffBpm > 0f)
            {
                startTime = origin.entryTime + (double)sourceAngleOffset / (3.0 * (double)originEffBpm);
                durationSeconds = (double)sourceDuration * 60.0 / (double)originEffBpm;
            }

            dispatches.Add(new MoveTrackDispatch
            {
                source = source,
                sourceIndex = sourceIndex,
                repeatIndex = 0,
                originFloor = sourceFloor,
                sourceFloor = sourceFloor,
                eventFloor = sourceFloor,
                startTime = startTime,
                durationSeconds = durationSeconds,
                angleOffset = sourceAngleOffset,
                duration = sourceDuration,
                eventTag = sourceEventTag,
                external = true,
                targetFloors = targetFloors
            });
        }

        /// <summary>
        /// 把一条派发计划落成逐砖的 MoveDecorations：除 tag/duration/angleOffset/ease/eventTag 外
        /// 全部可关属性先关掉，只按源事件里"启用且取值可用"的位置/旋转/大小/透明度重新打开，
        /// 避免把默认值当生效值写进去（SetObject 同款做法，防止连带重置颜色/透明度/图标）。
        /// </summary>
        private static void EmitMoveTrackCopy(scnEditor editor, MoveTrackDispatch dispatch, int from, string tag)
        {
            LevelEvent source = dispatch.source;
            Ease ease = source.Get<Ease>("ease");
            foreach (int tile in dispatch.targetFloors)
            {
                LevelEvent levelEvent = new LevelEvent(dispatch.eventFloor, LevelEventType.MoveDecorations);
                foreach (KeyValuePair<string, PropertyInfo> property in levelEvent.info.propertiesInfo)
                {
                    if (property.Value.canBeDisabled)
                        levelEvent.disabled[property.Key] = true;
                }

                PlanetMotionPlanner.SetGeneratedNote(levelEvent, "移动轨道");
                levelEvent["tag"] = ResolveTileTag(tag, tile - from + 1);
                levelEvent["duration"] = dispatch.duration;
                levelEvent["angleOffset"] = dispatch.angleOffset;
                levelEvent["ease"] = ease;
                levelEvent["eventTag"] = dispatch.eventTag ?? "";

                // 位移是"绝对目标 = 装饰原位 + offset"，逐轴搬：NaN 的轴本来就不动，原样留着
                if (TryGetEnabled(source, "positionOffset", out Vector2 positionOffset))
                {
                    Vector2 offset = new Vector2(SanitizeAxis(positionOffset.x), SanitizeAxis(positionOffset.y));
                    if (!float.IsNaN(offset.x) || !float.IsNaN(offset.y))
                    {
                        levelEvent.disabled["positionOffset"] = false;
                        levelEvent["positionOffset"] = offset;
                    }
                }
                if (TryGetEnabled(source, "rotationOffset", out float rotationOffset) && IsUsable(rotationOffset))
                {
                    levelEvent.disabled["rotationOffset"] = false;
                    levelEvent["rotationOffset"] = rotationOffset;
                }
                if (TryGetEnabled(source, "scale", out Vector2 scale))
                {
                    Vector2 target = new Vector2(SanitizeAxis(scale.x), SanitizeAxis(scale.y));
                    if (!float.IsNaN(target.x) || !float.IsNaN(target.y))
                    {
                        levelEvent.disabled["scale"] = false;
                        levelEvent["scale"] = target;
                    }
                }
                if (TryGetEnabled(source, "opacity", out float opacity) && IsUsable(opacity))
                {
                    levelEvent.disabled["opacity"] = false;
                    levelEvent["opacity"] = opacity;
                }

                editor.events.Add(levelEvent);
            }
        }

        /// <summary>
        /// 本次展开实际要挂副本的砖：按原承载砖解析受影响范围，夹进本批 from..to，升序去重。
        /// 一块都没有（范围与本批无交集）时返回 null，调用方跳过这条计划。
        /// </summary>
        private static int[] ResolveDispatchTargets(LevelEvent source, int originFloor, int lastFloor, int from, int to)
        {
            List<int> tiles = new List<int>();
            HashSet<int> seen = new HashSet<int>();
            foreach (int tile in ResolveTileRange(source, originFloor, lastFloor))
            {
                if (tile < from || tile > to || !seen.Add(tile))
                    continue;
                tiles.Add(tile);
            }
            return tiles.Count == 0 ? null : tiles.ToArray();
        }

        /// <summary>MoveTrack 是否带了会动的属性：四项里至少一个启用且取值可用（位置/大小按轴，旋转/透明度按整值）。</summary>
        private static bool HasMoveTrackPayload(LevelEvent moveTrack)
        {
            if (TryGetEnabled(moveTrack, "positionOffset", out Vector2 position)
                && (IsUsable(position.x) || IsUsable(position.y)))
                return true;
            if (TryGetEnabled(moveTrack, "rotationOffset", out float rotation) && IsUsable(rotation))
                return true;
            if (TryGetEnabled(moveTrack, "scale", out Vector2 scale)
                && (IsUsable(scale.x) || IsUsable(scale.y)))
                return true;
            if (TryGetEnabled(moveTrack, "opacity", out float opacity) && IsUsable(opacity))
                return true;
            return false;
        }

        /// <summary>
        /// 受影响范围里是否有自由漫游区的起始砖：这些砖在游戏里会连整片生成的格子一起移动，
        /// 而格子只在 scnLevelMaker.MakeFreeroamGrid（进关卡时）才创建、不进 listFloors，
        /// 编辑器阶段没有可靠 API 可枚举，因此只提示不同步，不静默当成已覆盖。
        /// </summary>
        private static bool TouchesFreeroam(List<scrFloor> listFloors, LevelEvent source, int originFloor, int lastFloor, int from, int to)
        {
            foreach (int tile in ResolveTileRange(source, originFloor, lastFloor))
            {
                if (tile < from || tile > to)
                    continue;
                if (listFloors[tile] != null && listFloors[tile].freeroam)
                    return true;
            }
            return false;
        }

        /// <summary>可作为动画目标数值：非 NaN 且非无穷。</summary>
        private static bool IsUsable(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>逐轴清洗：NaN 表示"该轴不动"原样保留，只有无穷这类坏值才折成 NaN 跳过。</summary>
        private static float SanitizeAxis(float value)
        {
            return IsUsable(value) ? value : float.NaN;
        }

        private static void CopyTrackStyles(scnEditor editor, List<scrFloor> listFloors, int from, int to, string tag, bool isCentralized)
        {
            int lastFloor = listFloors.Count - 1;
            float levelBpm = editor.levelData.bpm;
            if (from < 0 || from > lastFloor || to < from || to > lastFloor)
                return;

            char[] space = { ' ' };
            LevelEvent[] snapshot = editor.events.ToArray();

            BuildEventTriggers(snapshot, out Dictionary<int, Dictionary<string, Tuple<int, float, bool, int>>> repeats,
                out Dictionary<int, string[]> conditionalTags, out HashSet<string> reservedTags);

            HashSet<string> manualTags = TagCleanup.CollectHitboxEventTags(editor.levelData.decorations);

            // WHY：空事件 Tag 会被「标签为空的 Repeat」命中，生成结果只要没独立事件 Tag 就会被二次展开
            reservedTags.UnionWith(manualTags);
            string generatedEventTag = NewGeneratedEventTag(reservedTags);

            foreach (LevelEvent source in snapshot)
            {
                if (source.eventType != LevelEventType.RecolorTrack || !source.active)
                    continue;
                if (!IsEffectiveTrackStyleChange(source))
                    continue;

                int sourceFloor = source.floor;
                if (sourceFloor < 0 || sourceFloor > lastFloor)
                    continue;

                // WHY：条件 RecolorTrack 只在命中判定标签时生效，复制品挂回源砖并沿用原 eventTag，才继承游戏已有的条件注册
                if (IsConditionalSource(source, conditionalTags))
                {
                    float conditionalAngleOffset = source.GetFloat("angleOffset");
                    if (float.IsNaN(conditionalAngleOffset) || float.IsInfinity(conditionalAngleOffset))
                        continue;
                    EmitTrackStyleEvents(editor, lastFloor, from, to, tag, sourceFloor, sourceFloor, source,
                        conditionalAngleOffset, source.GetString("eventTag"));
                    continue;
                }

                bool isManual = TagCleanup.HasManualHitboxTag(source, manualTags);
                int repetitions = 0;
                float interval = 0f;
                bool executeOnCurrentFloor = false;
                int repeatGap = 1;
                bool matchedRepeat = false;
                string sourceEventTag = source.GetString("eventTag");
                if (sourceEventTag != null
                    && repeats.TryGetValue(sourceFloor, out Dictionary<string, Tuple<int, float, bool, int>> sourceRepeats))
                {
                    foreach (string token in sourceEventTag.Split(space, StringSplitOptions.None))
                    {
                        if (!sourceRepeats.TryGetValue(token, out Tuple<int, float, bool, int> matched))
                            continue;
                        matchedRepeat = true;
                        repetitions = matched.Item1;
                        interval = matched.Item2;
                        executeOnCurrentFloor = matched.Item3;
                        repeatGap = matched.Item4;
                        break;
                    }
                }

                // 外部触发且没被同标签 Repeat 接管：原样挂回源砖、沿用原 eventTag，才保住撞击才生效的语义
                if (isManual && !matchedRepeat)
                {
                    float manualAngleOffset = source.GetFloat("angleOffset");
                    if (float.IsNaN(manualAngleOffset) || float.IsInfinity(manualAngleOffset))
                        continue;
                    EmitTrackStyleEvents(editor, lastFloor, from, to, tag, sourceFloor, sourceFloor, source,
                        manualAngleOffset, sourceEventTag);
                    continue;
                }

                int manualCarrierFloor = -1;
                if (isManual)
                {
                    manualCarrierFloor = FindManualEventCarrier(listFloors, from, sourceFloor,
                        sourceEventTag.Split(space, StringSplitOptions.None), sourceEventTag, repeats, conditionalTags);
                    if (manualCarrierFloor < 0)
                    {
                        Main.Logger.Log($"CopyTrackStyles: 砖 {sourceFloor} 的手动触发风格含重复且无安全承载砖，未同步");
                        continue;
                    }
                }

                scrFloor srcFloor = listFloors[sourceFloor];
                bool intervalBased = interval > 0f;
                for (long n = 0; n <= repetitions; n++)
                {
                    long repeatFloor = intervalBased ? srcFloor.seqID : srcFloor.seqID + n * repeatGap;
                    if (repeatFloor < 0 || repeatFloor >= listFloors.Count)
                        break;

                    int originFloor = executeOnCurrentFloor ? (int)repeatFloor : sourceFloor;
                    scrFloor origin = listFloors[originFloor];

                    double repeatOffset = intervalBased
                        ? (double)(interval * (float)n * 180f)
                        : (executeOnCurrentFloor ? 0.0 : (listFloors[(int)repeatFloor].entryBeat - srcFloor.entryBeat) * 180.0);

                    // WHY：撞击后的实际延迟 = 60/关卡BPM × 承载砖速度 × 角度/180，换砖只能按速度反比折算角度，不能走时间轴的 BPM 换算
                    if (manualCarrierFloor >= 0)
                    {
                        float carrierSpeed = listFloors[manualCarrierFloor].speed;
                        float originSpeed = origin.speed;
                        if (originSpeed <= 0f || float.IsNaN(originSpeed) || float.IsInfinity(originSpeed))
                            continue;
                        double manualAngle = ((double)source.GetFloat("angleOffset") + repeatOffset)
                                             * ((double)originSpeed / (double)carrierSpeed);
                        if (double.IsNaN(manualAngle) || double.IsInfinity(manualAngle))
                            continue;
                        float manualRepeatAngleOffset = (float)manualAngle;
                        if (float.IsNaN(manualRepeatAngleOffset) || float.IsInfinity(manualRepeatAngleOffset))
                            continue;
                        EmitTrackStyleEvents(editor, lastFloor, from, to, tag, originFloor, manualCarrierFloor, source,
                            manualRepeatAngleOffset, sourceEventTag);
                        continue;
                    }

                    int eventFloor = isCentralized ? from : originFloor;
                    scrFloor destFloor = listFloors[eventFloor];
                    float sourceBpm = levelBpm * origin.speed;
                    float destBpm = levelBpm * destFloor.speed;
                    if (float.IsNaN(sourceBpm) || float.IsInfinity(sourceBpm) || sourceBpm <= 0f
                        || float.IsNaN(destBpm) || float.IsInfinity(destBpm) || destBpm <= 0f)
                        continue;

                    double angleOffset = (origin.entryTime - destFloor.entryTime) * destBpm * 3.0
                                         + ((double)source.GetFloat("angleOffset") + repeatOffset) * (destBpm / sourceBpm);
                    if (double.IsNaN(angleOffset) || double.IsInfinity(angleOffset))
                        continue;
                    float newAngleOffset = (float)angleOffset;
                    if (float.IsNaN(newAngleOffset) || float.IsInfinity(newAngleOffset))
                        continue;

                    EmitTrackStyleEvents(editor, lastFloor, from, to, tag, originFloor, eventFloor, source, newAngleOffset, generatedEventTag);
                }
            }
        }

        // WHY：原版贴图变更也会设定风格，但此处仅复制风格枚举。
        private static bool IsEffectiveTrackStyleChange(LevelEvent source)
        {
            bool styleUsed = source.disabled.TryGetValue("trackStyle", out bool isDisabled) && !isDisabled;
            bool textureUsed = source.disabled.TryGetValue("trackTexture", out isDisabled) && !isDisabled
                && source.TryGet<string>("trackTexture", out string texture) && !string.IsNullOrEmpty(texture);
            bool textureScaleUsed = source.disabled.TryGetValue("trackTextureScale", out isDisabled) && !isDisabled;
            return styleUsed || textureUsed || textureScaleUsed;
        }

        // 承载砖：复制品沿用原 eventTag，只能挂到不会被同标签 Repeat/条件再展开、且速度可用的砖上（优先源砖、再锚点砖）。
        // "同步轨道风格"与"同步移动轨道"的手动触发展开共用这一条判定。
        private static int FindManualEventCarrier(List<scrFloor> listFloors, int from, int sourceFloor,
            string[] sourceTokens, string sourceEventTag,
            Dictionary<int, Dictionary<string, Tuple<int, float, bool, int>>> repeats,
            Dictionary<int, string[]> conditionalTags)
        {
            bool Carries(int floor)
            {
                if (floor < 0 || floor >= listFloors.Count)
                    return false;
                scrFloor tile = listFloors[floor];
                if (tile == null || tile.speed <= 0f || float.IsNaN(tile.speed) || float.IsInfinity(tile.speed))
                    return false;
                if (repeats.TryGetValue(floor, out Dictionary<string, Tuple<int, float, bool, int>> tokens))
                {
                    foreach (string token in sourceTokens)
                    {
                        if (tokens.ContainsKey(token))
                            return false;
                    }
                }
                if (conditionalTags.TryGetValue(floor, out string[] slots))
                {
                    foreach (string slot in slots)
                    {
                        if (!string.IsNullOrEmpty(slot) && !slot.IsNoneConditionalTag() && slot == sourceEventTag)
                            return false;
                    }
                }
                return true;
            }

            if (Carries(sourceFloor))
                return sourceFloor;
            if (from != sourceFloor && Carries(from))
                return from;
            for (int floor = 0; floor < listFloors.Count; floor++)
            {
                if (Carries(floor))
                    return floor;
            }
            return -1;
        }

        private static void EmitTrackStyleEvents(scnEditor editor, int lastFloor, int from, int to, string tag,
            int originFloor, int eventFloor, LevelEvent source, float angleOffset, string eventTag)
        {
            TrackStyle sourceStyle = source.Get<TrackStyle>("trackStyle");
            int start = ResolveTileIndex(source.GetTile("startTile"), originFloor, lastFloor);
            int end = ResolveTileIndex(source.GetTile("endTile"), originFloor, lastFloor);
            if (start > end)
            {
                int swap = start;
                start = end;
                end = swap;
            }
            // WHY：步长相位从原始 start 起算（先取交集会错开相位），用 long 累加以免 gapLength 接近 int 上限时溢出
            long step = 1L + Math.Max(0, source.GetInt("gapLength"));
            long first = Math.Max(start, from);
            long last = Math.Min(end, to);
            for (long tile = first + (step - (first - start) % step) % step; tile <= last; tile += step)
            {
                LevelEvent levelEvent = new LevelEvent(eventFloor, LevelEventType.SetObject);
                // SetObject 的字段默认全是启用的，只关掉可关的才不会连带重置颜色/透明度/图标
                foreach (KeyValuePair<string, PropertyInfo> property in levelEvent.info.propertiesInfo)
                {
                    if (property.Value.canBeDisabled)
                        levelEvent.disabled[property.Key] = true;
                }
                levelEvent.disabled["trackStyle"] = false;
                levelEvent["trackStyle"] = sourceStyle;
                levelEvent["tag"] = ResolveTileTag(tag, (int)tile - from + 1);
                levelEvent["duration"] = 0f;
                levelEvent["angleOffset"] = angleOffset;
                levelEvent["eventTag"] = eventTag;
                editor.events.Add(levelEvent);
            }
        }

        private static string NewGeneratedEventTag(HashSet<string> reservedTags)
        {
            string generated;
            do
            {
                generated = Guid.NewGuid().ToString("N");
            }
            while (reservedTags.Contains(generated));
            return generated;
        }

        private static bool IsConditionalSource(LevelEvent source, Dictionary<int, string[]> conditionalTags)
        {
            string eventTag = source.GetString("eventTag");
            if (string.IsNullOrEmpty(eventTag) || !conditionalTags.TryGetValue(source.floor, out string[] tags))
                return false;

            foreach (string tag in tags)
            {
                if (!string.IsNullOrEmpty(tag) && !tag.IsNoneConditionalTag() && tag == eventTag)
                    return true;
            }
            return false;
        }

        /// <summary>只在属性被启用时取值（游戏 TryGetAndSet(onlyIfEnabled) 的语义，但对缺键安全）。</summary>
        private static bool TryGetEnabled<T>(LevelEvent levelEvent, string key, out T value)
        {
            value = default;
            return levelEvent.disabled.TryGetValue(key, out bool isDisabled) && !isDisabled && levelEvent.TryGet(key, out value);
        }

        /// <summary>把 MoveTrack 的 startTile/endTile（可相对本砖/起点/终点）解析成地砖编号。</summary>
        private static int ResolveTileIndex(Tuple<int, TileRelativeTo> tile, int eventFloor, int lastFloor)
        {
            if (tile == null)
                return eventFloor;

            int index;
            switch (tile.Item2)
            {
                case TileRelativeTo.End:
                    index = lastFloor + tile.Item1;
                    break;
                case TileRelativeTo.ThisTile:
                    index = eventFloor + tile.Item1;
                    break;
                default:
                    index = tile.Item1;
                    break;
            }
            return Mathf.Clamp(index, 0, lastFloor);
        }

        /// <summary>
        /// 把 MoveTrack 的受影响砖范围解析成地砖编号序列（已按大小排序）。
        /// 与原版 ffxMoveFloorPlus 一致：起点/终点无序时先交换，砖号先夹进关卡范围，
        /// 再按 step = 1 + gapLength 从 start 起跳（gapLength=0 即逐块连续，负数按关卡坏文件处理钳到 0）；
        /// 步长用 long 累加，避免 gapLength 接近 int 上限时溢出成负数死循环。
        /// </summary>
        private static IEnumerable<int> ResolveTileRange(LevelEvent moveTrack, int eventFloor, int lastFloor)
        {
            int start = ResolveTileIndex(moveTrack.GetTile("startTile"), eventFloor, lastFloor);
            int end = ResolveTileIndex(moveTrack.GetTile("endTile"), eventFloor, lastFloor);
            if (start > end)
            {
                int swap = start;
                start = end;
                end = swap;
            }

            long step = 1L + Math.Max(0, moveTrack.GetInt("gapLength"));
            for (long tile = start; tile <= end; tile += step)
                yield return (int)tile;
        }

        /// <summary>星球跟随曲线的一段：target 是绝对目标值，startValue 取上一段的 target。</summary>
        private struct PlanetMotionSegment
        {
            public double startTime;
            public double duration;
            public float startValue;
            public float target;
            public Ease ease;
        }

        /// <summary>
        /// 按秒排布的"绝对目标"曲线：一条源轨位移轴/旋转轴，或假星球基础动画的一个属性。
        /// WHY 相邻两段不能合成一段：同属性的新 tween 会 Kill(complete:true) 旧的那条，也就是先把旧段
        /// 结束点瞬间应用、再从那里走向新目标；中途被打断时带跳变，一条事件表达不出来。
        /// </summary>
        private sealed class PlanetMotionCurve
        {
            public readonly List<PlanetMotionSegment> segments = new List<PlanetMotionSegment>();

            /// <summary>最后一次动画结束的绝对时刻（秒），没有段时为负无穷。</summary>
            public double LastEndTime
            {
                get
                {
                    double end = double.NegativeInfinity;
                    foreach (PlanetMotionSegment segment in segments)
                    {
                        double candidate = segment.startTime + segment.duration;
                        if (candidate > end) end = candidate;
                    }
                    return end;
                }
            }

            public void Append(double startTime, double duration, float target, Ease ease)
            {
                if (double.IsNaN(duration) || duration < 0.0) duration = 0.0;
                float startValue = segments.Count > 0 ? segments[segments.Count - 1].target : 0f;
                segments.Add(new PlanetMotionSegment
                {
                    startTime = startTime,
                    duration = duration,
                    startValue = startValue,
                    target = target,
                    ease = ease
                });
            }

            /// <summary>
            /// t 时刻的取值。beforeBoundary=true 只认严格早于 t 开始的段（用于边界处的左极限，瞬变才表达得出来）；
            /// 同一时刻后追加的段胜出，对应游戏里后来者 Kill 掉前一条的顺序语义。
            /// </summary>
            public float Evaluate(double t, bool beforeBoundary)
            {
                int chosen = -1;
                for (int i = 0; i < segments.Count; i++)
                {
                    // 时间边界会经过 float angleOffset 编码；同一砖时刻可能只差末位。
                    // 与烘焙边界合并使用同一容差，左极限不能误取右侧 snap，右值也不能漏掉同刻 snap。
                    if (beforeBoundary ? segments[i].startTime < t - PlanetTimeEpsilon
                        : segments[i].startTime <= t + PlanetTimeEpsilon) chosen = i;
                }
                if (chosen < 0) return 0f;

                PlanetMotionSegment segment = segments[chosen];
                if (segment.duration <= 0.0 || t >= segment.startTime + segment.duration - PlanetTimeEpsilon) return segment.target;
                float percent = (float)((t - segment.startTime) / segment.duration);
                if (percent < 0f) percent = 0f;
                if (percent > 1f) percent = 1f;
                float eased = DOVirtual.EasedValue(0f, 1f, percent, segment.ease);
                return segment.startValue + (segment.target - segment.startValue) * eased;
            }

            public void CollectBoundaryTimes(List<double> times)
            {
                foreach (PlanetMotionSegment segment in segments)
                {
                    times.Add(segment.startTime);
                    times.Add(segment.startTime + segment.duration);
                }
            }
        }

        /// <summary>一个采样时刻的星球姿态：offset 是相对星球 AddObject.position 的绝对偏移（tile 单位），rotation 是绝对 rotationOffset。</summary>
        private struct PlanetPose
        {
            public Vector2 offset;
            public float rotation;
        }

        // 星球姿态的采样容差与细分上限：位置按 tile 单位、旋转按度。递归到上限仍超标时只记日志，不声称逐帧精确
        private const double PlanetPositionTolerance = 0.002;
        private const float PlanetRotationTolerance = 0.1f;
        private const int PlanetMaxRefineDepth = 12;
        private const double PlanetTimeEpsilon = 1E-06;
        // WHY 原始（未取模、未合成）角增量上限：弦比对用 DeltaAngle，0→1440° 的四圈会被折成 0°，
        // 采样点全"达标"却整段没细分；超过上限就强制继续二分，到深度上限仍超则记为未达标。
        private const float PlanetMaxSampleSweep = 15f;

        /// <summary>
        /// 粘性方块下的星球跟随烘焙：真轨道被 MoveTrack 平移/旋转时，被选中那颗真星球的可见位置是
        /// "移动后的砖位置 + R(砖的旋转增量)·公转偏移"，这里把同样的复合写回假星球。
        /// 基础星球曲线来自 PlanetMotionPlan（与输出的基础星球事件同一份运动规格，不再回读 editor.events），
        /// 与源轨位移/旋转这些按秒的绝对目标曲线复合出的姿态一般不是单条 MoveDecorations 能表达的独立缓动，
        /// 所以按自适应分段的 Linear 事件逼近，误差由 PlanetPositionTolerance/PlanetRotationTolerance 界定。
        /// 只烘自动触发的派发（撞击/条件的触发时刻无法静态预判），也不烘 scale/透明度——真轨道的大小与
        /// 透明度本来就不移动真星球。
        /// 返回"烘焙后的事件列表"，由调用方按旧顺序（基础星球事件之后）写入编辑器；
        /// out 参数是本次真正被接管的星球标签，调用方据此跳过这些星球的基础运动事件。
        /// </summary>
        private static List<LevelEvent> BakePlanetTrackMotion(scnEditor editor, List<scrFloor> listFloors, int from, int to,
            float pivotTrackOffset, Vector2 genOffset, string bluePlanetTag, string redPlanetTag,
            PlanetMotionPlan planetMotion, List<MoveTrackDispatch> dispatches, out HashSet<string> bakedPlanetTags,
            MotionOutputMode outputMode = MotionOutputMode.FrameByFrame)
        {
            List<LevelEvent> bakedEvents = new List<LevelEvent>();
            bakedPlanetTags = new HashSet<string>(StringComparer.Ordinal);
            if (editor?.events == null || editor.levelData == null || dispatches == null || planetMotion == null)
                return bakedEvents;
            if (from < 0 || to < from || to >= listFloors.Count)
                return bakedEvents;
            if (listFloors[from] == null || listFloors[to] == null || !IsUsable(pivotTrackOffset))
                return bakedEvents;
            // 与旧实现的"事件区间为空"等价：本轮没有产出任何星球/视差事件时不烘焙
            if (planetMotion.drafts.Count == 0)
                return bakedEvents;

            float levelBpm = editor.levelData.bpm;
            scrFloor anchor = listFloors[from];
            float anchorEffBpm = levelBpm * anchor.speed;
            if (!IsUsable(anchorEffBpm) || anchorEffBpm <= 0f)
                return bakedEvents;

            List<MoveTrackDispatch> automatic = dispatches
                .Where(d => d != null && !d.external && d.targetFloors != null)
                .OrderBy(d => d.startTime)
                .ThenBy(d => d.originFloor)
                .ThenBy(d => d.sourceIndex)
                .ThenBy(d => d.repeatIndex)
                .ToList();
            bool hasExternal = dispatches.Any(d => d != null && d.external);
            if (automatic.Count == 0)
            {
                Main.Logger.Log("BakePlanetTrackMotion: 本批只有外部触发的移动，触发时刻无法静态预判，星球跟随未烘焙（保留原星球动画）");
                return bakedEvents;
            }

            // 每块砖一条位移X/位移Y/旋转的绝对目标曲线（数组索引 0=X、1=Y、2=旋转）
            Dictionary<int, PlanetMotionCurve[]> motions = BuildSourceMotionCurves(automatic);
            if (motions.Count == 0)
                return bakedEvents;

            bool[] sticky = ResolveStickyFloorFlags(editor, listFloors, from, to);
            if (!motions.Keys.Any(floor => floor >= from && floor <= to && sticky[floor]))
                return bakedEvents;

            // 烘焙窗口：锚点砖入场时刻 → 最后一块砖入场之后（基础动画与源轨移动谁结束得晚就用谁）
            double timelineStart = anchor.entryTime;
            double timelineEnd = to + 1 < listFloors.Count && listFloors[to + 1] != null
                ? listFloors[to + 1].entryTime
                : anchor.entryTime;
            foreach (PlanetMotionCurve[] motionAxes in motions.Values)
            {
                foreach (PlanetMotionCurve curve in motionAxes)
                {
                    double motionEnd = curve.LastEndTime;
                    if (!double.IsNegativeInfinity(motionEnd) && motionEnd > timelineEnd) timelineEnd = motionEnd;
                }
            }

            // 安全的事件标签：不在 Repeat/条件表里、也不是手动撞击标签，免得被空标签 Repeat 二次执行
            BuildEventTriggers(editor.events.ToArray(), out _, out _, out HashSet<string> reservedTags);
            reservedTags.UnionWith(TagCleanup.CollectHitboxEventTags(editor.levelData.decorations));
            string bakedEventTag = NewGeneratedEventTag(reservedTags);

            float tileSize = ResolveTileSize();
            int totalEmitted = 0;

            foreach (string planetTag in new[] { bluePlanetTag, redPlanetTag })
            {
                Vector2 planetAnchor = ReadPlanetAnchor(editor, planetTag, genOffset);
                // 基础星球曲线与输出的基础星球事件同源：都来自 PlanetMotionPlan，不再回读刚写进去的事件
                PlanetMotionCurve[] baseCurves = BuildPlanetBaseCurves(planetMotion, planetTag);
                foreach (PlanetMotionCurve curve in baseCurves)
                {
                    double last = curve.LastEndTime;
                    if (!double.IsNegativeInfinity(last) && last > timelineEnd) timelineEnd = last;
                }

                int emitted = BakeOnePlanetMotion(bakedEvents, listFloors, from, to, pivotTrackOffset, tileSize,
                    timelineStart, anchorEffBpm, planetAnchor, genOffset, planetTag, bakedEventTag, baseCurves, motions, sticky, timelineEnd, outputMode);
                if (emitted <= 0)
                    continue;

                totalEmitted += emitted;
                bakedPlanetTags.Add(planetTag);
            }

            if (totalEmitted > 0)
            {
                Main.Logger.Log($"BakePlanetTrackMotion: 星球跟随输出模式 {outputMode}，整理前 {totalEmitted} 条事件" +
                                $"（位置容差 {PlanetPositionTolerance} tile、旋转容差 {PlanetRotationTolerance}°，独立缓动曲线为近似不是逐帧精确）");
                if (hasExternal)
                    Main.Logger.Log("BakePlanetTrackMotion: 本批混有外部触发的移动，只烘焙了自动触发的跟随，外部触发时段星球未同步（其副本仍按原标签生成）");
            }

            if (outputMode == MotionOutputMode.Simple)
                MergeEquivalentMotionTargets(bakedEvents);
            return bakedEvents;
        }

        /// <summary>
        /// 源轨位移/旋转曲线：按 (触发时刻, 承载砖, 原事件序号, Repeat 次数) 的稳定顺序逐段追加。
        /// 位移/旋转都是"绝对目标"（游戏里都是 startPos+offset 的重扫语义），不累加；NaN 的轴不建段。
        /// </summary>
        private static Dictionary<int, PlanetMotionCurve[]> BuildSourceMotionCurves(List<MoveTrackDispatch> automatic)
        {
            Dictionary<int, PlanetMotionCurve[]> motions = new Dictionary<int, PlanetMotionCurve[]>();
            foreach (MoveTrackDispatch dispatch in automatic)
            {
                LevelEvent source = dispatch.source;
                if (source == null || double.IsNaN(dispatch.startTime) || double.IsInfinity(dispatch.startTime))
                    continue;

                float targetX = float.NaN;
                float targetY = float.NaN;
                float targetRotation = float.NaN;
                if (TryGetEnabled(source, "positionOffset", out Vector2 positionOffset))
                {
                    if (IsUsable(positionOffset.x)) targetX = positionOffset.x;
                    if (IsUsable(positionOffset.y)) targetY = positionOffset.y;
                }
                if (TryGetEnabled(source, "rotationOffset", out float rotationOffset) && IsUsable(rotationOffset))
                    targetRotation = rotationOffset;
                if (float.IsNaN(targetX) && float.IsNaN(targetY) && float.IsNaN(targetRotation))
                    continue;

                Ease ease = source.Get<Ease>("ease");
                foreach (int tile in dispatch.targetFloors)
                {
                    if (!motions.TryGetValue(tile, out PlanetMotionCurve[] axes))
                        motions[tile] = axes = NewPlanetMotionCurves();
                    if (!float.IsNaN(targetX)) axes[0].Append(dispatch.startTime, dispatch.durationSeconds, targetX, ease);
                    if (!float.IsNaN(targetY)) axes[1].Append(dispatch.startTime, dispatch.durationSeconds, targetY, ease);
                    if (!float.IsNaN(targetRotation)) axes[2].Append(dispatch.startTime, dispatch.durationSeconds, targetRotation, ease);
                }
            }
            return motions;
        }

        private static PlanetMotionCurve[] NewPlanetMotionCurves()
        {
            return new[] { new PlanetMotionCurve(), new PlanetMotionCurve(), new PlanetMotionCurve() };
        }

        /// <summary>
        /// 每块砖的"粘滞"取值：按 scnGame 的同一口径重放 miscSettings.stickToFloors + PositionTrack.stickToFloors。
        /// 只看关卡全局开关会漏掉单砖覆盖（justThisTile），这正是旧实现漏跟的原因；
        /// scrFloor.stickToFloor 是运行期写出来的瞬时状态，编辑器里从没跑过关卡时全是默认值，
        /// 拿它做过任意一条就整批改写的短路会把"全局开 + 单砖关"直接吃掉，所以不参与判断。
        /// </summary>
        private static bool[] ResolveStickyFloorFlags(scnEditor editor, List<scrFloor> listFloors, int from, int to)
        {
            int count = listFloors.Count;
            bool[] flags = new bool[count];

            bool running = false;
            LevelEvent miscSettings = editor.levelData?.miscSettings;
            if (miscSettings != null && miscSettings.TryGet("stickToFloors", out bool globalSticky))
                running = globalSticky;

            // PositionTrack 的覆盖口径与 scnGame 一致：整局的改动影响本砖及其后，justThisTile 只改本砖
            Dictionary<int, List<LevelEvent>> positionTracks = new Dictionary<int, List<LevelEvent>>();
            foreach (LevelEvent levelEvent in editor.events)
            {
                if (levelEvent == null || !levelEvent.active || levelEvent.eventType != LevelEventType.PositionTrack)
                    continue;
                if (levelEvent.floor < 0 || levelEvent.floor > to)
                    continue;
                if (!positionTracks.TryGetValue(levelEvent.floor, out List<LevelEvent> list))
                    positionTracks[levelEvent.floor] = list = new List<LevelEvent>();
                list.Add(levelEvent);
            }

            for (int floor = 0; floor < count; floor++)
            {
                bool? singleTile = null;
                if (positionTracks.TryGetValue(floor, out List<LevelEvent> events))
                {
                    foreach (LevelEvent positionTrack in events)
                    {
                        if (!TryGetEnabled(positionTrack, "stickToFloors", out bool value))
                            continue;
                        singleTile = value;
                        if (!positionTrack.GetBool("justThisTile")) running = value;
                    }
                }
                flags[floor] = singleTile ?? running;
            }
            return flags;
        }

        /// <summary>
        /// 一格的边长（世界单位）。转发到 <see cref="TrackGeometry.TileSize"/>：位移换算用游戏真实 tileSize
        /// （MoveTrack/MoveDecorations 都按它折算），拿不到有效值才退回旧硬编码值 1.5。
        /// 与 LevelAnalysis 的基础位置换算共用同一个来源，烘焙与生成的格子口径不会分叉。
        /// </summary>
        private static float ResolveTileSize()
        {
            return TrackGeometry.TileSize;
        }

        /// <summary>
        /// 基础星球曲线：直接取本批运动规格里该星球的事件（时间口径已由规划算好），不再回读 editor.events。
        /// 旧实现是"先写事件 → 按事件所在砖的 entryTime/angleOffset 反算时间 → 排序 → 建曲线"，
        /// 这里与输出事件同源，同刻事件仍按输出顺序（同砖同属性后一条 Kill 前一条）。
        /// </summary>
        private static PlanetMotionCurve[] BuildPlanetBaseCurves(PlanetMotionPlan planetMotion, string planetTag)
        {
            PlanetMotionCurve[] curves = NewPlanetMotionCurves();
            if (planetMotion == null)
                return curves;

            List<PlanetMotionDraft> timed = new List<PlanetMotionDraft>();
            foreach (PlanetMotionDraft draft in planetMotion.drafts)
            {
                if (draft == null || draft.isParallaxAdjust || draft.tag != planetTag)
                    continue;
                if (double.IsNaN(draft.startTime) || double.IsInfinity(draft.startTime)
                    || double.IsNaN(draft.durationSeconds) || double.IsInfinity(draft.durationSeconds))
                    continue;
                timed.Add(draft);
            }

            // 只有明确写入的属性才替换该轴；缺省 positionOffset 是 NaN/NaN（不修改），绝不是 0/0。
            // 旋转补间必须保持之前的 snap 根位置，否则烘焙会凭空生成“根位置回原点”的运动。
            Ease defaultEase = GetDefaultMoveDecorationsEase();
            foreach (PlanetMotionDraft draft in timed.OrderBy(draft => draft.startTime))
            {
                if (draft.hasPosition)
                {
                    if (IsUsable(draft.position.x)) curves[0].Append(draft.startTime, draft.durationSeconds, draft.position.x, defaultEase);
                    if (IsUsable(draft.position.y)) curves[1].Append(draft.startTime, draft.durationSeconds, draft.position.y, defaultEase);
                }
                if (draft.hasRotation && IsUsable(draft.rotation))
                    curves[2].Append(draft.startTime, draft.durationSeconds, draft.rotation,
                        draft.inBackEase ? Ease.InBack : draft.outSineEase ? Ease.OutSine : draft.linearEase ? Ease.Linear : defaultEase);
            }
            return curves;
        }

        /// <summary>基础星球事件不写 ease，取游戏给 MoveDecorations 的默认值，避免把 Linear 硬编码进烘焙曲线。</summary>
        private static Ease GetDefaultMoveDecorationsEase()
        {
            return new LevelEvent(0, LevelEventType.MoveDecorations).Get<Ease>("ease");
        }

        /// <summary>读这颗星球本轮 AddObject 的 position 作为烘焙基准（tile 单位）；读不到退回面板的生成位置并记日志。</summary>
        private static Vector2 ReadPlanetAnchor(scnEditor editor, string planetTag, Vector2 genOffset)
        {
            char[] space = { ' ' };
            List<LevelEvent> decorations = editor.levelData?.decorations;
            if (decorations != null)
            {
                for (int i = decorations.Count - 1; i >= 0; i--)
                {
                    LevelEvent decoration = decorations[i];
                    if (decoration == null || !decoration.active || decoration.eventType != LevelEventType.AddObject)
                        continue;
                    if (!decoration.TryGet("objectType", out ObjectDecorationType objectType) || objectType != ObjectDecorationType.Planet)
                        continue;
                    string[] tokens = (decoration.GetString("tag") ?? "").Split(space, StringSplitOptions.RemoveEmptyEntries);
                    bool matched = false;
                    foreach (string token in tokens)
                    {
                        if (token == planetTag) matched = true;
                    }
                    if (!matched)
                        continue;

                    Vector2 position = decoration.Get<Vector2>("position");
                    if (IsUsable(position.x) && IsUsable(position.y)) return position;
                    break;
                }
            }

            Main.Logger.Log($"BakePlanetTrackMotion: 没读到星球 {planetTag} 的 AddObject 位置，按生成位置 {genOffset} 烘焙");
            return genOffset;
        }

        /// <summary>
        /// 单颗星球的烘焙：在 [timelineStart, timelineEnd] 上按边界（基础曲线段首尾、源轨派发段首尾、相位砖入场时刻）
        /// 切区间，边界写 0 时长瞬变事件钉住跳变，区间内用自适应细分的 Linear 段逼近复合姿态。
        /// planetAnchor 是这颗星球自己 AddObject 的初始位置（红蓝不同，决定姿态的相对基准），
        /// geometryAnchor 是两颗星球共享的公转中心（生成位置），只用来定砖心，两者不能互换。
        /// </summary>
        private static int BakeOnePlanetMotion(List<LevelEvent> output, List<scrFloor> listFloors, int from, int to,
            float pivotTrackOffset, float tileSize, double timelineStart, double anchorEffBpm,
            Vector2 planetAnchor, Vector2 geometryAnchor, string planetTag, string eventTag, PlanetMotionCurve[] baseCurves,
            Dictionary<int, PlanetMotionCurve[]> motions, bool[] sticky, double timelineEnd,
            MotionOutputMode outputMode = MotionOutputMode.FrameByFrame, int simpleAxis = -1)
        {
            // Independent runtime channels must not inherit each other's segmentation boundaries.
            if (outputMode == MotionOutputMode.Simple && simpleAxis < 0)
            {
                int total = 0;
                List<LevelEvent> channels = new List<LevelEvent>();
                for (int axis = 0; axis < 3; axis++)
                    total += BakeOnePlanetMotion(channels, listFloors, from, to, pivotTrackOffset, tileSize,
                        timelineStart, anchorEffBpm, planetAnchor, geometryAnchor, planetTag, eventTag,
                        baseCurves, motions, sticky, timelineEnd, outputMode, axis);
                output.AddRange(channels.OrderBy(e => e.Get<float>("angleOffset")));
                return total;
            }
            double duration = timelineEnd - timelineStart;
            if (double.IsNaN(duration) || double.IsInfinity(duration) || duration <= PlanetTimeEpsilon)
                return 0;

            List<double> marks = new List<double> { timelineStart, timelineEnd };
            for (int axis = 0; axis < 3; axis++)
                if (simpleAxis < 0 || (simpleAxis == 2 ? axis == 2 : axis < 2))
                    baseCurves[axis].CollectBoundaryTimes(marks);
            foreach (PlanetMotionCurve[] axes in motions.Values)
                for (int axis = 0; axis < 3; axis++)
                    if (simpleAxis < 0 || simpleAxis != 2 || axis == 2)
                        axes[axis].CollectBoundaryTimes(marks);
            for (int floor = from; floor <= to; floor++)
            {
                if (listFloors[floor] != null) marks.Add(listFloors[floor].entryTime);
            }
            marks.Sort();

            List<double> times = new List<double>();
            foreach (double mark in marks)
            {
                if (double.IsNaN(mark) || double.IsInfinity(mark))
                    continue;
                double clamped = mark < timelineStart ? timelineStart : (mark > timelineEnd ? timelineEnd : mark);
                if (times.Count > 0 && clamped - times[times.Count - 1] <= PlanetTimeEpsilon)
                    continue;
                times.Add(clamped);
            }
            if (times.Count < 2)
                return 0;

            int emitted = 0;
            int skipped = 0;
            bool unresolved = false;
            bool timeQuantized = false;
            bool hasPrevious = false;
            PlanetPose previous = default;

            for (int i = 0; i + 1 < times.Count; i++)
            {
                double left = times[i];
                double right = times[i + 1];
                PlanetPose leftPose = PoseAt(left, false);
                PlanetPose rightPose = PoseAt(right, true);

                // 边界处的跳变（Kill(complete:true) 留下的结束点、相位换砖）必须用 0 时长事件钉住；
                // 与上一区间终点已在容差内一致时不重复写
                if (!hasPrevious || !IsPoseWithinTolerance(leftPose, previous))
                {
                    bool added;
                    if (outputMode == MotionOutputMode.Simple)
                    {
                        Vector2 deltaTarget = new Vector2(simpleAxis == 0 && (!hasPrevious || Mathf.Abs(leftPose.offset.x - previous.offset.x) > 0.000001f) ? leftPose.offset.x : float.NaN,
                            simpleAxis == 1 && (!hasPrevious || Mathf.Abs(leftPose.offset.y - previous.offset.y) > 0.000001f) ? leftPose.offset.y : float.NaN);
                        float rotationTarget = simpleAxis == 2 && (!hasPrevious || Mathf.Abs(leftPose.rotation - previous.rotation) > 0.000001f) ? leftPose.rotation : float.NaN;
                        added = AppendSimpleMotionEvent(output, from, timelineStart, anchorEffBpm, planetTag, eventTag,
                            deltaTarget, rotationTarget, left, left, Ease.Linear);
                    }
                    else added = AppendPlanetPoseEvent(output, from, timelineStart, anchorEffBpm, planetTag, eventTag, leftPose, left, left);
                    if (added)
                        emitted++;
                    else
                        skipped++;
                }

                if (outputMode == MotionOutputMode.Simple && TryEmitSimple(left, right, leftPose, rightPose))
                {
                    previous = rightPose;
                    hasPrevious = true;
                    continue;
                }

                List<double> interior = new List<double>();
                if (Refine(left, right, leftPose, rightPose, interior, 0) > 0) unresolved = true;

                // WHY angleOffset/duration 是 float：极短正时长段会量化成与起点同一个 angleOffset，两条同刻 tween
                // 互相 Kill 等于没生效。这种段不单独发、起点不提前推进，继续并入下一 piece；整段都表达不出来时
                // 在区间末用瞬变钉住终点姿态（区间中点被跳过，只保证端点）。
                double pieceStart = left;
                bool pending = false;
                for (int j = 0; j <= interior.Count; j++)
                {
                    double pieceEnd = j < interior.Count ? interior[j] : right;
                    if (pieceEnd - pieceStart <= PlanetTimeEpsilon)
                        continue;
                    PlanetPose piecePose = j < interior.Count ? PoseAt(pieceEnd, false) : rightPose;
                    if (!IsPositiveSegmentRepresentable(pieceStart, pieceEnd))
                    {
                        pending = true;
                        continue;
                    }
                    if (pending) timeQuantized = true;
                    if (AppendAxisPose(piecePose, pieceStart, pieceEnd))
                        emitted++;
                    else
                        skipped++;
                    pieceStart = pieceEnd;
                    pending = false;
                }
                if (pending)
                {
                    timeQuantized = true;
                    if (AppendAxisPose(rightPose, right, right))
                        emitted++;
                    else
                        skipped++;
                }

                previous = rightPose;
                hasPrevious = true;
            }

            // 区间终点取的是左极限；timelineEnd 上恰好有换砖/瞬变时右值不同，不补一条 0 时长事件末尾就不会生效
            if (hasPrevious)
            {
                PlanetPose terminal = PoseAt(timelineEnd, false);
                if (!IsPoseWithinTolerance(terminal, previous))
                {
                    if (AppendAxisPose(terminal, timelineEnd, timelineEnd))
                        emitted++;
                    else
                        skipped++;
                }
            }

            if (unresolved)
                Main.Logger.Log($"BakePlanetTrackMotion: 星球 {planetTag} 的跟随曲线在递归上限 {PlanetMaxRefineDepth} 内仍未全部落进容差，这些区间是近似");
            if (timeQuantized)
                Main.Logger.Log($"BakePlanetTrackMotion: 星球 {planetTag} 受 angleOffset float 精度限制，合并了极短段，这些区间细节近似（不保证达到容差）");
            if (skipped > 0)
                Main.Logger.Log($"BakePlanetTrackMotion: 星球 {planetTag} 有 {skipped} 个采样点数值不可用，已跳过");
            return emitted;

            bool AppendAxisPose(PlanetPose pose, double left, double right)
            {
                if (simpleAxis < 0)
                    return AppendPlanetPoseEvent(output, from, timelineStart, anchorEffBpm, planetTag, eventTag, pose, left, right);
                return AppendSimpleMotionEvent(output, from, timelineStart, anchorEffBpm, planetTag, eventTag,
                    new Vector2(simpleAxis == 0 ? pose.offset.x : float.NaN, simpleAxis == 1 ? pose.offset.y : float.NaN),
                    simpleAxis == 2 ? pose.rotation : float.NaN, left, right, Ease.Linear);
            }

            bool TryEmitSimple(double left, double right, PlanetPose a, PlanetPose b)
            {
                if (!IsPositiveSegmentRepresentable(left, right)) return false;
                // 每轴独立保留原生缓动。区间内必须能由单一原生缓动表达；否则整段回退旧采样。
                int phase = PhaseFloorAt(left, false);
                PlanetMotionCurve[] sourceAxes = phase < sticky.Length && sticky[phase] && motions.TryGetValue(phase, out PlanetMotionCurve[] foundAxes)
                    ? foundAxes : NewPlanetMotionCurves();
                // 保守的结构门禁：动态旋转复合不作闭式简化；被边界截断的非线性缓动不能重启成同名 ease。
                if (simpleAxis != 2 && Mathf.Abs(sourceAxes[2].Evaluate(right, true) - sourceAxes[2].Evaluate(left, false)) > 0.000001f) return false;
                IEnumerable<PlanetMotionCurve> relevant = simpleAxis == 2
                    ? new[] { baseCurves[2], sourceAxes[2] }
                    : baseCurves.Take(2).Concat(sourceAxes);
                foreach (PlanetMotionCurve c in relevant)
                {
                    PlanetMotionSegment? active = null;
                    foreach (PlanetMotionSegment segment in c.segments)
                        if (segment.startTime <= left + PlanetTimeEpsilon) active = segment;
                    if (!active.HasValue) continue;
                    PlanetMotionSegment value = active.Value;
                    if (value.ease != Ease.Linear && value.duration > 0 && left < value.startTime + value.duration - PlanetTimeEpsilon
                        && (Math.Abs(left - value.startTime) > PlanetTimeEpsilon
                            || Math.Abs(right - value.startTime - value.duration) > PlanetTimeEpsilon)) return false;
                }
                Ease[] candidates = new[] { Ease.Linear }.Concat(baseCurves.SelectMany(c => c.segments).Select(s => s.ease))
                    .Concat(motions.Values.SelectMany(axes => axes).SelectMany(c => c.segments).Select(s => s.ease)).Distinct().ToArray();
                float sourceRotation = sourceAxes[2].Evaluate(left, false);
                double sourceRadians = sourceRotation * Math.PI / 180.0;
                double pivotRadians = pivotTrackOffset * Math.PI / 180.0;
                // 先证明每个轴的活跃贡献共享同一时间形状，再作数值交叉检查。
                // 同一完整非线性 ease 可按常数系数相加，Linear 可截取；不同非线性/错窗不可直接重启。
                bool SameShape(int axis)
                {
                    List<PlanetMotionCurve> contributors = new List<PlanetMotionCurve>();
                    if (axis == 2) { contributors.Add(baseCurves[2]); contributors.Add(sourceAxes[2]); }
                    else
                    {
                        double c = Math.Cos(sourceRadians), s = Math.Sin(sourceRadians);
                        double pc = Math.Cos(pivotRadians), ps = Math.Sin(pivotRadians);
                        if (Math.Abs(axis == 0 ? c : s) > 1e-7) contributors.Add(baseCurves[0]);
                        if (Math.Abs(axis == 0 ? s : c) > 1e-7) contributors.Add(baseCurves[1]);
                        if (Math.Abs(axis == 0 ? pc : ps) > 1e-7) contributors.Add(sourceAxes[0]);
                        if (Math.Abs(axis == 0 ? ps : pc) > 1e-7) contributors.Add(sourceAxes[1]);
                    }
                    Ease? shape = null;
                    foreach (PlanetMotionCurve curve in contributors)
                    {
                        PlanetMotionSegment? segment = null;
                        foreach (PlanetMotionSegment item in curve.segments)
                            if (item.startTime <= left + PlanetTimeEpsilon) segment = item;
                        if (!segment.HasValue) continue;
                        PlanetMotionSegment value = segment.Value;
                        if (value.duration <= 0 || left >= value.startTime + value.duration - PlanetTimeEpsilon
                            || Mathf.Abs(value.target - value.startValue) < 0.000001f) continue;
                        if (shape.HasValue && shape.Value != value.ease) return false;
                        shape = value.ease;
                    }
                    return true;
                }
                if (!SameShape(simpleAxis)) return false;
                float[] starts = { a.offset.x, a.offset.y, a.rotation };
                float[] ends = { b.offset.x, b.offset.y, b.rotation };
                Ease[] chosen = new Ease[3];
                for (int axis = simpleAxis; axis <= simpleAxis; axis++)
                {
                    bool found = false;
                    foreach (Ease ease in candidates)
                    {
                        bool fits = true;
                        for (int sample = 1; sample < 32; sample++)
                        {
                            float p = sample / 32f;
                            PlanetPose actual = PoseAt(left + (right - left) * p, false);
                            float value = axis == 0 ? actual.offset.x : axis == 1 ? actual.offset.y : actual.rotation;
                            float expected = starts[axis] + (ends[axis] - starts[axis]) * DOVirtual.EasedValue(0f, 1f, p, ease);
                            // 严格数值门限，远小于采样容差；旋转不取模，不能把整圈误当静止。
                            if (Mathf.Abs(value - expected) > (axis == 2 ? 0.0001f : 0.00001f)) { fits = false; break; }
                        }
                        if (fits) { chosen[axis] = ease; found = true; break; }
                    }
                    if (!found) return false;
                }
                for (int axis = simpleAxis; axis <= simpleAxis; axis++)
                {
                    if (Mathf.Abs(starts[axis] - ends[axis]) < 0.000001f) continue;
                    PlanetPose target;
                    target.offset = new Vector2(axis == 0 ? ends[0] : float.NaN, axis == 1 ? ends[1] : float.NaN);
                    target.rotation = axis == 2 ? ends[2] : float.NaN;
                    if (AppendSimpleMotionEvent(output, from, timelineStart, anchorEffBpm, planetTag, eventTag,
                        target.offset, target.rotation, left, right, chosen[axis])) emitted++;
                }
                return true;
            }

            bool IsPositiveSegmentRepresentable(double start, double end)
            {
                if (end - start <= PlanetTimeEpsilon) return false;
                float aoStart = (float)((start - timelineStart) * anchorEffBpm * 3.0);
                float aoEnd = (float)((end - timelineStart) * anchorEffBpm * 3.0);
                float duration = (float)((end - start) * anchorEffBpm / 60.0);
                if (!IsUsable(aoStart) || !IsUsable(aoEnd) || !IsUsable(duration)) return false;
                return aoEnd > aoStart && duration > 0f;
            }

            int PhaseFloorAt(double t, bool beforeBoundary)
            {
                int phase = from;
                for (int floor = from; floor <= to; floor++)
                {
                    scrFloor tile = listFloors[floor];
                    if (tile != null && (beforeBoundary ? tile.entryTime < t - PlanetTimeEpsilon
                        : tile.entryTime <= t + PlanetTimeEpsilon))
                        phase = floor;
                }
                return phase;
            }

            // 复合姿态 = 基础星球姿态经相位砖的刚体变换（绕砖心转 Δ 再平移 D），根位置与根旋转一起转，
            // 星球身上的 pivot/子物体/缩放会随之正确绕砖心转，不需要再把公转半径重算一遍。
            // 位移 D 与旋转 Δ 用源轨的原值（和假轨道副本搬的是同一个向量、同一个角度），
            // 只有砖心要跟基础星球曲线同一口径（geometryAnchor 是红蓝共享的生成中心，红球自己的 AddObject
            // 位置带 1 tile 偏移，拿它当中心会绕错；root 仍相对各自的 planetAnchor 算），
            // 所以下面砖的相对位置才转 -pivotTrackOffset
            PlanetPose PoseAt(double t, bool beforeBoundary)
            {
                Vector2 root = planetAnchor + new Vector2(baseCurves[0].Evaluate(t, beforeBoundary), baseCurves[1].Evaluate(t, beforeBoundary));
                float baseRotation = baseCurves[2].Evaluate(t, beforeBoundary);

                int phase = PhaseFloorAt(t, beforeBoundary);
                Vector2 translation = Vector2.zero;
                float rotationDelta = 0f;
                if (phase < sticky.Length && sticky[phase] && motions.TryGetValue(phase, out PlanetMotionCurve[] axes))
                {
                    translation = LevelAnalysis.GetPivotOffset(
                        new Vector2(axes[0].Evaluate(t, beforeBoundary), axes[1].Evaluate(t, beforeBoundary)), -pivotTrackOffset);
                    rotationDelta = axes[2].Evaluate(t, beforeBoundary);
                }

                Vector2 tileOffset = TileOffsetFromAnchor(listFloors, from, phase, tileSize);
                Vector2 center = geometryAnchor + LevelAnalysis.GetPivotOffset(tileOffset, -pivotTrackOffset);
                PlanetPose pose;
                pose.offset = center + translation + LevelAnalysis.GetPivotOffset(root - center, -rotationDelta) - planetAnchor;
                pose.rotation = baseRotation + rotationDelta;
                if (simpleAxis >= 0)
                {
                    if (simpleAxis != 0) pose.offset.x = 0f;
                    if (simpleAxis != 1) pose.offset.y = 0f;
                    if (simpleAxis != 2) pose.rotation = 0f;
                }
                return pose;
            }

            bool WithinLinearTolerance(double left, double right, PlanetPose leftPose, PlanetPose rightPose, double t)
            {
                float fraction = (float)((t - left) / (right - left));
                Vector2 chord = leftPose.offset + (rightPose.offset - leftPose.offset) * fraction;
                float chordRotation = leftPose.rotation + (rightPose.rotation - leftPose.rotation) * fraction;
                PlanetPose actual = PoseAt(t, false);
                if (Vector2.Distance(actual.offset, chord) > PlanetPositionTolerance) return false;
                return Mathf.Abs(Mathf.DeltaAngle(chordRotation, actual.rotation)) <= PlanetRotationTolerance;
            }

            bool RawSweepWithinLimit(double left, double right)
            {
                if (simpleAxis >= 0 && simpleAxis != 2) return true;
                float baseSweep = baseCurves[2].Evaluate(right, true) - baseCurves[2].Evaluate(left, false);
                if (!(Mathf.Abs(baseSweep) <= PlanetMaxSampleSweep)) return false;

                int phase = PhaseFloorAt(left, false);
                if (phase < sticky.Length && sticky[phase] && motions.TryGetValue(phase, out PlanetMotionCurve[] axes))
                {
                    float sourceSweep = axes[2].Evaluate(right, true) - axes[2].Evaluate(left, false);
                    if (!(Mathf.Abs(sourceSweep) <= PlanetMaxSampleSweep)) return false;
                }
                return true;
            }

            int Refine(double left, double right, PlanetPose leftPose, PlanetPose rightPose, List<double> interior, int depth)
            {
                if (right - left <= PlanetTimeEpsilon) return 0;
                if (RawSweepWithinLimit(left, right)
                    && WithinLinearTolerance(left, right, leftPose, rightPose, (left + right) * 0.5)
                    && WithinLinearTolerance(left, right, leftPose, rightPose, left + (right - left) * 0.25)
                    && WithinLinearTolerance(left, right, leftPose, rightPose, left + (right - left) * 0.75))
                    return 0;
                if (depth >= PlanetMaxRefineDepth) return 1;

                double half = (left + right) * 0.5;
                PlanetPose halfPose = PoseAt(half, false);
                int unresolvedLeft = Refine(left, half, leftPose, halfPose, interior, depth + 1);
                interior.Add(half);
                int unresolvedRight = Refine(half, right, halfPose, rightPose, interior, depth + 1);
                return unresolvedLeft + unresolvedRight;
            }
        }

        /// <summary>砖心相对锚点砖的位置（tile 单位，用真实 tileSize）。</summary>
        private static Vector2 TileOffsetFromAnchor(List<scrFloor> listFloors, int from, int floor, float tileSize)
        {
            scrFloor anchorTile = listFloors[from];
            scrFloor targetTile = listFloors[floor];
            if (anchorTile == null || targetTile == null) return Vector2.zero;
            Vector3 delta = targetTile.transform.position - anchorTile.transform.position;
            if (!IsUsable(delta.x) || !IsUsable(delta.y)) return Vector2.zero;
            return new Vector2(delta.x / tileSize, delta.y / tileSize);
        }

        /// <summary>把一个时刻的复合姿态写成挂在锚点砖上的 MoveDecorations（时长 0 即瞬变）。</summary>
        private static bool AppendPlanetPoseEvent(List<LevelEvent> output, int anchorFloor, double anchorEntryTime, double anchorEffBpm,
            string planetTag, string eventTag, PlanetPose pose, double timeStart, double timeEnd)
        {
            Vector2 offset = pose.offset;
            if (!IsUsable(offset.x) || !IsUsable(offset.y) || !IsUsable(pose.rotation))
                return false;

            double angleOffset = (timeStart - anchorEntryTime) * anchorEffBpm * 3.0;
            double durationValue = (timeEnd - timeStart) * anchorEffBpm / 60.0;
            if (double.IsNaN(angleOffset) || double.IsInfinity(angleOffset)
                || double.IsNaN(durationValue) || double.IsInfinity(durationValue))
                return false;

            float angleOffsetValue = (float)angleOffset;
            float duration = (float)durationValue;
            if (!IsUsable(angleOffsetValue) || !IsUsable(duration) || duration < 0f)
                return false;

            LevelEvent levelEvent = new LevelEvent(anchorFloor, LevelEventType.MoveDecorations);
            foreach (KeyValuePair<string, PropertyInfo> property in levelEvent.info.propertiesInfo)
            {
                if (property.Value.canBeDisabled)
                    levelEvent.disabled[property.Key] = true;
            }

            PlanetMotionPlanner.SetGeneratedNote(levelEvent, "移动星球");
            levelEvent["tag"] = planetTag;
            levelEvent["duration"] = duration;
            levelEvent["angleOffset"] = angleOffsetValue;
            levelEvent["ease"] = Ease.Linear;
            levelEvent["eventTag"] = eventTag ?? "";
            // 摆放参照显式写成 Tile 但保持 disabled：只借"目标 = 装饰初始位置 + 偏移"的重扫语义，
            // 不依赖游戏的默认取值，也不会让这条事件回头改装饰自己的锚点
            levelEvent["relativeTo"] = DecPlacementType.Tile;
            levelEvent.disabled["positionOffset"] = false;
            levelEvent["positionOffset"] = offset;
            levelEvent.disabled["rotationOffset"] = false;
            levelEvent["rotationOffset"] = pose.rotation;
            output.Add(levelEvent);
            return true;
        }

        private static MotionOutputMode ReadMotionOutputMode(LevelEvent panel)
        {
            return panel != null && panel.TryGet("motionOutputMode", out MotionOutputMode mode)
                && mode == MotionOutputMode.FrameByFrame ? mode : MotionOutputMode.Simple;
        }

        private static bool AppendSimpleMotionEvent(List<LevelEvent> output, int floor, double anchorTime, double bpm,
            string tag, string eventTag, Vector2 position, float rotation, double start, double end, Ease ease)
        {
            float offset = (float)((start - anchorTime) * bpm * 3.0);
            float duration = (float)((end - start) * bpm / 60.0);
            if (!IsUsable(offset) || !IsUsable(duration) || duration < 0f) return false;
            if (!IsUsable(position.x) && !IsUsable(position.y) && !IsUsable(rotation)) return false;
            LevelEvent e = new LevelEvent(floor, LevelEventType.MoveDecorations);
            foreach (KeyValuePair<string, PropertyInfo> p in e.info.propertiesInfo)
                if (p.Value.canBeDisabled) e.disabled[p.Key] = true;
            e["relativeTo"] = DecPlacementType.Tile;
            e["tag"] = tag;
            e["eventTag"] = eventTag ?? "";
            e["angleOffset"] = offset;
            e["duration"] = duration;
            e["ease"] = ease;
            if (IsUsable(position.x) || IsUsable(position.y))
            {
                e.disabled["positionOffset"] = false;
                e["positionOffset"] = position;
            }
            if (IsUsable(rotation))
            {
                e.disabled["rotationOffset"] = false;
                e["rotationOffset"] = rotation;
            }
            PlanetMotionPlanner.SetGeneratedNote(e, "移动星球");
            output.Add(e);
            return true;
        }

        // 通用整理：只合并除目标 tag 外序列化字段完全相同的移动装饰，绝不改缓动或跨时刻合并。
        // 外部事件标签/备注也参与比较，避免误合并触发域；不同目标不会互相 Kill。
        private static void MergeEquivalentMotionTargets(List<LevelEvent> events)
        {
            Dictionary<string, LevelEvent> seen = new Dictionary<string, LevelEvent>(StringComparer.Ordinal);
            for (int i = 0; i < events.Count; i++)
            {
                LevelEvent e = events[i];
                if (e == null || e.eventType != LevelEventType.MoveDecorations) continue;
                string key = MotionEventKey(e);
                if (key == null) continue;
                if (!seen.TryGetValue(key, out LevelEvent previous)) { seen[key] = e; continue; }
                HashSet<string> currentTargets = new HashSet<string>((e.GetString("tag") ?? "")
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
                string[] previousTargets = (previous.GetString("tag") ?? "")
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                // 同一对象重复执行也可能影响 Kill 顺序；这种情况不压缩。
                if (currentTargets.Count == 0 || previousTargets.Length == 0 || previousTargets.Any(currentTargets.Contains)) continue;
                int previousIndex = events.IndexOf(previous);
                bool intervening = false;
                for (int j = previousIndex + 1; j < i; j++)
                {
                    string[] middleTargets = (events[j].GetString("tag") ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (middleTargets.Any(currentTargets.Contains)) { intervening = true; break; }
                }
                if (intervening) continue;
                string[] targets = (previous.GetString("tag") + " " + e.GetString("tag"))
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                previous["tag"] = string.Join(" ", targets.Distinct());
                events.RemoveAt(i--);
            }
        }

        private static string MotionEventKey(LevelEvent e)
        {
            Dictionary<string, object> fields = e.Encode();
            fields.Remove("tag");
            fields["_disabled"] = e.disabled;
            fields["_relativeTo"] = e.Get<DecPlacementType>("relativeTo");
            var text = new System.Text.StringBuilder();
            return Write(fields) ? text.ToString() : null;

            bool Write(object value)
            {
                if (value == null) { text.Append("null;"); return true; }
                if (value is string s) { text.Append('s').Append(s.Length).Append(':').Append(s); return true; }
                if (value is Vector2 vector) return Write(vector.x) && Write(vector.y);
                if (value is float f) { text.Append('f').Append(f.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(';'); return true; }
                if (value is double d) { text.Append('d').Append(d.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(';'); return true; }
                if (value is bool boolean) { text.Append(boolean ? "true;" : "false;"); return true; }
                if (value is Enum || value is int || value is long)
                { text.Append(value.GetType().FullName).Append(':').Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)).Append(';'); return true; }
                if (value is System.Collections.IDictionary dictionary)
                {
                    text.Append('{');
                    var keys = new List<string>();
                    foreach (object item in dictionary.Keys) { if (!(item is string)) return false; keys.Add((string)item); }
                    keys.Sort(StringComparer.Ordinal);
                    foreach (string item in keys) if (!Write(item) || !Write(dictionary[item])) return false;
                    text.Append('}'); return true;
                }
                if (value is System.Collections.IEnumerable sequence)
                { text.Append('['); foreach (object item in sequence) if (!Write(item)) return false; text.Append(']'); return true; }
                return false; // 无法完整比较的类型不合并，不能靠显示字符串四舍五入猜相等。
            }
        }

        private static bool IsPoseWithinTolerance(PlanetPose left, PlanetPose right)
        {
            if (!IsUsable(left.offset.x) || !IsUsable(left.offset.y) || !IsUsable(left.rotation))
                return false;
            if (!IsUsable(right.offset.x) || !IsUsable(right.offset.y) || !IsUsable(right.rotation))
                return false;
            return Vector2.Distance(left.offset, right.offset) <= PlanetPositionTolerance
                && Mathf.Abs(Mathf.DeltaAngle(left.rotation, right.rotation)) <= PlanetRotationTolerance;
        }

        /// <summary>
        /// 把 #RRGGBB(AA) 颜色串的透明度换成指定百分比：100 → FF 不透明，0 → 00 全透明。
        /// 游戏里颜色属性存的就是 hex 字符串（LevelEvent.GetColor 走 HexToColor），
        /// 而 set_Item 是纯字典赋值，所以这里必须写字符串、不能写 Color 对象。
        /// </summary>
        private static string WithAlpha(string hexColor, float opacityPercent)
        {
            // 兜底用 ADOFAI 的默认轨道色；正常路径下新建的 AddObject 事件自带 trackColor 默认值
            string rgb = string.IsNullOrEmpty(hexColor) || hexColor.Length < 6 ? "debb7b" : hexColor.Substring(0, 6);
            int alpha = Mathf.Clamp(Mathf.RoundToInt(opacityPercent / 100f * 255f), 0, 255);
            return rgb + alpha.ToString("x2");
        }

        /// <summary>
        /// 蓝/红两颗星球的唯一差异只有颜色、标签和初始位置；轴心、深度、大小、视差完全一致，
        /// 因此只算一次公共几何，再按"摆放规格"逐颗创建，避免两段平行代码改一边忘一边。
        /// </summary>
        private static void CreatePlanets(scnEditor editor, LevelEvent dataPanel, int from, float pivotTrackOffset, string tag, string bluePlanetTag, string redPlanetTag, int planetDepth, Vector2 genOffset)
        {
            string blueColor = dataPanel.Get<string>("planetBlueColor");
            string redColor = dataPanel.Get<string>("planetRedColor");
            Tuple<float, float> parallaxChange = dataPanel.Get<Tuple<float, float>>("parallaxChange");

            // 轨道旋转量决定两颗星球相对生成位置的方向：蓝在中心，红沿 pivot 方向偏 1 格；
            // 轴心偏移两颗相同，都是反方向 1 格（旧实现如此，保持数值不变）
            float pivotRad = pivotTrackOffset * Mathf.Deg2Rad;
            Vector2 orbitDirection = new Vector2(Mathf.Cos(pivotRad), Mathf.Sin(pivotRad));
            Vector2 pivotOffset = new Vector2(-orbitDirection.x, -orbitDirection.y);
            float parallaxValue = parallaxChange.Item1;
            float scaleValue = 100f - parallaxValue;

            CreatePlanetDecoration(editor, from, blueColor, bluePlanetTag, tag, genOffset,
                pivotOffset, planetDepth, scaleValue, parallaxValue);
            CreatePlanetDecoration(editor, from, redColor, redPlanetTag, tag, genOffset + orbitDirection,
                pivotOffset, planetDepth, scaleValue, parallaxValue);
        }

        /// <summary>创建一颗星球装饰。tag 为空时直接用星球标签，否则写成"{主标签} {星球标签}"（旧格式）。</summary>
        private static void CreatePlanetDecoration(scnEditor editor, int floor, string color, string planetTag, string tag,
            Vector2 position, Vector2 pivotOffset, int depth, float scaleValue, float parallaxValue)
        {
            LevelEvent deco = new LevelEvent(floor, LevelEventType.AddObject);
            deco["relativeTo"] = DecPlacementType.Tile;
            deco["objectType"] = ObjectDecorationType.Planet;
            deco["planetColorType"] = PlanetDecorationColorType.Custom;
            deco["planetColor"] = color;
            deco["planetTailColor"] = color;
            deco["position"] = position;
            deco["pivotOffset"] = pivotOffset;
            deco["tag"] = string.IsNullOrEmpty(tag) ? planetTag : $"{tag} {planetTag}";
            deco["depth"] = depth;
            deco["scale"] = new Vector2(scaleValue, scaleValue);
            deco["parallax"] = new Vector2(parallaxValue, parallaxValue);

            editor.levelData.decorations.Add(deco);
            scrDecorationManager.instance.CreateDecoration(deco, out _, -1);
        }


        private static void ApplyTrackIcon(LevelEvent levelEvent, scrFloor floor, float trackRotation)
        {
            // FloorMesh 把图标方向烘进几何，shader 的 _IconAngle 存的是反号弧度（SetFloorIconAngle(T) 写 -T），故取负读回可见局部角，再减本砖几何形状角 trackRotation 做补偿
            float localIconAngle = 0f;
            bool sourceFlip = false;
            if (floor.floorRenderer is FloorMeshRenderer mesh)
            {
                localIconAngle = -mesh.material.GetFloat("_IconAngle") * Mathf.Rad2Deg;
                sourceFlip = mesh.material.GetInt("_IconFlipped") != 0;
            }
            else if (floor.iconsprite != null)
            {
                localIconAngle = floor.iconsprite.transform.localEulerAngles.z;
                sourceFlip = floor.iconsprite.flipX;
            }

            levelEvent["trackIconAngle"] = Mathf.Repeat(localIconAngle - trackRotation, 360f);
            levelEvent["trackIconFlipped"] = sourceFlip;

            // 图标映射走静态表（TrackIconMapping），不再维护 25 分支 switch；
            // 表里未收录的枚举值按旧 switch 的 default 处理成 None
            CustomFloorIcon icon = TrackIconMapping.Resolve(floor.floorIcon);
            levelEvent["trackIcon"] = icon;
            if (icon == CustomFloorIcon.Swirl)
            {
                // 仅漩涡图标要额外写"红色漩涡"：按本砖实际扫过角度决定方向，midSpin 没有扫角时按整圈
                float sweep = (float)scrMisc.GetAngleMoved((float)floor.entryangle, (float)floor.exitangle, !floor.isCCW);
                if (Mathf.Abs(sweep) <= 1E-06f && !floor.midSpin)
                {
                    sweep = (float)Math.PI * 2f;
                }
                levelEvent["trackRedSwirl"] = floor.isFake ? floor.redSwirl : sweep < (float)Math.PI;
            }
        }

        private static void CreateAnimation(scnEditor editor, LevelEvent dataPanel, TrackData[] trackDatas, RandomSource random, int from, int to, string tag)
        {
            // 面板读取、事件规划、落盘三步分开：BuildAnimationEvents 是纯函数，可脱离编辑器做离线对拍
            AnimationSettings settings = AnimationSettings.Read(dataPanel);

            float[] arrivalTimes = new float[trackDatas.Length];
            float[] departureTimes = new float[trackDatas.Length];
            for (int floor = 0; floor < trackDatas.Length; floor++)
            {
                // 先在 double 时间轴上减锚点，再转 float，首砖也不另扣倒计时。
                arrivalTimes[floor] = (float)(trackDatas[floor].gameEntryTime - trackDatas[from].gameEntryTime);
                departureTimes[floor] = (float)(trackDatas[floor].gameExitTime - trackDatas[from].gameEntryTime);
            }

            List<AnimationEventData> events = MultiTrackAnimationPlanner.BuildAnimationEvents(
                settings, tag, arrivalTimes, departureTimes, trackDatas[from].bpm, from, to, random);
            int before = editor.events.Count;
            MultiTrackAnimationPlanner.Emit(editor, events);
            if (ReadMotionOutputMode(dataPanel) == MotionOutputMode.Simple)
            {
                List<LevelEvent> generated = editor.events.Skip(before).ToList();
                MergeEquivalentMotionTargets(generated);
                editor.events.RemoveRange(before, editor.events.Count - before);
                editor.events.AddRange(generated);
            }
        }
    }
}
