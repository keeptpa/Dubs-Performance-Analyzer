using System;
using System.Collections.Generic;
using Verse;

namespace Analyzer.Profiling.Web
{
    /// <summary>
    /// Serialises everything the dashboard needs into one JSON document.
    /// Always runs on the main thread (called from <see cref="WebTelemetry.OnFrame"/>),
    /// so reading DPA's log list and Verse state here is safe.
    /// </summary>
    public static class WebSnapshot
    {
        private const int MaxFramePoints = 900;
        private const int MaxRecentFrames = 1500;
        private const int MaxLogs = 150;
        private const int MaxSpikesOut = 120;

        private static readonly JsonWriter Writer = new JsonWriter(1 << 17);
        private static long sequence;

        /// <summary>Filled by the HTTP layer so the page can show the real bind address.</summary>
        public static string LastError;

        public static string Build()
        {
            try
            {
                return BuildInner();
            }
            catch (Exception e)
            {
                // Never let a serialisation bug take the frame down; report it into the payload instead.
                Writer.Clear();
                Writer.BeginObject();
                Writer.Prop("seq", ++sequence);
                Writer.Prop("error", e.ToString());
                Writer.EndObject();
                return Writer.ToString();
            }
        }

        private static string BuildInner()
        {
            Writer.Clear();
            Writer.BeginObject();

            Writer.Prop("seq", ++sequence);
            Writer.Prop("t", WebTelemetry.SessionSeconds);
            Writer.Prop("session", WebTelemetry.SessionId);
            Writer.Prop("inGame", Current.Game != null);
            Writer.Prop("tick", Current.Game != null ? GenTicks.TicksAbs : 0);

            // ---- headline numbers -------------------------------------------
            Writer.Prop("fps", WebTelemetry.Fps);
            Writer.Prop("tps", WebTelemetry.Tps);
            Writer.Prop("tpsTarget", WebTelemetry.TpsTarget);
            Writer.Prop("frameMs", WebTelemetry.CurrentFrameMs);
            Writer.Prop("avgFrameMs", WebTelemetry.WindowAvgFrameMs);
            Writer.Prop("p95FrameMs", WebTelemetry.WindowP95FrameMs);
            Writer.Prop("p99FrameMs", WebTelemetry.WindowP99FrameMs);
            Writer.Prop("maxFrameMs", WebTelemetry.WindowMaxFrameMs);
            Writer.Prop("lastTickMs", WebTelemetry.LastTickMs);
            Writer.Prop("maxTickMs", WebTelemetry.WindowMaxTickMs);
            Writer.Prop("frames", WebTelemetry.FramesRendered);

            Writer.Prop("heapMB", WebTelemetry.HeapMB);
            Writer.Prop("wsMB", WebTelemetry.WorkingSetMB);
            Writer.Prop("gc0", WebTelemetry.Gc0PerSec);
            Writer.Prop("gc1", WebTelemetry.Gc1PerSec);
            Writer.Prop("gc2", WebTelemetry.Gc2PerSec);
            Writer.Prop("gc0Total", WebTelemetry.Gc0Total);
            Writer.Prop("gc1Total", WebTelemetry.Gc1Total);
            Writer.Prop("gc2Total", WebTelemetry.Gc2Total);

            // ---- monitor state ----------------------------------------------
            Writer.Prop("profiling", global::Analyzer.Profiling.Analyzer.CurrentlyProfiling);
            Writer.Prop("paused", global::Analyzer.Profiling.Analyzer.CurrentlyPaused);
            Writer.Prop("deep", WebProfilerControl.DeepActive);
            Writer.Prop("deepTick", WebProfilerControl.TickCategory);
            Writer.Prop("monitorEnabled", WebTelemetry.Enabled);
            Writer.Prop("thresholdMs", WebTelemetry.SpikeThresholdMs);
            Writer.Prop("captureStacks", WebTelemetry.CaptureStacks);
            Writer.Prop("payloadHz", WebEntry.PayloadHz);
            Writer.Prop("sortBy", global::Analyzer.Profiling.Analyzer.SortBy.ToString());

            Writer.BeginObject("server");
            Writer.Prop("status", WebTelemetry.ServerStatus);
            Writer.Prop("port", WebTelemetry.ServerPort);
            Writer.Prop("error", WebTelemetry.ServerError);
            Writer.EndObject();

            Writer.Prop("spikesTotal", WebTelemetry.FrameSpikesTotal);
            Writer.Prop("spikesRetained", WebTelemetry.SpikeCount);

            // ---- series -------------------------------------------------------
            WriteFrameSeries();
            Writer.BeginObject("hist");
            WriteHist("fps", WebTelemetry.HistFps);
            WriteHist("tps", WebTelemetry.HistTps);
            WriteHist("frameAvgMs", WebTelemetry.HistFrameAvgMs);
            WriteHist("heapMB", WebTelemetry.HistHeapMB);
            WriteHist("wsMB", WebTelemetry.HistWorkingSetMB);
            WriteHist("gc0", WebTelemetry.HistGc0);
            WriteHist("gc1", WebTelemetry.HistGc1);
            WriteHist("gc2", WebTelemetry.HistGc2);
            WriteHist("tickMs", WebTelemetry.HistTickMs);
            Writer.EndObject();

            WriteSpikes();
            WriteLogs();
            WriteMods();

            Writer.EndObject();
            return Writer.ToString();
        }

        private static void WriteFrameSeries()
        {
            int count = WebTelemetry.FrameCount;
            int start = (WebTelemetry.FrameHead - count + WebTelemetry.FrameCapacity) % WebTelemetry.FrameCapacity;
            int step = Math.Max(1, count / MaxFramePoints);

            // Long overview: whole session, thinned down so the payload stays small.
            Writer.Prop("frameStep", step);
            Writer.BeginArray("frameSeries");
            for (int i = 0; i < count; i += step)
                Writer.Item(WebTelemetry.FrameMs[(start + i) % WebTelemetry.FrameCapacity]);
            Writer.EndArray();

            // Recent detail: every single frame of the last ~25 seconds. This is the series you
            // actually read when you are hunting a stutter.
            int recentTake = Math.Min(count, MaxRecentFrames);
            int recentStart = start + count - recentTake;
            Writer.BeginArray("frameSeriesRecent");
            for (int i = 0; i < recentTake; i++)
                Writer.Item(WebTelemetry.FrameMs[(recentStart + i) % WebTelemetry.FrameCapacity]);
            Writer.EndArray();
        }

        private static void WriteHist(string key, float[] source)
        {
            int count = WebTelemetry.HistCount;
            int start = count < WebTelemetry.HistoryCapacity ? 0 : WebTelemetry.HistHead;

            Writer.BeginArray(key);
            for (int i = 0; i < count; i++)
                Writer.Item(source[(start + i) % WebTelemetry.HistoryCapacity]);
            Writer.EndArray();
        }

        private static void WriteSpikes()
        {
            int retained = WebTelemetry.SpikeCount;
            int skip = Math.Max(0, retained - MaxSpikesOut);

            Writer.BeginArray("spikes");
            for (int i = skip; i < retained; i++)
            {
                var s = WebTelemetry.SpikeAt(i);
                Writer.BeginObject();
                Writer.Prop("id", s.Id);
                Writer.Prop("t", s.AtSeconds);
                Writer.Prop("ms", s.FrameMs);
                Writer.Prop("tickMs", s.TickMs);
                Writer.Prop("tick", s.Tick);
                Writer.Prop("fps", s.Fps);
                Writer.Prop("heapMB", s.HeapMB);
                Writer.Prop("top", s.TopLabels);
                Writer.Prop("topMs", s.TopMs);
                Writer.Prop("stack", s.StackId);
                Writer.Prop("stackLines", s.StackLines);
                Writer.EndObject();
            }
            Writer.EndArray();
        }

        private static void WriteLogs()
        {
            var logs = global::Analyzer.Profiling.Analyzer.Logs;

            Writer.BeginArray("logs");
            if (logs != null)
            {
                int take = Math.Min(logs.Count, MaxLogs);
                for (int i = 0; i < take; i++)
                {
                    var log = logs[i];
                    if (log == null) continue;

                    Writer.BeginObject();
                    Writer.Prop("label", log.label);
                    Writer.Prop("mod", log.modKey);
                    Writer.Prop("key", log.key);
                    Writer.Prop("avg", log.average);
                    Writer.Prop("max", log.max);
                    Writer.Prop("total", log.total);
                    Writer.Prop("calls", log.calls);
                    Writer.Prop("percent", log.percent);
                    Writer.Prop("entries", log.entries);
                    Writer.Prop("pinned", log.pinned);
                    Writer.EndObject();
                }
            }
            Writer.EndArray();
        }

        /// <summary>
        /// Rolls the per-method table up per mod, which is the question players actually ask:
        /// "which mod is eating my TPS".
        /// </summary>
        private static void WriteMods()
        {
            var logs = global::Analyzer.Profiling.Analyzer.Logs;
            var mods = new Dictionary<string, ModAggregate>();

            if (logs != null)
            {
                for (int i = 0; i < logs.Count; i++)
                {
                    var log = logs[i];
                    if (log == null) continue;

                    string key = string.IsNullOrEmpty(log.modKey) ? ModInfoCache.UnknownKey : log.modKey;
                    if (!mods.TryGetValue(key, out var agg))
                    {
                        agg = new ModAggregate { Mod = key };
                        mods[key] = agg;
                    }

                    agg.Methods++;
                    agg.Total += log.total;
                    agg.Calls += log.calls;
                    if (log.max > agg.Max) agg.Max = log.max;
                    if (log.percent > agg.PeakPercent) agg.PeakPercent = log.percent;
                }
            }

            var list = new List<ModAggregate>(mods.Values);
            list.Sort((a, b) => b.Total.CompareTo(a.Total));

            Writer.BeginArray("mods");
            foreach (var m in list)
            {
                Writer.BeginObject();
                Writer.Prop("mod", m.Mod);
                Writer.Prop("total", m.Total);
                Writer.Prop("max", m.Max);
                Writer.Prop("calls", m.Calls);
                Writer.Prop("methods", m.Methods);
                Writer.Prop("peakPercent", m.PeakPercent);
                Writer.EndObject();
            }
            Writer.EndArray();
        }

        private class ModAggregate
        {
            public string Mod;
            public float Total;
            public float Max;
            public float Calls;
            public float PeakPercent;
            public int Methods;
        }
    }
}
