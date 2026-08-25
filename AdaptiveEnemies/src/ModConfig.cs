using BepInEx.Configuration;

namespace AdaptiveEnemies
{
    /// <summary>
    /// Every tunable, exposed through BepInEx config so it can be edited in
    /// ConfigurationManager (already installed) without a rebuild.
    /// </summary>
    public static class ModConfig
    {
        // General
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<string> TargetEnemies;
        public static ConfigEntry<bool> AdaptAllEnemies;
        public static ConfigEntry<string> ExcludeEnemies;
        public static ConfigEntry<float> BossHpThreshold;
        public static ConfigEntry<bool> AdaptBosses;
        public static ConfigEntry<float> RarityScaleWeight;
        public static ConfigEntry<bool> DumpEveryNewEnemy;

        // Testing
        public static ConfigEntry<bool> TestingMode;

        // Learning
        public static ConfigEntry<float> LearningRate;
        public static ConfigEntry<float> ConfidenceSamples;
        public static ConfigEntry<float> MinSamples;
        public static ConfigEntry<float> Authority;
        public static ConfigEntry<float> Kappa;
        public static ConfigEntry<float> ExplorationWeight;
        public static ConfigEntry<float> MinWeightMultiplier;
        public static ConfigEntry<float> MaxWeightMultiplier;
        public static ConfigEntry<float> HalfLifeSeconds;
        public static ConfigEntry<float> AvoidPenalty;
        public static ConfigEntry<float> DamageNormalizationFraction;
        public static ConfigEntry<float> ThreatRadius;
        public static ConfigEntry<bool> AttributeIndirectDamage;
        public static ConfigEntry<float> IndirectAttributionSeconds;
        public static ConfigEntry<float> IndirectAttributionRadius;

        // Hive mind
        public static ConfigEntry<float> SameClassShareWeight;
        public static ConfigEntry<float> CrossClassShareWeight;
        public static ConfigEntry<float> UniversalShareWeight;
        public static ConfigEntry<float> PriorStrengthCap;

        // Expression
        public static ConfigEntry<bool> AdaptSelection;
        public static ConfigEntry<bool> AdaptTiming;
        public static ConfigEntry<bool> AdaptSpacing;
        public static ConfigEntry<float> TimingVariance;
        public static ConfigEntry<float> SpacingVariance;
        public static ConfigEntry<float> ContactTurnVariance;
        public static ConfigEntry<float> ContactThreatRadius;
        public static ConfigEntry<float> ContactEpisodeSeconds;
        public static ConfigEntry<string> SpacingChildNames;
        public static ConfigEntry<string> ParameterMode;
        public static ConfigEntry<string> ParameterDenyPatterns;
        public static ConfigEntry<string> ParameterDenyActions;
        public static ConfigEntry<int> ParameterCommitEpisodes;
        public static ConfigEntry<float> ParameterSettleMargin;
        public static ConfigEntry<int> MaxLearnedParameters;
        public static ConfigEntry<int> MaxParametersPerState;
        public static ConfigEntry<float> ChainCreditWeight;
        public static ConfigEntry<float> ResponseModelWeight;
        public static ConfigEntry<float> PlayerModelHalfLifeSeconds;
        public static ConfigEntry<float> WarmupMultiplier;
        public static ConfigEntry<float> WarmupEpisodes;
        public static ConfigEntry<float> OutOfBoundsRadius;
        public static ConfigEntry<float> OutOfBoundsPenalty;
        public static ConfigEntry<bool> DebugFreeMove;
        public static ConfigEntry<float> DebugFreeMoveSpeed;
        public static ConfigEntry<bool> EncounterScaling;
        public static ConfigEntry<float> ScalingBaselineHp;
        public static ConfigEntry<float> MaxEncounterScale;
        public static ConfigEntry<bool> LearnHealContext;
        public static ConfigEntry<float> HealInterruptBonus;
        public static ConfigEntry<float> HealCompletedPenalty;
        public static ConfigEntry<string> LearnedParameters;

        public static bool ExploreParameters =>
            ParameterMode != null && string.Equals(ParameterMode.Value, "Explore", System.StringComparison.OrdinalIgnoreCase);
        public static ConfigEntry<string> ParameterCandidates;
        public static ConfigEntry<bool> AllowSpeedParameters;
        public static ConfigEntry<float> HitRateThreshold;
        public static ConfigEntry<float> HitRateMinEpisodes;
        public static ConfigEntry<float> MinEpisodeSeconds;

        // Persistence
        public static ConfigEntry<bool> PersistToDisk;
        public static ConfigEntry<float> SaveIntervalSeconds;

        // Debug
        public static ConfigEntry<bool> VerboseLogging;
        public static ConfigEntry<bool> DumpFsmStructure;
        public static ConfigEntry<bool> LogAdaptationSummary;

        public static void Init(ConfigFile cfg)
        {
            Enabled = cfg.Bind("General", "Enabled", true,
                "Master switch. When off, enemies behave exactly as vanilla and no data is recorded.");

            TargetEnemies = cfg.Bind("General", "TargetEnemies", "MossBone Fly,Mossbone Mother",
                "Comma-separated GameObject names to adapt. Matched against the authored name with " +
                "Unity's \"(1)\"/\"(Clone)\" suffixes stripped, so \"MossBone Crawler\" will NOT match " +
                "\"MossBone Crawler Fat\" or \"MossBone Crawler Summon\".");

            AdaptAllEnemies = cfg.Bind("General", "AdaptAllEnemies", true,
                "Adapt every enemy that has a HealthManager, instead of only those named in " +
                "TargetEnemies. Each one profiles itself the first time it is met — locomotion from its " +
                "components, weapons from the shape of its own FSM — so the whole roster classifies " +
                "itself during normal play and nothing needs listing by hand.");

            ExcludeEnemies = cfg.Bind("General", "ExcludeEnemies", "",
                "Comma-separated names to leave strictly vanilla even when AdaptAllEnemies is on.");

            BossHpThreshold = cfg.Bind("General", "BossHpThreshold", 120f,
                new ConfigDescription("Max HP at or above which an enemy is treated as a boss.",
                    new AcceptableValueRange<float>(20f, 2000f)));

            AdaptBosses = cfg.Bind("General", "AdaptBosses", true,
                "Whether enemies at or above BossHpThreshold adapt. On: a boss gets exactly the same " +
                "freedom as anything else — it profiles itself, picks its own cohort, and learns from " +
                "what happens to it. BossHpThreshold then only affects how fast it learns, since a boss " +
                "is met rarely and must adapt within the one fight it gets.");

            RarityScaleWeight = cfg.Bind("Learning", "RarityScaleWeight", 1.5f,
                new ConfigDescription("How much faster a rarely-met enemy learns. An enemy encountered " +
                    "twice in a playthrough gets almost no chance to adapt at the pace that suits one met " +
                    "every other room, so the fewer encounters a type has had, the more each one counts.",
                    new AcceptableValueRange<float>(0f, 5f)));

            DumpEveryNewEnemy = cfg.Bind("Debug", "DumpEveryNewEnemy", false,
                "Write an FSM dump the first time each distinct enemy type is met. Turn on for a normal " +
                "playthrough and the dumps accumulate on their own, without hunting anything down.");

            TestingMode = cfg.Bind("Testing", "TestingMode", false,
                "Drastically raises the learning rate and drops the confidence threshold so adaptation " +
                "is visible within a handful of exchanges. Overrides LearningRate, ConfidenceSamples, " +
                "MinSamples, Authority and HalfLifeSeconds while enabled.");

            LearningRate = cfg.Bind("Learning", "LearningRate", 1.0f,
                new ConfigDescription("Weight applied to each new observation.",
                    new AcceptableValueRange<float>(0.05f, 20f)));

            ConfidenceSamples = cfg.Bind("Learning", "ConfidenceSamples", 12f,
                new ConfigDescription("Effective samples at which an enemy reaches half its maximum " +
                    "deviation from vanilla. Higher = slower, more cautious adaptation.",
                    new AcceptableValueRange<float>(0.5f, 200f)));

            MinSamples = cfg.Bind("Learning", "MinSamples", 4f,
                new ConfigDescription("Hard floor: below this many effective samples a behaviour stays " +
                    "exactly vanilla. This is what keeps early encounters looking untouched.",
                    new AcceptableValueRange<float>(0f, 100f)));

            Authority = cfg.Bind("Learning", "Authority", 0.75f,
                new ConfigDescription("Ceiling on how far learning may override vanilla, at full " +
                    "confidence. 0 = never deviate, 1 = fully trust the model.",
                    new AcceptableValueRange<float>(0f, 1f)));

            Kappa = cfg.Bind("Learning", "Kappa", 1.1f,
                new ConfigDescription("Exponential gain converting a score into a weight multiplier.",
                    new AcceptableValueRange<float>(0.1f, 4f)));

            ExplorationWeight = cfg.Bind("Learning", "ExplorationWeight", 0.4f,
                new ConfigDescription("How strongly an enemy is drawn to behaviours it has not tried " +
                    "recently (UCB1-style optimism). Zero makes the policy pure exploitation, which " +
                    "collapses onto the first attack that happens to land and never revisits the others. " +
                    "It also lets an attack that was punished long ago drift back into rotation, so an " +
                    "enemy re-tests counters the player has stopped using.",
                    new AcceptableValueRange<float>(0f, 2f)));

            MinWeightMultiplier = cfg.Bind("Learning", "MinWeightMultiplier", 0.2f,
                new ConfigDescription("An attack's weight never falls below this fraction of vanilla, " +
                    "so an enemy never entirely abandons one of its attacks.",
                    new AcceptableValueRange<float>(0.01f, 1f)));

            MaxWeightMultiplier = cfg.Bind("Learning", "MaxWeightMultiplier", 3.0f,
                new ConfigDescription("Ceiling on an attack's weight as a multiple of vanilla.",
                    new AcceptableValueRange<float>(1f, 10f)));

            HalfLifeSeconds = cfg.Bind("Learning", "HalfLifeSeconds", 900f,
                new ConfigDescription("Half-life of a single data point, in seconds of active gameplay " +
                    "(not wall-clock). Applied per data point, so old evidence fades individually " +
                    "instead of the table being wiped.",
                    new AcceptableValueRange<float>(10f, 100000f)));

            DamageNormalizationFraction = cfg.Bind("Learning", "DamageNormalizationFraction", 0.1f,
                new ConfigDescription("Fraction of an enemy's max HP that counts as being fully punished " +
                    "in one episode. Without this, damage is measured against max HP and a boss barely " +
                    "registers being hit — 5 damage to a 10 HP flier is half its life, but to a 200 HP boss " +
                    "it is 2.5%, so the punishment gradient vanishes exactly where the fight is longest. " +
                    "Normalising against a fraction instead keeps a flier and a boss on the same scale.",
                    new AcceptableValueRange<float>(0.01f, 1f)));

            AvoidPenalty = cfg.Bind("Learning", "AvoidPenalty", 0.35f,
                new ConfigDescription("Extra penalty an attack takes when the player cleanly avoids it, " +
                    "on top of any damage the enemy received.",
                    new AcceptableValueRange<float>(0f, 1f)));

            AttributeIndirectDamage = cfg.Bind("Learning", "AttributeIndirectDamage", true,
                "Credit an enemy for damage done by things it caused but does not own — falling rocks, " +
                "summoned minions, lingering projectiles. These are separate root objects, so the owner " +
                "cannot be found by walking up the hierarchy, and without this an attack that works " +
                "indirectly scores nothing while a contact attack scores every time. Attribution is by " +
                "proximity and recency, not by any rule about which enemy spawns what.");

            IndirectAttributionSeconds = cfg.Bind("Learning", "IndirectAttributionSeconds", 4f,
                new ConfigDescription("How long after an enemy's attack its indirect consequences may " +
                    "still be credited to it. Rocks take time to fall and minions take time to reach the " +
                    "player, so the credit has to outlive the state that caused it.",
                    new AcceptableValueRange<float>(0.5f, 20f)));

            IndirectAttributionRadius = cfg.Bind("Learning", "IndirectAttributionRadius", 30f,
                new ConfigDescription("How far from the damage source an enemy may be and still be " +
                    "considered its cause. Generous, because a boss can drop rocks across a whole arena.",
                    new AcceptableValueRange<float>(5f, 100f)));

            ThreatRadius = cfg.Bind("Learning", "ThreatRadius", 12f,
                new ConfigDescription("Maximum distance in world units at which an enemy is considered " +
                    "close enough for the player's dodges and whiffs to be about IT. Without this, " +
                    "every jump anywhere in the room is credited to every enemy in it.",
                    new AcceptableValueRange<float>(2f, 60f)));

            SameClassShareWeight = cfg.Bind("HiveMind", "SameClassShareWeight", 0.6f,
                new ConfigDescription("How strongly an enemy trusts pooled data from other enemies of " +
                    "its own locomotion class (flying<->flying, ground<->ground).",
                    new AcceptableValueRange<float>(0f, 1f)));

            CrossClassShareWeight = cfg.Bind("HiveMind", "CrossClassShareWeight", 0.15f,
                new ConfigDescription("How strongly an enemy trusts pooled data from the other class.",
                    new AcceptableValueRange<float>(0f, 1f)));

            UniversalShareWeight = cfg.Bind("HiveMind", "UniversalShareWeight", 0.25f,
                new ConfigDescription("Weight of the thin universal layer of player-habit data that " +
                    "every enemy type draws on.",
                    new AcceptableValueRange<float>(0f, 1f)));

            PriorStrengthCap = cfg.Bind("HiveMind", "PriorStrengthCap", 0.6f,
                new ConfigDescription("Cap on how much of the confidence threshold the shared prior may " +
                    "supply, as a fraction. Stops transfer learning from letting a freshly spawned enemy " +
                    "skip straight to full deviation.",
                    new AcceptableValueRange<float>(0f, 1f)));

            AdaptSelection = cfg.Bind("Expression", "AdaptSelection", true,
                "Re-weight which of an enemy's existing attacks it picks. Never adds new attacks: " +
                "options with a vanilla weight of zero stay at zero. Note that simple enemies such as " +
                "MossBone Fly have only one attack and no weighted branch, so this does nothing for them.");

            AdaptTiming = cfg.Bind("Expression", "AdaptTiming", true,
                "Vary the duration of existing Wait actions (attack antic, recovery) within " +
                "TimingVariance. This is the main lever on single-attack enemies.");

            AdaptSpacing = cfg.Bind("Expression", "AdaptSpacing", true,
                "Vary attack-range trigger distance within SpacingVariance by scaling the enemy's " +
                "range child objects. Movement speed is never touched.");

            TimingVariance = cfg.Bind("Expression", "TimingVariance", 0.35f,
                new ConfigDescription("Maximum fractional change to an attack's timing (0.35 = +/-35%). " +
                    "On MossBone Fly this moves the 0.600s drill antic across roughly 0.39-0.81s, which is " +
                    "a readable difference rather than a statistical one.",
                    new AcceptableValueRange<float>(0f, 0.75f)));

            SpacingVariance = cfg.Bind("Expression", "SpacingVariance", 0.25f,
                new ConfigDescription("Maximum fractional change to attack-range spacing (0.25 = +/-25%).",
                    new AcceptableValueRange<float>(0f, 0.6f)));

            ContactTurnVariance = cfg.Bind("Expression", "ContactTurnVariance", 0.5f,
                new ConfigDescription("Maximum fractional change to a contact-damage crawler's turn-trigger " +
                    "distance (0.5 = +/-50%). Larger than the others because the underlying values are tiny " +
                    "(~0.2 units) and a small fraction of them is invisible. Never touches movement speed.",
                    new AcceptableValueRange<float>(0f, 0.9f)));

            ContactThreatRadius = cfg.Bind("Expression", "ContactThreatRadius", 3.5f,
                new ConfigDescription("How close the player must be for a contact-damage enemy to count as " +
                    "threatening. Such enemies have no attack state, so proximity is the only honest signal " +
                    "that their approach is the thing being dodged or punished.",
                    new AcceptableValueRange<float>(1f, 15f)));

            ParameterMode = cfg.Bind("Expression", "ParameterMode", "Explore",
                new ConfigDescription(
                    "Explore: the enemy may learn ANY numeric parameter of its combat behaviour except " +
                    "those matching ParameterDenyPatterns. This is what gives it room to develop attacks " +
                    "nobody designed — the shape of a behaviour is its numbers, and no one decides in " +
                    "advance which of them are allowed to change.\n" +
                    "Allowlist: only the fields named in LearnedParameters. Narrower and more predictable.",
                    new AcceptableValueList<string>("Explore", "Allowlist")));

            ParameterDenyPatterns = cfg.Bind("Expression", "ParameterDenyPatterns",
                "speed,accel,volume,pitch,fade,alpha,color,colour,opacity,shake,rumble,audio,zoom,blur",
                "Substrings that disqualify a parameter from being learned in Explore mode. Two kinds: " +
                "movement fields, because changing base movement speed is ruled out by the design; and " +
                "presentation fields, because varying them changes how the game looks rather than how the " +
                "enemy fights. Everything else is fair game.");

            ParameterDenyActions = cfg.Bind("Expression", "ParameterDenyActions",
                "FloatOperator,FloatAdd,FloatSubtract,FloatMultiply,FloatDivide,FloatClamp,FloatCompare," +
                "FloatAbs,FloatSign,KeepFloatPositive,ConvertFloat,SetFloatValue,GetScale,GetPosition," +
                "GetVector,SetVector,RayCast,GetAngle,GetDistance,FloatInterpolate,FloatSwitch," +
                "Wait,FadeAudio,AudioPlay,AudioStop,ObjectJitter,CameraShake,Tk2dPlayAnimation",
                "Action types whose numeric fields are NOT treated as behavioural dials. These are " +
                "arithmetic and plumbing: operands part-way through a calculation, raycast probe lengths, " +
                "scratch values. Changing them does not alter what the enemy is trying to do, it corrupts " +
                "how the attempt is computed — a swoop that cuts short or lands in the wrong place rather " +
                "than a swoop aimed somewhere new. The rule is generic to PlayMaker, not specific to any " +
                "enemy.");

            MaxParametersPerState = cfg.Bind("Expression", "MaxParametersPerState", 3,
                new ConfigDescription("Cap on learned parameters taken from any single state. Without it " +
                    "discovery fills the whole budget from whichever state it reaches first — on Mossbone " +
                    "Mother every slot went to the swoop, so her rock and crawler attacks had no dial to " +
                    "turn at all. Spreading the budget is what gives every attack room to develop.",
                    new AcceptableValueRange<int>(1, 16)));

            ChainCreditWeight = cfg.Bind("Learning", "ChainCreditWeight", 0.6f,
                new ConfigDescription("How much of an outcome is credited back to the choice that started " +
                    "the behaviour, as well as to the behaviour that was running when it happened. An " +
                    "attack is usually a chain — antic, strike, recovery — and the player punishes it " +
                    "somewhere in the middle. Without this the decision to attack never learns anything, " +
                    "because the punishment lands on a state the decision does not weigh.",
                    new AcceptableValueRange<float>(0f, 1f)));

            ResponseModelWeight = cfg.Bind("Learning", "ResponseModelWeight", 0.5f,
                new ConfigDescription("How much an enemy's choice of attack is guided by what THIS player " +
                    "tends to do, rather than by how the attack has performed on average. At zero it only " +
                    "knows which of its attacks work; above zero it also weighs how often you reach for " +
                    "each response and how well that response beats each of its attacks — so it starts " +
                    "picking the attack you are currently worst at answering.",
                    new AcceptableValueRange<float>(0f, 1f)));

            PlayerModelHalfLifeSeconds = cfg.Bind("Learning", "PlayerModelHalfLifeSeconds", 180f,
                new ConfigDescription("Separate, much shorter half-life for everything an enemy knows " +
                    "about the PLAYER — which responses you favour, how you dodge, what punishes what. " +
                    "An enemy's knowledge of its own attacks should last; its read on you should not, or " +
                    "it walks into every fight already knowing you and there is no arc. This is what lets " +
                    "a boss start each encounter reading you fresh while still remembering itself.",
                    new AcceptableValueRange<float>(15f, 3600f)));

            WarmupMultiplier = cfg.Bind("Learning", "WarmupMultiplier", 3f,
                new ConfigDescription("How much more the first observations of an encounter count. Front-" +
                    "loading the learning is what makes a fight feel like being figured out: sharp early " +
                    "reads that settle as the enemy grows confident, instead of a flat crawl.",
                    new AcceptableValueRange<float>(1f, 10f)));

            WarmupEpisodes = cfg.Bind("Learning", "WarmupEpisodes", 8f,
                new ConfigDescription("How many episodes the warm-up boost is spread over before learning " +
                    "settles to its normal rate.",
                    new AcceptableValueRange<float>(1f, 50f)));

            OutOfBoundsRadius = cfg.Bind("Learning", "OutOfBoundsRadius", 45f,
                new ConfigDescription("How far an enemy may travel from where it started before it is " +
                    "treated as having lost control of itself. Learning to decelerate less makes a dive " +
                    "carry further, which is a real discovery worth keeping — but carried far enough it " +
                    "leaves the arena. Rather than forbidding the parameter, overshooting is made a " +
                    "consequence the enemy can learn from, and the value is snapped back for safety.",
                    new AcceptableValueRange<float>(10f, 300f)));

            OutOfBoundsPenalty = cfg.Bind("Learning", "OutOfBoundsPenalty", 1f,
                new ConfigDescription("How heavily leaving the arena counts against whatever was being " +
                    "tried at the time. High, because it is never useful.",
                    new AcceptableValueRange<float>(0f, 2f)));

            DebugFreeMove = cfg.Bind("Debug", "DebugFreeMove", true,
                "Enable the free-move toggle key for getting unstuck while testing.");

            DebugFreeMoveSpeed = cfg.Bind("Debug", "DebugFreeMoveSpeed", 20f,
                new ConfigDescription("Units per second while free-moving. Hold Shift for triple.",
                    new AcceptableValueRange<float>(2f, 100f)));

            EncounterScaling = cfg.Bind("Learning", "EncounterScaling", true,
                "Let sturdier enemies learn faster per observation. A boss is fought once by most " +
                "players but throws thirty attacks in that fight; a common enemy is met constantly but " +
                "dies in three hits. At one fixed rate the boss never deviates before it dies — the " +
                "feature would be invisible on exactly the enemies where it matters most — while the " +
                "common enemy would swing wildly. Scaling by max HP tracks both encounter rarity and " +
                "fight length without naming any enemy as a boss.");

            ScalingBaselineHp = cfg.Bind("Learning", "ScalingBaselineHp", 25f,
                new ConfigDescription("Max HP up to which an enemy learns at exactly the configured rate. " +
                    "Above it, learning speeds up in proportion; below it, nothing changes — ordinary " +
                    "enemies are met often enough that the standard pace already works.",
                    new AcceptableValueRange<float>(1f, 500f)));

            MaxEncounterScale = cfg.Bind("Learning", "MaxEncounterScale", 5f,
                new ConfigDescription("Ceiling on that speed-up, so a very high-HP enemy cannot end up " +
                    "reacting to single events.",
                    new AcceptableValueRange<float>(1f, 20f)));

            LearnHealContext = cfg.Bind("Learning", "LearnHealContext", true,
                "Treat 'the player is healing' as a distinguishable situation, so enemies keep a separate " +
                "policy for it and work out for themselves what works while the player is vulnerable. " +
                "This is perception, not instruction: nothing tells them to use a fast attack, only that " +
                "healing is a different circumstance worth learning about separately.");

            HealInterruptBonus = cfg.Bind("Learning", "HealInterruptBonus", 0.5f,
                new ConfigDescription("Extra reward for damaging the player mid-heal. Denying a heal is " +
                    "worth more than an ordinary hit, and saying so is defining the goal rather than the " +
                    "method — how to reach it is still theirs to discover.",
                    new AcceptableValueRange<float>(0f, 2f)));

            HealCompletedPenalty = cfg.Bind("Learning", "HealCompletedPenalty", 0.4f,
                new ConfigDescription("Penalty applied to whatever an enemy was doing when the player " +
                    "completed a heal within its reach. Failing to punish a heal is a failure worth " +
                    "learning from, and it is what pushes them toward whichever of their attacks can " +
                    "actually reach in time.",
                    new AcceptableValueRange<float>(0f, 2f)));

            ParameterCommitEpisodes = cfg.Bind("Expression", "ParameterCommitEpisodes", 3,
                new ConfigDescription("How many episodes an enemy sticks with a chosen parameter value " +
                    "before reconsidering. Re-rolling every single attack means no two attacks look alike " +
                    "and no value ever gets a fair trial — the enemy reads as glitching rather than " +
                    "experimenting. Committing turns a slot machine into a hypothesis it actually tests.",
                    new AcceptableValueRange<int>(1, 20)));

            ParameterSettleMargin = cfg.Bind("Expression", "ParameterSettleMargin", 0.15f,
                new ConfigDescription("How much better the authored value must be than every alternative " +
                    "before the enemy stops experimenting with that parameter and settles back to it. " +
                    "This is how it learns that a dial is not worth turning, instead of fidgeting with it " +
                    "forever.",
                    new AcceptableValueRange<float>(0f, 1f)));

            MaxLearnedParameters = cfg.Bind("Expression", "MaxLearnedParameters", 12,
                new ConfigDescription("Cap on how many parameters one enemy may learn at once. Each needs " +
                    "its own evidence, so an unbounded set would spread the samples too thin to ever " +
                    "become confident about any of them.",
                    new AcceptableValueRange<int>(1, 64)));

            LearnedParameters = cfg.Bind("Expression", "LearnedParameters", "offsetX,offsetY",
                "Comma-separated PlayMaker action field names the enemy may learn a value for. " +
                "These are continuous parameters of behaviour it already has — on MossBone Fly, " +
                "ChaseObjectV2.offsetY is how far above the player it positions before diving. " +
                "Learning that value downward makes it approach level and ram you, which reads as a " +
                "new charge attack while adding no new state or action. Movement fields (speedMax, " +
                "accelerationForce) are refused unless AllowSpeedParameters is on.");

            ParameterCandidates = cfg.Bind("Expression", "ParameterCandidates", "0.25,0.5,1,1.5",
                "Candidate multipliers of the authored value that the enemy may choose between. " +
                "1 must be present — it is the vanilla level the policy falls back to before it is " +
                "confident, and the anchor everything is measured against.");

            AllowSpeedParameters = cfg.Bind("Expression", "AllowSpeedParameters", false,
                "Allow speedMax / accelerationForce to be learned too. Off by default because it " +
                "changes base movement speed, which the design explicitly rules out.");

            ContactEpisodeSeconds = cfg.Bind("Expression", "ContactEpisodeSeconds", 2f,
                new ConfigDescription("How often a contact-damage enemy's episode is closed and scored. " +
                    "Such enemies never change state — a crawler crawls until it dies — so without a " +
                    "time window their outcome would only ever be recorded at the moment of death.",
                    new AcceptableValueRange<float>(0.5f, 10f)));

            MinEpisodeSeconds = cfg.Bind("Learning", "MinEpisodeSeconds", 0.15f,
                new ConfigDescription("Episodes shorter than this are not scored. Some FSM states last a " +
                    "single frame (MossBone Fly's 'Bonk Frame' is one NextFrameEvent), and crediting them " +
                    "with damage that really belonged to the surrounding behaviour is how the system starts " +
                    "inventing attacks that do not exist.",
                    new AcceptableValueRange<float>(0f, 1f)));

            HitRateMinEpisodes = cfg.Bind("Learning", "HitRateMinEpisodes", 3f,
                new ConfigDescription("Real episodes (not learning-rate-weighted samples) a behaviour must " +
                    "have before it can promote itself to an attack. Guards against a single lucky hit " +
                    "convincing the enemy that some incidental state is a weapon.",
                    new AcceptableValueRange<float>(1f, 30f)));

            HitRateThreshold = cfg.Bind("Learning", "HitRateThreshold", 0.15f,
                new ConfigDescription("Fraction of episodes a behaviour must have damaged the player " +
                    "before the mod treats it as an attack in its own right. This is how it discovers " +
                    "which states are threatening rather than being told: a flier whose approach keeps " +
                    "connecting gets promoted to an attack by its own data.",
                    new AcceptableValueRange<float>(0.02f, 1f)));

            SpacingChildNames = cfg.Bind("Expression", "SpacingChildNames", "Attack Range",
                "Comma-separated child GameObject names treated as attack-range volumes. " +
                "\"Alert Range\" is deliberately excluded by default: scaling it changes aggro " +
                "distance rather than combat spacing.");

            PersistToDisk = cfg.Bind("Persistence", "PersistToDisk", true,
                "Persist learned tables to AdaptiveEnemies/hive.json so learning carries across " +
                "sessions. Turn off to have the hive start fresh every run.");

            SaveIntervalSeconds = cfg.Bind("Persistence", "SaveIntervalSeconds", 30f,
                new ConfigDescription("How often the hive is flushed to disk while dirty.",
                    new AcceptableValueRange<float>(5f, 600f)));

            VerboseLogging = cfg.Bind("Debug", "VerboseLogging", false,
                "Log every observation and weight change. Noisy; useful when tuning.");

            DumpFsmStructure = cfg.Bind("Debug", "DumpFsmStructure", false,
                "On first sight of each target enemy, write its full FSM structure (states, actions, " +
                "weights, wait times) to AdaptiveEnemies/fsm-dump/.");

            LogAdaptationSummary = cfg.Bind("Debug", "LogAdaptationSummary", true,
                "Every 15s of combat, log what each enemy type has actually learned and what it is " +
                "currently doing differently from vanilla. This is the quickest way to see whether " +
                "adaptation is happening at all.");
        }

        // Effective values, with testing mode folded in.

        public static bool Testing => TestingMode != null && TestingMode.Value;

        public static double EffLearningRate => Testing ? 6.0 : LearningRate.Value;
        public static double EffConfidenceSamples => Testing ? 1.5 : ConfidenceSamples.Value;
        public static double EffMinSamples => Testing ? 1.0 : MinSamples.Value;
        public static double EffAuthority => Testing ? 1.0 : Authority.Value;
        public static double EffHalfLife => Testing ? 240.0 : HalfLifeSeconds.Value;
    }
}
