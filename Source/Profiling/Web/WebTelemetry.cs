using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Verse;
using Diag = System.Diagnostics;

namespace Analyzer.Profiling.Web
{
    /// <summary>
    /// One recorded frame-time spike. Kept as a struct in a pre-sized ring so a stutter storm
    /// cannot grow the heap while the game is already struggling.
    /// </summary>
    public struct SpikeRecord
    {
        public int Id;
        public float AtSeconds;
        public float FrameMs;
        public float TickMs;
        public long Tick;
        public int Fps;
        public double HeapMB;
        public string TopLabels;
        public float TopMs;
        public int StackId;
        public int StackLines;
    }

    /// <summary>
    /// Main-thread sampler. Everything here is called from Harmony postfixes that run on the
    /// Unity main thread, so it is allowed to touch UnityEngine and Verse state.
    ///
    /// The web server thread never calls into this class except through <see cref="LatestPayload"/>,
    /// which is a plain string reference swap.
    /// </summary>
    public static class WebTelemetry
    {
        // ~2 minutes of raw frames at 60fps, and 10 minutes of 1Hz history.
        public const int FrameCapacity = 8192;
        public const int HistoryCapacity = 600;
        private const int RecentCapacity = 240;   // last ~4s, used for percentiles

        /// <summary>Master switch. When false the hooks return on the first instruction.</summary>
        public static bool Enabled = true;

        /// <summary>A frame at or above this many milliseconds is recorded as a spike.</summary>
        public static float SpikeThresholdMs = 100f;

        /// <summary>Capture a formatted stack trace on spikes. Rate limited.</summary>
        public static bool CaptureStacks = true;

        private const float StackCaptureIntervalSeconds = 1.5f;
        private const int MaxStackTraces = 40;

        public static int SessionId;
        public static bool Paused;

        // ---- raw frame ring -------------------------------------------------
        public static readonly float[] FrameMs = new float[FrameCapacity];
        public static int FrameHead;
        public static int FrameCount;

        // ---- 1Hz history ----------------------------------------------------
        public static readonly float[] HistFps = new float[HistoryCapacity];
        public static readonly float[] HistTps = new float[HistoryCapacity];
        public static readonly float[] HistFrameAvgMs = new float[HistoryCapacity];
        public static readonly float[] HistHeapMB = new float[HistoryCapacity];
        public static readonly float[] HistWorkingSetMB = new float[HistoryCapacity];
        public static readonly float[] HistGc0 = new float[HistoryCapacity];
        public static readonly float[] HistGc1 = new float[HistoryCapacity];
        public static readonly float[] HistGc2 = new float[HistoryCapacity];
        public static readonly float[] HistTickMs = new float[HistoryCapacity];
        public static int HistHead;
        public static int HistCount;

        // ---- current / per-second readouts ----------------------------------
        public static int Fps;
        public static int Tps;
        public static int TpsTarget;
        public static float CurrentFrameMs;
        public static float WindowAvgFrameMs;
        public static float WindowMaxFrameMs;
        public static float WindowP95FrameMs;
        public static float WindowP99FrameMs;
        public static float LastTickMs;
        public static float WindowMaxTickMs;
        public static double HeapMB;
        public static double WorkingSetMB;
        public static int Gc0PerSec, Gc1PerSec, Gc2PerSec;
        public static int Gc0Total, Gc1Total, Gc2Total;
        public static float SessionSeconds;
        public static long FramesRendered;
        public static long FrameSpikesTotal;

        // ---- spikes ---------------------------------------------------------
        public static readonly SpikeRecord[] Spikes = new SpikeRecord[300];
        public static int SpikeCount;
        public static int SpikeNext;
        private static long spikeSeq;
        private static float lastStackCapture = -999f;
        private static int stackSeq;
        private static readonly Dictionary<int, string> StackTraces = new Dictionary<int, string>();

        // ---- internals ------------------------------------------------------
        private static readonly float[] recent = new float[RecentCapacity];
        private static int recentHead;
        private static int recentCount;
        private static readonly float[] sortScratch = new float[RecentCapacity];

        private static float windowElapsed;
        private static int windowFrames;
        private static double windowMsSum;
        private static float windowMaxMs;
        private static float windowMaxTickMs;
        private static int lastTicksAbs = -1;
        private static int gc0Last, gc1Last, gc2Last;
        private static Game lastGame;
        private static float payloadElapsed;
        private static Diag.Process selfProcess;
        private static readonly Diag.Stopwatch tickWatch = new Diag.Stopwatch();

        /// <summary>Accumulated across all ticks inside the current frame; drained in OnFrame.</summary>
        private static double tickMsThisFrame;

        /// <summary>
        /// Latest serialised payload. Written on the main thread, read by every SSE pump,
        /// so it is volatile to keep the reference read from being hoisted.
        /// </summary>
        public static volatile string LatestPayload = "{\"ready\":false}";

        /// <summary>Set every frame so the UI can show whether DPA hotspot data is flowing.</summary>
        public static bool DeepProfiling;

        /// <summary>HTTP listener state, mirrored into the payload for the page header.</summary>
        public static int ServerPort = 25951;
        public static string ServerStatus = "stopped";
        public static string ServerError;

        public static void ResetSession()
        {
            SessionId++;
            FrameHead = 0;
            FrameCount = 0;
            HistHead = 0;
            HistCount = 0;
            SpikeCount = 0;
            SpikeNext = 0;
            spikeSeq = 0;
            stackSeq = 0;
            StackTraces.Clear();
            recentHead = 0;
            recentCount = 0;

            windowElapsed = 0f;
            windowFrames = 0;
            windowMsSum = 0;
            windowMaxMs = 0f;
            windowMaxTickMs = 0f;
            lastTicksAbs = -1;
            gc0Last = GC.CollectionCount(0);
            gc1Last = GC.CollectionCount(1);
            gc2Last = GC.CollectionCount(2);
            Gc0Total = gc0Last;
            Gc1Total = gc1Last;
            Gc2Total = gc2Last;
            FramesRendered = 0;
            FrameSpikesTotal = 0;
            Fps = 0;
            Tps = 0;
            SessionSeconds = 0f;
            CurrentFrameMs = 0f;
            WindowAvgFrameMs = 0f;
            WindowMaxFrameMs = 0f;
            WindowP95FrameMs = 0f;
            WindowP99FrameMs = 0f;
            WindowMaxTickMs = 0f;
            LastTickMs = 0f;
            tickMsThisFrame = 0;
        }

        /// <summary>Called from the TickManager.DoSingleTick prefix.</summary>
        public static void BeginTick()
        {
            if (!Enabled || Paused) return;
            tickWatch.Restart();
        }

        /// <summary>Called from the TickManager.DoSingleTick postfix.</summary>
        public static void EndTick()
        {
            if (!Enabled || Paused) return;
            tickWatch.Stop();
            float ms = (float)tickWatch.Elapsed.TotalMilliseconds;
            LastTickMs = ms;
            tickMsThisFrame += ms;
        }

        /// <summary>
        /// Called once per frame from the Root_Play.Update postfix. This is the only place
        /// we sample; everything else is derived from what lands here.
        /// </summary>
        public static void OnFrame()
        {
            Game game = Current.Game;
            if (!ReferenceEquals(game, lastGame))
            {
                lastGame = game;
                ResetSession();
            }

            if (!Enabled || Paused) return;
            if (LongEventHandler.ShouldWaitForEvent) return;

            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f) return;
            if (dt > 60f) dt = 60f; // machine slept / window suspended - not a game stutter

            float ms = dt * 1000f;
            CurrentFrameMs = ms;
            SessionSeconds += dt;
            FramesRendered++;

            FrameMs[FrameHead] = ms;
            FrameHead = (FrameHead + 1) % FrameCapacity;
            if (FrameCount < FrameCapacity) FrameCount++;

            recent[recentHead] = ms;
            recentHead = (recentHead + 1) % RecentCapacity;
            if (recentCount < RecentCapacity) recentCount++;

            windowElapsed += dt;
            windowFrames++;
            windowMsSum += ms;
            if (ms > windowMaxMs) windowMaxMs = ms;

            // All ticks belonging to this frame have now run, so the attribution is complete.
            float frameTickMs = (float)tickMsThisFrame;
            tickMsThisFrame = 0;
            if (frameTickMs > windowMaxTickMs) windowMaxTickMs = frameTickMs;

            if (ms >= SpikeThresholdMs)
            {
                RecordSpike(ms, frameTickMs);
                FrameSpikesTotal++;
            }

            if (windowElapsed >= 1f)
                RollWindow();

            payloadElapsed += dt;
            if (!WebServer.IsRunning) return; // nobody is listening - skip the serialisation cost

            float payloadInterval = 1f / Mathf.Clamp(WebEntry.PayloadHz, 0.5f, 10f);
            if (payloadElapsed >= payloadInterval)
            {
                payloadElapsed = 0f;
                LatestPayload = WebSnapshot.Build();
            }
        }

        /// <summary>
        /// Called from the menu root instead of <see cref="OnFrame"/>. Sampling is meaningless
        /// without a colony, but the payload still has to be refreshed so the page can see that
        /// the game left the map rather than silently freezing on stale numbers.
        /// </summary>
        public static void OnIdleFrame()
        {
            Game game = Current.Game;
            if (!ReferenceEquals(game, lastGame))
            {
                lastGame = game;
                ResetSession();
            }

            if (!Enabled || Paused) return;
            if (!WebServer.IsRunning) return;

            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f) return;

            payloadElapsed += dt;
            if (payloadElapsed < 1f / Mathf.Clamp(WebEntry.PayloadHz, 0.5f, 10f)) return;

            payloadElapsed = 0f;
            LatestPayload = WebSnapshot.Build();
        }

        private static void RollWindow()
        {
            float elapsed = windowElapsed <= 0f ? 1f : windowElapsed;

            Fps = Mathf.RoundToInt(windowFrames / elapsed);
            WindowAvgFrameMs = (float)(windowMsSum / Mathf.Max(1, windowFrames));
            WindowMaxFrameMs = windowMaxMs;
            ComputePercentiles();

            if (Current.Game != null)
            {
                int now = GenTicks.TicksAbs;
                if (lastTicksAbs >= 0)
                {
                    Tps = Mathf.RoundToInt((now - lastTicksAbs) / elapsed);
                }
                lastTicksAbs = now;
            }
            else
            {
                Tps = 0;
                lastTicksAbs = -1;
            }

            var tm = Find.TickManager;
            if (tm != null)
            {
                float mult = tm.TickRateMultiplier;
                TpsTarget = (int)Math.Round(mult <= 0f ? 0f : 60f * mult);
            }
            else
            {
                TpsTarget = 0;
            }

            int c0 = GC.CollectionCount(0);
            int c1 = GC.CollectionCount(1);
            int c2 = GC.CollectionCount(2);
            Gc0PerSec = c0 - gc0Last;
            Gc1PerSec = c1 - gc1Last;
            Gc2PerSec = c2 - gc2Last;
            gc0Last = c0; gc1Last = c1; gc2Last = c2;
            Gc0Total = c0; Gc1Total = c1; Gc2Total = c2;

            HeapMB = GC.GetTotalMemory(false) / 1048576.0;
            WorkingSetMB = ReadWorkingSetMB();

            HistFps[HistHead] = Fps;
            HistTps[HistHead] = Tps;
            HistFrameAvgMs[HistHead] = WindowAvgFrameMs;
            HistHeapMB[HistHead] = (float)HeapMB;
            HistWorkingSetMB[HistHead] = (float)WorkingSetMB;
            HistGc0[HistHead] = Gc0PerSec;
            HistGc1[HistHead] = Gc1PerSec;
            HistGc2[HistHead] = Gc2PerSec;
            HistTickMs[HistHead] = windowMaxTickMs;
            HistHead = (HistHead + 1) % HistoryCapacity;
            if (HistCount < HistoryCapacity) HistCount++;

            windowElapsed = 0f;
            windowFrames = 0;
            windowMsSum = 0;
            windowMaxMs = 0f;
            windowMaxTickMs = 0f;
        }

        private static double ReadWorkingSetMB()
        {
            try
            {
                selfProcess ??= Diag.Process.GetCurrentProcess();
                return selfProcess.WorkingSet64 / 1048576.0;
            }
            catch
            {
                return 0d;
            }
        }

        private static void ComputePercentiles()
        {
            int n = recentCount;
            if (n <= 0)
            {
                WindowP95FrameMs = 0f;
                WindowP99FrameMs = 0f;
                return;
            }

            Array.Copy(recent, sortScratch, n);
            Array.Sort(sortScratch, 0, n);

            WindowP95FrameMs = sortScratch[Mathf.Clamp((int)(n * 0.95f), 0, n - 1)];
            WindowP99FrameMs = sortScratch[Mathf.Clamp((int)(n * 0.99f), 0, n - 1)];
        }

        private static void RecordSpike(float frameMs, float frameTickMs)
        {
            var record = new SpikeRecord
            {
                Id = (int)++spikeSeq,
                AtSeconds = SessionSeconds,
                FrameMs = frameMs,
                TickMs = frameTickMs,
                Tick = Current.Game != null ? GenTicks.TicksAbs : 0,
                Fps = Fps,
                HeapMB = HeapMB,
                TopLabels = BuildTopLabels(out float topMs),
                TopMs = topMs
            };

            if (CaptureStacks && SessionSeconds - lastStackCapture >= StackCaptureIntervalSeconds
                && StackTraces.Count < MaxStackTraces)
            {
                lastStackCapture = SessionSeconds;
                string stack = CaptureStack();
                if (!string.IsNullOrEmpty(stack))
                {
                    int id = ++stackSeq;
                    StackTraces[id] = stack;
                    record.StackId = id;
                    record.StackLines = CountLines(stack);
                }
            }

            Spikes[SpikeNext] = record;
            SpikeNext = (SpikeNext + 1) % Spikes.Length;
            if (SpikeCount < Spikes.Length) SpikeCount++;
        }

        private static int CountLines(string s)
        {
            int n = 1;
            for (int i = 0; i < s.Length; i++)
                if (s[i] == '\n') n++;
            return n;
        }

        /// <summary>
        /// Grabs the top few profiled entries at the instant of the spike, so the timeline can
        /// say "this 400ms frame was Thing.Tick". Only populated while DPA is profiling.
        /// </summary>
        private static string BuildTopLabels(out float topMs)
        {
            topMs = 0f;
            var logs = global::Analyzer.Profiling.Analyzer.Logs;
            if (logs == null || logs.Count == 0) return null;

            int take = Mathf.Min(4, logs.Count);
            var sb = new StringBuilder(96);
            for (int i = 0; i < take; i++)
            {
                var log = logs[i];
                if (log == null) continue;
                if (i == 0) topMs = log.max;
                if (i > 0) sb.Append(" | ");
                sb.Append(log.label);
                sb.Append(' ');
                sb.Append(Math.Round(log.max, 1));
                sb.Append("ms");
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        private static string CaptureStack()
        {
            try
            {
                var trace = new Diag.StackTrace(2, true);
                return StackTraceUtility.GetStackTraceString(trace, out _);
            }
            catch (Exception e)
            {
                return "stack capture failed: " + e.Message;
            }
        }

        public static string GetStack(int id)
        {
            return StackTraces.TryGetValue(id, out var value) ? value : null;
        }

        public static void ClearSpikes()
        {
            SpikeCount = 0;
            SpikeNext = 0;
            StackTraces.Clear();
            FrameSpikesTotal = 0;
        }

        /// <summary>Logical index 0 is the oldest retained spike.</summary>
        public static SpikeRecord SpikeAt(int logicalIndex)
        {
            int start = SpikeCount < Spikes.Length ? 0 : SpikeNext;
            int idx = (start + logicalIndex) % Spikes.Length;
            return Spikes[idx];
        }
    }
}
