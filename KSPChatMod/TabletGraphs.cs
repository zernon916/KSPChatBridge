using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletGraphsScroll;

        void TabletGraphs(Rect area)
        {
            float row = Mathf.Max(44, tabletFont * 2.6f), gap = 8;
            float width = area.width - 26, pad = 16 + tabletFont * 1.6f;
            GUILayout.BeginArea(area);
            tabletGraphsScroll = GUILayout.BeginScrollView(tabletGraphsScroll);

            var stages = StageReader.ForFlight();
            double maxDv = 0;
            foreach (StageInfo stage in stages)
                if (stage.DvActual > maxDv) maxDv = stage.DvActual;

            Rect card = TabletUi.CardPanel(
                new Rect(0, 0, width, pad + Mathf.Max(1, stages.Count) * (row + gap)),
                "DV PER STAGE");
            float y = pad;
            foreach (StageInfo stage in stages)
            {
                double fraction = maxDv > 0 ? stage.DvActual / maxDv : 0;
                string label = "S" + stage.Stage + " " + stage.DvActual.ToString("F0") + " m/s";
                TabletUi.Bar(new Rect(12, y, width - 24, row), string.Empty, (float)fraction, label, TabletUi.Good);
                y += row + gap;
            }
            if (stages.Count == 0)
                TabletUi.Label(new Rect(12, y, width - 24, row), "No stage data", true);

            GUILayout.Space(card.height + gap);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
