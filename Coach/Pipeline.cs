namespace VocalTuner.Coach;

/// <summary>Runs every analysis module on a voice recording and builds the feedback.</summary>
public static class Pipeline
{
    /// <param name="voice">Mono voice at <see cref="Sig.Rate"/>.</param>
    /// <param name="backing">Optional mono backing track at <see cref="Sig.Rate"/> (used for its key and tuning).</param>
    public static CoachResult Run(float[] voice, float[]? backing, string source, string kind, string voiceType, Action<string>? progress = null)
    {
        var c = CoachConfig.Load();
        if (voice.Length < Sig.Rate * 3) throw new CoachException("short");
        var x = (float[])voice.Clone();
        Sig.Normalize(x);

        progress?.Invoke("pitch");
        var f = PitchTrack.Analyze(x, c);
        var r = new CoachResult
        {
            When = DateTime.Now, Source = source, Kind = kind, Voice = voiceType,
            Duration = Math.Round((double)x.Length / Sig.Rate, 2), Hop = f.Hop,
        };
        r.Notes = PitchTrack.Notes(f, c);
        r.Phrases = PitchTrack.Phrases(f, c);
        if (r.Notes.Count < 2) throw new CoachException("nonotes");
        r.Music = MusicCheck.Check(f, c, backing != null);

        progress?.Invoke("key");
        var music = KeyCheck.Tuning(r, x, f, backing);
        Intonation.Apply(r, c);
        Intonation.Range(r, f);
        KeyCheck.Apply(r, music);

        progress?.Invoke("shape");
        NoteShape.Apply(r, f, c);

        progress?.Invoke("quality");
        VoiceQuality.Apply(r, f, x, c);
        Registers.Apply(r, f, c);
        Dynamics.Apply(r, f, c);

        r.PitchCenti = f.Midi.Select(m => float.IsNaN(m) ? 0 : (int)Math.Round(m * 100)).ToArray();
        Round(r);
        Feedback.Build(r, c);
        return r;
    }

    /// <summary>Rounds stored values so the history JSON stays small and readable.</summary>
    static void Round(CoachResult r)
    {
        static double R(double v, int d) => double.IsNaN(v) ? v : Math.Round(v, d);
        foreach (var n in r.Notes)
        {
            n.T0 = R(n.T0, 3); n.T1 = R(n.T1, 3); n.CoreT0 = R(n.CoreT0, 3); n.CoreT1 = R(n.CoreT1, 3);
            n.Pitch = R(n.Pitch, 3); n.Cents = R(n.Cents, 1); n.StdSlow = R(n.StdSlow, 1); n.StdRaw = R(n.StdRaw, 1);
            n.Onset = n.Onset is { } o ? R(o, 1) : null; n.End = n.End is { } e ? R(e, 1) : null; n.EndStd = n.EndStd is { } es ? R(es, 1) : null;
            n.Db = R(n.Db, 1); n.Centroid = R(n.Centroid, 0); n.Hnr = R(n.Hnr, 1); n.Cpp = R(n.Cpp, 2);
            n.Jitter = n.Jitter is { } j ? R(j, 2) : null; n.Shimmer = n.Shimmer is { } s ? R(s, 2) : null;
        }
        foreach (var p in r.Phrases) { p.T0 = R(p.T0, 2); p.T1 = R(p.T1, 2); p.Db = R(p.Db, 1); }
    }
}

/// <summary>An analysis that cannot run; the code is turned into a friendly message by the report text.</summary>
public sealed class CoachException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
