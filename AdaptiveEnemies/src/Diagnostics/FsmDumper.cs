using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AdaptiveEnemies.Learning;
using HutongGames.PlayMaker;
using UnityEngine;

namespace AdaptiveEnemies.Runtime
{
    /// <summary>
    /// Writes an enemy's real FSM structure to disk.
    ///
    /// <para>The moss enemies' state graphs live inside Addressable scene bundles and
    /// cannot be read reliably offline, so the honest way to tune against them is to dump
    /// what the game actually loaded.</para>
    /// </summary>
    internal static class FsmDumper
    {
        private static readonly HashSet<string> Dumped = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Writes a best-effort dump for an enemy whose FSM has not initialised yet.
        /// Goes to a separate file and does not consume the one-shot real dump, so the
        /// full structure still gets written once the enemy actually wakes.
        /// </summary>
        public static void DumpProvisional(
            string typeName,
            GameObject root,
            List<DecisionPoint> decisions,
            List<TimingPoint> timings,
            List<SpacingPoint> spacings)
            => Write(typeName + " (provisional-dormant)", root, decisions, timings, spacings);

        public static void Dump(
            string typeName,
            GameObject root,
            List<DecisionPoint> decisions,
            List<TimingPoint> timings,
            List<SpacingPoint> spacings)
        {
            if (!Dumped.Add(typeName)) return;
            Write(typeName, root, decisions, timings, spacings);
        }

        private static void Write(
            string typeName,
            GameObject root,
            List<DecisionPoint> decisions,
            List<TimingPoint> timings,
            List<SpacingPoint> spacings)
        {
            try
            {
                var dir = Path.Combine(Store.DirectoryPath ?? ".", "fsm-dump");
                Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                sb.AppendLine($"# {typeName}");
                sb.AppendLine($"# GameObject: {root.name}");
                sb.AppendLine();

                sb.AppendLine("## Components");
                foreach (var c in root.GetComponentsInChildren<Component>(true))
                    if (c != null) sb.AppendLine($"  {PathOf(root.transform, c.transform)} : {c.GetType().Name}");
                sb.AppendLine();

                sb.AppendLine("## FSMs");
                foreach (var fsm in root.GetComponentsInChildren<PlayMakerFSM>(true))
                {
                    bool ready = fsm.Fsm != null && fsm.Fsm.Initialized;
                    sb.AppendLine($"  FSM '{fsm.FsmName}' on {PathOf(root.transform, fsm.transform)}  " +
                                  $"[initialised: {ready}, started: {fsm.Fsm?.Started}, active state: {fsm.ActiveStateName}]");

                    if (!ready)
                    {
                        // Reading Actions here would make PlayMaker log an error and return
                        // nothing, so report the state instead of forcing it.
                        sb.AppendLine("    <FSM not initialised — dormant when dumped; re-dump after engaging it>");
                        continue;
                    }

                    FsmState[] states = null;
                    try { states = fsm.FsmStates; } catch { }
                    if (states == null) { sb.AppendLine("    <states unavailable>"); continue; }

                    foreach (var st in states)
                    {
                        if (st == null) continue;
                        sb.AppendLine($"    State '{st.Name}'  [archetype: {TaxonomyUtil.ClassifyState(st.Name)}]");

                        try
                        {
                            if (st.Transitions != null)
                                foreach (var t in st.Transitions)
                                    if (t != null) sb.AppendLine($"      --{t.EventName}--> {t.ToState}");
                        }
                        catch { }

                        try
                        {
                            if (st.Actions != null)
                                foreach (var a in st.Actions)
                                    if (a != null) sb.AppendLine($"      action: {a.GetType().Name}");
                        }
                        catch { sb.AppendLine("      <actions unavailable>"); }
                    }
                    sb.AppendLine();
                }

                sb.AppendLine("## Adaptable decision points");
                if (decisions.Count == 0)
                    sb.AppendLine("  (none found — this enemy has a single fixed attack, no weighted branch)");
                foreach (var d in decisions)
                {
                    sb.AppendLine($"  {d.Fsm.FsmName} / state '{d.StateName}' / {d.ActionType}");
                    for (int i = 0; i < d.Vanilla.Length; i++)
                        sb.AppendLine($"    [{i}] -> '{d.OptionNames[i]}'  weight {d.Vanilla[i]:F3}  archetype {d.Archetypes[i]}");
                }
                sb.AppendLine();

                sb.AppendLine("## Adaptable timing points");
                if (timings.Count == 0) sb.AppendLine("  (none found)");
                foreach (var t in timings)
                    sb.AppendLine($"  state '{t.StateName}'  wait {t.Vanilla:F3}s  " +
                                  $"[archetype: {TaxonomyUtil.ClassifyState(t.StateName)}]");
                sb.AppendLine();

                sb.AppendLine("## Spacing volumes");
                if (spacings.Count == 0) sb.AppendLine("  (none found)");
                foreach (var s in spacings)
                    sb.AppendLine($"  {s.Target.name}  scale {s.Vanilla}");

                var file = Path.Combine(dir, Sanitize(typeName) + ".txt");
                File.WriteAllText(file, sb.ToString());
                Log.Info($"Wrote FSM dump: {file}");
            }
            catch (Exception ex)
            {
                Log.Error($"FSM dump failed for {typeName}: {ex.Message}");
            }
        }

        private static readonly HashSet<string> RosterSeen = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Appends one line per enemy type to a running roster file. Playing through the game
        /// with this on produces the full classification table without anyone hunting down
        /// two hundred enemies to inspect them.
        /// </summary>
        public static void AppendRosterEntry(string typeName, EnemyProfile profile, float hp,
            double learnScale, int decisions, int timings, int parameters)
        {
            if (!RosterSeen.Add(typeName)) return;

            try
            {
                var dir = Store.DirectoryPath ?? ".";
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "enemy-roster.tsv");

                if (!File.Exists(file))
                    File.WriteAllText(file,
                        "enemy\tprofile\tlocomotion\thp\tlearn_scale\tdecisions\ttimings\tparameters\n");

                File.AppendAllText(file,
                    $"{typeName}\t{profile.Signature}\t{profile.Locomotion}\t{hp:F0}\t" +
                    $"{learnScale:F2}\t{decisions}\t{timings}\t{parameters}\n");

                Log.Info($"Roster: {typeName} -> {profile.Signature} " +
                         $"(hp {hp:F0}, learn x{learnScale:F1}, {decisions} choice(s), {parameters} dial(s))");
            }
            catch (Exception ex)
            {
                Log.Error($"Roster append failed for {typeName}: {ex.Message}");
            }
        }

        private static string PathOf(Transform root, Transform t)
        {
            if (t == root) return ".";
            var parts = new List<string>();
            while (t != null && t != root) { parts.Insert(0, t.name); t = t.parent; }
            return string.Join("/", parts.ToArray());
        }

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}
