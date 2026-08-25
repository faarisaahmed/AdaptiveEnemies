using System;
using System.Collections.Generic;
using AdaptiveEnemies.Learning;
using GlobalEnums;
using UnityEngine;

namespace AdaptiveEnemies.Runtime
{
    /// <summary>
    /// Watches the player: classifies every nail swing and tool hit, resolves whether a
    /// swing landed or whiffed, and maintains the universal habit layer.
    /// </summary>
    public static class CombatTracker
    {
        private static readonly List<AdaptiveBrain> Brains = new List<AdaptiveBrain>();

        private static PlayerAction _pendingSwing = PlayerAction.None;
        private static bool _swingConnected;

        private static float _lastLandedAt = -999f;
        private static bool _wasOnGround;

        public static IReadOnlyList<AdaptiveBrain> ActiveBrains => Brains;

        public static void Register(AdaptiveBrain brain)
        {
            if (brain != null && !Brains.Contains(brain)) Brains.Add(brain);
        }

        public static void Unregister(AdaptiveBrain brain) => Brains.Remove(brain);

        public static void Clear()
        {
            Brains.Clear();
            _pendingSwing = PlayerAction.None;
            _swingConnected = false;
        }

        private static HeroControllerStates State()
        {
            var hc = HeroController.UnsafeInstance;
            return hc != null ? hc.cState : null;
        }

        /// <summary>Hero world position, or null when there is no hero right now.</summary>
        public static Vector3? HeroPosition()
        {
            var hc = HeroController.UnsafeInstance;
            return hc != null ? hc.transform.position : (Vector3?)null;
        }

        /// <summary>True while the player is focusing to heal — vulnerable, and stationary.</summary>
        public static bool PlayerIsHealing
        {
            get
            {
                var cs = State();
                return cs != null && cs.focusing;
            }
        }

        /// <summary>
        /// The player completed a heal. Every enemy close enough to have stopped it failed
        /// to, and learns from that.
        /// </summary>
        public static void OnPlayerHealed(int amount)
        {
            if (!ModConfig.Enabled.Value || amount <= 0) return;

            for (int i = 0; i < Brains.Count; i++)
                Brains[i]?.OnPlayerCompletedHeal();
        }

        /// <summary>Per-frame bookkeeping for the habit layer.</summary>
        public static void Tick()
        {
            var cs = State();
            if (cs == null) return;

            if (cs.onGround && !_wasOnGround) _lastLandedAt = Time.time;
            _wasOnGround = cs.onGround;
        }

        /// <summary>
        /// How the player is currently avoiding, if at all. Ordered by specificity so a
        /// parry is never mistaken for the jump that happened to precede it.
        /// </summary>
        /// <remarks>
        /// Callers must gate this on the enemy actually threatening — Hornet is airborne
        /// constantly, and treating that as "avoided the attack" would flood the tables
        /// with noise.
        /// </remarks>
        public static AvoidMethod SampleAvoid()
        {
            var cs = State();
            if (cs == null) return AvoidMethod.NotAvoided;

            if (cs.parryAttack) return AvoidMethod.CrossStitch;
            if (cs.parrying) return AvoidMethod.NeedleParry;
            if (cs.dashing || cs.airDashing || cs.evading || cs.backDashing) return AvoidMethod.Evade;
            if (cs.jumping || cs.doubleJumping || !cs.onGround) return AvoidMethod.Jump;

            return AvoidMethod.NotAvoided;
        }

        /// <summary>Maps an incoming hit onto the tracked player-action taxonomy.</summary>
        public static PlayerAction Classify(HitInstance hit)
        {
            try
            {
                if (hit.RepresentingTool != null)
                    return ToolBuckets.Classify(hit.RepresentingTool.name);

                if (hit.AttackType == AttackTypes.Nail)
                    return NailAction();
            }
            catch (Exception ex)
            {
                Log.Trace($"Hit classification failed: {ex.Message}");
            }

            return PlayerAction.OtherDamage;
        }

        /// <summary>
        /// Silksong's <c>AttackDirection</c> only distinguishes normal/upward/downward,
        /// so left versus right is taken from which way Hornet is facing.
        /// </summary>
        private static PlayerAction NailAction()
        {
            var cs = State();
            if (cs == null) return PlayerAction.OtherDamage;

            if (cs.downAttacking || cs.downSpiking || cs.downSpikeBouncing || cs.downTravelling)
                return PlayerAction.Pogo;

            if (cs.upAttacking) return PlayerAction.SlashUp;

            return cs.facingRight ? PlayerAction.SlashRight : PlayerAction.SlashLeft;
        }

        public static PlayerAction FromDirection(AttackDirection dir)
        {
            switch (dir)
            {
                case AttackDirection.downward: return PlayerAction.Pogo;
                case AttackDirection.upward: return PlayerAction.SlashUp;
                default:
                    var cs = State();
                    return cs != null && cs.facingRight ? PlayerAction.SlashRight : PlayerAction.SlashLeft;
            }
        }

        /// <summary>Called when the player starts a nail swing.</summary>
        public static void OnSwingStarted(AttackDirection dir)
        {
            _pendingSwing = FromDirection(dir);
            _swingConnected = false;

            // "Player favours up-slash right after landing" — a habit every enemy reads.
            if (_pendingSwing == PlayerAction.SlashUp && Time.time - _lastLandedAt < 0.35f)
                Plugin.Hive?.Record(HiveMind.UniversalKey("habit|upslash_after_land"), 1.0, ModConfig.EffLearningRate);
        }

        /// <summary>Called when a swing's damage window closes.</summary>
        public static void OnSwingEnded(bool didHit)
        {
            var action = _pendingSwing;
            _pendingSwing = PlayerAction.None;
            if (action == PlayerAction.None) return;

            bool landed = didHit || _swingConnected;
            _swingConnected = false;

            Plugin.Hive?.Record(
                HiveMind.UniversalKey("swing|" + action + "|" + (landed ? "landed" : "missed")),
                landed ? 1.0 : 0.0, ModConfig.EffLearningRate);

            // A whiffed swing is information for an enemy that was mid-attack nearby.
            // The brain decides whether it is close enough and actually threatening —
            // otherwise every miss in the room gets credited to every enemy in it.
            if (!landed)
            {
                for (int i = 0; i < Brains.Count; i++)
                    Brains[i]?.NotePlayerWhiff(action);
            }

            Log.Trace($"Swing {action} -> {(landed ? "landed" : "missed")}");
        }

        public static void NoteSwingConnected() => _swingConnected = true;

        /// <summary>
        /// Finds the enemy most likely responsible for damage from an object that no enemy
        /// owns — a dropped rock, a summoned minion, a projectile that outlived its caster.
        ///
        /// <para>Attribution is by recency and proximity: whichever tracked enemy most
        /// recently performed something threatening, close enough to the damage source to
        /// plausibly have caused it. Nothing here knows which enemy spawns what; a boss
        /// that gains a new summon is covered automatically.</para>
        /// </summary>
        public static void AttributeIndirectDamage(GameObject source, int amount)
        {
            if (!ModConfig.AttributeIndirectDamage.Value || source == null) return;

            var pos = source.transform.position;

            AdaptiveBrain best = null;
            float bestScore = 0f;

            for (int i = 0; i < Brains.Count; i++)
            {
                var brain = Brains[i];
                if (brain == null) continue;

                float score = brain.IndirectClaimScore(pos);
                if (score > bestScore) { bestScore = score; best = brain; }
            }

            if (best == null) return;

            best.OnIndirectPlayerDamaged(amount);
            Log.Trace($"Indirect damage from '{source.name}' attributed to {best.TypeName}");
        }
    }
}
