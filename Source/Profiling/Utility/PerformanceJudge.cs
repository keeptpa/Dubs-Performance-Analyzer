using System;
using UnityEngine;
using Verse;

namespace Analyzer.Profiling
{
    public enum PerformanceSeverity
    {
        Unknown,
        Good,
        Watch,
        Poor,
        Severe
    }

    public readonly struct PerformanceVerdict
    {
        public PerformanceSeverity Severity { get; }
        public bool IsPoor => Severity == PerformanceSeverity.Poor
            || Severity == PerformanceSeverity.Severe;
        public string Reason { get; }

        public PerformanceVerdict(PerformanceSeverity severity, string reason)
        {
            Severity = severity;
            Reason = reason;
        }
    }

    public static class PerformanceJudge
    {
        private const int MinimumSamples = 30;
        private const float FrameBudgetMs = 1000f / 60f;

        public static PerformanceVerdict Evaluate(ProfileLog log, Category category)
        {
            if (log == null || log.entries < MinimumSamples || log.calls <= 0)
                return new PerformanceVerdict(PerformanceSeverity.Unknown,
                    "Not enough samples to judge this function.");

            var budget = FrameBudgetMs;
            if (category == Category.Tick && Find.TickManager != null)
                budget /= Mathf.Max(1f, Find.TickManager.TickRateMultiplier);

            var budgetRatio = (float)(log.average / budget);
            var signal = Mathf.Max(log.percent, budgetRatio);
            var severity = signal >= .25f ? PerformanceSeverity.Severe
                : signal >= .10f ? PerformanceSeverity.Poor
                : signal >= .03f ? PerformanceSeverity.Watch
                : PerformanceSeverity.Good;

            return new PerformanceVerdict(severity,
                $"{severity}: average {log.average:0.000}ms, {log.percent * 100f:0.00}% of this cycle.");
        }

        public static bool IsPoor(ProfileLog log, Category category)
        {
            return Evaluate(log, category).IsPoor;
        }
    }
}
