using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace AdaptiveEnemies.Runtime
{
    /// <summary>
    /// Read/write access to one action's weighted event list, hiding the fact that
    /// Silksong ships two different layouts for the same idea.
    /// </summary>
    internal interface IWeightSlots
    {
        int Count { get; }
        string EventName(int index);
        float Read(int index);
        bool Write(int index, float value);
    }

    /// <summary>SendRandomEvent / V2 / V3 / V4: parallel <c>events</c> + <c>weights</c> arrays.</summary>
    internal sealed class ArraySlots : IWeightSlots
    {
        private readonly FsmEvent[] _events;
        private readonly FsmFloat[] _weights;

        public ArraySlots(FsmEvent[] events, FsmFloat[] weights) { _events = events; _weights = weights; }

        public int Count => Math.Min(_events?.Length ?? 0, _weights?.Length ?? 0);
        public string EventName(int i) => _events[i]?.Name ?? ("slot" + i);
        public float Read(int i) => _weights[i]?.Value ?? 0f;

        public bool Write(int i, float value)
        {
            var w = _weights[i];
            if (w == null) return false;

            // A weight bound to an FSM variable belongs to the enemy's own logic;
            // writing through it would leak into whatever else reads that variable.
            if (w.UseVariable || !string.IsNullOrEmpty(w.Name)) return false;

            w.Value = value;
            return true;
        }
    }

    /// <summary>SendRandomEventFair: one array of objects each carrying its own probability.</summary>
    internal sealed class ProbabilitySlots : IWeightSlots
    {
        private readonly Array _entries;
        private readonly FieldInfo _sendEvent;
        private readonly MemberInfo _probability;

        public ProbabilitySlots(Array entries, FieldInfo sendEvent, MemberInfo probability)
        {
            _entries = entries; _sendEvent = sendEvent; _probability = probability;
        }

        public int Count => _entries?.Length ?? 0;

        public string EventName(int i)
        {
            var entry = _entries.GetValue(i);
            if (entry == null) return "slot" + i;
            return (_sendEvent.GetValue(entry) as FsmEvent)?.Name ?? ("slot" + i);
        }

        public float Read(int i)
        {
            var entry = _entries.GetValue(i);
            if (entry == null) return 0f;
            return _probability is FieldInfo f
                ? Convert.ToSingle(f.GetValue(entry))
                : Convert.ToSingle(((PropertyInfo)_probability).GetValue(entry, null));
        }

        public bool Write(int i, float value)
        {
            var entry = _entries.GetValue(i);
            if (entry == null) return false;

            if (_probability is FieldInfo f) { f.SetValue(entry, value); return true; }

            var p = (PropertyInfo)_probability;
            if (!p.CanWrite) return false;
            p.SetValue(entry, value, null);
            return true;
        }
    }

    /// <summary>A weighted branch in the enemy's FSM — one attack-selection point.</summary>
    internal sealed class DecisionPoint
    {
        public PlayMakerFSM Fsm;
        public string StateName;
        public string ActionType;
        public IWeightSlots Slots;

        public float[] Vanilla;
        public string[] OptionNames;
        public Archetype[] Archetypes;
        public double[] Scratch;
    }

    /// <summary>A <c>Wait</c> action whose duration may be varied.</summary>
    internal sealed class TimingPoint
    {
        public string StateName;
        public FsmFloat Time;
        public float Vanilla;
    }

    /// <summary>A child range volume whose scale expresses combat spacing.</summary>
    internal sealed class SpacingPoint
    {
        public Transform Target;
        public Vector3 Vanilla;
    }

    /// <summary>
    /// A continuous parameter of behaviour the enemy already has, which it may learn a
    /// value for.
    ///
    /// <para>The motivating case is <c>ChaseObjectV2.offsetY</c> on MossBone Fly: how far
    /// above the player it positions before diving. Nothing about that is an attack — but
    /// learning it toward zero makes the flier approach at the player's level and collide
    /// with them, which reads as a charge it never had. The behaviour is emergent from the
    /// value, so nothing has to be authored for a new attack to appear.</para>
    /// </summary>
    internal sealed class ParameterPoint
    {
        public string StateName;
        public string ActionType;
        public string FieldName;
        public int Ordinal;
        public FsmFloat Target;
        public float Vanilla;

        /// <summary>
        /// Stable identity used as a hive key. Includes the action type and its position in
        /// the state, because one state can run the same action twice with different values
        /// — Mossbone Mother's 'Swoop Antic' has two Wait actions — and keying on state and
        /// field alone would silently merge their learning.
        /// </summary>
        public string Id => StateName + "/" + ActionType + "#" + Ordinal + "/" + FieldName;

        public void Apply(float multiplier)
        {
            if (Target != null) Target.Value = Vanilla * multiplier;
        }

        public void Restore()
        {
            if (Target != null) Target.Value = Vanilla;
        }
    }

    /// <summary>
    /// Discovers the adaptable structure of an enemy's PlayMaker FSMs by reflection.
    ///
    /// <para>Reflection rather than direct field access is deliberate: Silksong has six
    /// <c>SendRandomEvent*</c> variants across two layouts, and the moss enemies' state
    /// graphs are authored per-prefab. Probing for shape means unfamiliar states and new
    /// variants are handled without the mod knowing them by name.</para>
    /// </summary>
    internal static class FsmAdapter
    {
        private sealed class ActionShape
        {
            public FieldInfo Events;
            public FieldInfo Weights;
            public FieldInfo FairEntries;
            public FieldInfo FairSendEvent;
            public MemberInfo FairProbability;
            public FieldInfo WaitTime;
            public bool Useless;
        }

        private static readonly Dictionary<Type, ActionShape> ShapeCache = new Dictionary<Type, ActionShape>();

        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static ActionShape ShapeOf(Type t)
        {
            if (ShapeCache.TryGetValue(t, out var shape)) return shape;

            shape = new ActionShape();

            foreach (var f in t.GetFields(Flags))
            {
                if (f.FieldType == typeof(FsmEvent[]) && shape.Events == null) shape.Events = f;
                else if (f.FieldType == typeof(FsmFloat[]) && shape.Weights == null) shape.Weights = f;
                else if (f.FieldType == typeof(FsmFloat) &&
                         f.Name.IndexOf("time", StringComparison.OrdinalIgnoreCase) >= 0 &&
                         shape.WaitTime == null)
                {
                    shape.WaitTime = f;
                }
                else if (f.FieldType.IsArray && shape.FairEntries == null)
                {
                    var el = f.FieldType.GetElementType();
                    if (el == null || el.IsPrimitive) continue;

                    var send = el.GetField("SendEvent", Flags);
                    if (send == null || send.FieldType != typeof(FsmEvent)) continue;

                    MemberInfo prob = el.GetField("Probability", Flags);
                    if (prob == null) prob = el.GetProperty("Probability", Flags);
                    if (prob == null) continue;

                    shape.FairEntries = f;
                    shape.FairSendEvent = send;
                    shape.FairProbability = prob;
                }
            }

            bool hasChoice = (shape.Events != null && shape.Weights != null) || shape.FairEntries != null;
            shape.Useless = !hasChoice && shape.WaitTime == null;

            ShapeCache[t] = shape;
            return shape;
        }

        /// <returns>
        /// True when every FSM on the enemy was ready to inspect. False means at least
        /// one had not initialised yet — the caller should retry rather than conclude
        /// the enemy has nothing to adapt.
        /// </returns>
        public static bool Discover(
            GameObject root,
            List<DecisionPoint> decisions,
            List<TimingPoint> timings,
            List<SpacingPoint> spacings,
            List<ParameterPoint> parameters)
        {
            var fsms = root.GetComponentsInChildren<PlayMakerFSM>(true);
            bool complete = true;

            foreach (var fsm in fsms)
            {
                // Reading FsmState.Actions before the state is bound to a live Fsm makes
                // PlayMaker log an error and hand back nothing. Silksong's enemies sit
                // dormant until Hornet is near, so this is the normal case on spawn.
                if (fsm == null || fsm.Fsm == null || !fsm.Fsm.Initialized) { complete = false; continue; }

                FsmState[] states;
                try { states = fsm.FsmStates; }
                catch (Exception ex) { Log.Trace($"FSM '{fsm.FsmName}' states unavailable: {ex.Message}"); complete = false; continue; }
                if (states == null) { complete = false; continue; }

                foreach (var state in states)
                {
                    if (state == null) continue;
                    if (!state.IsInitialized) { complete = false; continue; }

                    FsmStateAction[] actions;
                    try { actions = state.Actions; }
                    catch (Exception ex) { Log.Trace($"State '{state.Name}' actions unavailable: {ex.Message}"); complete = false; continue; }
                    if (actions == null) { complete = false; continue; }

                    foreach (var action in actions)
                    {
                        if (action == null) continue;

                        // Learnable parameters are probed first and unconditionally: an
                        // action like ChaseObjectV2 has neither weights nor a wait, so the
                        // "useless" shortcut below would skip the very field that lets new
                        // behaviour emerge.
                        TryAddParameters(state, action, parameters);

                        ActionShape shape;
                        try { shape = ShapeOf(action.GetType()); }
                        catch (Exception ex) { Log.Trace($"Shape probe failed: {ex.Message}"); continue; }
                        if (shape.Useless) continue;

                        TryAddDecision(fsm, state, action, shape, decisions);
                        TryAddTiming(state, action, shape, timings);
                    }
                }
            }

            DiscoverSpacing(root, spacings);
            return complete;
        }

        /// <summary>
        /// Picks the FSM whose active state best represents "what this enemy is doing".
        /// Preference: one that owns a weighted branch, then the FSM on the enemy root,
        /// then whichever has the most states. Taking the first child FSM instead tends
        /// to land on an incidental one that sits in "Idle" forever.
        /// </summary>
        public static PlayMakerFSM ChoosePrimary(GameObject root, List<DecisionPoint> decisions)
        {
            if (decisions != null && decisions.Count > 0) return decisions[0].Fsm;

            PlayMakerFSM best = null;
            int bestScore = -1;

            foreach (var fsm in root.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (fsm == null || fsm.Fsm == null || !fsm.Fsm.Initialized) continue;

                int states = 0;
                try { states = fsm.FsmStates?.Length ?? 0; } catch { }

                int score = states + (fsm.gameObject == root ? 1000 : 0);
                if (score > bestScore) { bestScore = score; best = fsm; }
            }

            return best;
        }

        private static void TryAddDecision(
            PlayMakerFSM fsm, FsmState state, FsmStateAction action, ActionShape shape, List<DecisionPoint> into)
        {
            IWeightSlots slots = null;

            try
            {
                if (shape.Events != null && shape.Weights != null)
                {
                    var ev = shape.Events.GetValue(action) as FsmEvent[];
                    var wt = shape.Weights.GetValue(action) as FsmFloat[];
                    if (ev != null && wt != null && ev.Length > 0 && wt.Length > 0)
                        slots = new ArraySlots(ev, wt);
                }
                else if (shape.FairEntries != null)
                {
                    if (shape.FairEntries.GetValue(action) is Array arr && arr.Length > 0)
                        slots = new ProbabilitySlots(arr, shape.FairSendEvent, shape.FairProbability);
                }
            }
            catch (Exception ex)
            {
                Log.Trace($"Decision probe failed on '{state.Name}': {ex.Message}");
                return;
            }

            if (slots == null || slots.Count < 2) return; // A single option is not a choice.

            int n = slots.Count;
            var dp = new DecisionPoint
            {
                Fsm = fsm,
                StateName = state.Name,
                ActionType = action.GetType().Name,
                Slots = slots,
                Vanilla = new float[n],
                OptionNames = new string[n],
                Archetypes = new Archetype[n],
                Scratch = new double[n],
            };

            bool anyWeight = false;
            for (int i = 0; i < n; i++)
            {
                dp.Vanilla[i] = slots.Read(i);

                // Identify an option by the state it leads to, not the event that fires
                // it. The brain observes outcomes by watching the FSM's active state, so
                // both sides must agree on the same name.
                var evName = slots.EventName(i);
                dp.OptionNames[i] = ResolveTarget(fsm, state, evName) ?? evName;
                dp.Archetypes[i] = TaxonomyUtil.ClassifyState(dp.OptionNames[i]);

                if (dp.Vanilla[i] > 0f) anyWeight = true;
            }

            if (!anyWeight) return;
            into.Add(dp);
        }

        /// <summary>
        /// Follows an event from a state to the state it transitions into, checking the
        /// state's own transitions then the FSM's global ones.
        /// </summary>
        private static string ResolveTarget(PlayMakerFSM fsm, FsmState state, string eventName)
        {
            if (string.IsNullOrEmpty(eventName)) return null;

            try
            {
                var local = state.Transitions;
                if (local != null)
                    foreach (var t in local)
                        if (t != null && string.Equals(t.EventName, eventName, StringComparison.Ordinal))
                            return string.IsNullOrEmpty(t.ToState) ? null : t.ToState;

                var global = fsm.FsmGlobalTransitions;
                if (global != null)
                    foreach (var t in global)
                        if (t != null && string.Equals(t.EventName, eventName, StringComparison.Ordinal))
                            return string.IsNullOrEmpty(t.ToState) ? null : t.ToState;
            }
            catch (Exception ex)
            {
                Log.Trace($"Transition resolve failed for '{eventName}': {ex.Message}");
            }

            return null;
        }

        private static void TryAddTiming(FsmState state, FsmStateAction action, ActionShape shape, List<TimingPoint> into)
        {
            if (shape.WaitTime == null) return;

            // Restrict to genuine Wait actions. Probing purely by field shape also
            // matches audio fade and spawn delay fields, and varying those would change
            // presentation rather than attack timing.
            if (action.GetType().Name.IndexOf("Wait", StringComparison.OrdinalIgnoreCase) < 0) return;

            try
            {
                if (!(shape.WaitTime.GetValue(action) is FsmFloat t)) return;
                if (t.UseVariable || !string.IsNullOrEmpty(t.Name)) return;
                if (t.Value <= 0.001f) return;

                into.Add(new TimingPoint { StateName = state.Name, Time = t, Vanilla = t.Value });
            }
            catch (Exception ex)
            {
                Log.Trace($"Timing probe failed on '{state.Name}': {ex.Message}");
            }
        }

        private static readonly HashSet<string> ReportedSkips = new HashSet<string>(StringComparer.Ordinal);

        private static HashSet<string> _wantedParams;
        private static string _wantedParamsRaw;

        private static HashSet<string> WantedParameters()
        {
            var raw = ModConfig.LearnedParameters.Value ?? string.Empty;
            if (_wantedParams != null && string.Equals(raw, _wantedParamsRaw, StringComparison.Ordinal))
                return _wantedParams;

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in raw.Split(','))
            {
                var name = part.Trim();
                if (name.Length == 0) continue;

                // Movement fields stay out unless explicitly unlocked — changing base
                // movement speed is ruled out by the design.
                bool isMovement = name.IndexOf("speed", StringComparison.OrdinalIgnoreCase) >= 0
                                  || name.IndexOf("acceleration", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isMovement && !ModConfig.AllowSpeedParameters.Value)
                {
                    Log.Trace($"Refusing to learn movement parameter '{name}' (AllowSpeedParameters is off).");
                    continue;
                }

                set.Add(name);
            }

            _wantedParams = set;
            _wantedParamsRaw = raw;
            return set;
        }

        private static string[] _denyPatterns;
        private static string _denyRaw;

        private static string[] DenyPatterns()
        {
            var raw = ModConfig.ParameterDenyPatterns.Value ?? string.Empty;
            if (_denyPatterns != null && string.Equals(raw, _denyRaw, StringComparison.Ordinal))
                return _denyPatterns;

            var list = new List<string>();
            foreach (var part in raw.Split(','))
            {
                var p = part.Trim();
                if (p.Length > 0) list.Add(p);
            }

            _denyPatterns = list.ToArray();
            _denyRaw = raw;
            return _denyPatterns;
        }

        /// <summary>
        /// In Explore mode, decides whether a field is one the enemy may learn. Movement
        /// and presentation fields are excluded; everything else describing the shape of a
        /// combat behaviour is available, so new behaviour can come from anywhere rather
        /// than only from fields someone thought to list.
        /// </summary>
        private static bool IsExplorable(FsmState state, string fieldName)
        {
            // Only behaviour that is part of fighting. Idle, praying and death states have
            // numbers too, and learning them would be noise at best.
            if (TaxonomyUtil.ClassifyState(state.Name) == Archetype.Other) return false;

            bool isMovement = fieldName.IndexOf("speed", StringComparison.OrdinalIgnoreCase) >= 0
                              || fieldName.IndexOf("accel", StringComparison.OrdinalIgnoreCase) >= 0;

            // Movement fields are governed by AllowSpeedParameters rather than the deny
            // list, so unlocking them does not mean hand-editing the patterns.
            if (isMovement) return ModConfig.AllowSpeedParameters.Value;

            foreach (var deny in DenyPatterns())
                if (fieldName.IndexOf(deny, StringComparison.OrdinalIgnoreCase) >= 0) return false;

            return true;
        }

        private static string[] _denyActions;
        private static string _denyActionsRaw;

        private static string[] DenyActions()
        {
            var raw = ModConfig.ParameterDenyActions.Value ?? string.Empty;
            if (_denyActions != null && string.Equals(raw, _denyActionsRaw, StringComparison.Ordinal))
                return _denyActions;

            var list = new List<string>();
            foreach (var part in raw.Split(','))
            {
                var p = part.Trim();
                if (p.Length > 0) list.Add(p);
            }

            _denyActions = list.ToArray();
            _denyActionsRaw = raw;
            return _denyActions;
        }

        /// <summary>
        /// True for actions that compute rather than act. Their numeric fields are operands
        /// in the middle of a calculation, so varying them corrupts how a behaviour is
        /// worked out instead of changing what the behaviour is trying to do.
        /// </summary>
        private static bool IsPlumbing(string actionTypeName)
        {
            foreach (var deny in DenyActions())
                if (actionTypeName.IndexOf(deny, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static void TryAddParameters(FsmState state, FsmStateAction action, List<ParameterPoint> into)
        {
            bool explore = ModConfig.ExploreParameters;

            var wanted = WantedParameters();
            if (!explore && wanted.Count == 0) return;
            if (into.Count >= ModConfig.MaxLearnedParameters.Value) return;

            var actionType = action.GetType().Name;

            // Arithmetic helpers are excluded even in Explore mode — but a field asked for
            // by name in LearnedParameters still gets through, so the escape hatch works.
            bool plumbing = IsPlumbing(actionType);

            try
            {
                foreach (var f in action.GetType().GetFields(Flags))
                {
                    if (f.FieldType != typeof(FsmFloat)) continue;
                    if (into.Count >= ModConfig.MaxLearnedParameters.Value) return;

                    bool admitted = explore
                        ? ((!plumbing && IsExplorable(state, f.Name)) || wanted.Contains(f.Name))
                        : wanted.Contains(f.Name);

                    if (!admitted) continue;

                    if (!(f.GetValue(action) is FsmFloat v)) continue;

                    // Variable-backed values belong to the enemy's own logic; writing
                    // through them would leak into whatever else reads that variable.
                    // Report it rather than skipping silently — if the one field that
                    // matters is variable-driven, that changes how it has to be reached.
                    if (v.UseVariable || !string.IsNullOrEmpty(v.Name))
                    {
                        if (ReportedSkips.Add(state.Name + "/" + f.Name))
                        {
                            // Only worth flagging loudly for a field someone asked for by
                            // name; in Explore mode most fields are variable-driven and
                            // saying so about each would drown the log.
                            var msg = $"Parameter '{f.Name}' on state '{state.Name}' is driven by FSM " +
                                      $"variable '{v.Name}' rather than a literal, so it is not directly " +
                                      "learnable. Reaching it would mean adapting the variable itself.";
                            if (wanted.Contains(f.Name)) Log.Warn(msg); else Log.Trace(msg);
                        }
                        continue;
                    }

                    // A vanilla value of zero has no meaningful multiplier.
                    if (Mathf.Abs(v.Value) < 0.0001f)
                    {
                        if (ReportedSkips.Add(state.Name + "/" + f.Name))
                            Log.Trace($"Parameter '{f.Name}' on '{state.Name}' is 0 — no meaningful multiplier.");
                        continue;
                    }

                    // Spread the budget across states. Otherwise whichever state discovery
                    // reaches first takes every slot, and the enemy's other attacks end up
                    // with no dial to turn at all.
                    int fromThisState = 0;
                    foreach (var existing in into)
                        if (existing.StateName == state.Name) fromThisState++;
                    if (fromThisState >= ModConfig.MaxParametersPerState.Value) return;

                    // Distinguish repeated instances of the same action within a state.
                    int ordinal = 0;
                    foreach (var existing in into)
                        if (existing.StateName == state.Name && existing.ActionType == actionType) ordinal++;

                    into.Add(new ParameterPoint
                    {
                        StateName = state.Name,
                        ActionType = actionType,
                        FieldName = f.Name,
                        Ordinal = ordinal,
                        Target = v,
                        Vanilla = v.Value,
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Trace($"Parameter probe failed on '{state.Name}': {ex.Message}");
            }
        }

        private static void DiscoverSpacing(GameObject root, List<SpacingPoint> into)
        {
            var names = ModConfig.SpacingChildNames.Value;
            if (string.IsNullOrEmpty(names)) return;

            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in names.Split(','))
            {
                var trimmed = raw.Trim();
                if (trimmed.Length > 0) wanted.Add(trimmed);
            }
            if (wanted.Count == 0) return;

            foreach (var child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child == root.transform) continue;
                if (!wanted.Contains(Naming.StripInstanceSuffix(child.name))) continue;
                into.Add(new SpacingPoint { Target = child, Vanilla = child.localScale });
            }
        }

        /// <summary>Restores every discovered point to the value it was authored with.</summary>
        public static void RestoreVanilla(
            List<DecisionPoint> decisions, List<TimingPoint> timings, List<SpacingPoint> spacings,
            List<ParameterPoint> parameters)
        {
            if (parameters != null)
                foreach (var p in parameters) p.Restore();

            if (decisions != null)
                foreach (var d in decisions)
                    for (int i = 0; i < d.Vanilla.Length; i++) d.Slots.Write(i, d.Vanilla[i]);

            if (timings != null)
                foreach (var t in timings)
                    if (t.Time != null) t.Time.Value = t.Vanilla;

            if (spacings != null)
                foreach (var s in spacings)
                    if (s.Target != null) s.Target.localScale = s.Vanilla;
        }
    }
}
