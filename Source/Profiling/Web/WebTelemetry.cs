using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using Diag = System.Diagnostics;

namespace Analyzer.Profiling.Web
{
    /// <summary>
    /// One recorded frame-time spike, plus indices into the shared attribution pools.
    /// Kept as a struct in a pre-sized ring so a stutter storm cannot grow the heap while the
    /// game is already struggling.
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
        public int MethodSlots;
        public int ModSlots;
        public int MethodsSampled;
    }

    /// <summary>Running total of spike time attributed to one method or one mod.</summary>
    public class SpikeAttribution
    {
        public string Name;
        public string Mod;
        public double TotalMs;
        public float WorstMs;
        public int Spikes;
        public int Calls;
    }

    /// <summary>
    /// Main-thread sampler. Everything here is called from Harmony postfixes that run on the
    /// Unity main thread, so it is allowed to touch UnityEngine and Verse state.
    ///
    /// The web server thread never calls into this class except through <see cref="LatestPayload"/>,
    /// which is a plain volatile string reference swap.
    /// </summary>
    public static class WebTelemetry
    {
        // ~2 minutes of raw frames at 60fps, and 10 minutes of 1Hz history.
        public const int FrameCapacity = 8192;
        public const int HistoryCapacity = 600;
        private const int RecentCapacity = 240;   // last ~4s, used for percentiles
        private const int MaxSpikeSlots = 300;
        public const int TopMethodsPerSpike = 8;
        public const int TopModsPerSpike = 6;
        private const int MaxCyclesPerFrame = 8;

        /// <summary>Master switch. When false the hooks return on the first instruction.</summary>
        public static bool Enabled = true;

        /// <summary>A frame at or above this many milliseconds is recorded as a spike.</summary>
        public static float SpikeThresholdMs = 100f;

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

        /// <summary>False when the runtime cannot report a working set, so the UI can hide it.</summary>
        public static bool WorkingSetAvailable;

        public static int Gc0PerSec, Gc1PerSec, Gc2PerSec;
        public static int Gc0Total, Gc1Total, Gc2Total;
        public static float SessionSeconds;
        public static long FramesRendered;
        public static long FrameSpikesTotal;

        // ---- spikes ---------------------------------------------------------
        public static readonly SpikeRecord[] Spikes = new SpikeRecord[MaxSpikeSlots];
        public static int SpikeCount;
        public static int SpikeNext;
        private static long spikeSeq;

        /// <summary>
        /// Per-spike attribution pools. Flat arrays indexed by [spikeSlot * slots + i] so a spike
        /// never allocates; the strings are references to DPA's own labels.
        /// </summary>
        public static readonly float[] SpikeMethodMs = new float[MaxSpikeSlots * TopMethodsPerSpike];
        public static readonly string[] SpikeMethodLabels = new string[MaxSpikeSlots * TopMethodsPerSpike];
        public static readonly string[] SpikeMethodMods = new string[MaxSpikeSlots * TopMethodsPerSpike];
        public static readonly float[] SpikeModMs = new float[MaxSpikeSlots * TopModsPerSpike];
        public static readonly string[] SpikeModNames = new string[MaxSpikeSlots * TopModsPerSpike];

        /// <summary>Accumulated across every recorded spike - the "what keeps stuttering" answer.</summary>
        public static readonly Dictionary<string, SpikeAttribution> SpikeByMethod = new Dictionary<string, SpikeAttribution>();
        public static readonly Dictionary<string, SpikeAttribution> SpikeByMod = new Dictionary<string, SpikeAttribution>();

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

        /// <summary>How many times DPA closed a profiling cycle during this frame.</summary>
        private static int cyclesThisFrame;

        public static int CyclesThisFrame => cyclesThisFrame;

        /// <summary>Latest serialised payload. Written on the main thread, read by every SSE pump.</summary>
        public static volatile string LatestPayload = "{\"ready\":false}";

        /// <summary>Set every frame so the UI can show whether DPA hotspot data is flowing.</summary>
        public static bool DeepProfiling;

        /// <summary>HTTP listener state, mirrored into the payload for the page header.</summary>
        public static int ServerPort = 25951;
        public static string ServerStatus = "stopped";
        public static string ServerError;

        // scratch state for attribution, reused so a spike does not allocate
        private static readonly Dictionary<string, float> modTotalsScratch = new Dictionary<string, float>();
        private static readonly Dictionary<string, int> modCallsScratch = new Dictionary<string, int>();
        private static readonly Dictionary<string, string> modKeyCache = new Dictionary<string, string>();

        public static void ResetSession()
        {
            SessionId++;
            FrameHead = 0;
            FrameCount = 0;
            HistHead = 0;
            HistCount = 0;
            SpikeCount = 0;
            SpikeNext = 0;
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
            cyclesThisFrame = 0;

            ClearSpikes();
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
        /// Called when DPA closes a profiling cycle. One cycle is one frame in update mode and
        /// one tick in tick mode, which is why the attribution below sums the last N cycles.
        /// </summary>
        public static void NotifyUpdateCycle()
        {
            if (!Enabled || Paused) return;
            cyclesThisFrame++;
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

            if (!Enabled || Paused) { cyclesThisFrame = 0; return; }
            if (LongEventHandler.ShouldWaitForEvent) { cyclesThisFrame = 0; return; }

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

            cyclesThisFrame = 0;

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
                // Mono (which is what Unity gives us) does not populate Process.WorkingSet64,
                // it comes back as zero. Environment.WorkingSet is the portable one that does work.
                long bytes = Environment.WorkingSet;
                if (bytes <= 0)
                {
                    selfProcess ??= Diag.Process.GetCurrentProcess();
                    bytes = selfProcess.WorkingSet64;
                }

                WorkingSetAvailable = bytes > 0;
                return bytes / 1048576.0;
            }
            catch
            {
                WorkingSetAvailable = false;
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

        // ---- spike attribution ------------------------------------------------

        private static void RecordSpike(float frameMs, float frameTickMs)
        {
            int slot = SpikeNext;

            var record = new SpikeRecord
            {
                Id = (int)++spikeSeq,
                AtSeconds = SessionSeconds,
                FrameMs = frameMs,
                TickMs = frameTickMs,
                Tick = Current.Game != null ? GenTicks.TicksAbs : 0,
                Fps = Fps,
                HeapMB = HeapMB
            };

            CaptureAttribution(slot, ref record);

            Spikes[slot] = record;
            SpikeNext = (SpikeNext + 1) % Spikes.Length;
            if (SpikeCount < Spikes.Length) SpikeCount++;
        }

        /// <summary>
        /// Works out what the frame actually spent its time on.
        ///
        /// A stack trace taken here would be worthless: by the time the frame ends the only
        /// frames left are Root.Update and our own postfix, which says nothing about the work
        /// that just took 400ms. DPA already timed every patched method, so we read those
        /// timings back instead - summing the cycles that belong to this frame.
        /// </summary>
        private static void CaptureAttribution(int slot, ref SpikeRecord record)
        {
            int methodBase = slot * TopMethodsPerSpike;
            int modBase = slot * TopModsPerSpike;

            for (int i = 0; i < TopMethodsPerSpike; i++)
            {
                SpikeMethodMs[methodBase + i] = 0f;
                SpikeMethodLabels[methodBase + i] = null;
                SpikeMethodMods[methodBase + i] = null;
            }
            for (int i = 0; i < TopModsPerSpike; i++)
            {
                SpikeModMs[modBase + i] = 0f;
                SpikeModNames[modBase + i] = null;
            }

            var profiles = ProfileController.Profiles;
            if (profiles == null || profiles.IsEmpty) return;

            int cycles = Math.Max(1, Math.Min(cyclesThisFrame, MaxCyclesPerFrame));
            modTotalsScratch.Clear();
            modCallsScratch.Clear();

            int sampled = 0;

            foreach (var pair in profiles)
            {
                var profiler = pair.Value;
                if (profiler == null || profiler.Empty) continue;

                float total = 0f;
                int calls = 0;
                int index = (int)profiler.currentIndex;

                for (int j = 0; j < cycles; j++)
                {
                    int at = index - 1 - j;
                    if (at < 0) at += Profiler.RECORDS_HELD;
                    if (at >= Profiler.RECORDS_HELD) continue;
                    if (profiler.hits[at] == 0) continue; // no calls in that cycle; the time slot is stale

                    total += (float)profiler.times[at];
                    calls += profiler.hits[at];
                }

                if (total <= 0f) continue;
                sampled++;

                string mod = ModKeyFor(profiler);
                InsertMethod(methodBase, profiler.label ?? profiler.key, mod, total);
                Accumulate(modTotalsScratch, mod, total);
                Accumulate(modCallsScratch, mod, calls);

                AccumulateAttribution(SpikeByMethod, profiler.label ?? profiler.key, mod, total, calls);
            }

            int methods = 0;
            for (int i = 0; i < TopMethodsPerSpike; i++)
                if (SpikeMethodLabels[methodBase + i] != null) methods++;

            foreach (var pair in modTotalsScratch)
            {
                InsertMod(modBase, pair.Key, pair.Value);
                modCallsScratch.TryGetValue(pair.Key, out int calls);
                AccumulateAttribution(SpikeByMod, pair.Key, pair.Key, pair.Value, calls);
            }

            int mods = 0;
            for (int i = 0; i < TopModsPerSpike; i++)
                if (SpikeModNames[modBase + i] != null) mods++;

            record.MethodSlots = methods;
            record.ModSlots = mods;
            record.MethodsSampled = sampled;
        }

        private static void Accumulate(Dictionary<string, float> map, string key, float value)
        {
            map.TryGetValue(key, out float current);
            map[key] = current + value;
        }

        private static void Accumulate(Dictionary<string, int> map, string key, int value)
        {
            map.TryGetValue(key, out int current);
            map[key] = current + value;
        }

        private static void AccumulateAttribution(Dictionary<string, SpikeAttribution> map, string name, string mod, float ms, int calls)
        {
            if (!map.TryGetValue(name, out var entry))
            {
                entry = new SpikeAttribution { Name = name, Mod = mod };
                map[name] = entry;
            }

            entry.TotalMs += ms;
            entry.Spikes++;
            entry.Calls += calls;
            if (ms > entry.WorstMs) entry.WorstMs = ms;
        }

        /// <summary>Insertion sort into the fixed top-N array, which is at most 8 long.</summary>
        private static void InsertMethod(int baseIndex, string label, string mod, float ms)
        {
            int at = -1;
            for (int i = 0; i < TopMethodsPerSpike; i++)
            {
                if (SpikeMethodLabels[baseIndex + i] == null || ms > SpikeMethodMs[baseIndex + i])
                {
                    at = i;
                    break;
                }
            }
            if (at < 0) return;

            for (int i = TopMethodsPerSpike - 1; i > at; i--)
            {
                SpikeMethodMs[baseIndex + i] = SpikeMethodMs[baseIndex + i - 1];
                SpikeMethodLabels[baseIndex + i] = SpikeMethodLabels[baseIndex + i - 1];
                SpikeMethodMods[baseIndex + i] = SpikeMethodMods[baseIndex + i - 1];
            }

            SpikeMethodMs[baseIndex + at] = ms;
            SpikeMethodLabels[baseIndex + at] = label;
            SpikeMethodMods[baseIndex + at] = mod;
        }

        private static void InsertMod(int baseIndex, string name, float ms)
        {
            int at = -1;
            for (int i = 0; i < TopModsPerSpike; i++)
            {
                if (SpikeModNames[baseIndex + i] == null || ms > SpikeModMs[baseIndex + i])
                {
                    at = i;
                    break;
                }
            }
            if (at < 0) return;

            for (int i = TopModsPerSpike - 1; i > at; i--)
            {
                SpikeModMs[baseIndex + i] = SpikeModMs[baseIndex + i - 1];
                SpikeModNames[baseIndex + i] = SpikeModNames[baseIndex + i - 1];
            }

            SpikeModMs[baseIndex + at] = ms;
            SpikeModNames[baseIndex + at] = name;
        }

        private static string ModKeyFor(Profiler profiler)
        {
            if (modKeyCache.TryGetValue(profiler.key, out var cached)) return cached;

            // Same resolution DPA itself uses when it fills ProfileLog.modKey.
            var assembly = profiler.meth?.DeclaringType?.Assembly ?? profiler.type?.Assembly;
            string mod = ModInfoCache.GetFilterKey(assembly);
            modKeyCache[profiler.key] = mod;
            return mod;
        }

        public static void ClearSpikes()
        {
            SpikeCount = 0;
            SpikeNext = 0;
            FrameSpikesTotal = 0;
            SpikeByMethod.Clear();
            SpikeByMod.Clear();
            Array.Clear(SpikeMethodLabels, 0, SpikeMethodLabels.Length);
            Array.Clear(SpikeMethodMods, 0, SpikeMethodMods.Length);
            Array.Clear(SpikeModNames, 0, SpikeModNames.Length);
        }

        /// <summary>Logical index 0 is the oldest retained spike.</summary>
        public static SpikeRecord SpikeAt(int logicalIndex)
        {
            int start = SpikeCount < Spikes.Length ? 0 : SpikeNext;
            return Spikes[(start + logicalIndex) % Spikes.Length];
        }

        /// <summary>Physical ring slot for a logical index, used to reach the attribution pools.</summary>
        public static int SpikeSlot(int logicalIndex)
        {
            int start = SpikeCount < Spikes.Length ? 0 : SpikeNext;
            return (start + logicalIndex) % Spikes.Length;
        }
    }
}
