using System.Collections.Generic;
using System.Linq;
using Analyzer;

namespace Analyzer.Profiling
{
    public static class ModFilter
    {
        public static HashSet<string> BlockedKeys = new HashSet<string>();

        public static bool IsAllowed(ProfileLog log)
        {
            return log != null && !BlockedKeys.Contains(log.modKey ?? ModInfoCache.UnknownKey);
        }

        public static bool IsAllowed(string modKey)
        {
            return !BlockedKeys.Contains(modKey ?? ModInfoCache.UnknownKey);
        }

        public static void Toggle(string modKey)
        {
            if (string.IsNullOrEmpty(modKey)) return;
            if (!BlockedKeys.Add(modKey))
                BlockedKeys.Remove(modKey);
        }

        public static void SetAll(bool allowed)
        {
            if (allowed)
            {
                BlockedKeys.Clear();
                return;
            }

            BlockedKeys = new HashSet<string>(ModInfoCache.GetFilterOptions());
        }

        public static bool HasBlockedMods => BlockedKeys.Count != 0;

        public static IEnumerable<string> FilterOptions
            => ModInfoCache.GetFilterOptions()
                .Concat(BlockedKeys.Where(key => !ModInfoCache.GetFilterOptions().Contains(key)))
                .Distinct();
    }
}
