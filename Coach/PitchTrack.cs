namespace VocalTuner.Coach;

/// <summary>Per-frame measurements of the voice (10 ms hop).</summary>
public sealed class Frames
{
    public const int N = 1024, HopSamples = 220;
    public double Hop => (double)HopSamples / Sig.Rate;
    public int Count;
    public float[] Midi = [];      // NaN = unvoiced
    public float[] Smooth = [];    // ±70 ms moving average inside voiced runs (removes most vibrato)
    public float[] Clar = [];      // NSDF peak (periodicity)
    public float[] Db = [];        // frame level, dB re full scale (after normalisation)
    public float[] Centroid = [];  // Hz, voiced frames
    public float[] Cpp = [];       // dB, voiced frames
    public float[] Flat = [];      // spectral flatness, unvoiced frames that are loud enough
    public float[] Low = [];       // share of energy below 90 Hz, voiced frames
    public double Thr;             // level threshold used (dB)

    public double T(int i) => i * Hop + (double)N / 2 / Sig.Rate;
    public int Index(double t) => (int)Math.Round((t - (double)N / 2 / Sig.Rate) / Hop);
}

/// <summary>Pitch tracking (McLeod NSDF, the same method as the live tuner) and spectral features per frame.</summary>
public static class PitchTrack
{
    public static Frames Analyze(float[] x, CoachConfig c)
    {
        const int N = Frames.N, H = Frames.HopSamples;
        int count = Math.Max(0, (x.Length - N) / H + 1);
        var f = new Frames
        {
            Count = count, Midi = new float[count], Smooth = new float[count], Clar = new float[count], Db = new float[count],
            Centroid = new float[count], Cpp = new float[count], Flat = new float[count], Low = new float[count],
        };
        Array.Fill(f.Midi, float.NaN); Array.Fill(f.Smooth, float.NaN); Array.Fill(f.Centroid, float.NaN);
        Array.Fill(f.Cpp, float.NaN); Array.Fill(f.Flat, float.NaN); Array.Fill(f.Low, float.NaN);
        if (count == 0) return f;

        var rms = new double[count];
        Parallel.For(0, count, i =>
        {
            double s = 0;
            for (int k = 0; k < N; k++) { double v = x[i * H + k]; s += v * v; }
            rms[i] = Math.Sqrt(s / N);
            f.Db[i] = (float)(20 * Math.Log10(rms[i] + 1e-9));
        });
        var sorted = rms.OrderBy(v => v).ToArray();
        double noise = sorted[(int)(count * 0.10)], loud = sorted[(int)(count * 0.99)];
        double thr = Math.Max(Math.Max(loud * 0.02, noise * 2.5), 1e-4);
        f.Thr = 20 * Math.Log10(thr);

        Parallel.For(0, count, () => (new Mpm.Workspace(N), new double[N], new double[N]), (i, _, st) =>
        {
            var (ws, re, im) = st;
            var span = x.AsSpan(i * H, N);
            bool loudEnough = rms[i] >= thr;
            double peak = 0, hz = double.NaN;
            if (loudEnough) hz = Mpm.Detect(span, Sig.Rate, 0, c.Clarity, ws, out peak);
            f.Clar[i] = (float)peak;
            if (!double.IsNaN(hz)) f.Midi[i] = (float)(69 + 12 * Math.Log2(hz / 440.0));
            if (rms[i] < thr * 0.3) return st;   // too quiet for spectra

            var w = Sig.Hann(N);
            for (int k = 0; k < N; k++) { re[k] = span[k] * w[k]; im[k] = 0; }
            Sig.Fft(re, im);
            double binHz = (double)Sig.Rate / N;
            if (!double.IsNaN(hz)) Voiced(f, i, re, im, binHz, hz);
            else Unvoiced(f, i, re, im, binHz);
            return st;
        }, _ => { });

        Clean(f, c);
        return f;
    }

    static void Voiced(Frames f, int i, double[] re, double[] im, double binHz, double hz)
    {
        const int N = Frames.N;
        var mag = new double[N / 2 + 1];
        double num = 0, den = 0, low = 0, tot = 0;
        for (int k = 0; k <= N / 2; k++)
        {
            double m = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            mag[k] = m;
            double fr = k * binHz;
            if (fr >= 80 && fr <= 8000) { num += fr * m; den += m; }
            double p = m * m;
            tot += p; if (fr < 90) low += p;
        }
        f.Centroid[i] = den > 0 ? (float)(num / den) : float.NaN;
        f.Low[i] = tot > 0 ? (float)(low / tot) : 0;

        // cepstral peak prominence: real cepstrum of the dB spectrum, peak near the pitch period vs a regression line
        var cr = new double[N]; var ci = new double[N];
        for (int k = 0; k < N; k++)
        {
            int kk = k <= N / 2 ? k : N - k;
            cr[k] = 20 * Math.Log10(mag[kk] + 1e-9);
        }
        Sig.Fft(cr, ci, inverse: true);
        int q0 = Sig.Rate / 1000, q1 = Math.Min(N / 2 - 1, Sig.Rate / 60);
        double sx = 0, sy = 0, sxx = 0, sxy = 0; int nq = 0;
        for (int q = q0; q <= q1; q++) { double v = cr[q]; sx += q; sy += v; sxx += q * q; sxy += q * v; nq++; }
        double b = (nq * sxy - sx * sy) / (nq * sxx - sx * sx), a = (sy - b * sx) / nq;
        double period = Sig.Rate / hz;
        int lo = Math.Max(q0, (int)(period * 0.85)), hi = Math.Min(q1, (int)Math.Ceiling(period * 1.15));
        double best = double.NegativeInfinity; int bq = lo;
        for (int q = lo; q <= hi; q++) if (cr[q] > best) { best = cr[q]; bq = q; }
        f.Cpp[i] = hi >= lo ? (float)(best - (a + b * bq)) : float.NaN;
    }

    static void Unvoiced(Frames f, int i, double[] re, double[] im, double binHz)
    {
        const int N = Frames.N;
        double logSum = 0, sum = 0; int n = 0;
        for (int k = 1; k <= N / 2; k++)
        {
            double fr = k * binHz;
            double p = re[k] * re[k] + im[k] * im[k] + 1e-18;
            if (fr >= 100 && fr <= 4000) { logSum += Math.Log(p); sum += p; n++; }
        }
        f.Flat[i] = n > 0 ? (float)(Math.Exp(logSum / n) / (sum / n)) : float.NaN;
    }

    /// <summary>Removes isolated octave/detection glitches and very short voiced blips, then builds the smoothed curve.</summary>
    static void Clean(Frames f, CoachConfig c)
    {
        int n = f.Count;
        var m = f.Midi;
        var o = (float[])m.Clone();
        var win = new List<float>(7);
        for (int i = 0; i < n; i++)
        {
            if (float.IsNaN(m[i])) continue;
            win.Clear();
            for (int j = Math.Max(0, i - 3); j <= Math.Min(n - 1, i + 3); j++) if (!float.IsNaN(m[j])) win.Add(m[j]);
            win.Sort();
            float med = win[win.Count / 2];
            if (Math.Abs(m[i] - med) > 1.0f) o[i] = float.NaN;
        }
        int minRun = (int)Math.Ceiling(c.MinRunSec / f.Hop);
        for (int i = 0; i < n;)
        {
            if (float.IsNaN(o[i])) { i++; continue; }
            int j = i; while (j < n && !float.IsNaN(o[j])) j++;
            if (j - i < minRun) for (int k = i; k < j; k++) o[k] = float.NaN;
            i = j;
        }
        f.Midi = o;

        // smoothed pitch per voiced run (±7 frames ≈ ±70 ms)
        const int W = 7;
        for (int i = 0; i < n;)
        {
            if (float.IsNaN(o[i])) { i++; continue; }
            int a = i; while (i < n && !float.IsNaN(o[i])) i++;
            int b = i;
            var pre = new double[b - a + 1];
            for (int k = a; k < b; k++) pre[k - a + 1] = pre[k - a] + o[k];
            for (int k = a; k < b; k++)
            {
                int lo = Math.Max(a, k - W), hi = Math.Min(b - 1, k + W);
                f.Smooth[k] = (float)((pre[hi - a + 1] - pre[lo - a]) / (hi - lo + 1));
            }
        }
    }

    // ------------------------------------------------------------------ notes and phrases

    /// <summary>Splits voiced runs into notes: settled stretches ("plateaus") of the vibrato-smoothed pitch,
    /// with the glides between them split halfway. Phrase starts and ends stay attached to their notes,
    /// so scoops and falling endings can be measured.</summary>
    public static List<Note> Notes(Frames f, CoachConfig c)
    {
        var notes = new List<Note>();
        int n = f.Count;
        for (int i = 0; i < n;)
        {
            if (float.IsNaN(f.Midi[i])) { i++; continue; }
            int a = i; while (i < n && !float.IsNaN(f.Midi[i])) i++;
            SegmentRun(f, a, i, c, notes);
        }
        return notes;
    }

    static void SegmentRun(Frames f, int a, int b, CoachConfig c, List<Note> notes)
    {
        int len = b - a;
        var s = f.Smooth;
        const int K = 3; const double SlopeMax = 0.45;
        var plats = new List<(int s0, int s1)>();
        int p0 = -1; double sum = 0; int cnt = 0;
        for (int k = a; k < b; k++)
        {
            bool stable = len < 2 * K + 1 || (k - K >= a && k + K < b && Math.Abs(s[k + K] - s[k - K]) < SlopeMax);
            if (stable && p0 >= 0 && Math.Abs(s[k] - sum / cnt) > c.SplitSemitones) { plats.Add((p0, k)); p0 = -1; }
            if (stable)
            {
                if (p0 < 0) { p0 = k; sum = 0; cnt = 0; }
                sum += s[k]; cnt++;
            }
            else if (p0 >= 0) { plats.Add((p0, k)); p0 = -1; }
        }
        if (p0 >= 0) plats.Add((p0, b));
        plats.RemoveAll(p => p.s1 - p.s0 < 5);
        if (plats.Count == 0) return;

        double Level((int s0, int s1) p) => Sig.Median(Enumerable.Range(p.s0, p.s1 - p.s0).Select(k => (double)s[k]));
        var merged = new List<(int s0, int s1)> { plats[0] };
        for (int k = 1; k < plats.Count; k++)
        {
            var prev = merged[^1];
            if (Math.Abs(Level(prev) - Level(plats[k])) < 0.35) merged[^1] = (prev.s0, plats[k].s1);
            else merged.Add(plats[k]);
        }

        // a short stretch that keeps sliding is a glide, not a note
        if (merged.Count > 1)
            merged.RemoveAll(p => (p.s1 - p.s0) * f.Hop < 0.3 && Range(s, p.s0, p.s1) >= 0.4 && merged.Count > 1);
        // too little left at the start or end of the run: it is the note's onset or tail (a scoop, a fall-off)
        int minFrames = (int)Math.Ceiling(c.MinNoteSec / f.Hop);
        while (merged.Count > 1 && b - merged[^1].s0 < minFrames) merged.RemoveAt(merged.Count - 1);
        while (merged.Count > 1 && merged[0].s1 - a < minFrames) merged.RemoveAt(0);

        var levels = merged.Select(Level).ToArray();
        var cuts = new int[merged.Count + 1];
        cuts[0] = a; cuts[^1] = b;
        for (int k = 0; k + 1 < merged.Count; k++)
        {
            double mid = (levels[k] + levels[k + 1]) / 2;
            int from = merged[k].s1 - 1, to = merged[k + 1].s0, cut = (from + to + 1) / 2;
            double sign = Math.Sign(levels[k] - mid);
            for (int j = from; j <= to; j++) if (Math.Sign(s[j] - mid) != sign) { cut = j; break; }
            cuts[k + 1] = Math.Clamp(cut, merged[k].s0 + 1, merged[k + 1].s1 - 1);
        }

        for (int k = 0; k < merged.Count; k++)
        {
            int n0 = cuts[k], n1 = cuts[k + 1];
            if ((n1 - n0) * f.Hop < c.MinNoteSec) continue;
            int len2 = n1 - n0;
            int m0 = n0 + (int)(len2 * c.SteadyFrom), m1 = Math.Max(m0 + 1, n0 + (int)(len2 * c.SteadyTo));
            int c0 = Math.Max(m0, merged[k].s0), c1 = Math.Min(m1, merged[k].s1);
            if (c1 - c0 < 5) { c0 = merged[k].s0; c1 = merged[k].s1; }
            notes.Add(new Note
            {
                T0 = f.T(n0) - f.Hop / 2, T1 = f.T(n1 - 1) + f.Hop / 2,
                CoreT0 = f.T(merged[k].s0), CoreT1 = f.T(merged[k].s1 - 1),
                Pitch = Sig.Median(Enumerable.Range(c0, c1 - c0).Select(j => (double)f.Midi[j])),
                AfterGap = k == 0, BeforeGap = k == merged.Count - 1,
            });
        }
    }

    static double Range(float[] s, int i0, int i1)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = i0; i < i1; i++) { lo = Math.Min(lo, s[i]); hi = Math.Max(hi, s[i]); }
        return hi - lo;
    }

    /// <summary>Voiced regions separated by gaps longer than the phrase gap.</summary>
    public static List<Phrase> Phrases(Frames f, CoachConfig c)
    {
        var ph = new List<Phrase>();
        int gapFrames = (int)Math.Round(c.PhraseGapSec / f.Hop);
        int start = -1, last = -1;
        for (int i = 0; i < f.Count; i++)
        {
            if (float.IsNaN(f.Midi[i])) continue;
            if (start >= 0 && i - last > gapFrames) { ph.Add(new Phrase { T0 = f.T(start), T1 = f.T(last) }); start = -1; }
            if (start < 0) start = i;
            last = i;
        }
        if (start >= 0) ph.Add(new Phrase { T0 = f.T(start), T1 = f.T(last) });
        foreach (var p in ph)
        {
            var dbs = new List<double>();
            for (int i = f.Index(p.T0); i <= f.Index(p.T1) && i < f.Count; i++) if (!float.IsNaN(f.Midi[i])) dbs.Add(f.Db[i]);
            p.Db = dbs.Count > 0 ? Sig.Median(dbs) : double.NaN;
        }
        return ph;
    }
}
