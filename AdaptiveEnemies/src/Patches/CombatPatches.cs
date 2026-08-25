using System;
using AdaptiveEnemies.Runtime;
using GlobalEnums;
using HarmonyLib;
using UnityEngine;

namespace AdaptiveEnemies.Patches
{
    /// <summary>
    /// The four hooks the learning system feeds on. All are postfixes that only observe,
    /// so a failure inside the mod can never alter combat.
    /// </summary>
    internal static class CombatPatches
    {
        /// <summary>
        /// Every point at which the player damages an enemy funnels through
        /// <c>HealthManager.Hit</c>. The prefix records HP so the postfix can report what
        /// was actually dealt rather than what was nominally requested.
        /// </summary>
        [HarmonyPatch(typeof(HealthManager), nameof(HealthManager.Hit))]
        internal static class HealthManager_Hit
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.Last)]
            private static void Prefix(HealthManager __instance, out int __state)
            {
                __state = __instance != null ? __instance.hp : 0;
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.First)]
            private static void Postfix(HealthManager __instance, HitInstance hitInstance, int __state)
            {
                if (!ModConfig.Enabled.Value || __instance == null) return;

                try
                {
                    var brain = Attach(__instance.gameObject);
                    if (brain == null) return;

                    int dealt = Mathf.Max(0, __state - __instance.hp);
                    brain.OnDamaged(hitInstance, dealt);
                }
                catch (Exception ex)
                {
                    Log.Error($"HealthManager.Hit hook failed: {ex}");
                }
            }
        }

        /// <summary>Player started a nail swing. Gives the direction before it resolves.</summary>
        [HarmonyPatch(typeof(HeroController), "Attack", new[] { typeof(AttackDirection) })]
        internal static class HeroController_Attack
        {
            [HarmonyPostfix]
            private static void Postfix(AttackDirection attackDir)
            {
                if (!ModConfig.Enabled.Value) return;
                try { CombatTracker.OnSwingStarted(attackDir); }
                catch (Exception ex) { Log.Error($"Attack hook failed: {ex}"); }
            }
        }

        /// <summary>
        /// The swing's damage window closed. <c>didHit</c> is the game's own verdict on
        /// whether it connected, which makes landed/missed exact rather than inferred.
        /// </summary>
        [HarmonyPatch(typeof(NailAttackBase), "OnEndedDamage")]
        internal static class NailAttackBase_OnEndedDamage
        {
            [HarmonyPostfix]
            private static void Postfix(bool didHit)
            {
                if (!ModConfig.Enabled.Value) return;
                try { CombatTracker.OnSwingEnded(didHit); }
                catch (Exception ex) { Log.Error($"OnEndedDamage hook failed: {ex}"); }
            }
        }

        /// <summary>
        /// The player regained health. Everything nearby that could have stopped it did not,
        /// and that failure is worth learning from.
        /// </summary>
        [HarmonyPatch(typeof(HeroController), nameof(HeroController.AddHealth))]
        internal static class HeroController_AddHealth
        {
            [HarmonyPostfix]
            private static void Postfix(int amount)
            {
                if (!ModConfig.Enabled.Value) return;
                try { CombatTracker.OnPlayerHealed(amount); }
                catch (Exception ex) { Log.Error($"AddHealth hook failed: {ex}"); }
            }
        }

        /// <summary>Enemy damaged the player. Carries the source object, so it can be attributed.</summary>
        [HarmonyPatch(typeof(HeroController), nameof(HeroController.TakeDamage))]
        internal static class HeroController_TakeDamage
        {
            [HarmonyPostfix]
            private static void Postfix(GameObject go, int damageAmount)
            {
                if (!ModConfig.Enabled.Value || go == null || damageAmount <= 0) return;

                try
                {
                    var brain = go.GetComponentInParent<AdaptiveBrain>();
                    if (brain != null)
                    {
                        brain.OnPlayerDamaged(damageAmount);
                        return;
                    }

                    // Nothing owns this damage source. It may still be an enemy's doing —
                    // a rock it dropped, a minion it summoned — so let the trackers work
                    // out who most plausibly caused it.
                    CombatTracker.AttributeIndirectDamage(go, damageAmount);
                }
                catch (Exception ex)
                {
                    Log.Error($"TakeDamage hook failed: {ex}");
                }
            }
        }

        /// <summary>
        /// Attaches a brain the first time a target enemy is seen. Enemies are pooled and
        /// streamed in with the scene, so lazy attachment on first contact is more
        /// reliable than trying to catch every spawn path.
        /// </summary>
        internal static AdaptiveBrain Attach(GameObject go)
        {
            if (go == null) return null;

            var existing = go.GetComponent<AdaptiveBrain>();
            if (existing != null) return existing.enabled ? existing : null;

            if (!EnemyIdentity.IsTarget(go, out _)) return null;

            return go.AddComponent<AdaptiveBrain>();
        }
    }
}
