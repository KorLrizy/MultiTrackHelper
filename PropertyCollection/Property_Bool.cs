using System.Collections.Generic;

namespace MultiTrackHelper.PropertyCollection
{
    public sealed partial class Property
    {
        /// <summary>开关控件（ToggleGroup）。data 键序：type → default。</summary>
        public static Property Bool(string name, bool value_default = false, string key = null,
            bool canBeDisabled = false, bool startEnabled = false,
            Dictionary<string, string> enableIf = null, Dictionary<string, string> disableIf = null)
        {
            return new Property(PropertyKind.Bool, name, key, canBeDisabled, startEnabled, enableIf, disableIf)
            {
                TypeName = "Bool",
                DefaultValue = value_default
            };
        }

        private void WriteBool(Dictionary<string, object> data)
        {
            data["default"] = DefaultValue;
        }
    }
}
