namespace VocalTuner.Coach;

/// <summary>Cents offset per note, overall accuracy, sharp/flat tendency, accuracy per pitch zone, and the range used.</summary>
public static class Intonation
{
    public static void Apply(CoachResult r, CoachConfig c)
    {
        double refSt = r.M.RefCents / 100;
        r.ZoneEdges = VoiceTypes.Edges(r.Voice);
        foreach (var n in r.Notes)
        {
            double p = n.Pitch - refSt;
            n.Target = (int)Math.Round(p);
            n.Cents = (p - n.Target) * 100;
            n.Zone = VoiceTypes.ZoneOf(n.Target, r.ZoneEdges);
        }
        var m = r.M;
        var notes = r.Notes;
        m.NoteCount = notes.Count;
        if (notes.Count == 0) return;
        m.InTunePct = Pct(notes, n => Math.Abs(n.Cents) <= c.InTune);
        m.ClosePct = Pct(notes, n => Math.Abs(n.Cents) <= c.Close);
        m.MeanSigned = notes.Average(n => n.Cents);
        m.MedianSigned = Sig.Median(notes.Select(n => n.Cents));
        m.MeanAbs = notes.Average(n => Math.Abs(n.Cents));
        m.SharpShare = Pct(notes, n => n.Cents > 5);
        m.FlatShare = Pct(notes, n => n.Cents < -5);

        r.Zones.Clear();
        var e = r.ZoneEdges;
        for (int z = 0; z <= e.Length; z++)
        {
            var zs = new ZoneStat
            {
                Zone = z,
                Lo = z == 0 ? int.MinValue : e[z - 1],
                Hi = z == e.Length ? int.MaxValue : e[z] - 1,
            };
            var zn = notes.Where(n => n.Zone == z).ToList();
            zs.Notes = zn.Count;
            zs.Sec = zn.Sum(n => n.Dur);
            if (zn.Count > 0)
            {
                zs.InTunePct = Pct(zn, n => Math.Abs(n.Cents) <= c.InTune);
                zs.MeanSigned = zn.Average(n => n.Cents);
                zs.MeanAbs = zn.Average(n => Math.Abs(n.Cents));
            }
            r.Zones.Add(zs);
        }
    }

    /// <summary>Lowest/highest reliable notes (1st/99th percentile of sung frames) and where most of the singing sits.</summary>
    public static void Range(CoachResult r, Frames f)
    {
        double refSt = r.M.RefCents / 100;
        var v = new List<double>();
        for (int i = 0; i < f.Count; i++) if (!float.IsNaN(f.Midi[i])) v.Add(f.Midi[i] - refSt);
        r.M.VoicedSec = v.Count * f.Hop;
        if (v.Count == 0) return;
        r.M.RangeLo = Math.Round(Sig.Percentile(v, 1));
        r.M.RangeHi = Math.Round(Sig.Percentile(v, 99));
        r.M.TessLo = Math.Round(Sig.Percentile(v, 25));
        r.M.TessHi = Math.Round(Sig.Percentile(v, 75));
    }

    public static double Pct<T>(IReadOnlyCollection<T> xs, Func<T, bool> pred) => xs.Count == 0 ? double.NaN : 100.0 * xs.Count(pred) / xs.Count;
}
