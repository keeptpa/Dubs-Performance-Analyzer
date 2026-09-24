using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Analyzer.Profiling.Web
{
    /// <summary>
    /// Drives DPA's profiler without the in-game window.
    ///
    /// DPA normally only starts collecting once <see cref="Window_Analyzer"/> has been opened,
    /// because that is where entries are discovered and the Root_Play / DoSingleTick hooks are
    /// installed. The web monitor has to reproduce that bootstrap itself, otherwise the hotspot
    /// table stays empty forever.
    ///
    /// All methods here must run on the main thread (they are queued through <see cref="WebEntry"/>).
    /// </summary>
    public static class WebProfilerControl
    {
        public static bool DeepActive { get; private set; }

        /// <summary>When false DPA profiles the per-frame update work, when true the tick work.</summary>
        public static bool TickCategory { get; private set; }

        private static readonly Category[] DeepCategories =
        {
            Category.Tick,
            Category.Update
        };

        public static void SetDeep(bool on)
        {
            if (on) EnableDeep();
            else DisableDeep();
        }

        private static void EnableDeep()
        {
            try
            {
                if (!WebTelemetry.Enabled)
                    WebTelemetry.Enabled = true;

                LoadEntriesIfNeeded();
                InstallProfilingHooks();
                ApplySavedPatches();

                UseTickCategory(TickCategory);
                PatchAllEntries();

                global::Analyzer.Profiling.Analyzer.BeginProfiling();
                DeepActive = true;
                WebTelemetry.DeepProfiling = true;

                ThreadSafeLogger.Message("[Analyzer] Web monitor enabled deep DPA profiling");
            }
            catch (Exception e)
            {
                DeepActive = false;
                WebTelemetry.DeepProfiling = false;
                ThreadSafeLogger.ReportException(e, "Failed to enable deep profiling from the web monitor");
            }
        }

        private static void DisableDeep()
        {
            try
            {
                global::Analyzer.Profiling.Analyzer.EndProfiling();
            }
            catch (Exception e)
            {
                ThreadSafeLogger.ReportException(e, "Failed to disable deep profiling from the web monitor");
            }

            DeepActive = false;
            WebTelemetry.DeepProfiling = false;
        }

        /// <summary>Switch between tick-mode and update-mode profiling without touching the GUI.</summary>
        public static void UseTickCategory(bool tick)
        {
            TickCategory = tick;

            try
            {
                var tab = GUIController.Tab(tick ? Category.Tick : Category.Update);
                tab?.onClick?.Invoke();
            }
            catch (Exception e)
            {
                ThreadSafeLogger.ReportException(e, "Failed to switch profiler category");
            }
        }

        public static void ResetProfilers()
        {
            try
            {
                GUIController.ResetProfilers();
            }
            catch (Exception e)
            {
                ThreadSafeLogger.ReportException(e, "Failed to reset profilers from the web monitor");
            }
        }

        private static void LoadEntriesIfNeeded()
        {
            if (!Window_Analyzer.firstOpen) return;

            Window_Analyzer.LoadEntries();
            Window_Analyzer.firstOpen = false;
        }

        /// <summary>Mirrors Window_Analyzer.PreOpen - DPA will not collect anything without this.</summary>
        private static void InstallProfilingHooks()
        {
            if (Modbase.isPatched) return;
            if (Modbase.Harmony == null)
            {
                ThreadSafeLogger.Error("[Analyzer] Profiling Harmony instance is missing, cannot enable deep profiling");
                return;
            }

            var rootPlayUpdate = AccessTools.Method(typeof(Root_Play), nameof(Root_Play.Update));
            if (rootPlayUpdate != null)
            {
                Modbase.Harmony.Patch(rootPlayUpdate,
                    prefix: new HarmonyMethod(typeof(H_RootUpdate), nameof(H_RootUpdate.Prefix)),
                    postfix: new HarmonyMethod(typeof(H_RootUpdate), nameof(H_RootUpdate.Postfix)));
            }

            var doSingleTick = AccessTools.Method(typeof(TickManager), nameof(TickManager.DoSingleTick));
            if (doSingleTick != null)
            {
                Modbase.Harmony.Patch(doSingleTick,
                    prefix: new HarmonyMethod(typeof(H_DoSingleTickUpdate), nameof(H_DoSingleTickUpdate.Prefix)),
                    postfix: new HarmonyMethod(typeof(H_DoSingleTickUpdate), nameof(H_DoSingleTickUpdate.Postfix)));
            }

            Modbase.isPatched = true;
        }

        private static void ApplySavedPatches()
        {
            try
            {
                if (Settings.SavedPatches_Tick == null || Settings.SavedPatches_Update == null) return;

                foreach (var patch in Settings.SavedPatches_Tick)
                    Panel_DevOptions.ExecutePatch(CurrentInput.Method, patch, Category.Tick);

                foreach (var patch in Settings.SavedPatches_Update)
                    Panel_DevOptions.ExecutePatch(CurrentInput.Method, patch, Category.Update);
            }
            catch (Exception e)
            {
                ThreadSafeLogger.ReportException(e, "Failed to re-apply saved DPA patches from the web monitor");
            }
        }

        private static void PatchAllEntries()
        {
            var patched = 0;

            foreach (var tab in GUIController.Tabs)
            {
                if (Array.IndexOf(DeepCategories, tab.category) < 0) continue;

                foreach (var entry in new List<Entry>(tab.entries.Keys))
                {
                    try
                    {
                        if (!entry.isPatched) entry.PatchMethods();
                        entry.SetActive(true);
                        patched++;
                    }
                    catch (Exception e)
                    {
                        ThreadSafeLogger.ReportException(e, $"Failed to patch DPA entry {entry.name} from the web monitor");
                    }
                }
            }

            ThreadSafeLogger.Message($"[Analyzer] Web monitor activated {patched} DPA profiling entries");
        }
    }
}
