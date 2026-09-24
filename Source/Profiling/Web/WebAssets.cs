using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace Analyzer.Profiling.Web
{
    /// <summary>
    /// Serves the dashboard's static files out of the assembly's embedded resources, so the
    /// mod folder stays a normal RimWorld mod with no loose web files to lose.
    /// </summary>
    public static class WebAssets
    {
        private static readonly Dictionary<string, byte[]> Cache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private static readonly object Sync = new object();
        private static string[] resourceNames;

        public static byte[] Get(string name)
        {
            lock (Sync)
            {
                if (Cache.TryGetValue(name, out var cached)) return cached;

                byte[] data = Load(name);
                Cache[name] = data;
                return data;
            }
        }

        private static byte[] Load(string name)
        {
            try
            {
                var assembly = typeof(WebAssets).Assembly;
                resourceNames ??= assembly.GetManifestResourceNames();

                string suffix = ".Web.Assets." + name;
                foreach (var resource in resourceNames)
                {
                    if (!resource.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

                    using (var stream = assembly.GetManifestResourceStream(resource))
                    {
                        if (stream == null) continue;

                        using (var memory = new MemoryStream((int)stream.Length))
                        {
                            stream.CopyTo(memory);
                            return memory.ToArray();
                        }
                    }
                }

                LogMissing(name);
            }
            catch (Exception e)
            {
                return Encoding.UTF8.GetBytes($"/* failed to load embedded asset {name}: {e.Message} */");
            }

            return Encoding.UTF8.GetBytes($"/* embedded asset not found: {name} */");
        }

        private static bool warned;

        private static void LogMissing(string name)
        {
            if (warned) return;
            warned = true;
            var names = new StringBuilder();
            foreach (var resource in resourceNames) names.Append("\n  ").Append(resource);
            ThreadSafeLogger.Error($"[Analyzer] Embedded web asset '{name}' was not found. Available resources:{names}");
        }
    }
}
