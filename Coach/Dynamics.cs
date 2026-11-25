namespace VocalTuner.Coach;

/// <summary>Loudness over time, dynamic range, and whether the performance builds or stays flat.</summary>
public static class Dynamics
{
    public static void Apply(CoachResult r, Frames f, CoachConfig c)
    {
        var m = r.M;
        var voiced = new List<double>();
        for (int i = 0; i < f.Count; i++) if (!float.IsNaN(f.Midi[i])) voiced.Add(f.Db[i]);
        if (voiced.Count == 0) return;
        double top = Sig.Percentile(voiced, 99);
        m.DynRangeDb = Sig.Percentile(voiced, 90) - Sig.Percentile(voiced, 10);

        // loudness curve relative to the loudest singing
        int wins = (int)Math.Ceiling(r.Duration / r.LoudHop);
        r.Loud = new float[wins];
        for (int w = 0; w < wins; w++)
        {
            int i0 = Math.Max(0, f.Index(w * r.LoudHop)), i1 = Math.Min(f.Count, f.Index((w + 1) * r.LoudHop));
            double s = 0; int n = 0;
            for (int i = i0; i < i1; i++) if (!float.IsNaN(f.Midi[i])) { s += f.Db[i]; n++; }
            r.Loud[w] = n >= 5 ? (float)Math.Round(s / n - top, 1) : -200f;
        }

        var ph = r.Phrases.Where(p => !double.IsNaN(p.Db)).ToList();
        m.PhraseSpreadDb = ph.Count >= 3 ? Sig.Percentile(ph.Select(p => p.Db), 90) - Sig.Percentile(ph.Select(p => p.Db), 10) : double.NaN;
        double mid = r.Duration / 2;
        var early = new List<double>(); var late = new List<double>();
        for (int i = 0; i < f.Count; i++) if (!float.IsNaN(f.Midi[i])) (f.T(i) < mid ? early : late).Add(f.Db[i]);
        m.LateMinusEarlyDb = early.Count > 50 && late.Count > 50 ? Sig.Median(late) - Sig.Median(early) : 0;

        bool flatPhrases = double.IsNaN(m.PhraseSpreadDb) || m.PhraseSpreadDb < 3;
        m.DynShape = m.DynRangeDb < c.FlatRangeDb && flatPhrases ? "flat"
            : m.LateMinusEarlyDb >= c.BuildDb ? "builds"
            : m.LateMinusEarlyDb <= -c.BuildDb ? "fades"
            : "varied";
    }
}

/// <summary>Is there music in the voice recording? (Pitch results are less reliable when there is.)</summary>
public static class MusicCheck
{
    public static MusicInfo Check(Frames f, CoachConfig c, bool hadBacking)
    {
        var mi = new MusicInfo { HadBacking = hadBacking };
        int n = f.Count;
        if (n == 0) return mi;
        // distance (frames) to the nearest voiced frame
        var dist = new int[n];
        int last = -1_000_000;
        for (int i = 0; i < n; i++) { if (!float.IsNaN(f.Midi[i])) last = i; dist[i] = i - last; }
        last = 1_000_000;
        for (int i = n - 1; i >= 0; i--) { if (!float.IsNaN(f.Midi[i])) last = i; dist[i] = Math.Min(dist[i], last - i); }
        int first = Array.FindIndex(f.Midi, v => !float.IsNaN(v)), lastV = Array.FindLastIndex(f.Midi, v => !float.IsNaN(v));
        if (first < 0) return mi;

        int away = (int)Math.Round(0.3 / f.Hop);
        var gapDb = new List<double>(); var flat = new List<double>();
        var voiceDb = new List<double>(); var low = new List<double>();
        for (int i = 0; i < n; i++)
        {
            if (!float.IsNaN(f.Midi[i]))
            {
                voiceDb.Add(f.Db[i]);
                if (f.Midi[i] > 47 && !float.IsNaN(f.Low[i])) low.Add(f.Low[i]);
            }
            else if (i > first && i < lastV && dist[i] >= away)
            {
                gapDb.Add(f.Db[i]);
                if (!float.IsNaN(f.Flat[i])) flat.Add(f.Flat[i]);
            }
        }
        mi.LowRatio = low.Count > 0 ? Sig.Median(low) : 0;
        bool gapMusic = false;
        if (gapDb.Count >= 50)
        {
            mi.GapDb = Sig.Median(voiceDb) - Sig.Median(gapDb);
            if (flat.Count >= gapDb.Count / 2)
            {
                mi.Flatness = Sig.Median(flat);
                gapMusic = mi.GapDb < c.MusicGapDb && mi.Flatness < 0.3;
            }
        }
        mi.Detected = gapMusic || mi.LowRatio > 0.3;
        return mi;
    }
}
