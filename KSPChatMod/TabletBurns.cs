using System.Collections.Generic;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletBurnsScroll;

        void TabletBurns(Rect area)
        {
            float row = Mathf.Max(44, tabletFont * 2.6f);
            float width = area.width - 26;
            float pad = 16 + tabletFont * 1.6f;
            List<string> lines = NodeReader.Lines(FlightGlobals.ActiveVessel);
            float cardHeight = pad + row * Mathf.Max(1, lines.Count);

            GUILayout.BeginArea(area);
            try
            {
                tabletBurnsScroll = GUILayout.BeginScrollView(tabletBurnsScroll);
                try
                {
                    Rect card = TabletUi.CardPanel(new Rect(0, 0, width, cardHeight), "BURNS");
                    if (lines.Count == 0)
                    {
                        TabletUi.Label(new Rect(card.x, card.y, card.width, row), "No maneuver nodes", true);
                    }
                    else
                    {
                        for (int i = 0; i < lines.Count; i++)
                            TabletUi.Label(new Rect(card.x, card.y + i * row, card.width, row), lines[i], true);
                    }
                    GUILayout.Space(cardHeight);
                }
                finally
                {
                    GUILayout.EndScrollView();
                }
            }
            finally
            {
                GUILayout.EndArea();
            }
        }
    }
}
