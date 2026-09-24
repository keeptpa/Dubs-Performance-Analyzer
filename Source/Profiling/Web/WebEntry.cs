using System;
using System.Collections.Concurrent;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Analyzer.Profiling.Web
{
    /// <summary>
    /// Lifecycle and main-thread marshalling for the web monitor.
    ///
    /// The HTTP threads are not allowed to touch game state, so every mutation they request is
    /// queued here and drained from a Harmony postfix on the Unity main thread.
    /// </summary>
    public static class WebEntry
    {
        /// <summary>How often the JSON payload is rebuilt and pushed to subscribers.</summary>
        public static float PayloadHz = 2f;

        private static readonly ConcurrentQueue<Action> controls = new ConcurrentQueue<Action>();
        private static bool started;
        private static bool hooksApplied;

        public static void Start()
        {
            if (started) return;
            started = true;

            try
            {
                ApplyHooks();
                WebTelemetry.ServerPort = Settings.webMonitorPort;

                if (Settings.webMonitorEnabled)
                    StartServer();

                ThreadSafeLogger.Message("[Analyzer] Web performance monitor initialised"
                    + (Settings.webMonitorEnabled
                        ? $" - listening on http://127.0.0.1:{Settings.webMonitorPort}/"
                        : " - disabled in settings"));
            }
            catch (Exception e)
            {
                ThreadSafeLogger.ReportException(e, "Failed to initialise the web performance monitor");
            }
        }

        private static void ApplyHooks()
        {
            if (hooksApplied) return;
            if (Modbase.StaticHarmony == null) return;

            var harmony = Modbase.StaticHarmony;
            var self = typeof(WebEntry);

            var rootPlayUpdate = AccessTools.Method(typeof(Root_Play), nameof(Root_Play.Update));
            if (rootPlayUpdate == null)
            {
                ThreadSafeLogger.Error("[Analyzer] Could not find Root_Play.Update - the web monitor will not collect frame data");
            }
            else
            {
                harmony.Patch(rootPlayUpdate, postfix: new HarmonyMethod(self, nameof(FramePostfix)));
            }

            var doSingleTick = AccessTools.Method(typeof(TickManager), nameof(TickManager.DoSingleTick));
            if (doSingleTick == null)
            {
                ThreadSafeLogger.Error("[Analyzer] Could not find TickManager.DoSingleTick - tick timings will be missing");
            }
            else
            {
                harmony.Patch(doSingleTick,
                    prefix: new HarmonyMethod(self, nameof(TickPrefix)),
                    postfix: new HarmonyMethod(self, nameof(TickPostfix)));
            }

            // Menus do not run Root_Play, so drain the control queue there too.
            var entryUpdate = AccessTools.Method(AccessTools.TypeByName("Verse.Root_Entry"), "Update");
            if (entryUpdate != null)
            {
                harmony.Patch(entryUpdate, postfix: new HarmonyMethod(self, nameof(MenuPostfix)));
            }

            // Each call is one profiling cycle: a frame in update mode, a tick in tick mode.
            // The spike attribution sums the cycles that fall inside a frame.
            var endUpdate = AccessTools.Method(typeof(ProfileController), nameof(ProfileController.EndUpdate));
            if (endUpdate != null)
            {
                harmony.Patch(endUpdate, postfix: new HarmonyMethod(self, nameof(UpdateCyclePostfix)));
            }

            hooksApplied = true;
        }

        public static void FramePostfix()
        {
            DrainControls();
            ThreadSafeLogger.DisplayLogs();
            WebTelemetry.OnFrame();
        }

        public static void MenuPostfix()
        {
            DrainControls();
            ThreadSafeLogger.DisplayLogs();
            WebTelemetry.OnIdleFrame();
        }

        public static void TickPrefix() => WebTelemetry.BeginTick();

        public static void TickPostfix() => WebTelemetry.EndTick();

        public static void UpdateCyclePostfix() => WebTelemetry.NotifyUpdateCycle();

        /// <summary>Queue a main-thread action. Safe to call from an HTTP thread.</summary>
        public static void Enqueue(Action action)
        {
            if (action == null) return;
            controls.Enqueue(action);
        }

        private static void DrainControls()
        {
            while (controls.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    ThreadSafeLogger.ReportException(e, "A web monitor control action failed");
                }
            }
        }

        // ---- server plumbing -------------------------------------------------

        public static bool StartServer()
        {
            bool wasSampling = WebTelemetry.Enabled;
            ApplySettingsFromStorage();

            WebTelemetry.Enabled = true;
            if (!wasSampling) WebTelemetry.ResetSession();

            string error = WebServer.Start(Settings.webMonitorPort);
            WebTelemetry.ServerStatus = error == null ? "running" : "error";
            WebTelemetry.ServerError = error;
            WebTelemetry.ServerPort = Settings.webMonitorPort;
            if (error != null)
                ThreadSafeLogger.Error($"[Analyzer] Web monitor failed to listen on port {Settings.webMonitorPort}: {error}");
            return error == null;
        }

        public static void StopServer()
        {
            WebServer.Stop();
            WebTelemetry.Enabled = false;
            WebTelemetry.ServerStatus = "stopped";
            WebTelemetry.ServerError = null;
        }

        public static void RestartServer()
        {
            StopServer();
            StartServer();
        }

        private static void ApplySettingsFromStorage()
        {
            WebTelemetry.SpikeThresholdMs = Settings.webSpikeThresholdMs;
            WebTelemetry.Enabled = Settings.webMonitorEnabled;
            PayloadHz = Settings.webPayloadHz;
        }
    }
}
