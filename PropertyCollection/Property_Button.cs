using System.Collections.Generic;
using UnityEngine.Events;

namespace MultiTrackHelper.PropertyCollection
{
    public sealed partial class Property
    {
        /// <summary>
        /// 按钮型控件。序列化的 type 是游戏既有的 "Export"（游戏把这个类型渲染成按钮 prefab），
        /// 我们要的只是"点一下执行一个回调"，所以沿用同一个类型、不新造控件。
        /// data 键序：type → default（default 里放 UnityAction）。
        /// </summary>
        public static Property Button(string name, UnityAction action, string key = null,
            bool canBeDisabled = false, bool startEnabled = false,
            Dictionary<string, string> enableIf = null, Dictionary<string, string> disableIf = null)
        {
            return new Property(PropertyKind.Button, name, key, canBeDisabled, startEnabled, enableIf, disableIf)
            {
                TypeName = "Export",
                DefaultValue = action
            };
        }

        private void WriteButton(Dictionary<string, object> data)
        {
            data["default"] = DefaultValue;
        }
    }
}
