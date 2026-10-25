using ADOFAI;
using ADOFAI.LevelEditor.Controls;
using DG.Tweening;
using HarmonyLib;
using MultiTrackHelper.Features;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityModManagerNet;

namespace MultiTrackHelper
{
    /// <summary>
    /// The main class for the mod. Call other parts of your code from this
    /// class.
    /// </summary>
    public static class Main
    {
        /// <summary>本模组占用的事件类型编号，与 CustomTab.type、各补丁里的 810 一致。</summary>
        internal const int MultiTrackEventType = 810;

        /// <summary>事件类型名：GCS 字典键、面板对象名、LevelEventInfo.name。</summary>
        internal const string MultiTrackEventName = "MultiTrackHelper";

        private static Harmony harmony;
        private static MethodInfo loadImageMethod;
        private static Sprite iconCache;

        /// <summary>面板内容里的空白占位对象（给倍频器的每组之间撑出间隔）。</summary>
        private static readonly List<GameObject> insertSpacers = new List<GameObject>();

        /// <summary>当前面板对应的 LevelEvent（切换面板时由 ShowPanel 补丁写入）。</summary>
        internal static LevelEvent MultiTrackLevelEvent { get; set; }

        /// <summary>注入给编辑器的自定义标签页定义。</summary>
        internal static CustomTab MultiTrack { get; set; }

        /// <summary>面板文本查表（Localizations.json）。</summary>
        internal static Localization Localizations { get; set; }

        /// <summary>当前该显示哪些属性，名字对应面板里的控件对象名。</summary>
        internal static List<string> propertiesToActive { get; set; }

        /// <summary>标签页/面板是否已注入（每次编辑器 Awake 前重置，Start 兜底用）。</summary>
        internal static bool InsertUiInjected;

        /// <summary>
        /// Whether the mod is enabled.
        /// </summary>
        public static bool IsEnabled { get; private set; }

        /// <summary>
        /// UMM's logger instance.
        /// </summary>
        public static UnityModManager.ModEntry.ModLogger Logger { get; private set; }

        public static UnityModManager.ModEntry ModEntry { get; private set; }

        /// <summary>
        /// Perform any initial setup with the mod here.
        /// </summary>
        /// <param name="modEntry">UMM's mod entry for the mod.</param>
        internal static void Setup(UnityModManager.ModEntry modEntry)
        {
            Logger = modEntry.Logger;
            modEntry.OnToggle = OnToggle;
            ModEntry = modEntry;
            Localizations = new Localization(Path.Combine(modEntry.Path, "Localizations.json"));

            MultiTrack = new CustomTab(
                MultiTrackEventType,
                MultiTrackEventName,
                CreateSprite(Path.Combine(modEntry.Path, "MultiTrackIcon.png")),
                // saveSetting 只是内存缓存开关：按事件类型复用同一个 LevelEvent 实例（切面板再回来值还在），
                // 不写磁盘、不进关卡存档；关闭模组时由 StopMod 清掉。
                saveSetting: true,
                properties: PrefabProperties.Properties.Select(property => property.ToData()).ToList());

            SetActiveProperties(PrefabProperties.CreateTrackToActive);
        }

        /// <summary>切换面板上要显示的属性组（名字对应控件对象名）。</summary>
        internal static void SetActiveProperties(List<string> propertyNames)
        {
            propertiesToActive = propertyNames;
        }

        /// <summary>
        /// Handler for toggling the mod on/off.
        /// </summary>
        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            IsEnabled = value;
            if (value)
            {
                StartMod(modEntry);
            }
            else
            {
                StopMod(modEntry);
            }

            RequestEditorReload();
            return true;
        }

        /// <summary>
        /// 在 UMM 里开关 mod 后重启编辑器并重载当前关卡：本模组在 scnEditor.Awake/Start 时注入标签页/事件类型，
        /// 只有重载编辑器才能干净地加上（开启）或去掉（关闭）这些注入。
        /// 仅在关卡编辑器里生效；有未保存改动时走游戏自带的"未保存"提示，避免静默丢失改动。
        /// </summary>
        private static void RequestEditorReload()
        {
            try
            {
                scnEditor editor = ADOBase.editor;
                if (editor == null)
                    return;

                Logger?.Log("mod 开关切换：请求重启编辑器并重载当前关卡");
                editor.CheckUnsavedChanges(RestartEditorWithWipe, false);
            }
            catch (Exception e)
            {
                Logger?.Log("重启编辑器失败: " + e);
            }
        }

        /// <summary>
        /// 用游戏自带的黑屏过场重启编辑器：设置 levelToOpenOnLoad 后用 scrLoader.LoadSceneWithTransition
        /// 切到 scnEditor（与游戏 QuitToMenu 的过场同一套 API）。失败则回退到无过场的 RestartScene。
        /// </summary>
        private static void RestartEditorWithWipe()
        {
            try
            {
                string levelPath = ADOBase.levelPath;
                if (!string.IsNullOrEmpty(levelPath))
                    scnEditor.levelToOpenOnLoad = levelPath;

                DOTween.KillAll(false);

                scrLoader loader = ADOBase.loader;
                if (loader == null)
                {
                    ADOBase.RestartScene();
                    return;
                }

                loader.LoadSceneWithTransition(WipeDirection.StartsFromRight, "scnEditor");
            }
            catch (Exception e)
            {
                Logger?.Log("过场重启失败，回退 RestartScene: " + e);
                ADOBase.RestartScene();
            }
        }

        private static void StartMod(UnityModManager.ModEntry modEntry)
        {
            harmony = new Harmony(modEntry.Info.Id);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            // PACL 的删砖 scope 只快照 events，装饰要单独记账；此刻它可能还没异步加载完，Register 只存下实例稍后再试
            Patches.PaclDecorationUndo.Register(harmony);
        }

        private static void StopMod(UnityModManager.ModEntry modEntry)
        {
            harmony.UnpatchAll(modEntry.Info.Id);
            // UnpatchAll 不会回收已经插进 UnityEvent 的委托，按引用自己摘干净
            Patches.TextEndEditPatch.ReleaseAll();
            Patches.PaclDecorationUndo.Reset();
            // 本模组挂在游戏静态表/静态字段上的内存缓存全部清掉：面板事件缓存、粘贴元数据、PACL 守卫计数。
            // 这三样都只活在内存里，禁用后再启用会重新建立；剪贴板元数据的清理仅在关模组时做，
            // 不能放在 Awake（跨关卡粘贴需要在编辑器重载后仍认得那批剪贴板条目）。
            Patches.InspectorPanelShowPanelPatch.saves.Clear();
            Patches.PasteSyncPatches.ResetAll();
            Patches.PaclUndoGuardPatches.Reset();
            // 清掉注入到 GCS 的静态数据（跨场景重载仍存在），否则禁用后事件类型/图标仍残留
            Patches.scnEditorAwakePatch.RemoveInjection();
            insertSpacers.Clear();
            MultiTrackLevelEvent = null;
            InsertUiInjected = false;
        }

        // ── 面板显隐 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 按当前功能与生成类型，决定面板里哪些控件可见。
        /// 调用点很多（值变化、切面板、面板重建），所以全程判空：面板还没建好时直接返回，不抛异常。
        /// </summary>
        public static void activeChilden()
        {
            if (scnEditor.instance == null || scnEditor.instance.settingsPanel == null || MultiTrack == null)
                return;

            RefreshActiveProperties();

            PropertiesPanel panel = FindPanel();
            if (panel == null)
                return;

            Transform content = FindPanelContent(panel);
            if (content == null)
                return;

            Dictionary<string, Transform> childMap = IndexChildren(content);
            HideAllChildren(content);
            ShowActiveChildren(childMap);
            ApplyCustomLabels(panel);
            UpdateInsertSpacers(content, childMap);
        }

        /// <summary>插入倍频/轨道尺寸变化这两种功能的可见属性是动态算出来的，其余用固定列表。</summary>
        private static void RefreshActiveProperties()
        {
            if (IsInsertFeatureActive())
                SetActiveProperties(InsertBpmMultiplierFeature.BuildActiveProperties());
            else if (IsTrackSizeChangeActive())
                SetActiveProperties(PrefabProperties.TrackSizeChangeToActive);
        }

        private static PropertiesPanel FindPanel()
        {
            if (scnEditor.instance == null || scnEditor.instance.settingsPanel == null || MultiTrack == null)
                return null;

            List<PropertiesPanel> panels = scnEditor.instance.settingsPanel.panelsList;
            for (int i = 0; i < panels.Count; i++)
            {
                PropertiesPanel panel = panels[i];
                if (panel != null && panel.name == MultiTrack.name)
                    return panel;
            }
            return null;
        }

        private static Transform FindPanelContent(PropertiesPanel panel)
        {
            Transform viewport = panel.transform.Find("viewport");
            return viewport != null ? viewport.Find("content") : null;
        }

        /// <summary>一次性建立 名字→子对象 映射，避免对每个属性都做一次 Transform.Find（O(n²)）。</summary>
        private static Dictionary<string, Transform> IndexChildren(Transform content)
        {
            var childMap = new Dictionary<string, Transform>(content.childCount);
            for (int i = 0; i < content.childCount; i++)
            {
                Transform child = content.GetChild(i);
                if (!childMap.ContainsKey(child.name))
                    childMap[child.name] = child;
            }
            return childMap;
        }

        private static void HideAllChildren(Transform content)
        {
            for (int i = 0; i < content.childCount; i++)
                content.GetChild(i).gameObject.SetActive(false);
        }

        /// <summary>按 propertiesToActive 的顺序显示并排位；占位符（属性名里有但不该出现的）直接跳过。</summary>
        private static void ShowActiveChildren(Dictionary<string, Transform> childMap)
        {
            if (propertiesToActive == null)
                return;

            for (int i = 0; i < propertiesToActive.Count; i++)
            {
                string propertyName = propertiesToActive[i];
                if (string.IsNullOrEmpty(propertyName))
                    continue;
                if (propertyName == "trackGenerationModeNote" && !ShouldShowGenerationModeNote())
                    continue;

                Transform child;
                if (!childMap.TryGetValue(propertyName, out child) || child == null)
                    continue;

                child.gameObject.SetActive(true);
                child.SetSiblingIndex(i);
            }
        }

        /// <summary>轴心偏移的说明文本只在"集中式 + 轴心偏移"这一种组合下才有意义。</summary>
        private static bool ShouldShowGenerationModeNote()
        {
            LevelEvent levelEvent = MultiTrackLevelEvent;
            return levelEvent != null
                && levelEvent.Get<TrackDistribution>("TrackDistribution") == TrackDistribution.Centralized
                && levelEvent.Get<TrackGenerationMode>("trackGenerationMode") == TrackGenerationMode.PivotOffset;
        }

        /// <summary>逐行字面标签要在每次显隐之后重写一次（面板重建会把文本重置成本地化键）。</summary>
        private static void ApplyCustomLabels(PropertiesPanel panel)
        {
            foreach (KeyValuePair<string, Func<string>> pair in PrefabProperties.CustomLabels)
            {
                Property property;
                if (panel.properties.TryGetValue(pair.Key, out property) && property != null && property.label != null)
                    property.label.text = pair.Value();
            }
        }

        // ── 倍频器分组之间的占位条 ──────────────────────────────────────────────

        /// <summary>关卡（重新）加载时丢弃缓存的占位对象引用（对象已随场景销毁）。</summary>
        public static void ResetInsertSpacers()
        {
            insertSpacers.Clear();
        }

        private static void UpdateInsertSpacers(Transform content, Dictionary<string, Transform> childMap)
        {
            // 清理任何被销毁的引用（场景重载可能只销毁了一部分）
            for (int i = insertSpacers.Count - 1; i >= 0; i--)
            {
                if (insertSpacers[i] == null)
                    insertSpacers.RemoveAt(i);
            }

            int groups = IsInsertFeatureActive() ? InsertBpmMultiplierFeature.RowCount : 0;
            int needed = Mathf.Max(groups - 1, 0);

            while (insertSpacers.Count < needed)
            {
                var spacer = new GameObject("mth_insertSpacer", typeof(RectTransform), typeof(LayoutElement));
                spacer.transform.SetParent(content, false);
                spacer.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 28f);
                LayoutElement element = spacer.GetComponent<LayoutElement>();
                element.minHeight = 28f;
                element.preferredHeight = 28f;
                element.flexibleHeight = 0f;
                insertSpacers.Add(spacer);
            }

            for (int group = 2; group <= groups; group++)
            {
                GameObject spacer = insertSpacers[group - 2];
                if (spacer == null)
                    continue;

                Transform row;
                if (!childMap.TryGetValue(InsertBpmMultiplierFeature.PosDegName(group), out row) || row == null)
                {
                    spacer.SetActive(false);
                    continue;
                }

                spacer.SetActive(true);
                spacer.transform.SetSiblingIndex(row.GetSiblingIndex());
            }

            for (int i = needed; i < insertSpacers.Count; i++)
            {
                if (insertSpacers[i] != null)
                    insertSpacers[i].SetActive(false);
            }
        }

        // ── 功能状态查询 ────────────────────────────────────────────────────────

        /// <summary>当前面板是否处于"插入 BPM 倍频"功能。</summary>
        public static bool IsInsertFeatureActive()
        {
            LevelEvent levelEvent = MultiTrackLevelEvent;
            if (levelEvent == null || MultiTrack == null || !SettingsPanelIsOnOurTab())
                return false;
            return levelEvent.Get<TrackFeatures>("TrackFeatures") == TrackFeatures.InsertBpmMultiplier;
        }

        /// <summary>当前面板是否处于"轨道尺寸变化"功能。</summary>
        public static bool IsTrackSizeChangeActive()
        {
            LevelEvent levelEvent = MultiTrackLevelEvent;
            if (levelEvent == null || MultiTrack == null || !SettingsPanelIsOnOurTab())
                return false;
            return levelEvent.Get<TrackFeatures>("TrackFeatures") == TrackFeatures.TrackSizeChange;
        }

        /// <summary>设置面板是否停在本模组标签页上；编辑器还没起来时按"是"处理。</summary>
        private static bool SettingsPanelIsOnOurTab()
        {
            if (scnEditor.instance == null || scnEditor.instance.settingsPanel == null)
                return true;
            return (int)scnEditor.instance.settingsPanel.selectedEventType == MultiTrack.type;
        }

        /// <summary>取本模组的事件数据：面板正停在上面就用面板的，否则用缓存的那份。</summary>
        public static LevelEvent GetEvent(LevelEventType type)
        {
            if (scnEditor.instance == null || MultiTrack == null || (int)type != MultiTrack.type)
                return null;
            var settingsPanel = scnEditor.instance.settingsPanel;
            if (settingsPanel == null)
                return null;
            if (settingsPanel.selectedEventType == type)
                return settingsPanel.selectedEvent;
            if (!MultiTrack.saveSetting)
                return null;
            LevelEvent saved;
            return Patches.InspectorPanelShowPanelPatch.saves.TryGetValue(type, out saved) ? saved : null;
        }

        // ── 切换动画类型时的默认值 ──────────────────────────────────────────────

        /// <summary>
        /// 切到"出现/消失动画"时重置起始砖与透明度，并同步面板控件的显示。
        /// 事件数据必须写（否则用户看到旧值、生成的却是另一套），控件则可能还没建好，逐项判空。
        /// </summary>
        internal static void ApplyAnimationDefaults(TrackAnimation trackAnimation)
        {
            PropertiesPanel panel = FindPanel();
            Dictionary<string, Property> properties = panel != null ? panel.properties : null;

            switch (trackAnimation)
            {
                case TrackAnimation.DisappearAnimation:
                    ApplyTileDefault(properties, "startTile", new Tuple<int, TileRelativeTo>(-1, TileRelativeTo.ThisTile));
                    ApplyTextDefault(properties, "opacity", 0f, "0");
                    break;
                case TrackAnimation.AppearAnimation:
                    ApplyTileDefault(properties, "startTile", new Tuple<int, TileRelativeTo>(8, TileRelativeTo.ThisTile));
                    ApplyTextDefault(properties, "opacity", 100f, "100");
                    break;
            }
        }

        private static void ApplyTileDefault(Dictionary<string, Property> properties, string key, Tuple<int, TileRelativeTo> tile)
        {
            Property property;
            if (properties != null && properties.TryGetValue(key, out property)
                && property != null && property.control is PropertyControl_Tile)
            {
                ((PropertyControl_Tile)property.control).tileValue = tile;
            }
            if (MultiTrackLevelEvent != null)
                MultiTrackLevelEvent[key] = tile;
        }

        private static void ApplyTextDefault(Dictionary<string, Property> properties, string key, float value, string display)
        {
            Property property;
            if (properties != null && properties.TryGetValue(key, out property)
                && property != null && property.control is PropertyControl_Text)
            {
                ((PropertyControl_Text)property.control).text = display;
            }
            if (MultiTrackLevelEvent != null)
                MultiTrackLevelEvent[key] = value;
        }

        // ── 图标 ────────────────────────────────────────────────────────────────

        internal static Sprite CreateSprite(string path)
        {
            if (iconCache != null)
                return iconCache;
            if (!File.Exists(path))
                return null;

            // Unity 6 的 LoadImage 扩展签名含 ReadOnlySpan<byte>，本机编译器 corlib 缺少 Span 类型，
            // 因此用反射显式绑定 byte[] 重载（运行时与旧版一致）。
            if (loadImageMethod == null)
                loadImageMethod = typeof(ImageConversion).GetMethod("LoadImage", new Type[] { typeof(Texture2D), typeof(byte[]) });
            if (loadImageMethod == null)
            {
                Logger?.Log("未找到 LoadImage(byte[]) 重载，跳过图标加载");
                return null;
            }

            var texture = new Texture2D(0, 0);
            if (!(bool)loadImageMethod.Invoke(null, new object[] { texture, File.ReadAllBytes(path) }))
                return null;

            iconCache = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
            return iconCache;
        }
    }
}
