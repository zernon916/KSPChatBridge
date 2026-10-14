using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    internal sealed class ScienceSubjectProgress
    {
        internal readonly string SubjectId;
        internal readonly string Title;
        internal readonly double Science;
        internal readonly double Cap;

        internal ScienceSubjectProgress(string subjectId, string title, double science, double cap)
        {
            SubjectId = subjectId;
            Title = title;
            Science = science;
            Cap = cap;
        }

        internal double Remaining { get { return Cap - Science; } }
        internal double Fraction { get { return Science / Cap; } }
        internal bool Complete { get { return Fraction >= .99; } }
    }

    internal static class ScienceTracker
    {
        internal static ScienceSubjectProgress Create(string subjectId, string title, double science, double cap)
        {
            if (string.IsNullOrEmpty(subjectId) || double.IsNaN(cap) || double.IsInfinity(cap) || cap <= 0) return null;
            if (double.IsNaN(science) || double.IsInfinity(science)) science = 0;
            science = Math.Max(0, Math.Min(cap, science));
            return new ScienceSubjectProgress(subjectId, string.IsNullOrEmpty(title) ? subjectId : title, science, cap);
        }

        internal static List<ScienceSubjectProgress> Snapshot(IEnumerable<ScienceSubjectProgress> subjects)
        {
            var result = new List<ScienceSubjectProgress>();
            if (subjects != null)
            {
                foreach (ScienceSubjectProgress subject in subjects)
                    if (subject != null) result.Add(subject);
            }

            result.Sort((a, b) =>
            {
                int complete = a.Complete.CompareTo(b.Complete);
                if (complete != 0) return complete;
                int remaining = b.Remaining.CompareTo(a.Remaining);
                return remaining != 0 ? remaining : string.Compare(a.Title, b.Title, StringComparison.Ordinal);
            });
            return result;
        }
    }
}
