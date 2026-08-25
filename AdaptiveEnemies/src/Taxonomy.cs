using System;

namespace AdaptiveEnemies
{
    /// <summary>
    /// How the player hurt (or tried to hurt) an enemy. Tools are bucketed by
    /// function rather than tracked individually, except Cogflies which the
    /// design calls out as a standout and gives its own bucket.
    /// </summary>
    public enum PlayerAction
    {
        None = 0,

        Pogo,
        SlashLeft,
        SlashRight,
        SlashUp,

        ToolRangedProjectile,
        ToolCloseBurst,
        ToolTrapZone,
        ToolMobility,
        ToolSummon,
        ToolCogflies,
        ToolUnclassified,

        OtherDamage,
    }

    /// <summary>How the player dealt with an incoming enemy attack.</summary>
    /// <remarks>
    /// Silksong models parrying with two <c>HeroControllerStates</c> flags under one
    /// "Cross Stitch Parry" mechanic: <c>parrying</c> is the stance, <c>parryAttack</c>
    /// the counter-hit. Those map to <see cref="NeedleParry"/> and <see cref="CrossStitch"/>.
    /// <see cref="Evade"/> is an addition beyond the original four — Silksong's dash is
    /// a real avoidance channel and folding it into <see cref="Jump"/> would corrupt
    /// the jump statistics.
    /// </remarks>
    public enum AvoidMethod
    {
        NotAvoided = 0,
        Jump,
        NeedleParry,
        CrossStitch,
        Evade,
    }

    /// <summary>Locomotion class, which drives how strongly two enemy types share data.</summary>
    public enum EnemyClass
    {
        Ground = 0,
        Flying,
    }

    /// <summary>
    /// Coarse shape of an enemy behaviour, derived from its FSM state name. The hive
    /// shares at this granularity because raw state names are per-prefab and would
    /// never match across enemy types.
    /// </summary>
    public enum Archetype
    {
        Other = 0,  // not combat-relevant: idle, praying, spawning, dying
        Approach,   // closing distance
        Melee,      // contact attack or its telegraph
        Ranged,     // projectile
        Dive,       // committed aerial or charging attack
        Summon,     // calls in minions or hazards that fight for it
        Reposition, // recovery / turn / back off / defensive — the punish window
    }

    /// <summary>
    /// The subject a piece of knowledge is about. Sharing is topic-scoped so flying
    /// enemies trade dodge-timing data and ground enemies trade spacing/approach data,
    /// rather than pooling everything indiscriminately.
    /// </summary>
    public enum Topic
    {
        General = 0,
        Spacing,
        Approach,
        DodgeTiming,
    }

    public static class TaxonomyUtil
    {
        public static bool IsTool(this PlayerAction a)
            => a >= PlayerAction.ToolRangedProjectile && a <= PlayerAction.ToolUnclassified;

        public static bool IsNail(this PlayerAction a)
            => a >= PlayerAction.Pogo && a <= PlayerAction.SlashUp;

        /// <summary>
        /// True for states where the enemy is actually threatening the player. Used to
        /// gate scoring: an enemy sitting in "Idle" while Hornet jumps around nearby has
        /// not been "avoided", and treating that as evidence buries the real signal.
        /// </summary>
        public static bool IsThreatening(this Archetype a)
            => a == Archetype.Melee || a == Archetype.Ranged
            || a == Archetype.Dive || a == Archetype.Summon;

        /// <summary>
        /// Maps an FSM state name onto a shareable archetype.
        /// <para>
        /// The vocabulary is taken from every scene bundle in the game rather than from a
        /// handful of enemies, so it covers the conventions Team Cherry actually used across
        /// the whole roster: "Antic" for a telegraph, "Recover" for the punish window,
        /// "Parried Recover" after a successful parry, and so on.
        /// </para>
        /// </summary>
        public static Archetype ClassifyState(string stateName)
        {
            if (string.IsNullOrEmpty(stateName)) return Archetype.Other;
            var s = stateName.ToLowerInvariant();

            // Vocabulary mined from every scene bundle in the game: 590 scenes, 53k distinct
            // names. Ordering is load-bearing, because Silksong composes state names — an
            // "Attack Recover" is a recovery, a "Charge Antic" is the wind-up of a charge.
            // Each tier is tested before the ones whose words it might also contain.

            // 1. Recovery and defence. First, because "Attack Recover" and the 6,304
            //    "Parried Recover" states both carry an attack word they must not match.
            if (Has(s, "recover", "parried", "stagger", "reset", "collide", "end",
                       "land", "turn", "retreat", "back off", "hover", "cooldown",
                       "block", "guard", "shield", "evade", "dodge", "flinch"))
                return Archetype.Reposition;

            // 2. Not combat at all: idling, waking, spawning, dying, performing.
            if (Has(s, "idle", "sleep", "wake", "dormant", "init", "start", "spawn",
                       "pray", "sing", "needolin", "extract", "death", "die", "dead",
                       "corpse", "stun", "roar", "scream", "screech", "emerge", "reform",
                       "crumble", "burrow", "hide", "enter", "exit", "intro", "range out",
                       "still in range", "bonk", "rage", "transition"))
                return Archetype.Other;

            // 3. Calling in help. Its damage arrives indirectly, so it is credited through
            //    the chain root rather than by contact.
            if (Has(s, "summon", "call", "spew", "birth", "hatch"))
                return Archetype.Summon;

            // 4. Attacks at range.
            if (Has(s, "shoot", "spit", "fire", "throw", "projectile", "bullet", "launch",
                       "beam", "laser", "barrage", "volley", "sling", "bomb", "gas",
                       "cloud", "mine", "needle", "shot"))
                return Archetype.Ranged;

            // 5. Committed movement attacks — closing the distance IS the attack.
            if (Has(s, "dive", "drill", "swoop", "plunge", "lunge", "charge", "pounce",
                       "tackle", "ram", "drop", "fall", "rush", "dash"))
                return Archetype.Dive;

            // 6. Contact attacks. "antic" comes last here so that a "Charge Antic" is
            //    already a charge and only an unqualified wind-up lands as melee.
            if (Has(s, "attack", "slash", "bite", "strike", "slam", "stab", "hit", "nail",
                       "stomp", "smash", "crush", "pound", "whip", "sweep", "spin", "kick",
                       "headbutt", "pierce", "impale", "skewer", "snip", "snap", "grab",
                       "latch", "ambush", "swipe", "claw", "chomp", "antic"))
                return Archetype.Melee;

            // 7. Getting into position.
            if (Has(s, "chase", "run", "advance", "pursue", "approach", "walk", "fly",
                       "above", "crawl", "patrol", "pace", "jump", "leap", "hop", "bounce",
                       "pull", "drag", "move", "follow", "circle", "climb"))
                return Archetype.Approach;

            return Archetype.Other;
        }

        /// <summary>The topic a given archetype carries knowledge about.</summary>
        public static Topic TopicOf(Archetype a)
        {
            switch (a)
            {
                case Archetype.Approach: return Topic.Approach;
                case Archetype.Melee: return Topic.Spacing;
                case Archetype.Ranged: return Topic.Spacing;
                case Archetype.Dive: return Topic.DodgeTiming;
                case Archetype.Summon: return Topic.Spacing;
                case Archetype.Reposition: return Topic.DodgeTiming;
                default: return Topic.General;
            }
        }

        /// <summary>
        /// How relevant a topic is to a locomotion class — the "similarity" term in the
        /// similarity-weighted sharing model. Flying enemies care about dodge timing;
        /// ground enemies care about spacing and approach.
        /// </summary>
        public static double TopicAffinity(EnemyClass cls, Topic topic)
        {
            switch (topic)
            {
                case Topic.DodgeTiming:
                    return cls == EnemyClass.Flying ? 1.0 : 0.35;
                case Topic.Spacing:
                case Topic.Approach:
                    return cls == EnemyClass.Ground ? 1.0 : 0.35;
                default:
                    return 0.5;
            }
        }

        /// <summary>
        /// Whole-word match against a state name.
        ///
        /// <para>Substring matching cannot be used here. Checked naively, "ram" matches
        /// <c>Keyframe</c>, "end" matches <c>Renderer</c>, "pace" matches <c>Space</c> and
        /// "call" matches <c>PersistentCall</c> — so ordinary object and component names get
        /// read as attacks. Silksong writes state names as words ("Slam Antic", "Drill
        /// Recover"), sometimes camel-cased, so the name is split into words first and each
        /// word compared whole.</para>
        ///
        /// <para>Words of four letters or more also match by prefix, so "attack" catches
        /// "attacking" and "slash" catches "slashes", while short words like "ram" and "end"
        /// must match exactly.</para>
        /// </summary>
        private static bool Has(string haystack, params string[] needles)
        {
            if (string.IsNullOrEmpty(haystack)) return false;

            int start = 0;
            while (start < haystack.Length)
            {
                // Skip separators.
                while (start < haystack.Length && !char.IsLetter(haystack[start])) start++;
                if (start >= haystack.Length) break;

                // A word runs until the next separator, or the next capital in camelCase.
                int end = start + 1;
                while (end < haystack.Length && char.IsLetter(haystack[end]))
                {
                    if (char.IsUpper(haystack[end]) && char.IsLower(haystack[end - 1])) break;
                    end++;
                }

                int len = end - start;
                for (int i = 0; i < needles.Length; i++)
                {
                    var needle = needles[i];

                    // Multi-word needles ("back off") are matched against the whole name.
                    if (needle.IndexOf(' ') >= 0)
                    {
                        if (haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        continue;
                    }

                    if (len < needle.Length) continue;
                    if (len > needle.Length && needle.Length < 4) continue; // short words: exact only

                    if (string.Compare(haystack, start, needle, 0, needle.Length,
                            StringComparison.OrdinalIgnoreCase) == 0)
                        return true;
                }

                start = end;
            }

            return false;
        }
    }
}
