using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using Verse;

namespace Analyzer
{
    public static class ModInfoCache
    {
        public const string VanillaKey = "Vanilla";
        public const string UnknownKey = "Unknown";
        public static Dictionary<string, string> AssemblyToModname = new Dictionary<string, string>();

        public static void PopulateCache(string currentModName)
        {
            foreach (ModContentPack mod in LoadedModManager.RunningMods)
            {
                if (mod.Name == currentModName) continue;

                foreach (Assembly ass in mod.assemblies.loadedAssemblies)
                {
                    if (!AssemblyToModname.ContainsKey(ass.FullName))
                        AssemblyToModname.Add(ass.FullName, mod.Name);
                }
            }
        }

        public static string GetModName(string assemblyFullName)
        {
            if (string.IsNullOrEmpty(assemblyFullName)) return UnknownKey;
            if (assemblyFullName.Contains("Assembly-CSharp")
                || assemblyFullName.Contains("UnityEngine")
                || assemblyFullName.Contains("System"))
                return VanillaKey;

            return AssemblyToModname.TryGetValue(assemblyFullName, out var value)
                ? value
                : UnknownKey;
        }

        public static string GetFilterKey(Assembly assembly)
        {
            return assembly == null ? UnknownKey : GetModName(assembly.FullName);
        }

        public static IEnumerable<string> GetFilterOptions()
        {
            yield return VanillaKey;
            yield return UnknownKey;

            foreach (var mod in LoadedModManager.RunningMods
                .Where(mod => !AssemblyToModname.Values.Contains(mod.Name))
                .Select(mod => mod.Name)
                .Distinct()
                .OrderBy(name => name))
                yield return mod;
        }
    }
}
