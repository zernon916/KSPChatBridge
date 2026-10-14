using UnityEngine;
using System.Collections.Generic;
using System;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletFuelTimelineScroll;

        void TabletFuelTimeline(Rect area)
        {
            TabletUi.Ensure(tabletFont);
            float row = Mathf.Max(44, tabletFont * 2.6f), gap = 8;
            float width = area.width - 26, pad = 16 + tabletFont * 1.6f;
            GUILayout.BeginArea(area);
            tabletFuelTimelineScroll = GUILayout.BeginScrollView(tabletFuelTimelineScroll);
            Rect c = TabletUi.CardPanel(new Rect(0, 0, width, pad + row), "FUEL / BURN TIMELINE");
            float y = pad + row + gap;

            // Read stage info (StageReader not implemented yet - use dummy data for testing)
            var stages = new List<StageInfo>(); // TODO: StageReader.ForFlight() / ForEditor() when RK2c-RK2e are done
            if (stages == null || stages.Count == 0)
            {
                TabletUi.Label(new Rect(12, y, width - 24, row), "Stage data coming soon (RK2c-RK2e).", true);
                y += row + gap;
            }
            else
            {
                // Build timeline
                var timeline = FuelTimeline.Build(stages);
                double totalDuration = timeline.TotalDuration;
                double totalFuel = timeline.TotalFuel;

                // Summary line
                TabletUi.Label(new Rect(12, y, width - 24, row),
                    string.Format("Total fuel: {0:F0} kg  Total burn time: {1:F0} s", totalFuel, totalDuration), true);
                y += row + gap;

                // Timeline visualization: horizontal bar with stage markers
                float timelineHeight = Mathf.Max(60, tabletFont * 3.5f);
                float timelineX = 12;
                float timelineW = width - 24;
                Rect timelineRect = new Rect(timelineX, y, timelineW, timelineHeight);
                TabletUi.Fill(timelineRect, TabletUi.CardHi);

                // Draw stage segments
                if (totalDuration > 0)
                {
                    float currentX = timelineRect.x;
                    foreach (var s in stages)
                    {
                        if (s.BurnTime <= 0) continue;
                        float segmentWidth = (float)(s.BurnTime / totalDuration) * timelineRect.width;
                        Color stageColor = TabletUi.Series[s.Stage % TabletUi.Series.Length];
                        TabletUi.Fill(new Rect(currentX, timelineRect.y, segmentWidth, timelineRect.height), stageColor);
                        currentX += segmentWidth;
                    }
                }

                // Draw burn markers (mid-points)
                if (totalDuration > 0)
                {
                    foreach (var e in timeline.Events)
                    {
                        if (e.Type == "burn")
                        {
                            float markerX = timelineRect.x + (float)(e.Time / totalDuration) * timelineRect.width;
                            TabletUi.Rect(new Rect(markerX - 1, timelineRect.y, 2, timelineRect.height), TabletUi.Text);
                        }
                    }
                }

                y += timelineHeight + gap;

                // Legend
                float legendY = y;
                float legendX = 12;
                foreach (var s in stages)
                {
                    if (s.BurnTime <= 0) continue;
                    Color stageColor = TabletUi.Series[s.Stage % TabletUi.Series.Length];
                    float chipW = Mathf.Max(60, tabletFont * 3.5f);
                    TabletUi.Fill(new Rect(legendX, legendY, chipW, row - 8), stageColor);
                    TabletUi.Label(new Rect(legendX, legendY, chipW, row - 8), "S" + s.Stage, true);
                    legendX += chipW + 8;
                    if (legendX > width - 24) { legendX = 12; legendY += row; }
                }
                y = legendY + row + gap;

                // Event list
                TabletUi.Label(new Rect(12, y, width - 24, row), "Events:", true);
                y += row + gap;

                foreach (var e in timeline.Events)
                {
                    string eventText = string.Format("{0:F0}s: {1} - {2}", e.Time, e.Type.ToUpperInvariant(), e.Description);
                    TabletUi.Label(new Rect(12, y, width - 24, row), eventText, true);
                    y += row;
                }
            }

            if (!TabletLink)
            {
                TabletUi.Label(new Rect(12, y, width - 24, row), "INFO ONLY - enable LIVE LINK", true);
                y += row + gap;
            }

            GUILayout.Space(y + gap);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
