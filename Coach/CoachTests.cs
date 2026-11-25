using System.IO;
using System.Text;

namespace VocalTuner.Coach;

/// <summary>
/// Checks the measurements on synthetic singing with known answers (detuning, vibrato, scoops, falling ends,
/// legato, key, wobble, jitter, music in the background, a vocal shifted against the backing).
/// Run with: VocalTuner.exe --coachtest &lt;dir&gt;
/// </summary>
public static class CoachTests
{
    const int R = Sig.Rate;

    sealed record Seg(double Dur, Func<double, double>? Midi);

    static Seg Note(double dur, double midi, double cents = 0) => new(dur, _ => midi + cents / 100);
    static Seg Rest(double dur) => new(dur, null);

    /// <summary>Voice-like tone: 10 harmonics falling 1/k, short fades, optional cycle jitter and breath noise.</summary>
    static float[] Synth(IEnumerable<Seg> segs, double noise = 0.002, double jitter = 0, int seed = 1)
    {
        var rnd = new Random(seed);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());
        var o = new List<float>();
        double phase = 0, factor = 1;
        foreach (var s in segs)
        {
            int n = (int)(s.Dur * R);
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / R, v = 0;
                if (s.Midi != null)
                {
                    double f = 440 * Math.Pow(2, (s.Midi(t) - 69) / 12) * factor;
                    phase += f / R;
                    if (phase >= 1) { phase -= 1; if (jitter > 0) factor = 1 + Gauss() * jitter; }
                    double env = Math.Min(1, Math.Min(t / 0.01, (s.Dur - t) / 0.01));
                    for (int k = 1; k <= 10; k++) v += Math.Sin(2 * Math.PI * k * phase) / k;
                    v *= 0.25 * env;
                }
                o.Add((float)(v + noise * Gauss()));
            }
        }
        return o.ToArray();
    }

    static float[] Chords(double dur, params (double t0, double t1, int[] notes)[] chords)
    {
        var o = new float[(int)(dur * R)];
        foreach (var (t0, t1, notes) in chords)
            for (int i = (int)(t0 * R); i < Math.Min(o.Length, (int)(t1 * R)); i++)
            {
                double t = (double)i / R, v = 0;
                foreach (int m in notes)
                {
                    double f = 440 * Math.Pow(2, (m - 69) / 12.0);
                    v += Math.Sin(2 * Math.PI * f * t) + 0.4 * Math.Sin(4 * Math.PI * f * t);
                }
                o[i] += (float)(v * 0.05);
            }
        return o;
    }

    static float[] Mix(float[] a, float[] b)
    {
        var o = new float[Math.Max(a.Length, b.Length)];
        for (int i = 0; i < o.Length; i++) o[i] = (i < a.Length ? a[i] : 0) + (i < b.Length ? b[i] : 0);
        return o;
    }

    static CoachResult Run(float[] v, float[]? back = null)
    {
        if (v.Length < R * 4) Array.Resize(ref v, R * 4);   // short test clips: pad with silence
        return Pipeline.Run(v, back, "synthetic", "file", "tenor");
    }

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new StringBuilder();
        int fails = 0;
        void Check(string name, bool ok, string detail)
        {
            log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            if (!ok) fails++;
        }
        void Try(string name, Action a)
        {
            try { a(); }
            catch (Exception ex) { Check(name, false, "threw " + ex.Message); }
        }

        Try("detuning", () =>
        {
            var exp = new (int midi, double cents)[] { (60, 0), (62, 25), (64, -40), (67, 10), (57, -15), (65, 30), (59, -5) };
            var segs = new List<Seg> { Rest(0.3) };
            foreach (var (m, c) in exp) { segs.Add(Note(0.8, m, c)); segs.Add(Rest(0.25)); }
            var r = Run(Synth(segs));
            Check("detuning · note count", r.Notes.Count == exp.Length, $"{r.Notes.Count} notes, expected {exp.Length}");
            for (int i = 0; i < Math.Min(exp.Length, r.Notes.Count); i++)
            {
                var n = r.Notes[i];
                Check($"detuning · {Feedback.Note(exp[i].midi)} {exp[i].cents:+0;-0;0}¢", n.Target == exp[i].midi && Math.Abs(n.Cents - exp[i].cents) <= 3,
                    $"measured {Feedback.Note(n.Target)} {n.Cents:+0.0;-0.0}¢");
            }
        });

        Try("vibrato", () =>
        {
            var r = Run(Synth([Rest(0.3), new Seg(2.0, t => 57 + 0.40 * Math.Sin(2 * Math.PI * 5.5 * t)), Rest(0.3), Note(1.5, 60), Rest(0.3)]));
            var v = r.Notes.FirstOrDefault(n => n.Target == 57);
            var s = r.Notes.FirstOrDefault(n => n.Target == 60);
            Check("vibrato · detected", v is { Vibrato: true } && Math.Abs(v.VibRate!.Value - 5.5) <= 0.3 && Math.Abs(v.VibExtent!.Value - 40) <= 6,
                v == null ? "note not found" : $"vibrato={v.Vibrato} rate={v.VibRate} extent={v.VibExtent}");
            Check("vibrato · straight note has none", s is { Vibrato: false }, s == null ? "note not found" : $"vibrato={s.Vibrato}");
            Check("vibrato · not counted as wobble", v != null && !v.Flags.Contains('W'), v == null ? "" : $"slow std {v.StdSlow:0.0}¢");
        });

        Try("scoop", () =>
        {
            var r = Run(Synth([Rest(0.3), new Seg(1.0, t => 64 + (t < 0.12 ? -1.2 * (1 - t / 0.12) : 0)), Rest(0.3), Note(1.0, 67), Rest(0.3)]));
            var a = r.Notes.FirstOrDefault(n => n.Target == 64);
            var b = r.Notes.FirstOrDefault(n => n.Target == 67);
            Check("scoop · flagged", a != null && a.Flags.Contains('S'), a == null ? "note not found" : $"onset {a.Onset:0}¢ flags '{a.Flags}'");
            Check("scoop · clean start not flagged", b != null && !b.Flags.Contains('S') && !b.Flags.Contains('A'), b == null ? "note not found" : $"onset {b.Onset:0}¢ flags '{b.Flags}'");
        });

        Try("ending", () =>
        {
            var r = Run(Synth([Rest(0.3), new Seg(1.2, t => 55 + (t > 0.95 ? -1.0 * (t - 0.95) / 0.25 : 0)), Rest(0.3), Note(1.2, 59), Rest(0.3)]));
            var a = r.Notes.FirstOrDefault(n => n.Target == 55);
            var b = r.Notes.FirstOrDefault(n => n.Target == 59);
            Check("ending · fall flagged", a != null && a.Flags.Contains('D'), a == null ? "note not found" : $"end {a.End:0}¢ flags '{a.Flags}'");
            Check("ending · held end not flagged", b != null && !b.Flags.Contains('D') && !b.Flags.Contains('U'), b == null ? "note not found" : $"end {b.End:0}¢ flags '{b.Flags}'");
        });

        Try("legato", () =>
        {
            double Glide(double t, double a, double b, double at) => t < at ? a : t < at + 0.06 ? a + (b - a) * (t - at) / 0.06 : b;
            var r = Run(Synth([Rest(0.3), new Seg(1.6, t => Glide(t, 60, 64, 0.5) + Glide(t, 0, 3, 1.05)), Rest(0.3)]));
            var got = string.Join(" ", r.Notes.Select(n => Feedback.Note(n.Target)));
            Check("legato · three notes", got == "C4 E4 G4", got);
        });

        Try("key", () =>
        {
            int[] mel = [67, 69, 71, 72, 74, 76, 78, 79, 74, 71, 67, 72, 76, 74, 71, 69, 67, 62, 66, 67];
            var segs = new List<Seg> { Rest(0.2) };
            foreach (var m in mel) { segs.Add(Note(0.4, m)); segs.Add(Rest(0.12)); }
            var r = Run(Synth(segs));
            Check("key · G major", r.Key.Tonic == 7 && !r.Key.Minor, Feedback.KeyName(r.Key.Tonic, r.Key.Minor, "en") + $" (r={r.Key.Conf:0.00})");
            Check("key · nothing out of key", r.Key.OutOfKey.Count == 0, $"{r.Key.OutOfKey.Count} groups");
        });

        Try("wobble", () =>
        {
            var r = Run(Synth([Rest(0.3), new Seg(1.6, t => 62 + 0.35 * Math.Sin(2 * Math.PI * 1.5 * t)), Rest(0.3), Note(1.6, 64), Rest(0.3)]));
            var a = r.Notes.FirstOrDefault(n => n.Target == 62);
            var b = r.Notes.FirstOrDefault(n => n.Target == 64);
            Check("wobble · slow drift flagged", a != null && a.Flags.Contains('W'), a == null ? "note not found" : $"slow std {a.StdSlow:0.0}¢");
            Check("wobble · steady note not flagged", b != null && !b.Flags.Contains('W'), b == null ? "note not found" : $"slow std {b.StdSlow:0.0}¢");
        });

        Try("jitter", () =>
        {
            var clean = Run(Synth([Rest(0.3), Note(1.5, 57), Rest(0.3), Note(1.5, 60), Rest(0.3)]));
            var rough = Run(Synth([Rest(0.3), Note(1.5, 57), Rest(0.3), Note(1.5, 60), Rest(0.3)], jitter: 0.02, seed: 3));
            Check("jitter · clean is low", clean.M.Jitter < 0.4, $"{clean.M.Jitter:0.00}%");
            Check("jitter · 2% jitter is high", rough.M.Jitter > 1.0, $"{rough.M.Jitter:0.00}%");
            Check("hnr · clean clearer than jittered", clean.M.Hnr > rough.M.Hnr, $"{clean.M.Hnr:0.0} vs {rough.M.Hnr:0.0} dB");
        });

        Try("music", () =>
        {
            var segs = new List<Seg>();
            int[] mel = [60, 62, 64, 65, 67, 65, 64, 62];
            var chords = new List<(double, double, int[])>();
            double t = 0;
            for (int i = 0; i < mel.Length; i++)
            {
                segs.Add(Rest(0.8)); chords.Add((t, t + 0.8, i % 2 == 0 ? new[] { 48, 52, 55 } : new[] { 43, 47, 50 })); t += 0.8;
                segs.Add(Note(0.7, mel[i])); t += 0.7;
            }
            var voice = Synth(segs);
            var withMusic = Mix(voice, Chords(t, chords.ToArray()));
            var a = Run(withMusic);
            var b = Run(voice);
            Check("music · detected between phrases", a.Music.Detected, $"gap {a.Music.GapDb:0.0} dB, flatness {a.Music.Flatness:0.00}");
            Check("music · clean voice not flagged", !b.Music.Detected, $"gap {b.Music.GapDb:0.0} dB, flatness {b.Music.Flatness:0.00}");
        });

        Try("shift", () =>
        {
            // backing in C major, vocal melody in C# major (one semitone high)
            var chords = new List<(double, double, int[])>();
            double t = 0;
            int[][] prog = [[48, 52, 55, 60], [53, 57, 60, 65], [55, 59, 62, 67], [48, 52, 55, 60]];
            for (int i = 0; i < 12; i++) { chords.Add((t, t + 2, prog[i % 4])); t += 2; }
            var back = Chords(t, chords.ToArray());
            int[] mel = [60, 62, 64, 65, 67, 69, 71, 72, 67, 64, 62, 60, 65, 69, 67, 64, 62, 60, 59, 60];
            var segs = new List<Seg> { Rest(0.5) };
            foreach (var m in mel) { segs.Add(Note(0.9, m + 1)); segs.Add(Rest(0.25)); }
            var r = Run(Synth(segs), back);
            Check("shift · vocal one semitone above the backing", r.Key.Shift == 1,
                $"shift={r.Key.Shift?.ToString() ?? "none"} corr {r.Key.ShiftCorr:0.00} vs {r.Key.ZeroCorr:0.00}; music {Feedback.KeyName(r.Key.BackTonic, r.Key.BackMinor, "en")}");
            var ok = Run(Synth(segs.Select(s => s.Midi == null ? s : new Seg(s.Dur, x => s.Midi(x) - 1)).ToList()), back);
            Check("shift · matching vocal not flagged", ok.Key.Shift == null, $"shift={ok.Key.Shift?.ToString() ?? "none"} corr0 {ok.Key.ZeroCorr:0.00}");
            Check("shift · finding ranked first", r.Weaknesses.Count > 0 && r.Weaknesses[0].Id == "shift", string.Join(", ", r.Weaknesses.Select(w => w.Id)));
        });

        // a longer synthetic "song" exercising the whole report, in both languages
        Try("report", () =>
        {
            var rnd = new Random(7);
            var segs = new List<Seg> { Rest(0.5) };
            int[] mel = [57, 59, 60, 62, 64, 62, 60, 59, 64, 65, 67, 65, 64, 62, 64, 67, 69, 67, 66, 64];
            for (int rep = 0; rep < 3; rep++)
                foreach (var m in mel)
                {
                    double cents = (m >= 64 ? 18 : 0) + rnd.NextDouble() * 24 - 12, dur = 0.5 + rnd.NextDouble() * 0.6;
                    bool scoop = rnd.NextDouble() < 0.3;
                    segs.Add(new Seg(dur, x => m + cents / 100 + (scoop && x < 0.1 ? -0.9 * (1 - x / 0.1) : 0) + (dur > 0.8 ? 0.3 * Math.Sin(2 * Math.PI * 5.8 * x) * Math.Min(1, x / 0.3) : 0)));
                    segs.Add(Rest(0.2 + rnd.NextDouble() * 0.3));
                }
            var r = Run(Synth(segs));
            File.WriteAllText(Path.Combine(dir, "report-th.html"), HtmlReport.Build(r, null, "th"));
            File.WriteAllText(Path.Combine(dir, "report-en.html"), HtmlReport.Build(r, null, "en"));
            File.WriteAllText(Path.Combine(dir, "report.json"), History.ToJson(r, true));
            var back = History.FromFile(Path.Combine(dir, "report.json"));
            Check("report · html + json written and read back", back != null && back.Notes.Count == r.Notes.Count,
                $"{r.Notes.Count} notes, sharp share {r.M.SharpShare:0}%, weaknesses: {string.Join(", ", r.Weaknesses.Select(w => w.Id))}");
        });

        log.Insert(0, $"Vocal Tuner coach self-test · {DateTime.Now:s} · {(fails == 0 ? "ALL PASSED" : fails + " FAILED")}\n\n");
        File.WriteAllText(Path.Combine(dir, "coachtest.txt"), log.ToString(), new UTF8Encoding(true));
        return fails == 0 ? 0 : 1;
    }

    /// <summary>Command-line analysis: VocalTuner.exe --coach &lt;audio&gt; [--backing f] [--lang en|th] [--voice tenor] [--out dir] [--compare a.json …]</summary>
    public static int Cli(string[] args)
    {
        string? audio = null, backing = null, outDir = null; string lang = "en", voice = "tenor";
        var compare = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("missing value after " + a);
            switch (a)
            {
                case "--backing": backing = Next(); break;
                case "--lang": lang = Next() == "th" ? "th" : "en"; break;
                case "--voice": voice = Next(); break;
                case "--out": outDir = Next(); break;
                case "--compare": while (i + 1 < args.Length && !args[i + 1].StartsWith("--")) compare.Add(args[++i]); break;
                default: audio ??= a; break;
            }
        }
        if (audio == null || !File.Exists(audio)) { Console.Error.WriteLine("usage: VocalTuner.exe --coach <audio> [--backing f] [--lang en|th] [--voice tenor] [--out dir] [--compare a.json ...]"); return 2; }
        outDir ??= Path.GetDirectoryName(Path.GetFullPath(audio))!;
        Directory.CreateDirectory(outDir);
        string stem = Path.GetFileNameWithoutExtension(audio);
        try
        {
            var r = Pipeline.Run(Sig.LoadFile(audio), backing != null ? Sig.LoadFile(backing) : null, Path.GetFileName(audio), "file", voice);
            var prev = compare.Select(History.FromFile).Where(x => x != null).OrderBy(x => x!.When).LastOrDefault();
            File.WriteAllText(Path.Combine(outDir, stem + " - report.html"), HtmlReport.Build(r, prev, lang));
            File.WriteAllText(Path.Combine(outDir, stem + " - analysis.json"), History.ToJson(r, true));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(outDir, stem + " - error.txt"), Feedback.FriendlyError(ex, lang) + "\n\n" + ex);
            return 1;
        }
    }
}
