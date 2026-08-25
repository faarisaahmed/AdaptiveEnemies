using System;

namespace AdaptiveEnemies.Learning
{
    /// <summary>
    /// One decaying evidence accumulator.
    /// <para>
    /// Decay is exponential and applied lazily on every touch, which is mathematically
    /// identical to decaying each individual data point from its own insertion time —
    /// a point added at t contributes <c>v * 2^(-(now-t)/halfLife)</c> today. That
    /// satisfies "decay per data point, not a wipe of the whole table" while staying
    /// O(1) in both storage and update cost.
    /// </para>
    /// <see cref="N"/> decays alongside <see cref="Value"/>, so confidence fades with
    /// the evidence and an enemy genuinely forgets a counter it no longer sees.
    /// </summary>
    [Serializable]
    public class StatCell
    {
        public double Value;
        public double N;
        public double LastT;

        private const double Ln2 = 0.6931471805599453;

        public StatCell() { }
        public StatCell(double now) { LastT = now; }

        public void Decay(double now, double halfLife)
        {
            if (halfLife <= 0.0) { LastT = now; return; }

            double dt = now - LastT;
            if (dt <= 0.0)
            {
                if (dt < 0.0) LastT = now; // clock reset; re-anchor
                return;
            }

            double f = Math.Exp(-dt * Ln2 / halfLife);
            Value *= f;
            N *= f;
            LastT = now;

            if (N < 1e-6) { N = 0.0; Value = 0.0; }
        }

        public void Add(double now, double halfLife, double value, double weight)
        {
            Decay(now, halfLife);
            Value += value * weight;
            N += weight;
        }

        public double Mean => N > 1e-6 ? Value / N : 0.0;

        public bool IsEmpty => N <= 1e-6;

        /// <summary>Seeds this cell with pseudo-counts representing a transferred prior.</summary>
        public void SeedPrior(double now, double mean, double strength)
        {
            LastT = now;
            N = strength;
            Value = mean * strength;
        }
    }
}
