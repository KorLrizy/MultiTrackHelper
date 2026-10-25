using System.Collections.Generic;

namespace MultiTrackHelper.PropertyCollection
{
    public sealed partial class Property
    {
        /// <summary>
        /// 纯说明文本（不能改值）。data 键序：type → noteKey。
        /// noteKey 指向 Localizations.json / 游戏语言表里的说明文本。
        /// </summary>
        public static Property Note(string name, string noteKey, string key = null)
        {
            return new Property(PropertyKind.Note, name, key, false, false, null, null)
            {
                TypeName = "Note",
                NoteKey = noteKey
            };
        }

        private void WriteNote(Dictionary<string, object> data)
        {
            data["noteKey"] = NoteKey;
        }
    }
}
