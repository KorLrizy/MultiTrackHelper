using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MultiTrackHelper
{
    /// <summary>
    /// Localizations.json 的只读查表：键 → 语言 → 文本。
    /// 查不到当前语言回退英文；英文也没有就返回 null，由调用方决定是否回退成键名。
    /// 语言文件缺失或格式坏掉时不抛异常（否则会在 UMM 初始化阶段把整个模组带崩），只记一条警告。
    /// </summary>
    internal sealed class Localization
    {
        private readonly Dictionary<string, Dictionary<SystemLanguage, string>> table =
            new Dictionary<string, Dictionary<SystemLanguage, string>>();

        internal Localization(string jsonFile)
        {
            if (string.IsNullOrEmpty(jsonFile) || !File.Exists(jsonFile))
            {
                LogWarning("找不到语言文件 " + jsonFile + "，面板文本将回退为键名");
                return;
            }

            try
            {
                Load(jsonFile);
            }
            catch (Exception e)
            {
                LogWarning("语言文件解析失败(" + jsonFile + "): " + e.Message + "，面板文本将回退为键名");
            }
        }

        private static void LogWarning(string message)
        {
            if (Main.Logger != null)
                Main.Logger.Log("警告: " + message);
        }

        private void Load(string jsonFile)
        {
            var raw = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(
                File.ReadAllText(jsonFile));
            if (raw == null)
                return;

            foreach (KeyValuePair<string, Dictionary<string, string>> entry in raw)
            {
                var byLanguage = new Dictionary<SystemLanguage, string>();
                if (entry.Value != null)
                {
                    foreach (KeyValuePair<string, string> pair in entry.Value)
                    {
                        SystemLanguage language;
                        if (Enum.TryParse(pair.Key, out language))
                            byLanguage[language] = pair.Value;
                        else
                            LogWarning("未识别语言 " + pair.Key);
                    }
                }
                table[entry.Key] = byLanguage;
            }
        }

        /// <summary>
        /// 查一条文本。返回 true 表示命中（当前语言或英文），value 为文本。
        /// parameters 为游戏侧预留的格式化参数，本模组的文本里没有占位符，直接忽略。
        /// </summary>
        public bool Get(string key, out string value, Dictionary<string, object> parameters = null)
        {
            value = Resolve(key);
            return value != null;
        }

        /// <summary>查一条文本；没有就返回 null（调用方用 "?? 键名" 兜底）。</summary>
        public string GetValue(string key)
        {
            return Resolve(key);
        }

        private string Resolve(string key)
        {
            if (key == null)
                return null;

            Dictionary<SystemLanguage, string> byLanguage;
            if (!table.TryGetValue(key, out byLanguage) || byLanguage == null)
                return null;

            string text;
            if (byLanguage.TryGetValue(RDString.language, out text))
                return text;
            if (byLanguage.TryGetValue(SystemLanguage.English, out text))
                return text;
            return null;
        }
    }
}
