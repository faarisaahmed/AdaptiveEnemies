using System;
using System.Collections.Generic;
using UnityEngine;

namespace AdaptiveEnemies.Runtime
{
    /// <summary>
    /// Decides which GameObjects are adaptable enemies.
    ///
    /// <para>There is deliberately no roster here. An enemy qualifies by what it is — it can
    /// be damaged, and it can hurt the player — not by being on a list. That is what lets a
    /// two-hundred-enemy game be covered without anyone enumerating it, and what stops the
    /// mod going stale when the game adds something.</para>
    /// </summary>
    public static class EnemyIdentity
    {
        private static HashSet<string> _targets, _excludes;
        private static string _targetsRaw, _excludesRaw;

        private static HashSet<string> Parse(string raw, ref HashSet<string> cache, ref string cachedRaw)
        {
            raw ??= string.Empty;
            if (cache != null && string.Equals(raw, cachedRaw, StringComparison.Ordinal)) return cache;

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in raw.Split(','))
            {
                var name = part.Trim();
                if (name.Length > 0) set.Add(name);
            }

            cache = set;
            cachedRaw = raw;
            return set;
        }

        private static HashSet<string> Targets()
            => Parse(ModConfig.TargetEnemies.Value, ref _targets, ref _targetsRaw);

        private static HashSet<string> Excludes()
            => Parse(ModConfig.ExcludeEnemies.Value, ref _excludes, ref _excludesRaw);

        /// <summary>
        /// True when this object is an enemy the mod should adapt.
        ///
        /// <para>Explicit entries in TargetEnemies always qualify, which is how one boss can
        /// be worked on while the rest of the bosses are left alone.</para>
        /// </summary>
        public static bool IsTarget(GameObject go, out string typeName)
        {
            typeName = null;
            if (go == null) return false;

            var clean = Naming.StripInstanceSuffix(go.name);

            // Named explicitly: always in, whatever else is configured.
            if (Targets().Contains(clean)) { typeName = clean; return true; }

            if (!ModConfig.AdaptAllEnemies.Value) return false;
            if (Excludes().Contains(clean)) return false;

            var health = go.GetComponent<HealthManager>();
            if (health == null) return false;

            // Must be able to hurt the player, somewhere on itself. Breakables and scenery
            // carry a HealthManager too, and adapting those would be meaningless.
            if (go.GetComponentInChildren<DamageHero>(true) == null) return false;

            // Bosses are held back until the ordinary roster is settled, so one cannot be
            // altered by accident while testing something else.
            if (!ModConfig.AdaptBosses.Value && health.hp >= ModConfig.BossHpThreshold.Value) return false;

            typeName = clean;
            return true;
        }

        /// <summary>Locomotion, from components and physics rather than from a table.</summary>
        public static EnemyClass ClassOf(string typeName, GameObject go)
            => EnemyProfile.DetectLocomotion(go);
    }
}
