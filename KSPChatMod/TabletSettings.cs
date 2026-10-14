using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletSettingsScroll;

        void TabletSettings(Rect area)
        {
            float row = Mathf.Max(44, tabletFont * 2.6f), gap = 8;
            float width = area.width - 26;
            GUILayout.BeginArea(area);
            tabletSettingsScroll = GUILayout.BeginScrollView(tabletSettingsScroll);
            float y = 0;

            bool noVessel = FlightGlobals.ActiveVessel == null;
            float infoHeight = CardHeaderHeight + row * (noVessel ? 1 : 4);
            Rect c = TabletUi.CardPanel(new Rect(0, y, width, infoHeight), "SETTINGS");
            if (noVessel)
            {
                TabletUi.Label(new Rect(c.x, c.y, c.width, row), "No active vessel", true);
            }
            else
            {
                TabletUi.Chip(new Rect(c.x, c.y, c.width, row),
                    AicsCore.AiEnabled ? "AI ON" : "AI OFF", AicsCore.AiEnabled ? TabletUi.Good : TabletUi.Bad);
                TabletUi.Label(new Rect(c.x, c.y + row, c.width, row),
                    "AI backend: " + ChatWindow.BackendId(ChatWindow.BackendIndex), true);
                TabletUi.Bar(new Rect(c.x, c.y + row * 2, c.width, row), "Opacity",
                    (opacity - 0.2f) / 0.8f, opacity.ToString("0.00"), TabletUi.Accent);
                TabletUi.Chip(new Rect(c.x, c.y + row * 3, c.width, row),
                    preferMj ? "PREFER MECHJEB" : "MECHJEB OPTIONAL", preferMj ? TabletUi.Good : TabletUi.Dim);
            }
            y += infoHeight + gap;

            if (!TabletLink)
            {
                TabletUi.Label(new Rect(12, y, width - 24, row), "INFO ONLY - enable LIVE LINK", true);
                y += row + gap;
            }
            if (TabletUi.Button(new Rect(12, y, width - 24, row), "AI: " + (AicsCore.AiEnabled ? "ON" : "OFF"), false, TabletLink) && TabletLink)
            {
                AicsCore.SetAiEnabled(!AicsCore.AiEnabled);
                ChatLog.Write("g", "tablet settings: AI " + AicsCore.AiEnabled);
            }
            y += row + gap;

            TabletUi.Label(new Rect(12, y, width - 24, row), "AI backend", true);
            y += row + gap;
            string[] labels = ChatWindow.BackendLabels;
            int cur = ChatWindow.BackendIndex;
            for (int i = 0; i < labels.Length; i++)
            {
                if (TabletUi.Button(new Rect(12, y, width - 24, row), (i == cur ? "> " : "") + labels[i], false, TabletLink) && TabletLink)
                {
                    ChatWindow.BackendIndex = i;
                    ChatLog.Write("g", "tablet settings: backend " + labels[i]);
                }
                y += row + gap;
            }

            float half = (width - 24 - gap) / 2;
            if (TabletUi.Button(new Rect(12, y, half, row), "Opacity -", false, TabletLink) && TabletLink)
            {
                opacity = Mathf.Clamp(Mathf.Round((opacity - 0.05f) * 20f) / 20f, 0.2f, 1f);
                ChatLog.Write("g", "tablet settings: opacity " + opacity.ToString("0.00"));
            }
            if (TabletUi.Button(new Rect(12 + half + gap, y, half, row), "Opacity +", false, TabletLink) && TabletLink)
            {
                opacity = Mathf.Clamp(Mathf.Round((opacity + 0.05f) * 20f) / 20f, 0.2f, 1f);
                ChatLog.Write("g", "tablet settings: opacity " + opacity.ToString("0.00"));
            }
            y += row + gap;

            GUILayout.Space(y + gap);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}