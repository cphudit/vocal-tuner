namespace VocalTuner.Coach;

/// <summary>Loudness, brightness, breathiness (HNR, CPP) and roughness (jitter, shimmer) per note and per pitch zone.</summary>
public static class VoiceQuality
{
    public static void Apply(CoachResult r, Frames f, float[] x, CoachConfig c)
    {
        Parallel.ForEach(r.Notes, n =>
        {
            int i0 = Math.Max(0, f.Index(n.CoreT0)), i1 = Math.Min(f.Count, f.Index(n.CoreT1) + 1);
            var db = new List<double>(); var cen = new List<double>(); var hnr = new List<double>(); var cpp = new List<double>();
            for (int i = i0; i < i1; i++)
            {
                if (float.IsNaN(f.Midi[i])) continue;
                db.Add(f.Db[i]);
                if (!float.IsNaN(f.Centroid[i])) cen.Add(f.Centroid[i]);
                if (!float.IsNaN(f.Cpp[i])) cpp.Add(f.Cpp[i]);
                hnr.Add(Hnr(f.Clar[i]));
            }
            n.Db = db.Count > 0 ? Sig.Median(db) : double.NaN;
            n.Centroid = cen.Count > 0 ? Sig.Median(cen) : double.NaN;
            n.Hnr = hnr.Count > 0 ? Sig.Median(hnr) : double.NaN;
            n.Cpp = cpp.Count > 0 ? Sig.Median(cpp) : double.NaN;
            if (n.CoreT1 - n.CoreT0 >= 0.2)
            {
                var js = Cycles(x, f, n);
                if (js != null) { n.Jitter = js.Value.jit; n.Shimmer = js.Value.shim; }
            }
        });

        var m = r.M;
        var ns = r.Notes;
        double medDb = Sig.Median(ns.Select(n => n.Db));
        m.Hnr = Sig.Median(ns.Select(n => n.Hnr));
        m.Cpp = Sig.Median(ns.Select(n => n.Cpp));
        m.Centroid = Sig.Median(ns.Select(n => n.Centroid));
        var jn = ns.Where(n => n.Jitter.HasValue).ToList();
        if (jn.Count > 0) { m.Jitter = Sig.Median(jn.Select(n => n.Jitter!.Value)); m.Shimmer = Sig.Median(jn.Select(n => n.Shimmer!.Value)); }

        foreach (var z in r.Zones)
        {
            var zn = ns.Where(n => n.Zone == z.Zone).ToList();
            if (zn.Count == 0) continue;
            z.Db = Sig.Median(zn.Select(n => n.Db)) - medDb;
            z.Centroid = Sig.Median(zn.Select(n => n.Centroid));
            z.Hnr = Sig.Median(zn.Select(n => n.Hnr));
            z.Cpp = Sig.Median(zn.Select(n => n.Cpp));
            var zj = zn.Where(n => n.Jitter.HasValue).ToList();
            if (zj.Count > 0) { z.Jitter = Sig.Median(zj.Select(n => n.Jitter!.Value)); z.Shimmer = Sig.Median(zj.Select(n => n.Shimmer!.Value)); }
        }
    }

    /// <summary>Harmonics-to-noise ratio from the normalised autocorrelation peak (Boersma's relation).</summary>
    public static double Hnr(double peak)
    {
        double p = Math.Clamp(peak, 0.01, 0.999);
        return 10 * Math.Log10(p / (1 - p));
    }

    /// <summary>
    /// Cycle-by-cycle periods (cross-correlation of neighbouring cycles) and peak-to-peak amplitudes over the
    /// settled part of a note. Returns ppq5 jitter and apq5 shimmer in %, which ignore slow changes such as vibrato.
    /// </summary>
    public static (double jit, double shim)? Cycles(float[] x, Frames f, Note n)
    {
        int s0 = (int)(n.CoreT0 * Sig.Rate), s1 = Math.Min(x.Length, (int)(n.CoreT1 * Sig.Rate));
        var per = new List<double>(); var amp = new List<double>();
        int pos = s0;
        while (true)
        {
            int fi = Math.Clamp(f.Index((double)pos / Sig.Rate), 0, f.Count - 1);
            double midi = float.IsNaN(f.Midi[fi]) ? n.Pitch : f.Midi[fi];
            double T = Sig.Rate / (440 * Math.Pow(2, (midi - 69) / 12));
            int t = (int)Math.Round(T);
            int lo = (int)(T * 0.8), hi = (int)Math.Ceiling(T * 1.25);
            if (pos + hi + t >= s1) break;
            double best = double.NegativeInfinity; int bl = t;
            var cs = new double[hi - lo + 1];
            for (int L = lo; L <= hi; L++)
            {
                double dot = 0, na = 0, nb = 0;
                for (int k = 0; k < t; k++) { double a = x[pos + k], b = x[pos + L + k]; dot += a * b; na += a * a; nb += b * b; }
                double cc = dot / (Math.Sqrt(na * nb) + 1e-12);
                cs[L - lo] = cc;
                if (cc > best) { best = cc; bl = L; }
            }
            if (best < 0.5) { pos += t; continue; }   // not periodic here
            double frac = 0;
            int bi = bl - lo;
            if (bi > 0 && bi < cs.Length - 1)
            {
                double d = cs[bi - 1] - 2 * cs[bi] + cs[bi + 1];
                if (d < 0) frac = 0.5 * (cs[bi - 1] - cs[bi + 1]) / d;
            }
            double mx = double.NegativeInfinity, mn = double.PositiveInfinity;
            for (int k = 0; k < bl; k++) { double v = x[pos + k]; if (v > mx) mx = v; if (v < mn) mn = v; }
            per.Add(bl + frac); amp.Add(mx - mn);
            pos += bl;
        }
        if (per.Count < 10) return null;
        return (Ppq5(per) * 100, Ppq5(amp) * 100);
    }

    static double Ppq5(List<double> v)
    {
        double mean = v.Average(), s = 0; int n = 0;
        for (int i = 2; i < v.Count - 2; i++)
        {
            double local = (v[i - 2] + v[i - 1] + v[i] + v[i + 1] + v[i + 2]) / 5;
            s += Math.Abs(v[i] - local); n++;
        }
        return n > 0 ? s / n / mean : double.NaN;
    }
}

/// <summary>Where the voice changes character: quality steps between neighbouring pitches, and cracks/flips.</summary>
public static class Registers
{
    public static void Apply(CoachResult r, Frames f, CoachConfig c)
    {
        double refSt = r.M.RefCents / 100;
        // cracks / flips: inside a continuous voiced run the pitch suddenly jumps away (not an octave, which is
        // usually a tracking error) and comes back to the held note within 0.4 s. A deliberate leap to another
        // note does not come back, so it is not counted.
        for (int i = 1; i < f.Count; i++)
        {
            float a = f.Midi[i - 1], b = f.Midi[i];
            if (float.IsNaN(a) || float.IsNaN(b)) continue;
            double d = Math.Abs(b - a);
            if (d < c.BreakSemitones || (d > 11 && d < 13)) continue;
            if (f.Clar[i - 1] < 0.85 || f.Clar[i] < 0.85) continue;
            int j = i;
            while (j < f.Count && !float.IsNaN(f.Midi[j]) && j - i < 40 && Math.Abs(f.Midi[j] - a) >= 1.0) j++;
            if (j >= f.Count || float.IsNaN(f.Midi[j]) || j - i < 4 || j - i >= 40) continue;
            var away = Enumerable.Range(i, j - i).Select(k => (double)f.Midi[k]);
            r.Breaks.Add(new Break { T = Math.Round(f.T(i), 2), From = Math.Round(a - refSt, 2), To = Math.Round(Sig.Median(away) - refSt, 2) });
            i = j;
        }

        // quality per semitone (duration-weighted medians of the notes on that semitone)
        var bins = r.Notes.GroupBy(n => n.Target).ToDictionary(g => g.Key, g => g.ToList());
        if (bins.Count >= 4)
        {
            int lo = bins.Keys.Min(), hi = bins.Keys.Max();
            double bestScore = 0; int bestAt = 0;
            for (int m = lo + 1; m <= hi; m++)
            {
                var left = Enumerable.Range(m - 3, 3).Where(bins.ContainsKey).SelectMany(k => bins[k]).ToList();
                var right = Enumerable.Range(m, 3).Where(bins.ContainsKey).SelectMany(k => bins[k]).ToList();
                if (left.Sum(n => n.Dur) < 0.8 || right.Sum(n => n.Dur) < 0.8) continue;
                double dH = Sig.Median(right.Select(n => n.Hnr)) - Sig.Median(left.Select(n => n.Hnr));
                double dC = Sig.Median(right.Select(n => n.Cpp)) - Sig.Median(left.Select(n => n.Cpp));
                double score = Math.Max(double.IsNaN(dH) ? 0 : -dH / c.BreathyHnrDrop, double.IsNaN(dC) ? 0 : -dC / c.BreathyCppDrop);
                if (score > bestScore) { bestScore = score; bestAt = m; }
            }
            if (bestScore >= 1)
            {
                r.M.PassaggioLo = bestAt - 1; r.M.PassaggioHi = bestAt;
                r.M.PassaggioWhy = "quality";
            }
        }

        if (r.Breaks.Count >= 2)
        {
            var pitches = r.Breaks.Select(b => b.From).ToList();   // the note that was being held when it cracked
            double med = Math.Round(Sig.Median(pitches));
            if (!double.IsNaN(r.M.PassaggioLo) && Math.Abs(med - r.M.PassaggioHi) <= 2) r.M.PassaggioWhy = "both";
            else if (double.IsNaN(r.M.PassaggioLo)) { r.M.PassaggioLo = med - 1; r.M.PassaggioHi = med + 1; r.M.PassaggioWhy = "breaks"; }
        }
    }
}
