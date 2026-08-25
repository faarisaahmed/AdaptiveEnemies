using System;
using System.Reflection;
using UnityEngine;

namespace AdaptiveEnemies.Runtime
{
    /// <summary>
    /// Adapter for contact-damage enemies driven by the <c>Crawler</c> C# component
    /// rather than by a PlayMaker attack graph.
    ///
    /// <para>MossBone Crawler is the motivating case. Its only FSM is called
    /// "Noise Reaction" and handles waking, Needolin singing and death — the pacing is
    /// the <c>Crawler</c> MonoBehaviour, and the damage is a <c>DamageHero</c> collider.
    /// There is no attack state, no telegraph and no weighted branch, so the FSM-based
    /// path finds nothing at all on it.</para>
    ///
    /// <para>For such an enemy the approach <em>is</em> the attack, and the only honest
    /// lever is where it chooses to turn around. <c>rayFrontDistance</c> and
    /// <c>rayDownFrontPadding</c> feed <c>ShouldTurn()</c>: raising them makes it turn
    /// further from a wall or ledge, lowering them lets it press closer. Both are
    /// independent of <c>speed</c>, which is left strictly alone.</para>
    /// </summary>
    internal sealed class CrawlerAdapter
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static FieldInfo _rayFront;
        private static FieldInfo _rayDownPad;
        private static PropertyInfo _isTurning;
        private static PropertyInfo _isCrawling;
        private static bool _probed;

        private readonly Component _crawler;
        private readonly float _vanillaRayFront;
        private readonly float _vanillaRayDownPad;

        /// <summary>Behaviour names this adapter reports. Kept stable — they become hive keys.</summary>
        public const string Crawling = "Crawl";
        public const string Turning = "Turn";

        private CrawlerAdapter(Component crawler, float rayFront, float rayDownPad)
        {
            _crawler = crawler;
            _vanillaRayFront = rayFront;
            _vanillaRayDownPad = rayDownPad;
        }

        private static void Probe()
        {
            if (_probed) return;
            _probed = true;

            var t = AccessTools_TypeByName("Crawler");
            if (t == null) { Log.Trace("Crawler type not found; contact mode unavailable."); return; }

            _rayFront = t.GetField("rayFrontDistance", Flags);
            _rayDownPad = t.GetField("rayDownFrontPadding", Flags);
            _isTurning = t.GetProperty("IsTurning", Flags);
            _isCrawling = t.GetProperty("IsCrawling", Flags);

            if (_rayFront == null || _rayDownPad == null)
                Log.Warn("Crawler turn-distance fields not found — contact-mode spacing will be inert. " +
                         "The component's layout may have changed.");
        }

        private static Type AccessTools_TypeByName(string name)
        {
            try { return HarmonyLib.AccessTools.TypeByName(name); }
            catch { return null; }
        }

        /// <summary>Builds an adapter if this enemy is a contact-damage crawler, else null.</summary>
        public static CrawlerAdapter TryCreate(GameObject root)
        {
            Probe();
            if (_rayFront == null || _rayDownPad == null) return null;

            var type = AccessTools_TypeByName("Crawler");
            if (type == null) return null;

            var comp = root.GetComponent(type);
            if (comp == null) return null;

            // Only meaningful if it can actually hurt the player by touching them.
            if (root.GetComponent<DamageHero>() == null) return null;

            try
            {
                float front = Convert.ToSingle(_rayFront.GetValue(comp));
                float down = Convert.ToSingle(_rayDownPad.GetValue(comp));
                return new CrawlerAdapter(comp, front, down);
            }
            catch (Exception ex)
            {
                Log.Trace($"Crawler adapter creation failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Current behaviour, used as the episode key in place of an FSM state.</summary>
        public string CurrentBehaviour()
        {
            try
            {
                if (_isTurning != null && _isTurning.GetValue(_crawler, null) is bool turning && turning)
                    return Turning;
            }
            catch { }

            return Crawling;
        }

        /// <summary>
        /// Scales the turn-trigger distances. <paramref name="factor"/> above 1 turns
        /// further out (backing off), below 1 presses closer.
        /// </summary>
        public void ApplyTurnDistance(float factor)
        {
            try
            {
                _rayFront.SetValue(_crawler, _vanillaRayFront * factor);
                _rayDownPad.SetValue(_crawler, _vanillaRayDownPad * factor);
            }
            catch (Exception ex)
            {
                Log.Trace($"Crawler turn-distance write failed: {ex.Message}");
            }
        }

        public void RestoreVanilla()
        {
            try
            {
                _rayFront.SetValue(_crawler, _vanillaRayFront);
                _rayDownPad.SetValue(_crawler, _vanillaRayDownPad);
            }
            catch { }
        }

        public string Describe() => $"rayFront {_vanillaRayFront:F2}, rayDownPad {_vanillaRayDownPad:F2}";
    }
}
