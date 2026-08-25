using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AdaptiveEnemies.Runtime
{
    /// <summary>
    /// What an enemy <em>is</em>, worked out from what it has rather than from a list.
    ///
    /// <para>Two enemies share knowledge when they are built to fight the same way — same
    /// locomotion, same kinds of attack. A ground walker and a diving flier have almost
    /// nothing to teach each other, while two ground melee enemies have a great deal, even
    /// if they live in different areas and were authored years apart.</para>
    ///
    /// <para>The signature is derived at runtime from the enemy's components and the shape
    /// of its own FSM, so a roster of two hundred classifies itself as the player meets it.
    /// Nothing here names an enemy.</para>
    /// </summary>
    internal sealed class EnemyProfile
    {
        public EnemyClass Locomotion;

        /// <summary>Which kinds of behaviour this enemy actually possesses.</summary>
        public readonly HashSet<Archetype> Capabilities = new HashSet<Archetype>();

        /// <summary>True when the enemy hurts by touch rather than by a distinct attack.</summary>
        public bool ContactOnly;

        /// <summary>Stable key identifying "enemies built like this one".</summary>
        public string Signature { get; private set; } = "Unclassified";

        /// <summary>
        /// Reads locomotion from components and physics. Component evidence beats the
        /// gravity heuristic, because a hovering enemy under gravity is still a flier and a
        /// zero-gravity crawler is still a walker.
        /// </summary>
        public static EnemyClass DetectLocomotion(GameObject go)
        {
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                var n = c.GetType().Name;

                if (n == "Walker" || n == "Crawler" || n == "Climber") return EnemyClass.Ground;
                if (n.IndexOf("Flyer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Flier", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Hover", StringComparison.OrdinalIgnoreCase) >= 0)
                    return EnemyClass.Flying;
            }

            var rb = go.GetComponent<Rigidbody2D>();
            if (rb != null && Mathf.Abs(rb.gravityScale) < 0.01f) return EnemyClass.Flying;

            return EnemyClass.Ground;
        }

        /// <summary>
        /// Builds the profile from the enemy's discovered structure. Capabilities come from
        /// the archetypes its own states fall into, so an enemy that has no diving state can
        /// never be handed diving knowledge — there is nowhere for it to land.
        /// </summary>
        public void Build(GameObject go, IEnumerable<string> stateNames, bool contactOnly)
        {
            Locomotion = DetectLocomotion(go);
            ContactOnly = contactOnly;

            Capabilities.Clear();
            if (stateNames != null)
            {
                foreach (var s in stateNames)
                {
                    var a = TaxonomyUtil.ClassifyState(s);
                    if (a != Archetype.Other) Capabilities.Add(a);
                }
            }

            Signature = BuildSignature();
        }

        private string BuildSignature()
        {
            var sb = new StringBuilder();
            sb.Append(Locomotion);

            if (ContactOnly) { sb.Append("|Contact"); return sb.ToString(); }

            // Only the offensive shapes matter for who should learn from whom. Approach and
            // reposition are near-universal and would collapse every enemy into one class.
            bool any = false;
            foreach (var a in new[] { Archetype.Melee, Archetype.Ranged, Archetype.Dive, Archetype.Summon })
            {
                if (!Capabilities.Contains(a)) continue;
                sb.Append(any ? "," : "|").Append(a);
                any = true;
            }

            if (!any) sb.Append("|Passive");
            return sb.ToString();
        }

        /// <summary>
        /// How readily this profile should trust knowledge from another. Identical builds
        /// trade freely; a shared locomotion with different weapons trades cautiously; a
        /// flier and a walker have little to say to each other.
        /// </summary>
        public double AffinityTo(EnemyProfile other)
        {
            if (other == null) return 0.0;
            if (string.Equals(Signature, other.Signature, StringComparison.Ordinal)) return 1.0;

            double affinity = Locomotion == other.Locomotion
                ? ModConfig.SameClassShareWeight.Value
                : ModConfig.CrossClassShareWeight.Value;

            // Overlapping weapons matter more than overlapping legs.
            int shared = 0, total = 0;
            foreach (var a in new[] { Archetype.Melee, Archetype.Ranged, Archetype.Dive, Archetype.Summon })
            {
                bool mine = Capabilities.Contains(a), theirs = other.Capabilities.Contains(a);
                if (mine || theirs) total++;
                if (mine && theirs) shared++;
            }

            double overlap = total > 0 ? (double)shared / total : 0.0;
            return affinity * (0.4 + 0.6 * overlap);
        }

        public override string ToString() => Signature;
    }
}
