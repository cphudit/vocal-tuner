namespace VocalTuner.Coach;

/// <summary>What happens inside each note: steadiness, how it starts (scoops), how it ends, and vibrato.</summary>
public static class NoteShape
{
    public static void Apply(CoachResult r, Frames f, CoachConfig c)
    {
        foreach (var n in r.Notes)
        {
            n.Flags = "";
            Stability(n, f, c);
            Onset(n, f, c);
            Ending(n, f, c);
            Vibrato(n, f, c);
        }

        var m = r.M;
        var steady = r.Notes.Where(n => n.Dur >= 0.3).ToList();
        m.WobblyPct = Intonation.Pct(steady, n => n.Flags.Contains('W'));
        m.MedianSlowStd = steady.Count > 0 ? Sig.Median(steady.Select(n => n.StdSlow)) : double.NaN;

        var on = r.Notes.Where(n => n.Onset.HasValue).ToList();
        m.OnsetNotes = on.Count;
        m.ScoopPct = Intonation.Pct(on, n => n.Flags.Contains('S'));
        m.AbovePct = Intonation.Pct(on, n => n.Flags.Contains('A'));
        m.MedianOnset = on.Count > 0 ? Sig.Median(on.Select(n => n.Onset!.Value)) : double.NaN;

        var end = r.Notes.Where(n => n.End.HasValue).ToList();
        m.EndNotes = end.Count;
        m.EndDropPct = Intonation.Pct(end, n => n.Flags.Contains('D'));
        m.EndUnstablePct = Intonation.Pct(end, n => n.Flags.Contains('U'));
        if (r.Duration >= c.FatigueMinSec)
        {
            double cut = r.Duration * 2 / 3;
            var early = end.Where(n => n.T0 < cut).ToList();
            var late = end.Where(n => n.T0 >= cut).ToList();
            if (early.Count >= 3 && late.Count >= 3)
            {
                m.EndDropEarlyPct = Intonation.Pct(early, n => n.Flags.Contains('D'));
                m.EndDropLatePct = Intonation.Pct(late, n => n.Flags.Contains('D'));
            }
        }

        var longs = r.Notes.Where(n => n.Dur >= c.VibratoMinNoteSec).ToList();
        m.LongNotes = longs.Count;
        m.VibratoPct = Intonation.Pct(longs, n => n.Vibrato);
        var vib = longs.Where(n => n.Vibrato).ToList();
        if (vib.Count > 0)
        {
            m.VibRate = Sig.Median(vib.Select(n => n.VibRate!.Value));
            m.VibExtent = Sig.Median(vib.Select(n => n.VibExtent!.Value));
        }
    }

    static (int i0, int i1) Span(Frames f, double t0, double t1) =>
        (Math.Clamp(f.Index(t0 + f.Hop / 2), 0, f.Count), Math.Clamp(f.Index(t1 - f.Hop / 2) + 1, 0, f.Count));

    static List<int> Voiced(Frames f, int i0, int i1)
    {
        var l = new List<int>();
        for (int i = i0; i < i1; i++) if (!float.IsNaN(f.Midi[i])) l.Add(i);
        return l;
    }

    static void Stability(Note n, Frames f, CoachConfig c)
    {
        var (i0, i1) = Span(f, n.T0, n.T1);
        int len = i1 - i0;
        var idx = Voiced(f, i0 + (int)(len * c.SteadyFrom), i0 + (int)(len * c.SteadyTo));
        if (idx.Count < 3) return;
        n.StdRaw = Sig.Std(idx.Select(i => (double)f.Midi[i] * 100).ToList());
        n.StdSlow = Sig.Std(idx.Select(i => (double)f.Smooth[i] * 100).ToList());
        if (n.Dur >= 0.3 && n.StdSlow > c.WobbleCents) n.Flags += "W";
    }

    static void Onset(Note n, Frames f, CoachConfig c)
    {
        if (!n.AfterGap || n.Dur < 0.2) return;
        var (i0, _) = Span(f, n.T0, n.T1);
        var idx = Voiced(f, i0, i0 + (int)Math.Round(c.OnsetSec / f.Hop));
        if (idx.Count < 3) return;
        n.Onset = (Sig.Median(idx.Select(i => (double)f.Midi[i])) - n.Pitch) * 100;
        if (n.Onset < -c.ScoopCents) n.Flags += "S";
        else if (n.Onset > c.ScoopCents) n.Flags += "A";
    }

    static void Ending(Note n, Frames f, CoachConfig c)
    {
        if (!n.BeforeGap || n.Dur < c.LongNoteSec) return;
        var (_, i1) = Span(f, n.T0, n.T1);
        var idx = Voiced(f, i1 - (int)Math.Round(c.EndSec / f.Hop), i1);
        if (idx.Count < 4) return;
        // the vibrato-smoothed curve, so a vibrato swing in the last 150 ms isn't mistaken for a fall
        var cents = idx.Select(i => ((double)f.Smooth[i] - n.Pitch) * 100).ToList();
        n.End = Sig.Median(cents);
        n.EndStd = Sig.Std(cents);
        if (n.End < -c.EndDropCents) n.Flags += "D";
        else if (n.EndStd > c.EndUnstableCents) n.Flags += "U";
    }

    /// <summary>Detrends the held part of a long note and looks for a regular 3.5–8 Hz wave.</summary>
    static void Vibrato(Note n, Frames f, CoachConfig c)
    {
        if (n.Dur < c.VibratoMinNoteSec) return;
        double t0 = n.CoreT0, t1 = n.CoreT1;
        if (t1 - t0 < 0.45) { t0 = n.T0 + 0.12; t1 = n.T1 - 0.08; }
        var (i0, i1) = Span(f, t0, t1);
        var idx = Voiced(f, i0, i1);
        if (idx.Count < 40) return;
        var t = idx.Select(i => (i - idx[0]) * f.Hop).ToArray();
        var y = idx.Select(i => (double)f.Midi[i] * 100).ToArray();
        Detrend(t, y);
        double variance = y.Sum(v => v * v) / y.Length;
        if (variance <= 0) return;

        double bestA = 0, bestF = 0;
        var amps = new List<(double fr, double a)>();
        for (double fr = 2.5; fr <= 10.0001; fr += 0.05)
        {
            double re = 0, im = 0;
            for (int k = 0; k < y.Length; k++) { double ph = 2 * Math.PI * fr * t[k]; re += y[k] * Math.Cos(ph); im += y[k] * Math.Sin(ph); }
            double a = 2 * Math.Sqrt(re * re + im * im) / y.Length;
            amps.Add((fr, a));
            if (a > bestA) { bestA = a; bestF = fr; }
        }
        // refine the peak frequency (parabola through the neighbours)
        int bi = amps.FindIndex(p => p.fr == bestF);
        if (bi > 0 && bi < amps.Count - 1)
        {
            double a1 = amps[bi - 1].a, a2 = amps[bi].a, a3 = amps[bi + 1].a, d = a1 - 2 * a2 + a3;
            if (d < 0) bestF += 0.05 * 0.5 * (a1 - a3) / d;
        }
        double explained = bestA * bestA / 2 / variance;
        double cycles = (t[^1] - t[0]) * bestF;
        if (bestF >= c.VibratoMinHz && bestF <= c.VibratoMaxHz && bestA >= c.VibratoMinExtent && explained >= 0.35 && cycles >= 2)
        {
            n.Vibrato = true;
            n.VibRate = Math.Round(bestF, 2);
            n.VibExtent = Math.Round(bestA, 1);
        }
    }

    /// <summary>Removes a least-squares quadratic (slow drift) from y.</summary>
    static void Detrend(double[] t, double[] y)
    {
        int n = y.Length;
        double tm = t.Average();
        double s0 = n, s1 = 0, s2 = 0, s3 = 0, s4 = 0, b0 = 0, b1 = 0, b2 = 0;
        for (int i = 0; i < n; i++)
        {
            double x = t[i] - tm, x2 = x * x;
            s1 += x; s2 += x2; s3 += x2 * x; s4 += x2 * x2;
            b0 += y[i]; b1 += x * y[i]; b2 += x2 * y[i];
        }
        // solve the 3x3 normal equations (Cramer's rule)
        double[,] A = { { s0, s1, s2 }, { s1, s2, s3 }, { s2, s3, s4 } };
        double D = Det(A);
        if (Math.Abs(D) < 1e-12) { double mean = y.Average(); for (int i = 0; i < n; i++) y[i] -= mean; return; }
        double[] b = { b0, b1, b2 }, coef = new double[3];
        for (int k = 0; k < 3; k++)
        {
            var M = (double[,])A.Clone();
            for (int r = 0; r < 3; r++) M[r, k] = b[r];
            coef[k] = Det(M) / D;
        }
        for (int i = 0; i < n; i++) { double x = t[i] - tm; y[i] -= coef[0] + coef[1] * x + coef[2] * x * x; }
    }

    static double Det(double[,] m) =>
        m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
}
