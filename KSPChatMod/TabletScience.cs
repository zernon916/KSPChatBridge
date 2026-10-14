using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletScienceScroll;

        void TabletScience(Rect area)
        {
            TabletUi.Ensure(tabletFont);
            GUILayout.BeginArea(area);
            try
            {
                tabletScienceScroll = GUILayout.BeginScrollView(tabletScienceScroll);
                try
                {
                    float width = area.width - 26, gap = 8, row = Mathf.Max(44, tabletFont * 2.5f);
                    float y = 0;
                    ResearchAndDevelopment research = ResearchAndDevelopment.Instance;
                    List<ScienceSubject> knownSubjects = research == null ? null : ResearchAndDevelopment.GetSubjects();
                    var subjects = new List<ScienceSubjectProgress>();
                    if (knownSubjects != null)
                    {
                        for (int i = 0; i < knownSubjects.Count; i++)
                        {
                            ScienceSubject subject = knownSubjects[i];
                            if (subject == null) continue;
                            string id = subject.id;
                            ScienceSubjectProgress progress = ScienceTracker.Create(id, subject.title, subject.science, subject.scienceCap);
                            if (progress != null) subjects.Add(progress);
                        }
                    }
                    subjects = ScienceTracker.Snapshot(subjects);

                    double science = 0, cap = 0;
                    int complete = 0;
                    for (int i = 0; i < subjects.Count; i++)
                    {
                        science += subjects[i].Science;
                        cap += subjects[i].Cap;
                        if (subjects[i].Complete) complete++;
                    }

                    float overviewHeight = row * 2 + CardHeaderHeight;
                    Rect overview = TabletUi.CardPanel(new Rect(0, y, width, overviewHeight), "SCIENCE TRACKER");
                    TabletUi.Label(new Rect(overview.x, overview.y, overview.width, row),
                        "Tracked subjects: " + subjects.Count.ToString(CultureInfo.InvariantCulture) +
                        "  |  Complete: " + complete.ToString(CultureInfo.InvariantCulture), true);
                    string status = knownSubjects == null ? "Science progress is unavailable." :
                        subjects.Count == 0 ? "No science subjects have been recorded yet." :
                        "Recorded subject progress; undiscovered subjects are not included.";
                    TabletUi.Label(new Rect(overview.x, overview.y + row, overview.width, row),
                        status + (subjects.Count == 0 ? "" : "  Earned: " + science.ToString("0.0", CultureInfo.InvariantCulture) +
                            " / " + cap.ToString("0.0", CultureInfo.InvariantCulture) + " science"), true);
                    y += overviewHeight + gap;

                    for (int i = 0; i < subjects.Count; i++)
                        y = ScienceCard(subjects[i], width, row, y, gap);

                    GUILayout.Space(y + gap);
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

        float ScienceCard(ScienceSubjectProgress progress, float width, float row, float y, float gap)
        {
            float height = CardHeaderHeight + row * 2 + 12;
            Rect card = TabletUi.CardPanel(new Rect(0, y, width, height), progress.Title);
            TabletUi.Label(new Rect(card.x, card.y, card.width, row),
                progress.Science.ToString("0.0", CultureInfo.InvariantCulture) + " / " +
                progress.Cap.ToString("0.0", CultureInfo.InvariantCulture) + " science  |  Remaining: " +
                progress.Remaining.ToString("0.0", CultureInfo.InvariantCulture), true);
            float fraction = (float)progress.Fraction;
            TabletUi.Bar(new Rect(card.x, card.y + row, card.width, row), "Subject progress", fraction,
                (fraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%", progress.Complete ? TabletUi.Good : TabletUi.Accent);
            return y + height + gap;
        }
    }
}
