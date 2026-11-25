using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VocalTuner;

/// <summary>
/// Guided warm-up: the piano plays a starting chord, then a 5-note scale (1-2-3-4-5-4-3-2-1) or an arpeggio
/// (1-3-5-8-5-3-1); every round moves up a semitone up to the chosen top note (and optionally back down).
/// Your pitch is checked against each target note as you sing.
/// </summary>
public partial class WarmupView : UserControl
{
    MainWindow M = null!;
    AppSettings S => M.S;
    PianoSynth Syn => M.Synth;

    static readonly int[] Five = [0, 2, 4, 5, 7, 5, 4, 2, 0];
    static readonly int[] Arp = [0, 4, 7, 12, 7, 4, 0];
    int[] Pattern => S.WarmEx == "arp" ? Arp : Five;
    const int CueBeats = 2, LastBeats = 2;      // chord + breath before each round; last note held
    const double OutLatencyMs = 50;             // stream clock runs ahead of what you hear by about the buffer size
    const int MinNote = 36, MaxNote = 84;       // C2 … C6

    sealed class Step
    {
        public double T0, T1;                   // ms, same clock as the pitch trace
        public int Midi, Round, Index;
        public readonly List<double> Cents = new();
        public TargetGrade Grade;
    }

    public bool Running { get; private set; }
    readonly List<Step> steps = new();
    List<int> roots = new();
    int nextRound, streamGen, shownRound = -1;
    double nextRoundAt;                         // stream seconds
    double liveCents;
    bool loading;

    public WarmupView() => InitializeComponent();

    public void Init(MainWindow m)
    {
        M = m;
        loading = true;
        var notes = Enumerable.Range(MinNote, MaxNote - MinNote + 1).Select(n => (n.ToString(), Name(n))).ToList();
        StartSel.SetItems(notes, S.WarmStart.ToString());
        TopSel.SetItems(notes, S.WarmTop.ToString());
        (S.WarmEx == "arp" ? ExArp : ExFive).IsChecked = true;
        BpmSl.Value = S.WarmBpm; VolSl.Value = S.PianoVol;
        DownChk.IsChecked = S.WarmDown; MelodyChk.IsChecked = S.WarmMelody;
        loading = false;

        ExFive.Checked += (_, _) => SetEx("five");
        ExArp.Checked += (_, _) => SetEx("arp");
        StartSel.SelectionChanged += v => { S.WarmStart = int.Parse(v); if (S.WarmTop < S.WarmStart) { S.WarmTop = S.WarmStart; TopSel.Select(v, false); } Changed(); };
        TopSel.SelectionChanged += v => { S.WarmTop = int.Parse(v); if (S.WarmStart > S.WarmTop) { S.WarmStart = S.WarmTop; StartSel.Select(v, false); } Changed(); };
        PresetLow.Click += (_, _) => Preset(48, 57);
        PresetHigh.Click += (_, _) => Preset(60, 67);
        BpmSl.Changed += v => { S.WarmBpm = (int)v; ShowSetup(); };
        BpmSl.Committed += _ => S.Save();
        VolSl.Changed += v => { S.PianoVol = (int)v; VolVal.Text = (int)v + "%"; M.PianoVolumeChanged(); };
        VolSl.Committed += _ => S.Save();
        DownChk.Click += (_, _) => { S.WarmDown = DownChk.IsChecked == true; Changed(); };
        MelodyChk.Click += (_, _) => { S.WarmMelody = MelodyChk.IsChecked == true; Changed(); };
        GoBtn.Click += (_, _) => Toggle();
        ShowSetup();
        ShowIdle();
    }

    static string Name(int midi) => Draw.NoteName(midi) + Draw.Octave(midi);

    void SetEx(string ex) { if (loading) return; S.WarmEx = ex; Changed(); }

    void Preset(int lo, int hi)
    {
        S.WarmStart = lo; S.WarmTop = hi;
        StartSel.Select(lo.ToString(), false); TopSel.Select(hi.ToString(), false);
        Changed();
    }

    void Changed()
    {
        if (loading) return;
        S.Save();
        ShowSetup();
        if (Running) { Stop(); Start(); }      // apply new settings right away
        else ShowIdle();
    }

    /// <summary>The piano volume slider on the Piano page changed: mirror it here.</summary>
    public void SyncVolume() { VolSl.Value = S.PianoVol; VolVal.Text = S.PianoVol + "%"; }

    List<int> Roots()
    {
        var up = Enumerable.Range(S.WarmStart, S.WarmTop - S.WarmStart + 1).ToList();
        if (S.WarmDown) up.AddRange(Enumerable.Range(S.WarmStart, S.WarmTop - S.WarmStart).Reverse());
        return up;
    }

    int BeatsPerRound => CueBeats + Pattern.Length - 1 + LastBeats;

    void ShowSetup()
    {
        PatternTxt.Text = S.WarmEx == "arp" ? "1 3 5 8 5 3 1   ·   do mi sol do sol mi do" : "1 2 3 4 5 4 3 2 1   ·   do re mi fa sol fa mi re do";
        BpmVal.Text = S.WarmBpm + " BPM";
        VolVal.Text = S.PianoVol + "%";
        var r = Roots();
        double secs = r.Count * BeatsPerRound * 60.0 / S.WarmBpm;
        int highest = S.WarmTop + Pattern.Max();
        PlanTxt.Text = $"{Name(S.WarmStart)} → {Name(S.WarmTop)}{(S.WarmDown ? " and back" : "")} · {r.Count} rounds · highest note {Name(highest)} · about {Math.Max(1, (int)Math.Round(secs / 60))} min";
    }

    // ------------------------------------------------------------------ run

    public void Toggle() { if (Running) Stop(); else Start(); }

    public void Start()
    {
        try { Syn.Open(); } catch (Exception ex) { RoundTxt.Text = "Can't open the sound output: " + MainWindow.Friendly(ex); return; }
        streamGen = Syn.StreamGen;
        roots = Roots();
        steps.Clear();
        nextRound = 0; shownRound = -1;
        nextRoundAt = Syn.Now + 0.3;
        Running = true;
        ScheduleAhead();
        GoLbl.Text = "Stop";
        GoIcon.Data = Geometry.Parse("M0,0 H10 V10 H0 Z");
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        Syn.CancelScheduled();
        GoLbl.Text = "Start warm-up";
        GoIcon.Data = Geometry.Parse("M0,0 L10,6 L0,12 Z");
        ShowIdle();
    }

    void ScheduleAhead()
    {
        while (Running && nextRound < roots.Count && nextRoundAt - Syn.Now < 1.5) ScheduleRound();
    }

    void ScheduleRound()
    {
        int root = roots[nextRound];
        double beat = 60.0 / S.WarmBpm, t = nextRoundAt, vol = S.PianoVol / 100.0;
        double wall = LiveInput.NowMs - Syn.Now * 1000 + OutLatencyMs;   // stream seconds -> trace ms
        // starting chord (root position) so you hear the new key, then a breath
        foreach (int n in new[] { root, root + 4, root + 7 }) Syn.Schedule(t, n, vol * 0.62, beat * CueBeats * 0.85);
        t += beat * CueBeats;
        var pat = Pattern;
        for (int i = 0; i < pat.Length; i++)
        {
            double len = (i == pat.Length - 1 ? LastBeats : 1) * beat;
            int midi = root + pat[i];
            if (S.WarmMelody) Syn.Schedule(t, midi, vol, len * 0.93);
            steps.Add(new Step { T0 = t * 1000 + wall, T1 = (t + len) * 1000 + wall, Midi = midi, Round = nextRound, Index = i });
            t += len;
        }
        nextRoundAt = t;
        nextRound++;
    }

    /// <summary>A raw pitch result from the mic (time in ms, Hz or NaN).</summary>
    public void OnPitch(double tMs, double freq)
    {
        if (!Running || double.IsNaN(freq) || freq <= 0) return;
        var st = steps.FindLast(s => tMs >= s.T0 + 120 && tMs <= s.T1 - 30);   // skip the scoop into each note
        if (st == null) return;
        st.Cents.Add(Fold(69 + 12 * Math.Log2(freq / 440.0) - st.Midi) * 100);
    }

    /// <summary>Semitones off, folded to ±6 so singing an octave up or down still counts.</summary>
    static double Fold(double semis) => semis - 12 * Math.Round(semis / 12);

    public void OnFrame(double now)
    {
        if (Running && Syn.StreamGen != streamGen) { Stop(); RoundTxt.Text = "stopped: the sound output changed"; }
        if (Running) ScheduleAhead();

        // grade finished notes
        foreach (var s in steps)
        {
            if (s.Grade != TargetGrade.Pending || now < s.T1 + 80) continue;
            if (s.Cents.Count < 3) { s.Grade = TargetGrade.Missed; continue; }
            var sorted = s.Cents.Select(Math.Abs).OrderBy(c => c).ToList();
            double med = sorted[sorted.Count / 2];
            s.Grade = med <= 15 ? TargetGrade.Good : med <= 35 ? TargetGrade.Close : TargetGrade.Off;
        }

        var cur = steps.Find(s => now >= s.T0 && now < s.T1);
        var next = cur ?? steps.Find(s => s.T0 > now);
        if (Running && next == null && nextRound >= roots.Count && steps.Count > 0 && now > steps[^1].T1 + 300) Finish();

        // big target + round info
        if (Running)
        {
            var show = cur ?? next;
            if (show != null)
            {
                TargetEl.Text = Name(show.Midi);
                TargetEl.SetResourceReference(TextBlock.ForegroundProperty, cur != null ? "Text" : "Faint");
                if (show.Round != shownRound) { shownRound = show.Round; BuildChips(show.Round); }
                RoundTxt.Text = $"key of {Draw.NoteName(roots[show.Round])} · round {show.Round + 1} of {roots.Count}";
            }
            UpdateChips(cur);
        }

        // your pitch against the current target
        var tr = M.Tracker;
        if (tr.Voiced && tr.Freq > 0)
        {
            double midi = 69 + 12 * Math.Log2(tr.Freq / 440.0);
            YouEl.Text = Name((int)Math.Round(midi));
            YouEl.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            if (cur != null)
            {
                double c = Fold(midi - cur.Midi) * 100;
                liveCents += (c - liveCents) * 0.3;
                double sc = Math.Clamp(liveCents, -50, 50);
                YouCents.Text = (sc >= 0 ? "+" : "") + sc.ToString("0") + " ¢";
                YouCents.Foreground = Theme.P.B(Draw.CentsColor(sc / 3));        // warm-up tolerance: ±15 ¢ shows green
                WBar.Cents = sc; WBar.Active = true;
            }
            else { YouCents.Text = "— ¢"; YouCents.SetResourceReference(TextBlock.ForegroundProperty, "Faint"); WBar.Active = false; }
        }
        else
        {
            YouEl.Text = "—"; YouEl.SetResourceReference(TextBlock.ForegroundProperty, "Off");
            YouCents.Text = "— ¢"; YouCents.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
            WBar.Active = false;
        }
        WBar.InvalidateVisual();

        // pitch roll: 5.5 s of history, 2.5 s of upcoming notes, "now" line
        WRoll.Points = tr.Trace;
        WRoll.TRight = now + 2500; WRoll.FocusT = now; WRoll.PlayT = Running ? now : null; WRoll.MinBar = S.MinBar;
        WRoll.Targets = steps.Where(s => s.T1 > now - 8000 && s.T0 < now + 3000)
                             .Select(s => new RollTarget(s.T0, s.T1, s.Midi, s.Grade, s == cur)).ToList();
        WRoll.FocusMidi = Running ? (cur ?? next)?.Midi : null;
        WRoll.InvalidateVisual();
        steps.RemoveAll(s => s.T1 < now - 9000);
    }

    void Finish()
    {
        var done = steps.Where(s => s.Grade != TargetGrade.Pending).ToList();
        Stop();
        int good = done.Count(s => s.Grade == TargetGrade.Good), sung = done.Count(s => s.Grade != TargetGrade.Missed);
        TargetEl.Text = "Done";
        TargetEl.SetResourceReference(TextBlock.ForegroundProperty, "Green");
        RoundTxt.Text = sung == 0 ? "warm-up finished" : $"last rounds: {good} of {sung} sung notes on pitch";
    }

    // ------------------------------------------------------------------ pattern chips

    readonly List<Border> chips = new();
    int chipRound = -1;

    void BuildChips(int round)
    {
        chipRound = round;
        Chips.Children.Clear(); chips.Clear();
        foreach (var s in steps.Where(s => s.Round == round).OrderBy(s => s.Index))
        {
            var b = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(3, 0, 3, 0), BorderThickness = new Thickness(1) };
            b.Child = new TextBlock { Text = Name(s.Midi), FontSize = 12, FontFamily = Theme.Mono };
            b.Tag = s;
            chips.Add(b); Chips.Children.Add(b);
        }
        UpdateChips(null);
    }

    void UpdateChips(Step? cur)
    {
        var P = Theme.P;
        foreach (var b in chips)
        {
            var s = (Step)b.Tag;
            var (bg, edge, fg) = s == cur ? (P.Text, P.Text, P.Bg)
                : s.Grade switch
                {
                    TargetGrade.Good => (P.Green, P.Green, P.Bg),
                    TargetGrade.Close => (P.Amber, P.Amber, P.Bg),
                    TargetGrade.Off => (P.Red, P.Red, P.Bg),
                    TargetGrade.Missed => (P.Muted, P.Border, P.Faint),
                    _ => (P.Muted, P.Border, P.Sub),
                };
            b.Background = P.B(bg); b.BorderBrush = P.B(edge); ((TextBlock)b.Child).Foreground = P.B(fg);
        }
    }

    void ShowIdle()
    {
        if (Running) return;
        TargetEl.Text = Name(S.WarmStart);
        TargetEl.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        RoundTxt.Text = "first note · press Start";
        Chips.Children.Clear(); chips.Clear(); chipRound = -1;
        foreach (int off in Pattern)
        {
            var b = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(3, 0, 3, 0), BorderThickness = new Thickness(1) };
            b.SetResourceReference(Border.BackgroundProperty, "Muted"); b.SetResourceReference(Border.BorderBrushProperty, "BorderB");
            var t = new TextBlock { Text = Name(S.WarmStart + off), FontSize = 12, FontFamily = Theme.Mono };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Sub");
            b.Child = t; Chips.Children.Add(b);
        }
        WBar.Active = false; WBar.InvalidateVisual();
    }
}
