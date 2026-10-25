using ADOFAI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace MultiTrackHelper.Features
{
    /// <summary>清理范围：TracksAndEvents 连装饰与本批 style-only SetObject 一起清；EventsOnly 只清动画事件，保留星球环绕动画与已生成的轨道风格。</summary>
    internal enum CleanupScope
    {
        TracksAndEvents,
        EventsOnly
    }

    internal static class TagCleanup
    {
        internal const string ReservedPrefix = "_MultiTrack";

        private const string TrackStyleKey = "trackStyle";

        private static readonly char[] TagSeparator = { ' ' };

        internal static bool MatchesTrackToken(string token, string tag)
        {
            if (string.IsNullOrEmpty(token))
                return false;

            string batchTag = string.IsNullOrEmpty(tag) ? ReservedPrefix : tag;
            if (token == batchTag)
                return true;
            if (!token.StartsWith(batchTag, StringComparison.Ordinal))
                return false;

            string suffix = token.Substring(batchTag.Length);
            foreach (char c in suffix)
            {
                if (c < '0' || c > '9')
                    return false;
            }
            return suffix.Length > 0;
        }

        private static string[] SplitTag(string tagValue)
        {
            if (string.IsNullOrEmpty(tagValue))
                return Array.Empty<string>();
            return tagValue.Split(TagSeparator, StringSplitOptions.RemoveEmptyEntries);
        }

        private static bool IsPlanetDecoration(LevelEvent deco)
        {
            return deco != null
                && deco.eventType == LevelEventType.AddObject
                && deco.TryGet<object>("objectType", out object objectType)
                && object.Equals(objectType, ObjectDecorationType.Planet);
        }

        private static bool TryGetBatchDeclaration(LevelEvent deco, out string batch, out bool isPlanet, out string[] tokens)
        {
            batch = null;
            isPlanet = false;
            tokens = Array.Empty<string>();

            if (deco == null || deco.eventType != LevelEventType.AddObject)
                return false;
            if (!deco.TryGet<string>("tag", out string tagValue))
                return false;

            tokens = SplitTag(tagValue);
            if (tokens.Length == 0)
                return false;

            isPlanet = IsPlanetDecoration(deco);
            if (isPlanet)
                return false;

            if (tokens.Length >= 2)
            {
                string firstToken = tokens[0];
                if (tokens.Any(t => t != firstToken && MatchesTrackToken(t, firstToken)))
                {
                    batch = firstToken;
                    return true;
                }
            }

            if (tokens.Length == 1 && tokens[0] != ReservedPrefix && MatchesTrackToken(tokens[0], ReservedPrefix))
            {
                batch = ReservedPrefix;
                return true;
            }

            return false;
        }

        private static void AddOwner(Dictionary<string, HashSet<string>> owners, string token, string batch)
        {
            if (!owners.TryGetValue(token, out HashSet<string> batches))
            {
                batches = new HashSet<string>(StringComparer.Ordinal);
                owners[token] = batches;
            }
            batches.Add(batch);
        }

        // 数字后缀本身有歧义（foo2 既可能是本批第 2 号也可能是已存在的 foo2 批），故只认「基名」或「基名±纯整数」
        private static bool NumericNamingOf(string token, string candidateBase)
        {
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(candidateBase))
                return false;
            if (token == candidateBase)
                return true;
            if (token.Length <= candidateBase.Length || !token.StartsWith(candidateBase, StringComparison.Ordinal))
                return false;

            string suffix = token.Substring(candidateBase.Length);
            int digitStart = suffix[0] == '-' ? 1 : 0;
            if (digitStart == suffix.Length)
                return false;
            for (int i = digitStart; i < suffix.Length; i++)
            {
                if (suffix[i] < '0' || suffix[i] > '9')
                    return false;
            }
            return true;
        }

        internal static Dictionary<string, HashSet<string>> CollectBatchOwners(IEnumerable<LevelEvent> decorations, out HashSet<string> planetTokens, out HashSet<string> unownedPlanetTokens)
        {
            Dictionary<string, HashSet<string>> owners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            planetTokens = new HashSet<string>(StringComparer.Ordinal);
            unownedPlanetTokens = new HashSet<string>(StringComparer.Ordinal);
            if (decorations == null)
                return owners;

            HashSet<string> trackBases = new HashSet<string>(StringComparer.Ordinal);
            List<string[]> planetTokenLists = new List<string[]>();

            foreach (LevelEvent deco in decorations)
            {
                if (TryGetBatchDeclaration(deco, out string batch, out bool isPlanet, out string[] tokens))
                {
                    trackBases.Add(batch);
                    foreach (string token in tokens)
                    {
                        if (token == batch || !MatchesTrackToken(token, batch))
                            continue;
                        AddOwner(owners, token, batch);
                    }
                    AddOwner(owners, batch, batch);
                    continue;
                }

                if (isPlanet)
                    planetTokenLists.Add(tokens);
            }

            // 星球首令牌可能是多词自定义名的一部分，只有它确实是某条轨道已证实的基名时才把其余令牌挂到该批次
            foreach (string[] tokens in planetTokenLists)
            {
                string batch = tokens[0];
                if (trackBases.Contains(batch))
                {
                    foreach (string token in tokens.Skip(1))
                    {
                        planetTokens.Add(token);
                        AddOwner(owners, token, batch);
                    }
                    continue;
                }

                // WHY：首词未证实时整串星球名身份不明，挂成轨道归属会把未知星球的事件当本批的编号 tile 删掉
                foreach (string token in tokens)
                {
                    planetTokens.Add(token);
                    unownedPlanetTokens.Add(token);
                }
            }

            // 同一个令牌可能说得通地属于多个已声明基名，全部标上后清理与粘贴都按「身份不明」避让
            List<string> declaredBases = new List<string>();
            foreach (HashSet<string> batches in owners.Values)
            {
                foreach (string batch in batches)
                {
                    if (!declaredBases.Contains(batch))
                        declaredBases.Add(batch);
                }
            }
            foreach (KeyValuePair<string, HashSet<string>> entry in owners)
            {
                foreach (string candidateBase in declaredBases)
                {
                    if (NumericNamingOf(entry.Key, candidateBase))
                        entry.Value.Add(candidateBase);
                }
            }

            return owners;
        }

        /// <summary>判定「只启用 trackStyle 这一个可禁用属性」的 SetObject 事件，即同步轨道风格生成的那条。</summary>
        internal static bool IsStyleOnlySetObject(LevelEvent e)
        {
            if (e == null || e.eventType != LevelEventType.SetObject)
                return false;

            Dictionary<string, ADOFAI.PropertyInfo> propertiesInfo = e.info?.propertiesInfo;
            if (propertiesInfo == null || propertiesInfo.Count == 0)
                return false;

            if (!IsPropertyEnabled(e, TrackStyleKey))
                return false;

            foreach (KeyValuePair<string, ADOFAI.PropertyInfo> pair in propertiesInfo)
            {
                ADOFAI.PropertyInfo property = pair.Value;
                if (property == null || !property.canBeDisabled)
                    continue;

                if (pair.Key == TrackStyleKey)
                    continue;

                if (IsPropertyEnabled(e, pair.Key))
                    return false;
            }

            return true;
        }

        // 与 LevelEvent.Decode 的「存档里没有该属性即视为关闭」一致：未显式启用的一律不当成本条事件生效的功能
        private static bool IsPropertyEnabled(LevelEvent e, string key)
        {
            return e.disabled != null && e.disabled.TryGetValue(key, out bool isDisabled) && !isDisabled;
        }

        /// <summary>装饰 hitbox 登记的事件标签：游戏只给启用的图片装饰（decorationImage 非 prefab:）登记，命中的事件在实机改为撞击触发。</summary>
        internal static HashSet<string> CollectHitboxEventTags(IEnumerable<LevelEvent> decorations)
        {
            HashSet<string> tags = new HashSet<string>(StringComparer.Ordinal);
            if (decorations == null)
                return tags;

            foreach (LevelEvent deco in decorations)
            {
                if (deco == null || !deco.active || deco.eventType != LevelEventType.AddDecoration)
                    continue;
                if (deco.TryGet<string>("decorationImage", out string image) && image != null
                    && image.StartsWith("prefab:", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!deco.TryGet<HitboxType>("hitbox", out HitboxType hitbox) || hitbox == HitboxType.None)
                    continue;

                deco.TryGet<string>("hitboxEventTag", out string hitboxEventTag);
                string[] tokens = SplitTag(hitboxEventTag);
                if (tokens.Length == 0)
                {
                    tags.Add("NO TAG");
                    continue;
                }
                foreach (string token in tokens)
                    tags.Add(token);
            }

            return tags;
        }

        /// <summary>事件的 eventTag 是否撞上某块装饰 hitbox 登记的标签（按空格分词，与原游戏注册一致）。</summary>
        internal static bool HasManualHitboxTag(LevelEvent e, HashSet<string> manualTags)
        {
            if (e == null || manualTags == null || manualTags.Count == 0)
                return false;
            if (!e.TryGet<string>("eventTag", out string eventTag) || string.IsNullOrEmpty(eventTag))
                return false;

            foreach (string token in SplitTag(eventTag))
            {
                if (manualTags.Contains(token))
                    return true;
            }
            return false;
        }

        // 与 scnGame 建表一致：早/晚 Perfect 回落 hit、很早/很晚回落 barely、太早/太晚回落 miss
        private static string[] ConditionalSlotTags(LevelEvent conditional)
        {
            return new string[9]
            {
                conditional.GetString("perfectTag"),
                conditional.GetString("earlyPerfectTag").NullIfEmptyConditionalTag() ?? conditional.GetString("hitTag"),
                conditional.GetString("latePerfectTag").NullIfEmptyConditionalTag() ?? conditional.GetString("hitTag"),
                conditional.GetString("veryEarlyTag").NullIfEmptyConditionalTag() ?? conditional.GetString("barelyTag"),
                conditional.GetString("veryLateTag").NullIfEmptyConditionalTag() ?? conditional.GetString("barelyTag"),
                conditional.GetString("tooEarlyTag").NullIfEmptyConditionalTag() ?? conditional.GetString("missTag"),
                conditional.GetString("tooLateTag").NullIfEmptyConditionalTag() ?? conditional.GetString("missTag"),
                conditional.GetString("lossTag"),
                conditional.GetString("onCheckpointTag")
            };
        }

        /// <summary>事件是否被外部触发持有：装饰 hitbox 撞击，或同砖（取最后一条启用的）条件判定按整串 eventTag 命中。</summary>
        internal static bool HasExternalStyleTrigger(LevelEvent e, IEnumerable<LevelEvent> events, HashSet<string> manualTags)
        {
            if (e == null)
                return false;
            if (!e.TryGet<string>("eventTag", out string eventTag) || string.IsNullOrEmpty(eventTag))
                return false;
            if (HasManualHitboxTag(e, manualTags))
                return true;
            if (events == null)
                return false;

            string[] slots = null;
            foreach (LevelEvent conditional in events)
            {
                if (conditional == null || !conditional.active)
                    continue;
                if (conditional.eventType != LevelEventType.SetConditionalEvents || conditional.floor != e.floor)
                    continue;
                slots = ConditionalSlotTags(conditional);
            }
            if (slots == null)
                return false;

            foreach (string slot in slots)
            {
                if (!string.IsNullOrEmpty(slot) && !slot.IsNoneConditionalTag() && slot == eventTag)
                    return true;
            }
            return false;
        }

        public static void RemovePrevious(scnEditor editor, string tag, string bluePlanetTag, string redPlanetTag, CleanupScope scope)
        {
            bool tagEmpty = string.IsNullOrEmpty(tag);
            string baseTag = tagEmpty ? ReservedPrefix : tag;
            Dictionary<string, HashSet<string>> owners = CollectBatchOwners(editor.levelData.decorations, out HashSet<string> planetTokens, out HashSet<string> unownedPlanetTokens);

            HashSet<string> declaredBases = new HashSet<string>(StringComparer.Ordinal);
            foreach (HashSet<string> batches in owners.Values)
            {
                foreach (string batch in batches)
                    declaredBases.Add(batch);
            }

            bool OwnedByBase(string token)
            {
                if (owners.TryGetValue(token, out HashSet<string> batches))
                    return batches.Count == 1 && batches.Contains(baseTag);

                if (!NumericNamingOf(token, baseTag))
                    return false;
                foreach (string batch in declaredBases)
                {
                    if (batch != baseTag && NumericNamingOf(token, batch))
                        return false;
                }
                return true;
            }

            bool ClaimedForeign(string token, string own)
            {
                if (!owners.TryGetValue(token, out HashSet<string> batches))
                    return false;
                foreach (string batch in batches)
                {
                    if (batch != baseTag && batch != own)
                        return true;
                }
                return false;
            }

            bool ClaimedByOtherBatch(string token)
            {
                return ClaimedForeign(token, null);
            }

            bool IsPlanetToken(string token)
                => token.EndsWith("_RedPlanet", StringComparison.Ordinal) || token.EndsWith("_BluePlanet", StringComparison.Ordinal);

            bool IsResolvedPlanetToken(string token)
                => token == bluePlanetTag || token == redPlanetTag;

            bool MatchesDecoration(LevelEvent deco)
            {
                if (!deco.TryGet<string>("tag", out string tagValue))
                    return false;
                string[] tokens = SplitTag(tagValue);
                if (tokens.Length == 0)
                    return false;

                if (IsPlanetDecoration(deco))
                {
                    bool exactGenerated = tagEmpty
                        ? tagValue == bluePlanetTag || tagValue == redPlanetTag
                        : tagValue == $"{baseTag} {bluePlanetTag}" || tagValue == $"{baseTag} {redPlanetTag}";
                    return exactGenerated && !tokens.Any(ClaimedByOtherBatch);
                }

                return TryGetBatchDeclaration(deco, out string batch, out _, out _) && batch == baseTag;
            }

            bool MatchesEvent(LevelEvent e)
            {
                if (!e.TryGet<string>("tag", out string tagValue))
                    return false;
                string[] tokens = SplitTag(tagValue);
                if (tokens.Length == 0)
                    return false;

                if (scope == CleanupScope.EventsOnly)
                {
                    // 生成动画：不清理星球环绕事件
                    if (tokens.Any(t => IsPlanetToken(t) || planetTokens.Contains(t) || IsResolvedPlanetToken(t)))
                        return false;
                }
                else if (tagValue == bluePlanetTag || tagValue == redPlanetTag || tagValue == $"{redPlanetTag} {bluePlanetTag}")
                {
                    return !tokens.Any(ClaimedByOtherBatch);
                }

                if (tokens.Any(t => unownedPlanetTokens.Contains(t)))
                    return false;

                if (tagValue == baseTag)
                    return !ClaimedByOtherBatch(baseTag);

                return tokens.All(OwnedByBase);
            }

            List<LevelEvent> doomedDecorations = new List<LevelEvent>();
            if (scope == CleanupScope.TracksAndEvents)
            {
                foreach (LevelEvent deco in editor.levelData.decorations)
                {
                    if (deco.eventType != LevelEventType.AddObject)
                        continue;
                    if (MatchesDecoration(deco))
                        doomedDecorations.Add(deco);
                }
            }

            List<LevelEvent> doomedEvents = new List<LevelEvent>();
            foreach (LevelEvent e in editor.events)
            {
                // WHY：EventsOnly 只负责重建动画，一条 SetObject 都不碰，否则生成动画会抹掉之前生成轨道的风格
                bool inScope = e.eventType == LevelEventType.MoveDecorations
                    || (scope == CleanupScope.TracksAndEvents && IsStyleOnlySetObject(e));
                if (!inScope)
                    continue;
                if (MatchesEvent(e))
                    doomedEvents.Add(e);
            }

            if (doomedDecorations.Count == 0 && doomedEvents.Count == 0)
                return;

            if (doomedDecorations.Count > 0)
                DestroyDecorationVisuals(doomedDecorations);

            foreach (LevelEvent deco in doomedDecorations)
                editor.levelData.decorations.Remove(deco);
            foreach (LevelEvent e in doomedEvents)
                editor.events.Remove(e);

            Main.Logger.Log($"TagCleanup: 清除了 {doomedDecorations.Count} 个旧轨道装饰和 {doomedEvents.Count} 个旧生成事件（动画/风格）");
        }

        /// <summary>
        /// 通过反射定位 scrDecorationManager 上的装饰列表，销毁与被删数据对应的场景对象。
        /// 装饰元素的判定标准：其类型上存在类型为 LevelEvent 的实例字段（即来源事件）。
        /// 找不到成员时只记录日志并跳过（数据层已清理，重新进入编辑器后状态一致）。
        /// </summary>
        private static void DestroyDecorationVisuals(List<LevelEvent> doomed)
        {
            scrDecorationManager manager = scrDecorationManager.instance;
            if (manager == null || doomed.Count == 0)
                return;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            IEnumerable<MemberInfo> members = manager.GetType()
                .GetMembers(flags)
                .Where(m => m.MemberType == MemberTypes.Field || m.MemberType == MemberTypes.Property);

            foreach (MemberInfo member in members)
            {
                object value;
                try
                {
                    if (member is FieldInfo field)
                        value = field.GetValue(manager);
                    else if (member is System.Reflection.PropertyInfo property && property.GetIndexParameters().Length == 0)
                        value = property.GetValue(manager, null);
                    else
                        continue;
                }
                catch (TargetInvocationException)
                {
                    continue;
                }

                if (!(value is IEnumerable enumerable) || value is string)
                    continue;

                Type elementType = GetElementType(member, manager.GetType());
                if (elementType == null && enumerable.GetEnumerator() is IEnumerator it && it.MoveNext() && it.Current != null)
                    elementType = it.Current.GetType();
                if (elementType == null)
                    continue;

                // 装饰对象必然持有指向其来源 LevelEvent 的字段
                FieldInfo eventField = elementType.GetFields(flags)
                    .FirstOrDefault(f => f.FieldType == typeof(LevelEvent));
                if (eventField == null)
                    continue;

                List<object> toDestroy = new List<object>();
                foreach (object deco in enumerable)
                {
                    if (deco == null)
                        continue;
                    LevelEvent source = eventField.GetValue(deco) as LevelEvent;
                    if (source != null && doomed.Contains(source))
                        toDestroy.Add(deco);
                }

                IList listAsIList = value as IList;
                foreach (object deco in toDestroy)
                {
                    if (deco is Component component)
                        UnityEngine.Object.Destroy(component.gameObject);
                    else if (deco is UnityEngine.Object unityObject)
                        UnityEngine.Object.Destroy(unityObject);
                    listAsIList?.Remove(deco);
                }

                if (listAsIList == null)
                    Main.Logger.Log("TagCleanup: 装饰列表不是 IList，场景对象已销毁但列表项未移除");
                return;
            }

            Main.Logger.Log("TagCleanup: 未在 scrDecorationManager 上找到装饰列表，跳过场景对象清理");
        }

        private static Type GetElementType(MemberInfo member, Type declaringType)
        {
            Type collectionType;
            if (member is FieldInfo field)
                collectionType = field.FieldType;
            else if (member is System.Reflection.PropertyInfo property)
                collectionType = property.PropertyType;
            else
                return null;

            if (collectionType.IsArray)
                return collectionType.GetElementType();

            foreach (Type iface in collectionType.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    return iface.GetGenericArguments()[0];
            }
            if (collectionType.IsGenericType && collectionType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return collectionType.GetGenericArguments()[0];
            return null;
        }
    }
}
