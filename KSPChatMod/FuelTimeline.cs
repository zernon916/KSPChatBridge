using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    /// <summary>Fuel/burn timeline: stages along time with burn markers (System only).</summary>
    internal static class FuelTimeline
    {
        /// <summary>A point on the timeline: time offset (s), event type, description.</summary>
        internal sealed class TimelineEvent
        {
            public double Time;           // Time offset from start (s)
            public string Type;           // "stage", "burn", "depletion"
            public string Description;    // Human-readable description
            public int Stage;              // Stage number (if applicable)
            public double RemainingFuel;   // Fuel remaining at this point (kg)
        }

        /// <summary>Complete timeline: events sorted by time, total duration.</summary>
        internal sealed class Timeline
        {
            public List<TimelineEvent> Events = new List<TimelineEvent>();
            public double TotalDuration;    // Total time to empty (s)
            public double TotalFuel;        // Total initial fuel (kg)
        }

        /// <summary>Build a timeline from stage info. stages in order from first (highest) to last (dropped).</summary>
        internal static Timeline Build(List<StageInfo> stages)
        {
            if (stages == null || stages.Count == 0)
                return new Timeline { TotalDuration = 0, TotalFuel = 0 };

            var timeline = new Timeline();
            double currentTime = 0;
            double totalFuel = 0;

            // Calculate total fuel first
            foreach (var s in stages)
                totalFuel += s.FuelMass;
            timeline.TotalFuel = totalFuel;

            // Process stages in order (highest stage number first = stage 0 drops first)
            // StageInfo.Stage: higher number = later stage (stage 0 drops first)
            // Sort by stage ascending (0, 1, 2, ...) so we process from first drop to last
            var sorted = new List<StageInfo>(stages);
            sorted.Sort((a, b) => a.Stage.CompareTo(b.Stage));

            double cumulativeFuel = totalFuel;

            foreach (var s in sorted)
            {
                if (s.BurnTime <= 0 || s.FuelMass <= 0)
                    continue;

                // Stage start event
                timeline.Events.Add(new TimelineEvent
                {
                    Time = currentTime,
                    Type = "stage",
                    Description = "Stage " + s.Stage + " active",
                    Stage = s.Stage,
                    RemainingFuel = cumulativeFuel
                });

                // Calculate mid-point burn event (optional visualization point)
                double midTime = currentTime + s.BurnTime * 0.5;
                double midFuel = cumulativeFuel - s.FuelMass * 0.5;
                timeline.Events.Add(new TimelineEvent
                {
                    Time = midTime,
                    Type = "burn",
                    Description = "Stage " + s.Stage + " burn",
                    Stage = s.Stage,
                    RemainingFuel = midFuel
                });

                // Stage depletion event
                currentTime += s.BurnTime;
                cumulativeFuel -= s.FuelMass;
                timeline.Events.Add(new TimelineEvent
                {
                    Time = currentTime,
                    Type = "depletion",
                    Description = "Stage " + s.Stage + " empty",
                    Stage = s.Stage,
                    RemainingFuel = cumulativeFuel
                });
            }

            timeline.TotalDuration = currentTime;
            return timeline;
        }

        /// <summary>Get fuel remaining at a given time (linear interpolation between events).</summary>
        internal static double FuelAtTime(Timeline timeline, double time)
        {
            if (timeline == null || timeline.Events.Count == 0)
                return 0;

            if (time <= 0)
                return timeline.TotalFuel;
            if (time >= timeline.TotalDuration)
                return 0;

            // Find the event just before this time
            TimelineEvent before = null;
            TimelineEvent after = null;

            foreach (var e in timeline.Events)
            {
                if (e.Time <= time)
                    before = e;
                else
                {
                    after = e;
                    break;
                }
            }

            if (before == null)
                return timeline.TotalFuel;
            if (after == null)
                return before.RemainingFuel;

            // Linear interpolation
            double frac = (time - before.Time) / (after.Time - before.Time);
            return before.RemainingFuel - frac * (before.RemainingFuel - after.RemainingFuel);
        }

        /// <summary>Get the stage active at a given time, or -1 if past all stages.</summary>
        internal static int StageAtTime(Timeline timeline, double time)
        {
            if (timeline == null || timeline.Events.Count == 0)
                return -1;

            if (time <= 0)
                return timeline.Events.Count > 0 ? timeline.Events[0].Stage : -1;
            if (time >= timeline.TotalDuration)
                return -1;

            // Find the last stage start before this time
            int lastStageStart = -1;
            foreach (var e in timeline.Events)
            {
                if (e.Type == "stage" && e.Time <= time)
                    lastStageStart = e.Stage;
            }

            if (lastStageStart == -1)
                return -1;

            // Check if this stage has depleted before the time
            foreach (var e in timeline.Events)
            {
                if (e.Type == "depletion" && e.Stage == lastStageStart && e.Time <= time)
                {
                    // This stage is depleted, find the next stage
                    foreach (var next in timeline.Events)
                    {
                        if (next.Type == "stage" && next.Time > time)
                            return next.Stage;
                    }
                    return -1;
                }
            }

            return lastStageStart;
        }
    }
}
