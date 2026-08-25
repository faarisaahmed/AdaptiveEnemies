using System;
using System.Collections.Generic;
using System.Text;
using AdaptiveEnemies.Learning;
using HutongGames.PlayMaker;
using UnityEngine;

namespace AdaptiveEnemies.Runtime
{
    /// <summary>
    /// One enemy's share of the hive.
    ///
    /// <para>Lifecycle: on enable it discovers the FSM's adaptable structure, seeds a
    /// live table from the hive's shared prior, then refines that table from its own
    /// encounter while feeding every observation back into the pool.</para>
    ///
    /// <para>What it may change is deliberately narrow — which of the enemy's existing
    /// attacks it selects, how long the existing waits run, and how far its existing
    /// attack-range volume reaches. Movement speed is never touched and no behaviour is
    /// introduced that the enemy did not ship with.</para>
    /// </summary>
    public class AdaptiveBrain : MonoBehaviour
    {
        /// <summary>Probe count after which a still-dormant enemy gets a provisional dump (~30s).</summary>
        private const int ProvisionalDumpAttempt = 20;

        private static readonly HashSet<string> ReportedTypes = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> NextSummaryAt = new Dictionary<string, float>(StringComparer.Ordinal);

        private string _type;
        private EnemyClass _class;
        private HealthManager _health;

        private readonly List<DecisionPoint> _decisions = new List<DecisionPoint>();
        private readonly List<TimingPoint> _timings = new List<TimingPoint>();
        private readonly List<SpacingPoint> _spacings = new List<SpacingPoint>();
        private readonly List<ParameterPoint> _parameters = new List<ParameterPoint>();

        /// <summary>Candidate multipliers for learned parameters, and the index of the vanilla (1.0) level.</summary>
        private static float[] _candidates;
        private static int _vanillaLevel;
        private static string _candidatesRaw;

        /// <summary>Level currently chosen for each parameter, keyed by ParameterPoint.Id.</summary>
        private readonly Dictionary<string, int> _chosenLevel = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Episodes still owed to the current value of each parameter before it is reconsidered.</summary>
        private readonly Dictionary<string, int> _holdRemaining = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Every state the enemy can explicitly choose at one of its decision points.</summary>
        private readonly HashSet<string> _options = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The chosen behaviour whose chain is currently playing out, and when it began.</summary>
        private string _chainRoot;
        private float _chainRootAt = -999f;

        /// <summary>Situation tag for this episode, so a policy can differ by circumstance.</summary>
        private string _episodeContext = string.Empty;
        private bool _playerHealingThisEpisode;

        /// <summary>
        /// The situation an episode is happening in. Keys are suffixed with it, so an enemy
        /// keeps a separate policy for fighting a healing player without being told anything
        /// about what to do differently.
        /// </summary>
        private static string CurrentContext()
            => ModConfig.LearnHealContext.Value && CombatTracker.PlayerIsHealing ? "@heal" : string.Empty;

        private bool IsChoosableOption(string state)
            => state != null && _options.Contains(state);

        /// <summary>Scratch buffers for level selection, sized to the candidate count.</summary>
        private double[] _levelMeans, _levelNs, _levelWeights;

        private float _episodeStartedAt;

        private PlayMakerFSM _primary;

        /// <summary>
        /// Set for contact-damage enemies with no adaptable FSM structure. Their episodes
        /// come from the Crawler component instead of an FSM state, and their threat gate
        /// is proximity rather than an attack archetype.
        /// </summary>
        private CrawlerAdapter _contact;

        /// <summary>What this enemy is, worked out from its components and its own FSM.</summary>
        private readonly EnemyProfile _profile = new EnemyProfile();

        private bool _discovered;
        private float _nextDiscoveryAttempt;
        private int _discoveryAttempts;

        /// <summary>Per-encounter refinement, seeded from the hive prior.</summary>
        private readonly Dictionary<string, StatCell> _live = new Dictionary<string, StatCell>(StringComparer.Ordinal);

        /// <summary>Per-behaviour record of how often the player avoids it; drives timing variation.</summary>
        private readonly Dictionary<string, StatCell> _avoided = new Dictionary<string, StatCell>(StringComparer.Ordinal);

        // Current episode
        private string _episodeState;
        private Archetype _episodeArchetype;
        private double _damageThisEpisode;
        private bool _playerHitThisEpisode;
        private AvoidMethod _bestAvoid;
        private readonly HashSet<PlayerAction> _responses = new HashSet<PlayerAction>();

        private float _referenceHp = 1f;

        /// <summary>
        /// How much faster this enemy learns than the configured baseline, from its own
        /// durability. A boss met once must adapt within that single fight or the whole
        /// system is invisible on it; a three-hit enemy met constantly should not lurch.
        /// </summary>
        private double _learnScale = 1.0;

        /// <summary>Episodes scored in this encounter, driving the warm-up boost.</summary>
        private double _episodesThisEncounter;

        /// <summary>Where this enemy began, used to notice when it has flung itself out of the arena.</summary>
        private Vector3 _spawnPosition;
        private bool _outOfBoundsHandled;
        private bool _weightsDirty = true;
        private float _lastTimingFactor = 1f;
        private float _lastSpacingFactor = 1f;

        public string TypeName => _type;
        public EnemyClass Class => _class;

        private void OnEnable()
        {
            if (!EnemyIdentity.IsTarget(gameObject, out _type)) { enabled = false; return; }

            _class = EnemyIdentity.ClassOf(_type, gameObject);
            _health = GetComponent<HealthManager>();
            _referenceHp = _health != null && _health.hp > 0 ? _health.hp : 1f;
            // Rarity is read before this encounter is counted, so the first meeting with a
            // type gets the full newcomer boost.
            double seen = 0.0;
            Plugin.Hive?.Sample(HiveMind.SeenKey(_type), out _, out seen);
            _learnScale = Policy.EncounterScale(_referenceHp, seen);
            Plugin.Hive?.Record(HiveMind.SeenKey(_type), 1.0, 1.0);
            _episodesThisEncounter = 0.0;
            _spawnPosition = transform.position;
            _outOfBoundsHandled = false;

            _discovered = false;
            _discoveryAttempts = 0;
            _nextDiscoveryAttempt = 0f;
            _weightsDirty = true;

            _live.Clear();
            _avoided.Clear();
            _chosenLevel.Clear();
            _holdRemaining.Clear();
            _chainRoot = null;
            EnsureCandidates();
            ResetEpisode();

            CombatTracker.Register(this);
        }

        private void OnDisable()
        {
            CloseEpisode();
            CombatTracker.Unregister(this);

            // Pooled enemies are reused; hand the prefab back exactly as authored.
            try
            {
                FsmAdapter.RestoreVanilla(_decisions, _timings, _spacings, _parameters);
                _contact?.RestoreVanilla();
            }
            catch (Exception ex) { Log.Trace($"Restore failed on {_type}: {ex.Message}"); }

            _decisions.Clear();
            _timings.Clear();
            _spacings.Clear();
            _parameters.Clear();
            _primary = null;
            _contact = null;
        }

        private void Update()
        {
            if (!ModConfig.Enabled.Value) return;

            if (!_discovered) { TryDiscover(); return; }

            CheckBounds();
            TrackEpisode();

            if (_weightsDirty)
            {
                ApplyPolicy();
                _weightsDirty = false;
            }

            if (ModConfig.LogAdaptationSummary.Value) MaybeLogSummary();
        }

        // --- Discovery -----------------------------------------------------------

        private void TryDiscover()
        {
            // Silksong's enemies stay dormant until Hornet approaches, and PlayMaker does
            // not bind state actions until the FSM initialises. An enemy that looks empty
            // on spawn is usually just asleep, so keep retrying with a backoff rather than
            // concluding it has nothing to adapt.
            if (Time.time < _nextDiscoveryAttempt) return;

            _discoveryAttempts++;
            _nextDiscoveryAttempt = Time.time + Mathf.Min(0.25f * _discoveryAttempts, 2f);

            _decisions.Clear();
            _timings.Clear();
            _spacings.Clear();
            _parameters.Clear();

            bool complete;
            try { complete = FsmAdapter.Discover(gameObject, _decisions, _timings, _spacings, _parameters); }
            catch (Exception ex)
            {
                Log.Warn($"FSM discovery failed on {_type}: {ex.Message}");
                _discovered = true;
                return;
            }

            // Something was still asleep. Come back once it wakes, unless we already
            // found weighted branches — in which case there is enough to work with.
            if (!complete && _decisions.Count == 0)
            {
                // Retrying forever is right for adaptation but useless for diagnosis: an
                // enemy that never wakes would never produce a dump at all. After a while,
                // write what we can see anyway and say plainly that it was still dormant.
                if (ModConfig.DumpFsmStructure.Value && _discoveryAttempts == ProvisionalDumpAttempt)
                {
                    Log.Warn($"{_type}: FSM still dormant after ~{ProvisionalDumpAttempt} probes. Writing a " +
                             "provisional dump so there is something to inspect. Engage the enemy in combat " +
                             "and it will be replaced with the real structure.");
                    FsmDumper.DumpProvisional(_type, gameObject, _decisions, _timings, _spacings);
                }
                return;
            }

            _primary = FsmAdapter.ChoosePrimary(gameObject, _decisions);
            _discovered = true;

            BuildProfile();

            _options.Clear();
            foreach (var dp in _decisions)
                foreach (var opt in dp.OptionNames)
                    if (!string.IsNullOrEmpty(opt)) _options.Add(opt);

            // Nothing adaptable in the FSM at all? It may still be a contact-damage
            // crawler, whose approach is its attack and whose turn distance is its
            // only honest lever.
            if (_decisions.Count == 0 && _timings.Count == 0 && _spacings.Count == 0 && _parameters.Count == 0)
                _contact = CrawlerAdapter.TryCreate(gameObject);

            SeedPriors();

            var summary = $"{_type}: {_decisions.Count} decision point(s), {_timings.Count} timing point(s), " +
                          $"{_spacings.Count} spacing volume(s), {_parameters.Count} learned parameter(s)" +
                          (_parameters.Count > 0 ? " [" + string.Join(", ", _parameters.ConvertAll(p => $"{p.Id}={p.Vanilla:F2}").ToArray()) + "]" : "") +
                          $". Profile={_profile.Signature}, learn x{_learnScale:F1} (hp {_referenceHp:F0}). " +
                          $"Primary FSM='{_primary?.FsmName ?? "<none>"}' after {_discoveryAttempts} probe(s)." +
                          (_contact != null ? $" CONTACT MODE ({_contact.Describe()})." : string.Empty);

            // One line per enemy type, not per instance — a room of eleven crawlers
            // should not produce eleven identical reports.
            if (ReportedTypes.Add(_type))
            {
                Log.Info(summary);

                if (_contact != null)
                    Log.Info($"{_type}: contact-damage enemy with no attack graph. Adapting turn distance " +
                             "(where it reverses) rather than attack selection or timing. Speed untouched.");
                else if (_decisions.Count == 0)
                    Log.Warn($"{_type}: no weighted attack branch in its FSM — it has a single fixed attack, " +
                             "so selection cannot adapt. Timing and spacing are the levers for this enemy.");
            }
            else Log.Trace(summary);

            if (ModConfig.DumpFsmStructure.Value || ModConfig.DumpEveryNewEnemy.Value)
                FsmDumper.Dump(_type, gameObject, _decisions, _timings, _spacings);

            // One line per enemy type, appended as the roster is met. Playing normally with
            // DumpEveryNewEnemy on builds the whole classification table on its own.
            if (ModConfig.DumpEveryNewEnemy.Value)
                FsmDumper.AppendRosterEntry(_type, _profile, _referenceHp, _learnScale,
                    _decisions.Count, _timings.Count, _parameters.Count);
        }

        /// <summary>
        /// Encounter start: pull what the hive already knows into this enemy's live table
        /// as pseudo-counts. Everything after this is its own experience layered on top.
        /// </summary>
        /// <summary>
        /// Works out this enemy's build from its components and the states its FSM actually
        /// contains, then uses that as its cohort for sharing. An enemy with no diving state
        /// cannot receive diving knowledge, because there is nowhere for it to land.
        /// </summary>
        private void BuildProfile()
        {
            var states = new List<string>();

            if (_primary != null)
            {
                try
                {
                    var all = _primary.FsmStates;
                    if (all != null)
                        foreach (var st in all)
                            if (st != null) states.Add(st.Name);
                }
                catch (Exception ex) { Log.Trace($"Profile state read failed: {ex.Message}"); }
            }

            _profile.Build(gameObject, states, _contact != null);
            _class = _profile.Locomotion;
        }

        private void SeedPriors()
        {
            var hive = Plugin.Hive;
            if (hive == null) return;

            foreach (var dp in _decisions)
            {
                for (int i = 0; i < dp.OptionNames.Length; i++)
                {
                    var option = dp.OptionNames[i];
                    if (_live.ContainsKey(option)) continue;

                    var prior = Policy.ComputePrior(hive, _type, _profile.Signature, _class, option, dp.Archetypes[i]);

                    var cell = new StatCell(hive.Clock);
                    if (prior.Strength > 0.0) cell.SeedPrior(hive.Clock, prior.Mean, prior.Strength);
                    _live[option] = cell;

                    if (prior.Strength > 0.0)
                        Log.Trace($"{_type}/{option}: prior mean {prior.Mean:F2} strength {prior.Strength:F1}");
                }
            }

            _weightsDirty = true;
        }

        // --- Episodes ------------------------------------------------------------

        /// <summary>
        /// Whether this enemy is currently a live threat, so the player's dodges and
        /// whiffs can honestly be attributed to it.
        ///
        /// <para>For an enemy with a real attack graph this means being in an attack
        /// state and within ThreatRadius. For a contact-damage crawler there is no
        /// attack state at all, so proximity is the whole signal — being close enough to
        /// be walked into is what makes dodging it meaningful.</para>
        /// </summary>
        private bool IsThreateningNow()
        {
            var hero = CombatTracker.HeroPosition();
            if (!hero.HasValue) return false;

            float r = _contact != null ? ModConfig.ContactThreatRadius.Value : ModConfig.ThreatRadius.Value;
            if ((hero.Value - transform.position).sqrMagnitude > r * r) return false;

            if (_contact != null || _episodeArchetype.IsThreatening()) return true;

            // Otherwise, let the data decide. A behaviour that has been landing hits on the
            // player is an attack whatever its state name suggests — this is how a flier's
            // approach gets recognised as a charge without anyone declaring it one.
            return LearnedToBeThreatening(_episodeState);
        }

        /// <summary>
        /// True once a behaviour has damaged the player often enough to count as an attack
        /// in its own right. Nothing here is hardcoded per enemy; it is read back out of
        /// the same hive the enemies write to.
        /// </summary>
        private bool LearnedToBeThreatening(string state)
        {
            if (state == null) return false;

            var hive = Plugin.Hive;
            if (hive == null) return false;

            if (!hive.Sample(HiveMind.HitRateKey(_type, state), out var rate, out var n)) return false;
            if (Policy.Confidence(n, _learnScale) <= 0.0) return false;

            // Samples are learning-rate-weighted, so convert back to real episodes before
            // judging. In testing mode one lucky hit reads as n=6, which was enough to
            // promote a single-frame state to "attack" on its own.
            double episodes = n / Math.Max(1e-6, ModConfig.EffLearningRate);
            if (episodes < ModConfig.HitRateMinEpisodes.Value) return false;

            return rate >= ModConfig.HitRateThreshold.Value;
        }

        private void TrackEpisode()
        {
            string active;

            if (_contact != null)
            {
                active = _contact.CurrentBehaviour();
            }
            else
            {
                if (_primary == null) return;
                try { active = _primary.ActiveStateName; }
                catch { return; }
            }

            // A contact-damage enemy never changes state — a crawler crawls until it dies —
            // so its outcome would otherwise only ever be scored at the moment of death.
            // Close the window on a timer instead.
            bool windowExpired = _contact != null && _episodeState != null
                                 && Time.time - _episodeStartedAt >= ModConfig.ContactEpisodeSeconds.Value;

            if (windowExpired || !string.Equals(active, _episodeState, StringComparison.Ordinal))
            {
                CloseEpisode();
                OpenEpisode(active);
            }

            if (_episodeState == null) return;

            // Only count avoidance while this enemy is actually threatening and nearby.
            // Hornet is airborne almost constantly; without this gate every jump anywhere
            // in the room registers as "dodged that idle enemy's attack".
            if (!IsThreateningNow()) return;

            if (CombatTracker.PlayerIsHealing) _playerHealingThisEpisode = true;

            var avoid = CombatTracker.SampleAvoid();
            if (avoid != AvoidMethod.NotAvoided && Rank(avoid) > Rank(_bestAvoid))
                _bestAvoid = avoid;
        }

        private static int Rank(AvoidMethod m)
        {
            switch (m)
            {
                case AvoidMethod.CrossStitch: return 4;
                case AvoidMethod.NeedleParry: return 3;
                case AvoidMethod.Evade: return 2;
                case AvoidMethod.Jump: return 1;
                default: return 0;
            }
        }

        private void OpenEpisode(string state)
        {
            _episodeState = string.IsNullOrEmpty(state) ? null : state;
            if (_episodeState == null) return;

            _episodeArchetype = TaxonomyUtil.ClassifyState(_episodeState);
            _damageThisEpisode = 0.0;
            _playerHitThisEpisode = false;
            _bestAvoid = AvoidMethod.NotAvoided;
            _responses.Clear();
            _episodeStartedAt = Time.time;
            _episodeContext = CurrentContext();
            _playerHealingThisEpisode = CombatTracker.PlayerIsHealing;

            // Remember any behaviour that could set delayed damage in motion, so a rock
            // that lands after the slam is over is still credited to the slam.
            if (_episodeArchetype.IsThreatening() || LearnedToBeThreatening(_episodeState))
            {
                LastThreateningState = _episodeState;
                LastThreateningAt = Time.time;
            }

            // If this state is one the enemy explicitly chose, it becomes the root of the
            // chain that follows. An attack is antic -> strike -> recovery, and the player
            // punishes it somewhere in the middle; without this the choice to attack never
            // sees the consequence, because the consequence lands on a later state.
            if (IsChoosableOption(_episodeState)) { _chainRoot = _episodeState; _chainRootAt = Time.time; }

            ChooseParameterLevels(_episodeState);
            ApplyTimingVariation(_episodeState);
        }

        private void CloseEpisode()
        {
            if (_episodeState == null) return;

            var hive = Plugin.Hive;

            // Record only genuinely informative episodes: the enemy hurt the player, was
            // hurt itself, or had a real attack cleanly avoided. Anything else is the
            // enemy standing around, and logging that buries the signal.
            // A state that lasted a frame cannot meaningfully own an outcome; whatever
            // happened during it belongs to the behaviour either side.
            bool longEnough = Time.time - _episodeStartedAt >= ModConfig.MinEpisodeSeconds.Value;

            // _bestAvoid is only ever set while IsThreateningNow() held, so its presence
            // already implies a real attack (or a real contact threat) was dodged.
            bool informative = longEnough
                               && (_damageThisEpisode > 0.0
                                   || _playerHitThisEpisode
                                   || _bestAvoid != AvoidMethod.NotAvoided);

            if (hive != null && informative)
            {
                double score = Policy.ScoreEpisode(_playerHitThisEpisode, _damageThisEpisode, _referenceHp, _bestAvoid);

                // Denying a heal is worth more than an ordinary hit. This states the goal;
                // which attack can actually reach in time is still theirs to work out.
                if (_playerHealingThisEpisode && _playerHitThisEpisode)
                    score = Policy.Clamp(score + ModConfig.HealInterruptBonus.Value, -1.0, 1.0);

                var keyed = _episodeState + _episodeContext;

                _episodesThisEncounter += 1.0;
                double warm = Policy.WarmupWeight(_episodesThisEncounter);

                Policy.RecordEpisode(hive, _type, _profile.Signature, _class, keyed, _episodeArchetype,
                    score, _bestAvoid, _responses, warm);

                // Did this behaviour actually connect? Recorded separately from the score so
                // the mod can work out on its own which behaviours are attacks.
                hive.Record(HiveMind.HitRateKey(_type, _episodeState),
                    _playerHitThisEpisode ? 1.0 : 0.0, ModConfig.EffLearningRate);

                // What the player was doing when it landed versus when it missed. These
                // teach opposite lessons and must not be averaged together.
                foreach (var r in _responses)
                {
                    var key = _playerHitThisEpisode
                        ? HiveMind.LandedKey(_type, _episodeState, r)
                        : HiveMind.MissedKey(_type, _episodeState, r);
                    hive.Record(key, 1.0, ModConfig.EffLearningRate);
                }

                ScoreParameterLevels(_episodeState, score);

                // Push the outcome back to the choice that started this chain, discounted
                // because the later state shares responsibility for it.
                if (_chainRoot != null && !string.Equals(_chainRoot, _episodeState, StringComparison.Ordinal))
                {
                    double chainWeight = ModConfig.EffLearningRate * ModConfig.ChainCreditWeight.Value;
                    if (chainWeight > 0.0)
                    {
                        var rootKey = _chainRoot + _episodeContext;
                        hive.Record(HiveMind.PrivateKey(_type, rootKey), score, chainWeight);
                        LiveCell(rootKey).Add(hive.Clock, ModConfig.EffHalfLife, score, chainWeight);

                        Log.Trace($"{_type}: '{_episodeState}' outcome {score:F2} credited back to choice '{_chainRoot}'");
                    }
                }

                LiveCell(keyed).Add(hive.Clock, ModConfig.EffHalfLife, score, ModConfig.EffLearningRate * warm);

                if (_contact != null || _episodeArchetype.IsThreatening())
                {
                    AvoidCell(keyed).Add(hive.Clock, ModConfig.EffHalfLife,
                        _bestAvoid != AvoidMethod.NotAvoided ? 1.0 : 0.0, ModConfig.EffLearningRate);
                }

                _weightsDirty = true;

                Log.Trace($"{_type}/{_episodeState} [{_episodeArchetype}]: score {score:F2} " +
                          $"(dmg {_damageThisEpisode:F0}, playerHit {_playerHitThisEpisode}, avoid {_bestAvoid})");
            }

            ResetEpisode();
        }

        private void ResetEpisode()
        {
            _episodeState = null;
            _damageThisEpisode = 0.0;
            _playerHitThisEpisode = false;
            _bestAvoid = AvoidMethod.NotAvoided;
            _responses.Clear();
        }

        private StatCell LiveCell(string option)
        {
            if (_live.TryGetValue(option, out var cell)) return cell;

            var hive = Plugin.Hive;
            cell = new StatCell(hive?.Clock ?? 0.0);

            // Seed from the hive on first use rather than only at encounter start, so a
            // situation met for the first time mid-fight still inherits what is known.
            if (hive != null)
            {
                var prior = Policy.ComputePrior(hive, _type, _profile.Signature, _class, option, TaxonomyUtil.ClassifyState(option));
                if (prior.Strength > 0.0) cell.SeedPrior(hive.Clock, prior.Mean, prior.Strength);
            }

            _live[option] = cell;
            return cell;
        }

        private StatCell AvoidCell(string option)
        {
            if (_avoided.TryGetValue(option, out var cell)) return cell;
            cell = new StatCell(Plugin.Hive?.Clock ?? 0.0);
            _avoided[option] = cell;
            return cell;
        }

        /// <summary>
        /// Notices an enemy that has thrown itself out of the arena — usually by learning to
        /// decelerate so little that a dive carries past the level. The value that did it is
        /// marked down hard and snapped back to the authored one, so the enemy discovers the
        /// limit rather than being forbidden the parameter.
        /// </summary>
        private void CheckBounds()
        {
            float r = ModConfig.OutOfBoundsRadius.Value;
            bool outside = (transform.position - _spawnPosition).sqrMagnitude > r * r;

            if (!outside) { _outOfBoundsHandled = false; return; }
            if (_outOfBoundsHandled) return;
            _outOfBoundsHandled = true;

            double penalty = -ModConfig.OutOfBoundsPenalty.Value;
            var hive = Plugin.Hive;

            if (hive != null)
            {
                // Blame the parameter values that were in play, not just the behaviour.
                foreach (var p in _parameters)
                {
                    if (!_chosenLevel.TryGetValue(p.Id, out var level)) continue;
                    hive.Record(HiveMind.ParameterKey(_type, p.Id, level), penalty, ModConfig.EffLearningRate);
                }

                var blamed = _chainRoot ?? _episodeState;
                if (blamed != null)
                {
                    hive.Record(HiveMind.PrivateKey(_type, blamed), penalty, ModConfig.EffLearningRate);
                    LiveCell(blamed).Add(hive.Clock, ModConfig.EffHalfLife, penalty, ModConfig.EffLearningRate);
                }
            }

            // Safety rail: put the authored values back so the fight stays playable.
            foreach (var p in _parameters) p.Restore();
            _chosenLevel.Clear();
            _holdRemaining.Clear();
            _weightsDirty = true;

            Log.Warn($"{_type} left the arena ({(transform.position - _spawnPosition).magnitude:F0}u from " +
                     $"spawn) while doing '{_chainRoot ?? _episodeState}'. Parameters reset and marked down.");
        }

        // --- Learned parameters --------------------------------------------------

        /// <summary>Parses the candidate multiplier list once, and locates the vanilla (1.0) level.</summary>
        private static void EnsureCandidates()
        {
            var raw = ModConfig.ParameterCandidates.Value ?? "1";
            if (_candidates != null && string.Equals(raw, _candidatesRaw, StringComparison.Ordinal)) return;

            var list = new List<float>();
            foreach (var part in raw.Split(','))
            {
                if (float.TryParse(part.Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0f)
                    list.Add(v);
            }

            if (list.Count == 0) list.Add(1f);

            // The vanilla level must exist: it is the anchor the policy falls back to
            // before it is confident, and what every other level is measured against.
            int vanilla = list.FindIndex(v => Mathf.Approximately(v, 1f));
            if (vanilla < 0) { list.Add(1f); vanilla = list.Count - 1; }

            _candidates = list.ToArray();
            _vanillaLevel = vanilla;
            _candidatesRaw = raw;
        }

        /// <summary>
        /// Picks a level for every learnable parameter belonging to the state being
        /// entered, and applies it. Selection is probabilistic, so the enemy keeps
        /// sampling alternatives instead of locking onto its first success.
        /// </summary>
        private void ChooseParameterLevels(string state)
        {
            if (_parameters.Count == 0) return;

            var hive = Plugin.Hive;
            if (hive == null) return;

            EnsureCandidates();
            int n = _candidates.Length;

            if (_levelWeights == null || _levelWeights.Length != n)
            {
                _levelMeans = new double[n];
                _levelNs = new double[n];
                _levelWeights = new double[n];
            }

            foreach (var p in _parameters)
            {
                if (!string.Equals(p.StateName, state, StringComparison.Ordinal)) continue;

                // Stick with the current value until its trial is up. Re-rolling every
                // attack means no value ever gets tested properly, and every attack looks
                // different — which reads as the enemy malfunctioning, not learning.
                if (_holdRemaining.TryGetValue(p.Id, out var hold) && hold > 0
                    && _chosenLevel.TryGetValue(p.Id, out var held))
                {
                    _holdRemaining[p.Id] = hold - 1;
                    p.Apply(_candidates[held]);
                    continue;
                }

                for (int i = 0; i < n; i++)
                {
                    hive.Sample(HiveMind.ParameterKey(_type, p.Id, i), out var mean, out var count);
                    _levelMeans[i] = mean;
                    _levelNs[i] = count;
                }

                int chosen;
                if (ShouldSettle(n))
                {
                    // Every alternative has been tried and none beat the authored value.
                    // Stop fidgeting with this dial — it is not one worth turning.
                    chosen = _vanillaLevel;
                }
                else
                {
                    Policy.LevelWeights(_levelMeans, _levelNs, _vanillaLevel, _levelWeights, _learnScale);
                    chosen = SampleIndex(_levelWeights);
                }

                _chosenLevel[p.Id] = chosen;
                _holdRemaining[p.Id] = Mathf.Max(1, ModConfig.ParameterCommitEpisodes.Value) - 1;
                p.Apply(_candidates[chosen]);
            }
        }

        /// <summary>
        /// True when the authored value has beaten every alternative by a clear margin, on
        /// real evidence. Lets an enemy conclude that a parameter is not a useful dial and
        /// leave it alone, rather than exploring it forever.
        /// </summary>
        private bool ShouldSettle(int levels)
        {
            double vanillaMean = _levelMeans[_vanillaLevel];
            if (_levelNs[_vanillaLevel] <= 1e-6) return false;

            double margin = ModConfig.ParameterSettleMargin.Value;

            for (int i = 0; i < levels; i++)
            {
                if (i == _vanillaLevel) continue;

                // An untried alternative is not evidence that vanilla is best.
                if (_levelNs[i] <= 1e-6) return false;
                if (_levelMeans[i] > vanillaMean - margin) return false;
            }

            return true;
        }

        private static int SampleIndex(double[] weights)
        {
            double total = 0.0;
            for (int i = 0; i < weights.Length; i++) total += weights[i];
            if (total <= 1e-9) return 0;

            double roll = UnityEngine.Random.value * total;
            for (int i = 0; i < weights.Length; i++)
            {
                roll -= weights[i];
                if (roll <= 0.0) return i;
            }
            return weights.Length - 1;
        }

        /// <summary>Credits the episode's outcome to whichever levels were in play for it.</summary>
        private void ScoreParameterLevels(string state, double score)
        {
            if (_parameters.Count == 0) return;

            var hive = Plugin.Hive;
            if (hive == null) return;

            foreach (var p in _parameters)
            {
                if (!string.Equals(p.StateName, state, StringComparison.Ordinal)) continue;
                if (!_chosenLevel.TryGetValue(p.Id, out var level)) continue;

                hive.Record(HiveMind.ParameterKey(_type, p.Id, level), score, ModConfig.EffLearningRate);
            }
        }

        // --- Inbound events ------------------------------------------------------

        /// <summary>The player damaged this enemy.</summary>
        public void OnDamaged(HitInstance hit, int damage)
        {
            if (!ModConfig.Enabled.Value) return;

            var action = CombatTracker.Classify(hit);
            if (action != PlayerAction.None) _responses.Add(action);
            if (damage > 0) _damageThisEpisode += damage;

            CombatTracker.NoteSwingConnected();
            Log.Trace($"{_type}: took {damage} from {action} during '{_episodeState}'");
        }

        /// <summary>This enemy's attack connected with the player.</summary>
        public void OnPlayerDamaged(int amount)
        {
            if (!ModConfig.Enabled.Value) return;
            _playerHitThisEpisode = true;
        }

        /// <summary>Most recent behaviour that could plausibly have caused delayed damage, and when.</summary>
        public string LastThreateningState { get; private set; }
        public float LastThreateningAt { get; private set; } = -999f;

        /// <summary>
        /// How good a candidate this enemy is for having caused damage it does not own,
        /// or a negative number if it is not a candidate at all. Higher is better.
        /// </summary>
        public float IndirectClaimScore(Vector3 sourcePosition)
        {
            if (LastThreateningState == null) return -1f;

            float age = Time.time - Mathf.Max(LastThreateningAt, _chainRootAt);
            if (age > ModConfig.IndirectAttributionSeconds.Value) return -1f;

            float r = ModConfig.IndirectAttributionRadius.Value;
            float dist = (sourcePosition - transform.position).magnitude;
            if (dist > r) return -1f;

            // Prefer the enemy that attacked most recently, then the nearest one.
            return (1f - age / ModConfig.IndirectAttributionSeconds.Value) * 2f + (1f - dist / r);
        }

        /// <summary>
        /// Credits this enemy for damage caused by something it does not own — a rock it
        /// dropped, a minion it summoned. The credit goes to the behaviour that was running
        /// when the cause was set in motion, not to whatever the enemy happens to be doing
        /// now, so a slam is rewarded for rocks that land a second later.
        /// </summary>
        public void OnIndirectPlayerDamaged(int amount)
        {
            if (!ModConfig.Enabled.Value) return;

            // Prefer the behaviour the enemy actually chose. She decided to drop a rock, so
            // the rock's damage belongs to that choice — not to whichever attack state
            // happened to be the most recent threatening one.
            var cause = LastThreateningState;
            if (_chainRoot != null && Time.time - _chainRootAt <= ModConfig.IndirectAttributionSeconds.Value)
                cause = _chainRoot;

            if (cause == null) return;

            if (string.Equals(cause, _episodeState, StringComparison.Ordinal))
            {
                _playerHitThisEpisode = true;
                Log.Trace($"{_type}: indirect hit credited to current '{_episodeState}'");
                return;
            }

            // The causing behaviour is already over, so record against it directly.
            var hive = Plugin.Hive;
            if (hive == null) return;

            var state = cause;
            var arch = TaxonomyUtil.ClassifyState(state);
            double w = ModConfig.EffLearningRate;

            hive.Record(HiveMind.PrivateKey(_type, state), 1.0, w);
            hive.Record(HiveMind.ClassKey(_profile.Signature, TaxonomyUtil.TopicOf(arch), arch), 1.0, w);
            hive.Record(HiveMind.UniversalKey("arch|" + arch), 1.0, w);
            hive.Record(HiveMind.HitRateKey(_type, state), 1.0, w);

            LiveCell(state).Add(hive.Clock, ModConfig.EffHalfLife, 1.0, w);
            _weightsDirty = true;

            Log.Trace($"{_type}: indirect hit credited back to '{state}'");
        }

        /// <summary>
        /// The player finished a heal. If this enemy was close enough to have stopped it,
        /// whatever it was doing gets marked down — which is what pushes it toward the
        /// attacks that can actually reach in time, without ever being told which those are.
        /// </summary>
        public void OnPlayerCompletedHeal()
        {
            if (!ModConfig.Enabled.Value || _episodeState == null) return;
            if (ModConfig.HealCompletedPenalty.Value <= 0f) return;

            var hero = CombatTracker.HeroPosition();
            if (!hero.HasValue) return;

            float r = ModConfig.ThreatRadius.Value;
            if ((hero.Value - transform.position).sqrMagnitude > r * r) return;

            var hive = Plugin.Hive;
            if (hive == null) return;

            var blamed = (_chainRoot ?? _episodeState) + "@heal";
            double penalty = -ModConfig.HealCompletedPenalty.Value;

            hive.Record(HiveMind.PrivateKey(_type, blamed), penalty, ModConfig.EffLearningRate);
            LiveCell(blamed).Add(hive.Clock, ModConfig.EffHalfLife, penalty, ModConfig.EffLearningRate);
            _weightsDirty = true;

            Log.Trace($"{_type}: failed to stop a heal while doing '{blamed}'");
        }

        /// <summary>
        /// The player swung and missed while this enemy was mid-attack nearby. Gated on
        /// both proximity and the enemy actually attacking — otherwise a single missed
        /// swing gets credited to every enemy in the room, which is exactly what buried
        /// the real data in the first test run.
        /// </summary>
        public void NotePlayerWhiff(PlayerAction action)
        {
            if (!ModConfig.Enabled.Value || _episodeState == null) return;
            if (!IsThreateningNow()) return;

            Plugin.Hive?.Record(HiveMind.PrivateResponseKey(_type, _episodeState, action),
                0.25, ModConfig.EffLearningRate);
        }

        // --- Expression ----------------------------------------------------------

        private void ApplyPolicy()
        {
            if (ModConfig.AdaptSelection.Value) ApplyWeights();
            if (ModConfig.AdaptSpacing.Value) ApplySpacing();
        }

        private void ApplyWeights()
        {
            double clock = Plugin.Hive?.Clock ?? 0.0;
            var ctx = CurrentContext();

            foreach (var dp in _decisions)
            {
                int n = dp.Vanilla.Length;

                // Total evidence across this decision point, so an option can be judged
                // against how much the enemy has learned overall rather than in isolation.
                double totalN = 0.0;
                for (int i = 0; i < n; i++)
                {
                    if (dp.Vanilla[i] <= 0f) continue;
                    if (!_live.TryGetValue(dp.OptionNames[i] + ctx, out var c0)) continue;
                    c0.Decay(clock, ModConfig.EffHalfLife);
                    totalN += c0.N;
                }

                for (int i = 0; i < n; i++)
                {
                    if (dp.Vanilla[i] <= 0f) { dp.Scratch[i] = 0.0; continue; }

                    var cell = _live.TryGetValue(dp.OptionNames[i] + ctx, out var c) ? c : null;
                    double score = 0.0, evidence = 0.0;

                    if (cell != null)
                    {
                        score = cell.Mean;
                        evidence = cell.N;
                    }

                    // Blend in what this player actually does, so the enemy favours the
                    // attack you are currently worst at answering.
                    score = Policy.ResponseAdjusted(Plugin.Hive, _type, dp.OptionNames[i], score);

                    dp.Scratch[i] = dp.Vanilla[i] * Policy.WeightMultiplier(score, evidence, totalN, _learnScale);
                }

                Policy.Renormalize(dp.Vanilla, dp.Scratch);

                for (int i = 0; i < n; i++)
                    dp.Slots.Write(i, (float)dp.Scratch[i]);
            }
        }

        /// <summary>
        /// Re-rolls the duration of the waits belonging to a state as it is entered.
        ///
        /// <para>Two components. A <b>random</b> part whose size grows with how reliably
        /// the player avoids this attack — a telegraph that is always 0.6s is one the
        /// player reads, so varying it is the point. And a <b>directional</b> part that
        /// shortens the wait when the attack is being punished, giving less reaction
        /// time. Both stay inside TimingVariance, so the attack is still the same attack.</para>
        /// </summary>
        private void ApplyTimingVariation(string state)
        {
            if (!ModConfig.AdaptTiming.Value || _timings.Count == 0) return;

            var avoidCell = _avoided.TryGetValue(state, out var a) ? a : null;
            var liveCell = _live.TryGetValue(state, out var l) ? l : null;
            if (avoidCell == null && liveCell == null) return;

            double clock = Plugin.Hive?.Clock ?? 0.0;
            avoidCell?.Decay(clock, ModConfig.EffHalfLife);
            liveCell?.Decay(clock, ModConfig.EffHalfLife);

            double evidence = Math.Max(avoidCell?.N ?? 0.0, liveCell?.N ?? 0.0);
            double confidence = Policy.Confidence(evidence, _learnScale) * ModConfig.EffAuthority;
            if (confidence <= 0.0) return;

            double variance = ModConfig.TimingVariance.Value;
            double avoidRate = Policy.Clamp(avoidCell?.Mean ?? 0.0, 0.0, 1.0);
            double punished = Policy.Clamp(-(liveCell?.Mean ?? 0.0), 0.0, 1.0);

            // Take the stronger of the two pressures rather than letting each dilute the
            // other, and pass it through the responsiveness curve. Multiplying variance
            // by confidence by raw pressure compounds to near-nothing in practice.
            double pressure = Policy.Responsiveness(Math.Max(avoidRate, punished));
            double magnitude = variance * confidence * pressure;

            double jitter = UnityEngine.Random.Range(-1f, 1f) * magnitude;
            double bias = -magnitude * punished;

            double factor = Policy.Clamp(1.0 + jitter + bias, 1.0 - variance, 1.0 + variance);
            _lastTimingFactor = (float)factor;

            foreach (var tp in _timings)
            {
                if (tp.Time == null) continue;
                if (!string.Equals(tp.StateName, state, StringComparison.Ordinal)) continue;
                tp.Time.Value = (float)(tp.Vanilla * factor);
            }
        }

        /// <summary>
        /// Nudges the attack-range volume. An attack that keeps getting punished pushes
        /// the trigger distance outward so the enemy commits from further away; one that
        /// is landing leaves the authored spacing alone.
        /// </summary>
        private void ApplySpacing()
        {
            if (_spacings.Count == 0 && _contact == null) return;

            double clock = Plugin.Hive?.Clock ?? 0.0;
            double sum = 0.0, weight = 0.0;

            foreach (var kv in _live)
            {
                var cell = kv.Value;
                cell.Decay(clock, ModConfig.EffHalfLife);
                double conf = Policy.Confidence(cell.N, _learnScale);
                if (conf <= 0.0) continue;
                sum += cell.Mean * conf;
                weight += conf;
            }

            if (weight <= 0.0) return;

            double mean = Policy.Clamp(sum / weight, -1.0, 1.0);

            // Same responsiveness curve as timing: react to the magnitude of the pressure,
            // keeping the sign. Negative mean (being punished) pushes the factor above 1,
            // so the enemy commits from further out.
            double response = Policy.Responsiveness(Math.Abs(mean)) * Math.Sign(mean);

            double variance = _contact != null
                ? ModConfig.ContactTurnVariance.Value
                : ModConfig.SpacingVariance.Value;

            double factor = 1.0 - variance * response * ModConfig.EffAuthority;
            _lastSpacingFactor = (float)factor;

            foreach (var sp in _spacings)
            {
                if (sp.Target == null) continue;
                sp.Target.localScale = sp.Vanilla * (float)factor;
            }

            _contact?.ApplyTurnDistance((float)factor);
        }

        // --- Diagnostics ---------------------------------------------------------

        /// <summary>
        /// Periodically reports what this enemy type has learned and what it is currently
        /// doing differently. The fastest way to answer "is it adapting at all".
        /// </summary>
        private void MaybeLogSummary()
        {
            if (_type == null) return;

            float now = Time.time;
            if (NextSummaryAt.TryGetValue(_type, out var next) && now < next) return;
            NextSummaryAt[_type] = now + 15f;

            if (_live.Count == 0) return;

            double clock = Plugin.Hive?.Clock ?? 0.0;
            var sb = new StringBuilder();
            sb.Append(_type).Append(" | ");

            double poolN = 0.0;
            foreach (var kv in _live) { kv.Value.Decay(clock, ModConfig.EffHalfLife); poolN += kv.Value.N; }

            bool any = false;
            foreach (var kv in _live)
            {
                var cell = kv.Value;
                if (cell.N <= 0.01) continue;

                any = true;
                double conf = Policy.Confidence(cell.N, _learnScale);
                sb.Append($"{kv.Key}: score {cell.Mean:+0.00;-0.00} n={cell.N:F1} conf={conf:P0}");

                if (_decisions.Count > 0)
                    sb.Append($" weight x{Policy.WeightMultiplier(cell.Mean, cell.N, poolN, _learnScale):F2}" +
                              $" (explore +{Policy.Exploration(cell.N, poolN):F2})");

                sb.Append("; ");
            }

            if (!any) return;

            // Learned parameter levels, and whether a behaviour has taught itself to be an attack.
            foreach (var p in _parameters)
            {
                if (!_chosenLevel.TryGetValue(p.Id, out var level)) continue;
                sb.Append($"{p.Id} x{_candidates[level]:F2} (={p.Vanilla * _candidates[level]:F2}); ");
            }

            foreach (var kv in _live)
            {
                if (TaxonomyUtil.ClassifyState(kv.Key).IsThreatening()) continue;
                if (LearnedToBeThreatening(kv.Key))
                    sb.Append($"[learned '{kv.Key}' is an attack]; ");
            }

            if (_contact != null)
                sb.Append($"turn distance x{_lastSpacingFactor:F2} (contact mode: no attack graph)");
            else
            {
                sb.Append($"timing x{_lastTimingFactor:F2}, spacing x{_lastSpacingFactor:F2}");
                if (_decisions.Count == 0) sb.Append(" (no weighted branch: single fixed attack)");
            }

            Log.Info(sb.ToString());
        }
    }
}
