using System;
using System.Collections;
using System.IO;
using AdaptiveEnemies.Learning;
using AdaptiveEnemies.Runtime;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AdaptiveEnemies
{
    /// <summary>
    /// AdaptiveEnemies — Grandmother Silk watches Hornet fight, and what one of her
    /// creatures learns, the rest inherit.
    /// </summary>
    [BepInPlugin(Guid, "Adaptive Enemies", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.faaris.adaptiveenemies";

        /// <summary>The shared pool. Null until the plugin has woken up.</summary>
        public static HiveMind Hive { get; private set; }

        private Harmony _harmony;
        private float _saveTimer;
        private bool _lastTestingMode;

        private void Awake()
        {
            Log.Bind(Logger);
            ModConfig.Init(Config);

            var pluginDir = Path.GetDirectoryName(Info.Location) ?? Paths.PluginPath;
            Store.Init(pluginDir);

            Hive = new HiveMind();
            Store.Load(Hive);

            try
            {
                _harmony = new Harmony(Guid);
                _harmony.PatchAll(typeof(Patches.CombatPatches).Assembly);
            }
            catch (Exception ex)
            {
                Log.Error($"Harmony patching failed — the mod is inert this run: {ex}");
                return;
            }

            gameObject.AddComponent<Runtime.DebugFreeMove>();

            SceneManager.sceneLoaded += OnSceneLoaded;
            _lastTestingMode = ModConfig.Testing;

            Log.Info($"Adaptive Enemies ready. Targets: {ModConfig.TargetEnemies.Value}. " +
                     $"Persistence: {(ModConfig.PersistToDisk.Value ? "on" : "off")}. " +
                     $"Testing mode: {(ModConfig.Testing ? "ON" : "off")}.");

            if (ModConfig.Testing) LogTestingBanner();
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            Flush();
            _harmony?.UnpatchSelf();
        }

        private void OnApplicationQuit() => Flush();

        private void Update()
        {
            if (Hive == null) return;

            // A gameplay clock, not wall-clock: evidence should not evaporate while the
            // game sits paused in a menu or the player steps away.
            Hive.Clock += Time.unscaledDeltaTime;

            CombatTracker.Tick();

            if (ModConfig.Testing != _lastTestingMode)
            {
                _lastTestingMode = ModConfig.Testing;
                if (_lastTestingMode) LogTestingBanner();
                else Log.Info("Testing mode off — back to normal learning rate and confidence threshold.");
            }

            _saveTimer += Time.unscaledDeltaTime;
            if (_saveTimer >= ModConfig.SaveIntervalSeconds.Value)
            {
                _saveTimer = 0f;
                if (Hive.Dirty) Flush();
            }
        }

        private static void LogTestingBanner()
        {
            Log.Info("TESTING MODE — learning rate x6, confidence threshold 1.5 samples, min samples 1, " +
                     "full authority, 4-minute decay half-life. Adaptation should be visible within a few exchanges.");
        }

        private void Flush()
        {
            if (Hive == null) return;
            Hive.Prune();
            Store.Save(Hive);
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            CombatTracker.Clear();
            StartCoroutine(SweepScene());
        }

        /// <summary>
        /// Attaches brains to target enemies already in the scene. Enemies stream in over
        /// the first couple of seconds, so this sweeps a few times; lazy attachment on
        /// first hit covers anything later.
        /// </summary>
        private IEnumerator SweepScene()
        {
            float[] delays = { 0f, 0.5f, 1.5f, 3f };

            foreach (var delay in delays)
            {
                if (delay > 0f) yield return new WaitForSeconds(delay);
                if (!ModConfig.Enabled.Value) yield break;

                int attached = 0;
                HealthManager[] managers;

                try { managers = FindObjectsByType<HealthManager>(FindObjectsSortMode.None); }
                catch (Exception ex) { Log.Trace($"Scene sweep failed: {ex.Message}"); continue; }

                foreach (var hm in managers)
                {
                    if (hm == null) continue;
                    try { if (Patches.CombatPatches.Attach(hm.gameObject) != null) attached++; }
                    catch (Exception ex) { Log.Trace($"Attach failed on {hm.name}: {ex.Message}"); }
                }

                if (attached > 0) Log.Trace($"Scene sweep attached {attached} brain(s).");
            }
        }
    }
}
