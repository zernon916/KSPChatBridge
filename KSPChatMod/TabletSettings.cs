using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletSettingsScroll;

        void TabletSettings(Rect area)
        {
            var c = TabletUi.CardPanel(area, "SETTINGS");
            float row = Mathf.Max(44, tabletFont * 2.6f);
            if (FlightGlobals.ActiveVessel == null)
            {
                TabletUi.Label(new Rect(c.x + 6, c.y + 6, c.width - 12, row), "No active vessel", true);
                return;
            }
            float y = c.y + 6;
            TabletUi.Chip(new Rect(c.x + 6, y, c.width - 12, row),
                AicsCore.AiEnabled ? "AI ON" : "AI OFF", AicsCore.AiEnabled ? TabletUi.Good : TabletUi.Bad);
            y += row + 8;
            TabletUi.Label(new Rect(c.x + 6, y, c.width - 12, row),
                "AI backend: " + ChatWindow.BackendId(ChatWindow.BackendIndex), true);
            y += row + 8;
            TabletUi.Bar(new Rect(c.x + 6, y, c.width - 12, row), "Opacity",
                (opacity - 0.2f) / 0.8f, opacity.ToString("0.00"), TabletUi.Accent);
            y += row + 8;
            TabletUi.Chip(new Rect(c.x + 6, y, c.width - 12, row),
                preferMj ? "PREFER MECHJEB" : "MECHJEB OPTIONAL", preferMj ? TabletUi.Good : TabletUi.Dim);
            y += row + 8;
        }
    }
}