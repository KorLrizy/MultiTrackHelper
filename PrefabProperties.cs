using System;
using System.Collections.Generic;
using MultiTrackHelper.PropertyCollection;
using UnityEngine;

namespace MultiTrackHelper
{
    /// <summary>
    /// 面板控件清单。
    /// 这里的顺序就是面板显示顺序、也是 ToData() 导出顺序。
    ///
    /// 两个名字要分清：
    /// · name（各工厂方法的第一个参数）是属性名，同时是控件对象名、LevelEvent 里的数据键、
    ///   条件表（enableIf/disableIf）与显隐列表里的匹配键；它一变，已保存的事件数据与显隐列表就全对不上，所以不能改。
    /// · key 是本地化键，只决定控件标题文本（null 表示用游戏默认的 editor.{事件名}.{属性名}），改它不动任何数据。
    ///
    /// 顺序一变，离线对拍（182 项 / 35167 字节）就对不上，因此也不要重排。
    /// </summary>
    public static class PrefabProperties
    {
        public static List<Property> Properties { get; } = new List<Property>();

        /// <summary>
        /// 逐行的字面标签(PropertyInfo.customLabel)。行号无法用固定本地化键表达，
        /// 这里存"取标签的委托"而非成品文本——mod 加载时游戏语言尚未确定，
        /// 必须等到编辑器打开、PropertyInfo 构建时再按当前语言求值。
        /// </summary>
        public static Dictionary<string, Func<string>> CustomLabels { get; } = new Dictionary<string, Func<string>>();

        static PrefabProperties()
        {
            AddRangeProperties();              // 1-4   砖块范围
            AddMotionProperties();             // 5-15  位移/缩放/透明度/缓动
            AddDistributionProperties();       // 16-19 分布方式与轴心
            AddSyncProperties();               // 20-25 同步开关
            AddDepthProperties();              // 26-29 深度与生成位置
            AddAnimationProperties();          // 30-35 动画与随机种子
            AddPlanetProperties();             // 36-46 星球与视差
            AddTrackSizeProperties();          // 47-50 轨道尺寸变化
            AddCreateButton();                 // 51    生成按钮
            AddInsertBpmMultiplierProperties();
        }

        private static void Add(Property property)
        {
            Properties.Add(property);
        }

        /// <summary>登记一个控件，并给它挂一条逐行字面标签（构建 PropertyInfo 时求值）。</summary>
        private static void AddLabeled(string name, Property property, Func<string> label)
        {
            Properties.Add(property);
            CustomLabels[name] = label;
        }

        /// <summary>
        /// 构造条件表。游戏的 PropertyInfo 要求 [键, 值] 交替的字符串列表，
        /// 多个条件之间是"或"的关系（任一满足即按该条件处理）。
        /// </summary>
        private static Dictionary<string, string> When(params string[] pairs)
        {
            var conditions = new Dictionary<string, string>();
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                conditions[pairs[i]] = pairs[i + 1];
            return conditions;
        }

        private static string L(string key)
        {
            return Main.Localizations != null ? (Main.Localizations.GetValue(key) ?? key) : key;
        }

        /// <summary>砖块范围：作用在哪些砖、以及起始砖。</summary>
        private static void AddRangeProperties()
        {
            Add(Property.Tile("affectTileRangeFrom", value_default: (0, TileRelativeTo.Start),
                hideButtons: new List<int> { Property.THIS_TILE },
                disableIf: When("affectAt", "SelectedTiles"),
                key: "mth.affectTileRangeFrom"));
            Add(Property.Tile("affectTileRangeTo", value_default: (0, TileRelativeTo.Start),
                hideButtons: new List<int> { Property.THIS_TILE },
                disableIf: When("affectAt", "SelectedTiles"),
                key: "mth.affectTileRangeTo"));
            Add(Property.EnumOf<AffectAt>("affectAt", value_default: AffectAt.SelectedTiles, key: "mth.affectAt"));
            Add(Property.Tile("startTile", value_default: (-1, TileRelativeTo.ThisTile),
                hideButtons: new List<int> { Property.START, Property.END },
                key: "mth.startTile"));
        }

        /// <summary>位移、旋转、缩放、透明度与缓动；parallaxRange/scaleRange 等都是"起始→结束"两值。</summary>
        private static void AddMotionProperties()
        {
            Add(Property.EnumOf<MotionOutputMode>("motionOutputMode", value_default: MotionOutputMode.Simple,
                key: "mth.motionOutputMode"));
            Add(Property.Input("duration", InputKind.Float, value_default: 1, min: 0,
                unit: "beats", key: "editor.duration"));
            Add(Property.FloatPair("xPosOffsetRange", value_default: new Vector2(0, 0),
                canBeDisabled: true, startEnabled: true, isRange: false, key: "mth.xPosOffsetRange"));
            Add(Property.FloatPair("yPosOffsetRange", value_default: new Vector2(0, 0),
                canBeDisabled: true, startEnabled: true, isRange: false, key: "mth.yPosOffsetRange"));
            Add(Property.FloatPair("rotationOffsetRange", value_default: new Vector2(0, 0),
                canBeDisabled: true, startEnabled: true, isRange: false, key: "mth.rotationOffsetRange"));
            Add(Property.FloatPair("parallaxRange", value_default: new Vector2(0, 0),
                canBeDisabled: true, startEnabled: false, isRange: false, key: "mth.parallaxRange"));
            Add(Property.Input("parallaxRevertTo", InputKind.Float, value_default: 0,
                canBeDisabled: true, startEnabled: false, key: "mth.parallaxRevertTo"));
            Add(Property.FloatPair("scaleRange", value_default: new Vector2(100, 100),
                canBeDisabled: true, startEnabled: true, isRange: false, key: "mth.scaleRange"));
            Add(Property.Input("opacity", InputKind.Float, value_default: 0, unit: "%",
                canBeDisabled: true, startEnabled: true, key: "editor.opacity"));
            Add(Property.Input("angleOffset", InputKind.Float, value_default: 0, unit: "°",
                key: "editor.angleOffset"));
            Add(Property.Ease("ease", key: "editor.ease"));
            Add(Property.Input("scaleRevertTo", InputKind.Float, value_default: 100,
                canBeDisabled: true, startEnabled: true, key: "mth.scaleRevertTo"));
        }

        /// <summary>分布式/集中式的选择，以及集中式下的轴心偏移（trackRotation）。</summary>
        private static void AddDistributionProperties()
        {
            Add(Property.EnumOf<TrackDistribution>("TrackDistribution", value_default: TrackDistribution.Centralized,
                disableIf: When("syncSpeedOnPaste", "Enabled"), key: "mth.trackDistribution"));
            Add(Property.EnumOf<TrackGenerationMode>("trackGenerationMode", value_default: TrackGenerationMode.Position,
                enableIf: When("TrackDistribution", "Centralized"), key: "mth.trackGenerationMode"));
            Add(Property.Note("trackGenerationModeNote", noteKey: "mth.trackGenerationModeNote",
                key: "mth.generationModeLimit"));
            Add(Property.Input("trackRotation", InputKind.Float, value_default: 0, unit: "°",
                enableIf: When("TrackDistribution", "Centralized"), key: "mth.rotation"));
        }

        /// <summary>同步类开关；opacitySyncType 只在开启透明度同步后可选。</summary>
        private static void AddSyncProperties()
        {
            Add(Property.Bool("syncSize", value_default: true, key: "mth.syncSize"));
            Add(Property.Bool("syncOpacity", value_default: true, key: "mth.syncOpacity"));
            Add(Property.EnumOf<OpacitySyncType>("opacitySyncType", value_default: OpacitySyncType.TrackOpacity,
                enableIf: When("syncOpacity", "Enabled"), key: "mth.opacitySyncType"));
            Add(Property.Bool("syncStyle", value_default: true, key: "mth.syncStyle"));
            Add(Property.Bool("syncMoveTrack", value_default: true,
                // enableIf 条件按 OR 匹配，分布式残留轴心选项不应被禁用。
                enableIf: When("TrackDistribution", "Distributed", "trackGenerationMode", "Position"),
                key: "mth.syncMoveTrack"));
            Add(Property.Bool("useIncreasingDepth", key: "mth.useIncreasingDepth"));
        }

        /// <summary>递增深度；开启后 initialDepth/increasingValue 生效、depth 置灰。</summary>
        private static void AddDepthProperties()
        {
            Add(Property.Input("initialDepth", InputKind.Int, value_default: 1,
                enableIf: When("useIncreasingDepth", "Enabled"), key: "mth.initialDepth"));
            Add(Property.Input("increasingValue", InputKind.Int, value_default: 1,
                enableIf: When("useIncreasingDepth", "Enabled"), key: "mth.increasingValue"));
            Add(Property.Input("depth", InputKind.Int, value_default: 0,
                disableIf: When("useIncreasingDepth", "Enabled"), key: "mth.depth"));
            Add(Property.Input("generationPosition", InputKind.Vector2, value_default: new Vector2(0, 0),
                key: "mth.generationPosition"));
        }

        /// <summary>生成类型与动画形态；随机种子只在勾选"使用种子"后可填。</summary>
        private static void AddAnimationProperties()
        {
            Add(Property.EnumOf<TrackAnimation>("TrackAnimation", value_default: TrackAnimation.DisappearAnimation,
                key: "mth.TrackAnimation"));
            Add(Property.Input("tag", InputKind.String, value_default: "", key: "mth.tag"));
            Add(Property.EnumOf<TrackFeatures>("TrackFeatures", value_default: TrackFeatures.CreateTrack,
                key: "mth.TrackFeatures"));
            Add(Property.Bool("replaceSameTag", value_default: true, key: "mth.replaceSameTag"));
            Add(Property.Bool("useSeed", key: "mth.useSeed"));
            Add(Property.Input("seed", InputKind.Int, value_default: 1,
                enableIf: When("useSeed", "Enabled"), key: "mth.seed"));
        }

        /// <summary>星球生成与视差改动；未勾选"使用星球"/"改变视差"时相关项置灰。</summary>
        private static void AddPlanetProperties()
        {
            Add(Property.Bool("usePlanet", value_default: true, key: "mth.usePlanet"));
            Add(Property.Color("planetBlueColor", value_default: new Color(0f, 0f, 1f, 1f),
                key: "mth.planetBlueColor", enableIf: When("usePlanet", "Enabled")));
            Add(Property.Input("planetBlueTag", InputKind.String, value_default: "",
                enableIf: When("usePlanet", "Enabled"), key: "mth.planetBlueTag"));
            Add(Property.Color("planetRedColor", value_default: new Color(1f, 0f, 0f, 1f),
                key: "mth.planetRedColor", enableIf: When("usePlanet", "Enabled")));
            Add(Property.Input("planetRedTag", InputKind.String, value_default: "",
                enableIf: When("usePlanet", "Enabled"), key: "mth.planetRedTag"));
            Add(Property.EnumOf<TrackDistribution>("planetAnimationDistribution",
                value_default: TrackDistribution.Centralized,
                enableIf: When("usePlanet", "Enabled"),
                disableIf: When("syncSpeedOnPaste", "Enabled"),
                key: "mth.planetAnimationDistribution"));
            Add(Property.Bool("changeParallax", key: "mth.changeParallax"));
            Add(Property.FloatPair("parallaxChange", value_default: new Vector2(0, 0), min: -100, max: 100,
                isRange: false, enableIf: When("changeParallax", "Enabled"), key: "mth.parallaxChange"));
            Add(Property.Bool("affectPlanet", enableIf: When("changeParallax", "Enabled"),
                key: "mth.affectPlanet"));
            Add(Property.EnumOf<TrackDistribution>("eventDistribution", value_default: TrackDistribution.Centralized,
                disableIf: When("syncSpeedOnPaste", "Enabled"), key: "mth.eventDistribution"));
            Add(Property.Bool("syncSpeedOnPaste", value_default: true, key: "mth.syncSpeedOnPaste"));
        }

        /// <summary>
        /// “轨道尺寸变化”的四条范围：范围内逐砖按缓速曲线在起始值→结束值之间插值。
        /// 默认启用、数值 100→100（与上游 MappingHelper 一致）。
        /// </summary>
        private static void AddTrackSizeProperties()
        {
            Add(Property.FloatPair("positionTrackScale", value_default: new Vector2(100, 100), min: 0,
                isRange: false, canBeDisabled: true, startEnabled: true, key: "mth.positionTrackScale"));
            Add(Property.FloatPair("positionTrackOpacity", value_default: new Vector2(100, 100), min: 0,
                isRange: false, canBeDisabled: true, startEnabled: true, key: "mth.positionTrackOpacity"));
            Add(Property.FloatPair("scaleRadiusScale", value_default: new Vector2(100, 100), min: 0,
                isRange: false, canBeDisabled: true, startEnabled: true, key: "mth.scaleRadiusScale"));
            Add(Property.FloatPair("scalePlanetsScale", value_default: new Vector2(100, 100), min: 0,
                isRange: false, canBeDisabled: true, startEnabled: true, key: "mth.scalePlanetsScale"));
        }

        private static void AddCreateButton()
        {
            Add(Property.Button("createButton", Features.MultiTrackFeature.Create, key: "mth.create"));
        }

        /// <summary>
        /// 插入 BPM 倍频：每行四格（位置角度、位置砖数、目标 BPM、倍率）+ 一个锁定开关。
        /// 行号进标签文本，所以这几项走 AddLabeled。
        /// </summary>
        private static void AddInsertBpmMultiplierProperties()
        {
            Add(Property.Bool("realtimeCapture", value_default: false, key: "mth.realtimeCapture"));

            AddLabeled(
                "targetAngleDeg",
                Property.Input("targetAngleDeg", InputKind.Float, value_default: 0, unit: "°"),
                () => string.Format("{0} [{1}]", L("mth.targetAngle"), Features.InsertBpmMultiplierFeature.RangeText));

            AddLabeled(
                "targetAngleBeats",
                Property.Input("targetAngleBeats", InputKind.Float, value_default: 0, unit: "beats"),
                () => string.Format("{0} [{1}]", L("mth.targetAngle"), Features.InsertBpmMultiplierFeature.RangeText));

            for (int i = 1; i <= Features.InsertBpmMultiplierFeature.MaxMultipliers; i++)
            {
                int index = i;

                AddLabeled(
                    Features.InsertBpmMultiplierFeature.PosDegName(i),
                    Property.Input(Features.InsertBpmMultiplierFeature.PosDegName(i), InputKind.Float,
                        value_default: 0, unit: "°"),
                    () => string.Format("{0}{1} · {2} ({3})", L("mth.bpMultiplier"), index,
                        L("mth.bpPosition"), L("mth.degrees")));

                AddLabeled(
                    Features.InsertBpmMultiplierFeature.PosTileName(i),
                    Property.Input(Features.InsertBpmMultiplierFeature.PosTileName(i), InputKind.Int,
                        value_default: 0, unit: "tilesFrom"),
                    () => string.Format("{0} ({1})", L("mth.bpPosition"), L("mth.tiles")));

                AddLabeled(
                    Features.InsertBpmMultiplierFeature.ValueBpmName(i),
                    Property.Input(Features.InsertBpmMultiplierFeature.ValueBpmName(i), InputKind.Float,
                        value_default: 0, unit: "bpm"),
                    () => string.Format("{0} ({1})", L("mth.bpValue"), L("mth.bpm")));

                AddLabeled(
                    Features.InsertBpmMultiplierFeature.ValueMultName(i),
                    Property.Input(Features.InsertBpmMultiplierFeature.ValueMultName(i), InputKind.Float,
                        value_default: 0, unit: "X"),
                    () => string.Format("{0} ({1})", L("mth.bpValue"), L("mth.times")));

                Add(Property.Bool(Features.InsertBpmMultiplierFeature.LockName(i), value_default: false,
                    key: "mth.lock"));
            }

            Add(Property.Button("deleteMultiplierButton", Features.InsertBpmMultiplierFeature.DeleteMultiplier,
                key: "mth.deleteMultiplier"));
            Add(Property.Button("addMultiplierButton", Features.InsertBpmMultiplierFeature.AddMultiplier,
                key: "mth.addMultiplier"));
            Add(Property.Button("calculateButton", Features.InsertBpmMultiplierFeature.Compute,
                key: "mth.calculate"));
        }

        /// <summary>
        /// 生成轨道时可见的属性组。列表里写的是**属性名**（同 name），不是本地化键；
        /// 显隐靠 Main.ShowActiveChildren 按名找控件对象，所以这里的顺序就是面板上的行序。
        /// 已定稿的顺序与成员不要为了排版去调（产品文档已固定）。
        /// </summary>
        public static List<string> CreateTrackToActive { get; set; } = new List<string>()
            {
                "affectTileRangeFrom",
                "affectTileRangeTo",
                "affectAt",
                "TrackFeatures",
                "tag",
                "replaceSameTag",
                "syncSpeedOnPaste",
                "TrackDistribution",
                "trackGenerationMode",
                "motionOutputMode",
                "trackGenerationModeNote",
                "trackRotation",
                "syncSize",
                "syncOpacity",
                "opacitySyncType",
                "syncStyle",
                "syncMoveTrack",
                "useIncreasingDepth",
                "initialDepth",
                "increasingValue",
                "depth",
                "generationPosition",
                "usePlanet",
                "planetBlueColor",
                "planetBlueTag",
                "planetRedColor",
                "planetRedTag",
                "planetAnimationDistribution",
                "changeParallax",
                "parallaxChange",
                "affectPlanet",
                "createButton",
            };

        public static List<string> DisappearAnimationToActive { get; set; } = new List<string>()
            {
                "affectTileRangeFrom",
                "affectTileRangeTo",
                "affectAt",
                "TrackFeatures",
                "TrackAnimation",
                "tag",
                "replaceSameTag",
                "syncSpeedOnPaste",
                "eventDistribution",
                "startTile",
                "duration",
                "xPosOffsetRange",
                "yPosOffsetRange",
                "rotationOffsetRange",
                "scaleRange",
                "parallaxRange",
                "motionOutputMode",
                "opacity",
                "angleOffset",
                "ease",
                "useSeed",
                "seed",
                "createButton",
            };

        public static List<string> AppearAnimationToActive { get; set; } = new List<string>()
            {
                "affectTileRangeFrom",
                "affectTileRangeTo",
                "affectAt",
                "TrackFeatures",
                "TrackAnimation",
                "tag",
                "replaceSameTag",
                "syncSpeedOnPaste",
                "eventDistribution",
                "startTile",
                "duration",
                "xPosOffsetRange",
                "yPosOffsetRange",
                "rotationOffsetRange",
                "scaleRange",
                "scaleRevertTo",
                "parallaxRange",
                "parallaxRevertTo",
                "motionOutputMode",
                "opacity",
                "angleOffset",
                "ease",
                "useSeed",
                "seed",
                "createButton",
            };

        public static List<string> TrackSizeChangeToActive { get; set; } = new List<string>()
            {
                "affectTileRangeFrom",
                "affectTileRangeTo",
                "affectAt",
                "TrackFeatures",
                "positionTrackScale",
                "positionTrackOpacity",
                "scaleRadiusScale",
                "scalePlanetsScale",
                "ease",
                "createButton",
            };
    }
}
