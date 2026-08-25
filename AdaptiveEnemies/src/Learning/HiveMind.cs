using System;
using System.Collections.Generic;

namespace AdaptiveEnemies.Learning
{
    /// <summary>
    /// Grandmother Silk. The shared pool every enemy contributes to and draws from.
    ///
    /// <para>Three layers, all in one keyed table so persistence stays trivial:</para>
    /// <list type="bullet">
    /// <item><b>Private</b> (<c>P|</c>, <c>PR|</c>) — per enemy type, per FSM state.
    ///       Nothing outside that type reads it.</item>
    /// <item><b>Class</b> (<c>C|</c>, <c>CR|</c>) — pooled per locomotion class and
    ///       <see cref="Topic"/>, so flying enemies trade dodge-timing knowledge and
    ///       ground enemies trade spacing/approach knowledge.</item>
    /// <item><b>Universal</b> (<c>U|</c>) — a thin layer of player-habit data every
    ///       enemy type may read.</item>
    /// </list>
    /// </summary>
    public class HiveMind
    {
        /// <summary>Gameplay-time clock in seconds. Advances only while the game runs.</summary>
        public double Clock;

        public readonly Dictionary<string, StatCell> Cells = new Dictionary<string, StatCell>(StringComparer.Ordinal);

        public bool Dirty { get; private set; }

        public void MarkDirty() => Dirty = true;
        public void ClearDirty() => Dirty = false;

        public static string PrivateKey(string type, string option)
            => "P|" + Naming.Key(type) + "|" + Naming.Key(option);

        public static string PrivateResponseKey(string type, string option, PlayerAction response)
            => "PR|" + Naming.Key(type) + "|" + Naming.Key(option) + "|" + response;

        /// <summary>
        /// A shared pool, identified by whatever cohort it represents. Two levels are used:
        /// an exact build signature ("Flying|Dive,Melee") for enemies constructed to fight
        /// the same way, and a plain locomotion ("Flying") as a broader fallback. Specific
        /// knowledge is trusted more; the general pool keeps a rare build from starting
        /// from nothing.
        /// </summary>
        public static string ClassKey(string poolId, Topic topic, Archetype arch)
            => "C|" + Naming.Key(poolId) + "|" + topic + "|" + arch;

        public static string ClassResponseKey(string poolId, Topic topic, Archetype arch, PlayerAction response)
            => "CR|" + Naming.Key(poolId) + "|" + topic + "|" + arch + "|" + response;

        /// <summary>How many times this enemy type has been encountered, driving rarity scaling.</summary>
        public static string SeenKey(string type) => "U|seen|" + Naming.Key(type);

        public static string UniversalKey(string habit)
            => "U|" + Naming.Key(habit);

        /// <summary>
        /// What the player was doing when this behaviour LANDED on them. Kept apart from
        /// the missed case because they teach opposite lessons: a response present when
        /// the attack connects is one the enemy wants to keep provoking.
        /// </summary>
        public static string LandedKey(string type, string option, PlayerAction response)
            => "PL|" + Naming.Key(type) + "|" + Naming.Key(option) + "|" + response;

        /// <summary>What the player was doing when this behaviour MISSED.</summary>
        public static string MissedKey(string type, string option, PlayerAction response)
            => "PM|" + Naming.Key(type) + "|" + Naming.Key(option) + "|" + response;

        /// <summary>
        /// Fraction of episodes in which this behaviour damaged the player. This is how
        /// the mod works out for itself which states are attacks, instead of being told.
        /// </summary>
        public static string HitRateKey(string type, string option)
            => "PH|" + Naming.Key(type) + "|" + Naming.Key(option);

        /// <summary>
        /// Value of one candidate level for a continuous action parameter, e.g. how far
        /// above the player a flier positions before diving. Learning over these is what
        /// lets new behaviour emerge without any of it being authored.
        /// </summary>
        public static string ParameterKey(string type, string parameter, int level)
            => "PP|" + Naming.Key(type) + "|" + Naming.Key(parameter) + "|" + level;

        /// <summary>
        /// How fast a key's evidence fades. What an enemy knows about ITSELF — which of its
        /// attacks work — should last across sessions. What it knows about the PLAYER —
        /// which response you favour, what punishes what — should not, or it walks into
        /// every fight already knowing you and the fight has no arc. Read-your-opponent
        /// knowledge has to be re-earned.
        /// </summary>
        public static double HalfLifeFor(string key)
        {
            if (key == null) return ModConfig.EffHalfLife;

            bool aboutPlayer =
                key.StartsWith("PR|", StringComparison.Ordinal) ||
                key.StartsWith("PL|", StringComparison.Ordinal) ||
                key.StartsWith("PM|", StringComparison.Ordinal) ||
                key.StartsWith("CR|", StringComparison.Ordinal) ||
                key.StartsWith("U|resp", StringComparison.Ordinal) ||
                key.StartsWith("U|avoid", StringComparison.Ordinal) ||
                key.StartsWith("U|habit", StringComparison.Ordinal) ||
                key.StartsWith("U|swing", StringComparison.Ordinal);

            return aboutPlayer
                ? Math.Min(ModConfig.PlayerModelHalfLifeSeconds.Value, ModConfig.EffHalfLife)
                : ModConfig.EffHalfLife;
        }

        public StatCell Get(string key, bool create = true)
        {
            if (Cells.TryGetValue(key, out var cell)) return cell;
            if (!create) return null;
            cell = new StatCell(Clock);
            Cells[key] = cell;
            return cell;
        }

        public StatCell Peek(string key) => Get(key, create: false);

        public void Record(string key, double value, double weight)
        {
            var cell = Get(key);
            cell.Add(Clock, HalfLifeFor(key), value, weight);
            Dirty = true;
        }

        /// <summary>Decayed mean and evidence for a key, without creating it.</summary>
        public bool Sample(string key, out double mean, out double n)
        {
            mean = 0.0; n = 0.0;
            var cell = Peek(key);
            if (cell == null) return false;
            cell.Decay(Clock, HalfLifeFor(key));
            if (cell.IsEmpty) return false;
            mean = cell.Mean;
            n = cell.N;
            return true;
        }

        /// <summary>Drops cells that have decayed to nothing, keeping the save file small.</summary>
        public int Prune()
        {
            List<string> dead = null;
            foreach (var kv in Cells)
            {
                kv.Value.Decay(Clock, HalfLifeFor(kv.Key));
                if (kv.Value.IsEmpty)
                {
                    dead ??= new List<string>();
                    dead.Add(kv.Key);
                }
            }
            if (dead == null) return 0;
            foreach (var k in dead) Cells.Remove(k);
            Dirty = true;
            return dead.Count;
        }

        public void Reset()
        {
            Cells.Clear();
            Dirty = true;
        }
    }
}
