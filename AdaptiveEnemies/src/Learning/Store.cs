using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace AdaptiveEnemies.Learning
{
    /// <summary>
    /// Persists the hive to JSON next to the plugin.
    ///
    /// <para>Cells are stored as a compact <c>[value, n, lastT]</c> triple rather than
    /// named objects — there is one entry per behaviour/response pair and the file is
    /// rewritten repeatedly, so the terse form keeps it small.</para>
    ///
    /// <para>An explicit serializer is used rather than <c>JsonConvert</c>'s ambient
    /// defaults: Silksong ships Newtonsoft.Json.UnityConverters, which installs global
    /// converters that have no business touching these plain values.</para>
    /// </summary>
    public static class Store
    {
        private const int FormatVersion = 1;

        private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver(),
            Formatting = Formatting.None,
            Culture = CultureInfo.InvariantCulture,
            FloatFormatHandling = FloatFormatHandling.DefaultValue,
            NullValueHandling = NullValueHandling.Ignore,
        });

        private class SaveFile
        {
            public int Version;
            public double Clock;
            public Dictionary<string, double[]> Cells;
        }

        public static string DirectoryPath { get; private set; }
        public static string FilePath { get; private set; }

        public static void Init(string pluginDirectory)
        {
            DirectoryPath = pluginDirectory;
            FilePath = Path.Combine(pluginDirectory, "hive.json");
        }

        public static void Load(HiveMind hive)
        {
            hive.Reset();
            hive.Clock = 0.0;

            if (!ModConfig.PersistToDisk.Value)
            {
                Log.Info("Persistence disabled — the hive starts empty this run.");
                hive.ClearDirty();
                return;
            }

            if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
            {
                Log.Info("No hive.json yet — starting a fresh hive.");
                hive.ClearDirty();
                return;
            }

            try
            {
                SaveFile data;
                using (var reader = new JsonTextReader(new StreamReader(FilePath)))
                    data = Serializer.Deserialize<SaveFile>(reader);

                if (data == null)
                {
                    Log.Warn("hive.json was empty — starting fresh.");
                    hive.ClearDirty();
                    return;
                }

                if (data.Version != FormatVersion)
                {
                    Log.Warn($"hive.json is format v{data.Version}, expected v{FormatVersion}. " +
                             "Discarding it and starting fresh.");
                    hive.ClearDirty();
                    return;
                }

                hive.Clock = data.Clock;

                if (data.Cells != null)
                {
                    foreach (var kv in data.Cells)
                    {
                        var v = kv.Value;
                        if (v == null || v.Length < 3) continue;
                        hive.Cells[kv.Key] = new StatCell { Value = v[0], N = v[1], LastT = v[2] };
                    }
                }

                Log.Info($"Loaded hive: {hive.Cells.Count} cells, clock {hive.Clock:F0}s.");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to read hive.json ({ex.GetType().Name}: {ex.Message}). Starting fresh; " +
                          "the existing file is left untouched.");
                hive.Reset();
                hive.Clock = 0.0;
            }

            hive.ClearDirty();
        }

        public static void Save(HiveMind hive)
        {
            if (!ModConfig.PersistToDisk.Value || string.IsNullOrEmpty(FilePath)) return;

            try
            {
                Directory.CreateDirectory(DirectoryPath);

                var cells = new Dictionary<string, double[]>(hive.Cells.Count, StringComparer.Ordinal);
                foreach (var kv in hive.Cells)
                {
                    var c = kv.Value;
                    if (c.IsEmpty) continue;
                    cells[kv.Key] = new[] { c.Value, c.N, c.LastT };
                }

                var data = new SaveFile { Version = FormatVersion, Clock = hive.Clock, Cells = cells };

                // Write beside the target and swap, so a crash mid-write cannot leave a
                // truncated hive.json behind.
                var tmp = FilePath + ".tmp";
                using (var writer = new JsonTextWriter(new StreamWriter(tmp, false)))
                    Serializer.Serialize(writer, data);

                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);

                hive.ClearDirty();
                Log.Trace($"Saved hive: {cells.Count} cells.");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to write hive.json ({ex.GetType().Name}: {ex.Message}).");
            }
        }
    }
}
