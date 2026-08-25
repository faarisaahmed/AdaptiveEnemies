using System;
using System.Collections.Generic;

namespace AdaptiveEnemies.Learning
{
    /// <summary>The evidence transferred into a fresh encounter for one behaviour.</summary>
    public struct Prior
    {
        public double Mean;
        public double Strength;
    }

    /// <summary>
    /// Turns accumulated evidence into behaviour adjustments.
    ///
    /// <para>Sign convention: positive means the behaviour is working for the enemy
    /// (it damaged the player); negative means the player is reliably punishing it.</para>
    /// </summary>
    public static class Policy
    {
        /// <summary>
        /// Builds the encounter-start prior by blending the enemy's own private memory
        /// with similarity-weighted class knowledge and the universal habit layer.
        /// </summary>
        public static Prior ComputePrior(HiveMind hive, string type, string signature, EnemyClass cls,
            string option, Archetype arch)
        {
            var topic = TaxonomyUtil.TopicOf(arch);
            var other = cls == EnemyClass.Ground ? EnemyClass.Flying : EnemyClass.Ground;

            double confK = ModConfig.EffConfidenceSamples;

            // The enemy type's own history. Not "shared", so it carries across sessions
            // at close to full strength — this is what makes persistence visible.
            double privMean = 0.0, privStrength = 0.0;
            if (hive.Sample(HiveMind.PrivateKey(type, option), out var pm, out var pn))
            {
                privMean = pm;
                privStrength = Math.Min(pn, confK * 1.5);
            }

            // Similarity-weighted transfer from the wider hive.
            double sharedNum = 0.0, sharedDen = 0.0;

            // Enemies built exactly like this one: the most relevant cohort there is.
            Accumulate(hive, HiveMind.ClassKey(signature, topic, arch),
                ModConfig.SameClassShareWeight.Value * TaxonomyUtil.TopicAffinity(cls, topic),
                ref sharedNum, ref sharedDen);

            // Everything that moves the same way, whatever it fights with. Broader and
            // trusted less, but it stops an unusual build starting from nothing.
            Accumulate(hive, HiveMind.ClassKey(cls.ToString(), topic, arch),
                ModConfig.SameClassShareWeight.Value * 0.5 * TaxonomyUtil.TopicAffinity(cls, topic),
                ref sharedNum, ref sharedDen);

            Accumulate(hive, HiveMind.ClassKey(other.ToString(), topic, arch),
                ModConfig.CrossClassShareWeight.Value * TaxonomyUtil.TopicAffinity(other, topic),
                ref sharedNum, ref sharedDen);

            Accumulate(hive, HiveMind.UniversalKey("arch|" + arch),
                ModConfig.UniversalShareWeight.Value,
                ref sharedNum, ref sharedDen);

            double sharedMean = sharedDen > 1e-6 ? sharedNum / sharedDen : 0.0;
            double sharedStrength = Math.Min(sharedDen, confK * ModConfig.PriorStrengthCap.Value);

            double totalStrength = privStrength + sharedStrength;
            if (totalStrength <= 1e-6) return new Prior { Mean = 0.0, Strength = 0.0 };

            double mean = (privMean * privStrength + sharedMean * sharedStrength) / totalStrength;

            return new Prior
            {
                Mean = Clamp(mean, -1.0, 1.0),
                Strength = Math.Min(totalStrength, confK * 2.0),
            };
        }

        private static void Accumulate(HiveMind hive, string key, double weight, ref double num, ref double den)
        {
            if (weight <= 0.0) return;
            if (!hive.Sample(key, out var mean, out var n)) return;

            // Cap a single source's influence so one over-sampled pool cannot dominate.
            double evidence = Math.Min(n, ModConfig.EffConfidenceSamples * 2.0) * weight;
            num += mean * evidence;
            den += evidence;
        }

        /// <summary>
        /// Confidence in [0,1): how far a behaviour may move from vanilla. Below
        /// MinSamples it is pinned to zero, which keeps early encounters stock.
        /// </summary>
        /// <param name="scale">
        /// Per-enemy learning speed-up. Above 1 both lowers the evidence needed for
        /// confidence and the floor before any deviation, so a rarely-fought enemy can
        /// adapt inside a single encounter.
        /// </param>
        public static double Confidence(double n, double scale = 1.0)
        {
            if (scale <= 0.0) scale = 1.0;

            if (n < ModConfig.EffMinSamples / scale) return 0.0;
            double k = ModConfig.EffConfidenceSamples / scale;
            return n / (n + k);
        }

        /// <summary>
        /// Multiplier applied to an attack's vanilla weight. Returns exactly 1.0 when
        /// the model has no authority yet, and is clamped so no attack is ever fully
        /// abandoned or allowed to crowd out the rest.
        /// </summary>
        public static double WeightMultiplier(double score, double n, double totalN, double scale = 1.0)
        {
            double authority = Confidence(n, scale) * ModConfig.EffAuthority;

            double effective = Clamp(score + Exploration(n, totalN), -1.0, 1.0);

            // An option with no evidence of its own still deserves to be tried, so it is
            // pushed by the exploration term even when its own confidence is zero.
            if (authority <= 0.0)
            {
                double curiosity = Exploration(n, totalN) * ModConfig.EffAuthority;
                if (curiosity <= 0.0) return 1.0;
                return Clamp(Math.Exp(ModConfig.Kappa.Value * curiosity),
                    ModConfig.MinWeightMultiplier.Value, ModConfig.MaxWeightMultiplier.Value);
            }

            double mult = Math.Exp(ModConfig.Kappa.Value * effective * authority);
            return Clamp(mult, ModConfig.MinWeightMultiplier.Value, ModConfig.MaxWeightMultiplier.Value);
        }

        /// <summary>
        /// Optimism under uncertainty, in the UCB1 style: a bonus that shrinks as an
        /// option accumulates evidence of its own.
        ///
        /// <para>Without this the policy is pure exploitation and collapses onto the first
        /// thing that happens to work — one landed swoop and the boss swoops forever,
        /// because the attacks she never retried have no evidence to argue with. The bonus
        /// keeps rarely-sampled options in contention, and has a second use: an attack that
        /// was punished long ago drifts back into rotation instead of being written off
        /// permanently, which is what lets an enemy re-test a counter the player has
        /// stopped using.</para>
        /// </summary>
        public static double Exploration(double n, double totalN)
        {
            double w = ModConfig.ExplorationWeight.Value;
            if (w <= 0.0) return 0.0;

            // Scale counts back to real episodes so the bonus does not depend on how
            // aggressively the learning rate is configured.
            double rate = Math.Max(1e-6, ModConfig.EffLearningRate);
            double own = Math.Max(0.0, n / rate);
            double all = Math.Max(1.0, totalN / rate);

            return w * Math.Sqrt(Math.Log(all + 1.0) / (own + 1.0));
        }

        /// <summary>
        /// Scales adjusted weights back onto the vanilla total, so the FSM's weighted
        /// picker sees the magnitude it was authored with. Attacks whose vanilla weight
        /// is zero stay zero — the mod re-weights existing attacks and never introduces
        /// one the enemy did not have.
        /// </summary>
        public static void Renormalize(float[] vanilla, double[] adjusted)
        {
            double vanillaSum = 0.0, adjustedSum = 0.0;
            for (int i = 0; i < vanilla.Length; i++)
            {
                if (vanilla[i] <= 0f) { adjusted[i] = 0.0; continue; }
                vanillaSum += vanilla[i];
                adjustedSum += adjusted[i];
            }

            if (adjustedSum <= 1e-9 || vanillaSum <= 1e-9)
            {
                for (int i = 0; i < vanilla.Length; i++) adjusted[i] = vanilla[i];
                return;
            }

            double scale = vanillaSum / adjustedSum;
            for (int i = 0; i < adjusted.Length; i++) adjusted[i] *= scale;
        }

        /// <summary>Scores one completed attack episode from the enemy's point of view. Roughly [-1, +1].</summary>
        public static double ScoreEpisode(bool playerWasHit, double damageTaken, double referenceHp, AvoidMethod avoid)
        {
            double reward = playerWasHit ? 1.0 : 0.0;

            // Measure damage against a fraction of max HP rather than max HP itself, so the
            // signal means the same thing on a 10 HP flier and a 200 HP boss. Against raw
            // max HP a boss's punishment term is near zero and only the avoid penalty
            // survives — losing the gradient in the fight that has the most data to give.
            double punish = 0.0;
            if (referenceHp > 0.0)
            {
                double scale = referenceHp * Clamp(ModConfig.DamageNormalizationFraction.Value, 0.01, 1.0);
                punish += Clamp(damageTaken / Math.Max(1e-6, scale), 0.0, 1.0);
            }

            if (avoid != AvoidMethod.NotAvoided)
                punish += ModConfig.AvoidPenalty.Value;

            return Clamp(reward - punish, -1.0, 1.0);
        }

        /// <summary>
        /// Writes one episode's outcome into every layer it belongs to. The single place
        /// an observation fans out into the hive.
        /// </summary>
        public static void RecordEpisode(
            HiveMind hive, string type, string signature, EnemyClass cls, string option, Archetype arch,
            double score, AvoidMethod avoid, IEnumerable<PlayerAction> responses, double warmup = 1.0)
        {
            double w = ModConfig.EffLearningRate * warmup;
            var topic = TaxonomyUtil.TopicOf(arch);

            hive.Record(HiveMind.PrivateKey(type, option), score, w);

            // Contribute to both cohorts: the exact build, and the broader locomotion.
            hive.Record(HiveMind.ClassKey(signature, topic, arch), score, w);
            hive.Record(HiveMind.ClassKey(cls.ToString(), topic, arch), score, w);
            hive.Record(HiveMind.UniversalKey("arch|" + arch), score, w);
            hive.Record(HiveMind.UniversalKey("avoid|" + avoid), avoid == AvoidMethod.NotAvoided ? 0.0 : 1.0, w);

            if (responses == null) return;

            foreach (var r in responses)
            {
                if (r == PlayerAction.None) continue;

                // Which specific response punished this behaviour, privately and pooled.
                hive.Record(HiveMind.PrivateResponseKey(type, option, r), score, w);
                hive.Record(HiveMind.ClassResponseKey(signature, topic, arch, r), score, w);

                // Universal habit layer: how well this response works for the player overall.
                hive.Record(HiveMind.UniversalKey("resp|" + r), -score, w);
            }
        }

        /// <summary>
        /// Builds a selection distribution over the candidate levels of a learned
        /// parameter, writing normalised probabilities into <paramref name="weights"/>.
        ///
        /// <para>Softmax over learned value, then blended toward a point mass on the
        /// vanilla level by confidence. With no evidence the enemy sits exactly on the
        /// authored value; as evidence accumulates it explores, and settles on whatever
        /// actually works. This is the same weighted-policy machinery used for attack
        /// selection, pointed at a continuous parameter instead of an event — which is
        /// what allows new behaviour to emerge without any of it being written down.</para>
        /// </summary>
        public static void LevelWeights(double[] means, double[] ns, int vanillaIndex, double[] weights, double scale = 1.0)
        {
            int n = weights.Length;

            double totalN = 0.0;
            for (int i = 0; i < n; i++) totalN += ns[i];

            double authority = Confidence(totalN, scale) * ModConfig.EffAuthority;

            double sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                double m = ns[i] > 1e-6 ? Clamp(means[i], -1.0, 1.0) : 0.0;

                // Same optimism as attack selection: a level that has not been tried
                // recently stays in contention instead of being frozen out by whichever
                // one happened to work first.
                m = Clamp(m + Exploration(ns[i], totalN), -1.0, 1.0);

                weights[i] = Math.Exp(ModConfig.Kappa.Value * m * authority);
                sum += weights[i];
            }

            if (sum <= 1e-9)
            {
                for (int i = 0; i < n; i++) weights[i] = i == vanillaIndex ? 1.0 : 0.0;
                return;
            }

            for (int i = 0; i < n; i++)
            {
                double learned = weights[i] / sum;
                double anchor = i == vanillaIndex ? 1.0 : 0.0;
                weights[i] = learned * authority + anchor * (1.0 - authority);
            }
        }

        /// <summary>
        /// Maps how much pressure a behaviour is under, in [0,1], onto how strongly the
        /// enemy should react.
        ///
        /// <para>This exists because the obvious formulation — multiplying variance by
        /// confidence by pressure — compounds three sub-1 factors and collapses to
        /// nothing. In the first working play session, 89% confidence against a
        /// consistently-dodged attack produced a 5% timing change, roughly 30ms on a
        /// 0.6s antic: mathematically correct and completely imperceptible. The square
        /// root keeps zero at zero while lifting the broad middle, so an attack that is
        /// being punished about a third of the time still visibly reacts.</para>
        /// </summary>
        public static double Responsiveness(double pressure) => Math.Sqrt(Clamp(pressure, 0.0, 1.0));

        /// <summary>
        /// Adjusts an attack's value by what THIS player actually does.
        ///
        /// <para>The enemy already records which response punished which attack. Until now
        /// nothing read those tables, so it could notice "my swoop keeps failing" but never
        /// "he always jumps, and jumping beats the swoop." This weighs each response by how
        /// often the player currently reaches for it, against how well that response has
        /// beaten this particular attack — so the enemy drifts toward whatever you are
        /// worst at answering right now.</para>
        ///
        /// <para>Both halves fade on the short player-model half-life, so a read has to be
        /// re-earned each fight rather than carried in from the last one.</para>
        /// </summary>
        public static double ResponseAdjusted(HiveMind hive, string type, string option, double baseScore)
        {
            double w = ModConfig.ResponseModelWeight.Value;
            if (w <= 0.0 || hive == null) return baseScore;

            double num = 0.0, den = 0.0;

            foreach (PlayerAction r in Enum.GetValues(typeof(PlayerAction)))
            {
                if (r == PlayerAction.None) continue;

                // How often the player currently reaches for this response.
                if (!hive.Sample(HiveMind.UniversalKey("resp|" + r), out _, out var freq)) continue;
                if (freq <= 1e-6) continue;

                // How this attack has fared when they did.
                if (!hive.Sample(HiveMind.PrivateResponseKey(type, option, r), out var outcome, out var seen)) continue;
                if (seen <= 1e-6) continue;

                num += outcome * freq;
                den += freq;
            }

            if (den <= 1e-6) return baseScore;

            double predicted = Clamp(num / den, -1.0, 1.0);
            return Clamp(baseScore * (1.0 - w) + predicted * w, -1.0, 1.0);
        }

        /// <summary>
        /// Front-loads learning within an encounter: the first observations count for more,
        /// settling to the normal rate as evidence accumulates. A fight should feel like
        /// being read quickly and then held, not like a flat crawl.
        /// </summary>
        public static double WarmupWeight(double episodesSoFar)
        {
            double boost = ModConfig.WarmupMultiplier.Value;
            if (boost <= 1.0) return 1.0;

            double span = Math.Max(1.0, ModConfig.WarmupEpisodes.Value);
            return 1.0 + (boost - 1.0) / (1.0 + episodesSoFar / span);
        }

        /// <summary>
        /// Learning speed-up for an enemy of the given max HP. HP stands in for two things
        /// at once: how rarely the enemy is met, and how many attacks one encounter yields.
        /// </summary>
        public static double EncounterScale(double referenceHp, double timesSeen = 0.0)
        {
            if (!ModConfig.EncounterScaling.Value) return 1.0;

            double baseline = Math.Max(1.0, ModConfig.ScalingBaselineHp.Value);
            double durability = Math.Max(1.0, referenceHp / baseline);

            // Rarity. An enemy met twice in a playthrough never gets the repetitions that
            // suit one met every other room, so the fewer encounters a type has had, the
            // more each one counts. This fades as the type becomes familiar.
            double rarity = 1.0 + ModConfig.RarityScaleWeight.Value / (1.0 + timesSeen / 10.0);

            return Clamp(durability * rarity, 1.0, ModConfig.MaxEncounterScale.Value);
        }

        public static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
