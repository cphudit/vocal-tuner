namespace VocalTuner.Coach;

/// <summary>
/// Key of the vocal (Krumhansl–Kessler profiles on the sung pitch classes), notes that keep falling outside it,
/// and — when music is available — the music's tuning and key, and whether the whole vocal is shifted against it.
/// </summary>
public static class KeyCheck
{
    static readonly double[] Maj = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
    static readonly double[] Min = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];
    static readonly int[] MajorScale = [0, 2, 4, 5, 7, 9, 11];
    static readonly int[] MinorScale = [0, 2, 3, 5, 7, 8, 10, 11];   // natural minor + raised 7th

    public sealed class Music
    {
        public double[] Chroma = new double[12];
        public double Tuning, TuningR;
        public int Frames;
    }

    public static (int tonic, bool minor, double r) Estimate(double[] pc)
    {
        int bt = 0; bool bm = false; double br = double.NegativeInfinity;
        for (int t = 0; t < 12; t++)
            for (int mode = 0; mode < 2; mode++)
            {
                var prof = mode == 0 ? Maj : Min;
                var rot = new double[12];
                for (int k = 0; k < 12; k++) rot[k] = prof[((k - t) % 12 + 12) % 12];
                double r = Sig.Pearson(pc, rot);
                if (r > br) { br = r; bt = t; bm = mode == 1; }
            }
        return (bt, bm, br);
    }

    /// <summary>Analyses the backing track, or the music heard between phrases, and sets the tuning reference.</summary>
    public static Music? Tuning(CoachResult r, float[] voice, Frames f, float[]? backing)
    {
        Music? mu = null;
        if (backing != null && backing.Length > Sig.Rate * 5) { mu = Analyze(backing, null); r.Key.BackFrom = "track"; }
        else if (r.Music.Detected)
        {
            // only where nobody is singing (≥ 0.2 s away from voiced frames)
            var dist = new int[f.Count];
            int last = -1_000_000;
            for (int i = 0; i < f.Count; i++) { if (!float.IsNaN(f.Midi[i])) last = i; dist[i] = i - last; }
            last = 1_000_000;
            for (int i = f.Count - 1; i >= 0; i--) { if (!float.IsNaN(f.Midi[i])) last = i; dist[i] = Math.Min(dist[i], last - i); }
            int away = (int)Math.Round(0.3 / f.Hop);
            mu = Analyze(voice, t => { int i = f.Index(t); return i >= 0 && i < f.Count && dist[i] >= away; });
            r.Key.BackFrom = "recording";
        }
        if (mu != null && mu.Frames >= 20 && mu.TuningR >= 0.25 && Math.Abs(mu.Tuning) >= 5)
        {
            r.M.RefCents = Math.Round(mu.Tuning);
            r.M.RefFrom = backing != null ? "backing" : "recording";
        }
        return mu;
    }

    public static void Apply(CoachResult r, Music? mu)
    {
        var k = r.Key;
        var v = new double[12];
        foreach (var n in r.Notes) v[((n.Target % 12) + 12) % 12] += n.Dur;
        if (r.Notes.Count >= 8)
        {
            (k.Tonic, k.Minor, k.Conf) = Estimate(v);
            if (k.Conf >= 0.6)
            {
                var scale = k.Minor ? MinorScale : MajorScale;
                foreach (var g in r.Notes.Where(n => !scale.Contains((((n.Target - k.Tonic) % 12) + 12) % 12)).GroupBy(n => ((n.Target % 12) + 12) % 12))
                {
                    var list = g.ToList();
                    if (list.Count < 3 || list.Count < r.Notes.Count * 0.05) continue;
                    k.OutOfKey.Add(new OutNote { Pc = g.Key, Count = list.Count, Times = list.Select(n => Math.Round(n.T0, 2)).ToList() });
                    foreach (var n in list) n.Flags += "K";
                }
            }
        }

        if (mu == null || mu.Frames < 20) return;
        (k.BackTonic, k.BackMinor, k.BackConf) = Estimate(mu.Chroma);
        if (r.Notes.Count < 8) return;
        double Corr(int s)
        {
            var rot = new double[12];
            for (int i = 0; i < 12; i++) rot[i] = mu.Chroma[((i - s) % 12 + 12) % 12];
            return Sig.Pearson(v, rot);
        }
        k.ZeroCorr = Corr(0);
        int bs = 0; double bc = k.ZeroCorr;
        foreach (int s in new[] { -2, -1, 1, 2 }) { double cc = Corr(s); if (cc > bc) { bc = cc; bs = s; } }
        k.ShiftCorr = bc;
        if (bs != 0 && bc >= 0.6 && bc - k.ZeroCorr >= 0.2) k.Shift = bs;
    }

    /// <summary>Chroma and tuning of music (4096-point STFT, no overlap). <paramref name="include"/> picks frames by time.</summary>
    static Music Analyze(float[] x, Func<double, bool>? include)
    {
        const int N = 4096;
        int frames = x.Length / N;
        double binHz = (double)Sig.Rate / N;
        var mags = new double[frames][];
        Parallel.For(0, frames, i =>
        {
            double t = (i * N + N / 2.0) / Sig.Rate;
            if (include != null && !include(t)) return;
            var re = new double[N]; var im = new double[N]; var w = Sig.Hann(N);
            double e = 0;
            for (int k = 0; k < N; k++) { re[k] = x[i * N + k] * w[k]; e += re[k] * re[k]; }
            if (e < 1e-6) return;
            Sig.Fft(re, im);
            var mag = new double[N / 2 + 1];
            for (int k = 0; k <= N / 2; k++) mag[k] = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            mags[i] = mag;
        });
        var mu = new Music();
        // tuning: circular mean of the deviation of strong spectral peaks from the equal-tempered grid
        double C = 0, S = 0, W = 0;
        int k0 = (int)(150 / binHz), k1 = (int)(2000 / binHz);
        foreach (var mag in mags)
        {
            if (mag == null) continue;
            mu.Frames++;
            double max = 0;
            for (int k = k0; k <= k1; k++) max = Math.Max(max, mag[k]);
            for (int k = k0; k <= k1; k++)
            {
                double m = mag[k];
                if (m < max * 0.1 || m <= mag[k - 1] || m < mag[k + 1]) continue;
                double l1 = Math.Log(mag[k - 1] + 1e-12), l2 = Math.Log(m + 1e-12), l3 = Math.Log(mag[k + 1] + 1e-12);
                double d = l1 - 2 * l2 + l3, delta = d < 0 ? 0.5 * (l1 - l3) / d : 0;
                double fr = (k + delta) * binHz;
                double midi = 69 + 12 * Math.Log2(fr / 440);
                double th = 2 * Math.PI * (midi - Math.Round(midi));
                C += m * Math.Cos(th); S += m * Math.Sin(th); W += m;
            }
        }
        if (W > 0) { mu.Tuning = Math.Atan2(S, C) / (2 * Math.PI) * 100; mu.TuningR = Math.Sqrt(C * C + S * S) / W; }
        double tun = mu.TuningR >= 0.25 ? mu.Tuning / 100 : 0;
        int c0 = (int)(110 / binHz), c1 = (int)(2000 / binHz);
        foreach (var mag in mags)
        {
            if (mag == null) continue;
            for (int k = Math.Max(1, c0); k <= c1; k++)
            {
                double midi = 69 + 12 * Math.Log2(k * binHz / 440) - tun;
                mu.Chroma[(((int)Math.Round(midi)) % 12 + 12) % 12] += mag[k];
            }
        }
        return mu;
    }
}
