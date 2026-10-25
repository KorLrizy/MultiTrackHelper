using ADOFAI;
using System.Collections.Generic;
using System.Linq;

namespace MultiTrackHelper.Utils
{
    public static class LevelEventUtils
    {
        public static void UpdatePanel(this LevelEvent e)
        {
            if (e == null)
                return;
            scnEditor editor = scnEditor.instance;
            if (editor == null)
                return;
            InspectorPanel settingsPanel = editor.settingsPanel;
            if (settingsPanel == null)
                return;
            List<PropertiesPanel> panelsList = settingsPanel.panelsList;
            if (panelsList == null)
                return;
            PropertiesPanel panel = panelsList.Find(panel2 => panel2.levelEventType == e.eventType);
            if (panel == null)
                return;
            panel.SetProperties(e);
        }
    }
}
