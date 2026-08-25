using System;
using System.Text;
using BepInEx.Logging;

namespace AdaptiveEnemies
{
    /// <summary>Thin logging facade so nothing else has to hold the BepInEx source.</summary>
    public static class Log
    {
        private static ManualLogSource _log;

        public static void Bind(ManualLogSource source) => _log = source;

        public static void Info(string msg) => _log?.LogInfo(msg);
        public static void Warn(string msg) => _log?.LogWarning(msg);
        public static void Error(string msg) => _log?.LogError(msg);

        /// <summary>Verbose channel, gated on Debug.VerboseLogging.</summary>
        public static void Trace(string msg)
        {
            if (ModConfig.VerboseLogging != null && ModConfig.VerboseLogging.Value)
                _log?.LogInfo(msg);
        }
    }

    public static class Naming
    {
        /// <summary>
        /// Unity decorates duplicates and pooled instances: "MossBone Crawler (1)",
        /// "Silk Bomb(Clone)". Strip that so names match the authored asset name.
        /// Returns the original reference when there is nothing to strip.
        /// </summary>
        public static string StripInstanceSuffix(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;

            var s = name;
            bool changed = false;

            while (true)
            {
                int cut = -1;

                if (s.EndsWith("(Clone)", StringComparison.Ordinal))
                    cut = s.Length - "(Clone)".Length;

                if (cut < 0 && s.Length > 2 && s[s.Length - 1] == ')')
                {
                    int open = s.LastIndexOf('(');
                    if (open > 0)
                    {
                        bool allDigits = open + 1 < s.Length - 1;
                        for (int i = open + 1; i < s.Length - 1 && allDigits; i++)
                            if (!char.IsDigit(s[i])) allDigits = false;
                        if (allDigits) cut = open;
                    }
                }

                if (cut < 0) break;
                s = s.Substring(0, cut).TrimEnd();
                changed = true;
            }

            return changed ? s : name;
        }

        /// <summary>Builds a stable, JSON-safe key fragment.</summary>
        public static string Key(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "_";
            var sb = new StringBuilder(raw.Length);
            foreach (var c in raw)
                sb.Append(c == '|' || c == '\n' || c == '\r' || c == '\t' ? '_' : c);
            return sb.ToString();
        }
    }
}
