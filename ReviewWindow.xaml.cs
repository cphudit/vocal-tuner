using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Path = System.IO.Path;

namespace VocalTuner;

/// <summary>Mix settings of the current take; kept by the main window so closing/reopening the review keeps them.</summary>
public sealed class TakeSession(TakeInfo take, string mode, string pair)
{
    public readonly TakeInfo Take = take;
    public readonly string Mode = mode, Pair = pair;
    public double VoiceDb, BackDb, SyncMs;
    public bool Exported;
    public List<RollPoint>? Points;
}

/// <summary>Pop-up window for listening to, mixing and exporting the last take. Closing it keeps the take.</summary>
public partial class ReviewWindow : Window
{
    readonly MainWindow M;
    readonly TakeSession T;
    AppSettings S => M.S;
    TakeInfo Take => T.Take;
    List<RollPoint> pts = new();
    bool playOn; double playPos, tickPos, tickAt;
    const double BackMinDb = -40;

    public ReviewWindow(MainWindow main, TakeSession session)
    {
        M = main; T = session;
        InitializeComponent();
        Theme.Track(this);
        Body.MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 80);

        PlayBtn.Click += (_, _) => TogglePlay();
        Wave.Seek += f => Seek(f * Take.Duration);
        VoiceGainSl.Changed += v => { T.VoiceDb = v; MixChanged(false); };
        BackGainSl.Changed += v => { T.BackDb = v; MixChanged(false); };
        SyncSl.Changed += v => { T.SyncMs = v; MixChanged(false); };
        SyncSl.Committed += _ => MixChanged(true);
        NudgeMinus.Click += (_, _) => Nudge(-10);
        NudgePlus.Click += (_, _) => Nudge(10);
        FmtSel.SetItems([("wav24", "WAV · 24-bit (lossless, best for analysis)"), ("wav32f", "WAV · 32-bit float (lossless)"), ("mp3", "MP3 · 320 kbps (small, for sharing)")], S.ExportFormat);
        FmtSel.SelectionChanged += v => { S.ExportFormat = v; S.Save(); };
        NormChk.IsChecked = S.Normalize;
        VoiceChk.IsChecked = S.AlsoVoice;
        NormChk.Click += (_, _) => { S.Normalize = NormChk.IsChecked == true; S.Save(); };
        VoiceChk.Click += (_, _) => { S.AlsoVoice = VoiceChk.IsChecked == true; S.Save(); };
        ExportBtn.Click += async (_, _) => await ExportTake();
        CoachBtn.Click += async (_, _) => { StopPreview(); CoachBtn.IsEnabled = false; await M.PageCoach.AnalyzeTake(); CoachBtn.IsEnabled = true; };
        PreviewKeyDown += OnKey;
        Closing += (_, _) => StopPreview();

        // pitch points for the roll (built once per take)
        if (T.Points == null)
        {
            var list = new List<RollPoint>();
            double hopMs = Take.PitchHop * 1000;
            for (int i = 0; i < Take.Pitch.Length; i++)
            {
                float m = Take.Pitch[i];
                if (float.IsNaN(m) || m <= 0) { if (list.Count > 0 && !list[^1].IsGap) list.Add(RollPoint.Gap(i * hopMs)); continue; }
                int r = (int)Math.Round(m);
                list.Add(new RollPoint(i * hopMs, m, r, (m - r) * 100));
            }
            T.Points = list;
        }
        pts = T.Points;

        VoiceGainSl.Value = T.VoiceDb; BackGainSl.Value = T.BackDb; SyncSl.Value = T.SyncMs;
        var bv = Take.HasBacking ? Visibility.Visible : Visibility.Collapsed;
        BackGainRow.Visibility = bv; SyncRow.Visibility = bv; SyncHint.Visibility = bv; VoiceChk.Visibility = bv;
        Title = "Last take · " + MainWindow.Fmt(Take.Duration);
        TakeLen.Text = "· " + MainWindow.Fmt(Take.Duration);
        TakeInfoTxt.Text = (Take.SampleRate / 1000.0) + " kHz · " +
            (T.Mode == "file" ? "voice + backing file" : T.Mode == "system" ? "voice + system audio" : "voice only");
        UpdateMixLabels(); UpdatePlayUi();
        Loaded += (_, _) => DrawReview();
    }

    string SyncKey => (Take.SyncBasis is { } b && b != "none" ? b : T.Mode) + "-" + T.Pair;
    double OffsetMs => Take.EstOffsetMs + T.SyncMs;
    static float DbToGain(double db, double? min = null) => min.HasValue && db <= min ? 0 : (float)Math.Pow(10, db / 20);

    void UpdateMixLabels()
    {
        VoiceGainVal.Text = (T.VoiceDb > 0 ? "+" : "") + T.VoiceDb + " dB";
        BackGainVal.Text = T.BackDb <= BackMinDb ? "off" : (T.BackDb > 0 ? "+" : "") + T.BackDb + " dB";
        SyncVal.Text = T.SyncMs == 0 ? "as recorded" : Math.Abs(T.SyncMs) + " ms " + (T.SyncMs < 0 ? "earlier" : "later");
    }

    void MixChanged(bool persistSync)
    {
        UpdateMixLabels(); DrawReview();
        if (persistSync && Take.HasBacking) { S.Sync[SyncKey] = T.SyncMs; S.Save(); }
        if (playOn) M.Engine.PreviewParams(DbToGain(T.VoiceDb), DbToGain(T.BackDb, BackMinDb), OffsetMs);
    }

    void Nudge(double d)
    {
        T.SyncMs = Math.Clamp(T.SyncMs + d, -500, 500);
        SyncSl.Value = T.SyncMs; MixChanged(true);
    }

    // ------------------------------------------------------------------ playback

    double CurPos() => !playOn ? playPos : Math.Min(Take.Duration, tickPos + (LiveInput.NowMs - tickAt) / 1000);

    void TogglePlay() { if (playOn) StopPreview(); else StartPreview(); }

    void StartPreview()
    {
        if (playPos >= Take.Duration - 0.05) playPos = 0;
        try { M.Engine.PreviewPlay(S.OutId, playPos, DbToGain(T.VoiceDb), DbToGain(T.BackDb, BackMinDb), OffsetMs); }
        catch (Exception ex) { ShowErr("Playback failed: " + MainWindow.Friendly(ex)); return; }
        ShowErr(null);
        playOn = true; tickPos = playPos; tickAt = LiveInput.NowMs;
        UpdatePlayUi();
    }

    public void StopPreview()
    {
        if (!playOn) return;
        playPos = CurPos(); playOn = false;
        M.Engine.StopPreview();
        UpdatePlayUi(); DrawReview();
    }

    /// <summary>Called by the main window's frame loop with the engine's playback clock.</summary>
    public void OnFrame(EngineTick? tick, double now)
    {
        if (!playOn) return;
        if (tick is { Playing: true }) { tickPos = tick.PlayPos; tickAt = now; }
        DrawReview();
    }

    public void OnPreviewEnded()
    {
        if (!playOn) return;
        playOn = false;
        playPos = CurPos();
        if (playPos >= Take.Duration - 0.25) playPos = 0;
        UpdatePlayUi(); DrawReview();
    }

    void UpdatePlayUi()
    {
        IconPlay.Visibility = playOn ? Visibility.Collapsed : Visibility.Visible;
        IconPause.Visibility = playOn ? Visibility.Visible : Visibility.Collapsed;
    }

    void Seek(double sec)
    {
        sec = Math.Clamp(sec, 0, Take.Duration);
        playPos = sec;
        if (playOn) { tickPos = sec; tickAt = LiveInput.NowMs; M.Engine.PreviewSeek(sec); }
        ReviewRoll.ResetView();
        DrawReview();
    }

    void ShowErr(string? msg)
    {
        ErrTxt.Text = msg ?? "";
        ErrTxt.Visibility = msg == null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void DrawReview()
    {
        double pos = CurPos();
        PlayTime.Text = MainWindow.Fmt(pos) + " / " + MainWindow.Fmt(Take.Duration);
        int idx = (int)Math.Round(pos / Take.PitchHop);
        float m = idx >= 0 && idx < Take.Pitch.Length ? Take.Pitch[idx] : float.NaN;
        if (!float.IsNaN(m) && m > 0)
        {
            int r = (int)Math.Round(m); double c = (m - r) * 100;
            PlayNote.Text = Draw.NoteName(r) + Draw.Octave(r) + " " + (c >= 0 ? "+" : "") + c.ToString("0") + "¢";
            PlayNote.Foreground = Theme.P.B(Draw.CentsColor(c));
        }
        else PlayNote.Text = "";

        Wave.Take = Take; Wave.OffsetSec = OffsetMs / 1000; Wave.Pos = pos; Wave.InvalidateVisual();

        const double WIN = 10000;
        double pMs = pos * 1000;
        int lo = LowerBound(pts, pMs - WIN), hi = LowerBound(pts, pMs + WIN);
        ReviewRoll.Points = pts.GetRange(lo, hi - lo);
        ReviewRoll.TRight = pMs + WIN * 0.65; ReviewRoll.FocusT = pMs; ReviewRoll.PlayT = pMs; ReviewRoll.MinBar = S.MinBar;
        ReviewRoll.InvalidateVisual();
    }

    static int LowerBound(List<RollPoint> list, double t)
    {
        int a = 0, b = list.Count;
        while (a < b) { int m = (a + b) / 2; if (list[m].T < t) a = m + 1; else b = m; }
        return a;
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        else if (e.Key == Key.Space && !e.IsRepeat) { e.Handled = true; TogglePlay(); }
    }

    // ------------------------------------------------------------------ export

    async Task ExportTake()
    {
        StopPreview();
        string fmt = FmtSel.SelectedValue ?? "wav24", ext = fmt == "mp3" ? ".mp3" : ".wav";
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export recording",
            FileName = $"Vocal take {DateTime.Now:yyyy-MM-dd HH.mm}{ext}",
            Filter = fmt == "mp3" ? "MP3 audio|*.mp3" : "WAV audio|*.wav",
            DefaultExt = ext,
            OverwritePrompt = true,
            InitialDirectory = Directory.Exists(S.LastDir) ? S.LastDir : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        };
        if (dlg.ShowDialog(this) != true) return;
        string path = dlg.FileName;
        float vg = DbToGain(T.VoiceDb), bg = DbToGain(T.BackDb, BackMinDb);
        double off = OffsetMs;
        bool norm = NormChk.IsChecked == true, alsoVoice = VoiceChk.IsChecked == true && Take.HasBacking;
        ExportBtn.IsEnabled = false; ExportBtn.Content = "Exporting…";
        ExportRes.Children.Clear();
        try
        {
            var r = await Task.Run(() => M.Engine.Export(path, fmt, vg, bg, off, norm, alsoVoice));
            T.Exported = true;
            S.LastDir = Path.GetDirectoryName(r.Files[0]) ?? ""; S.Save();
            var head = new TextBlock { FontSize = 12.5 };
            head.SetResourceReference(TextBlock.ForegroundProperty, "Sub");
            var ok = new Run("Saved") { FontWeight = FontWeights.SemiBold }; ok.SetResourceReference(TextElement.ForegroundProperty, "Green");
            head.Inlines.Add(ok);
            head.Inlines.Add(new Run($" · peak {r.PeakDb} dBFS" + (r.GainDb != 0 ? $" · gain {(r.GainDb > 0 ? "+" : "")}{r.GainDb} dB" : "")));
            ExportRes.Children.Add(head);
            foreach (var f in r.Files)
            {
                var link = new Hyperlink(new Run(f)) { ToolTip = "Show in folder" };
                link.SetResourceReference(TextElement.ForegroundProperty, "Green");
                string file = f;
                link.Click += (_, _) => { if (File.Exists(file)) Process.Start("explorer.exe", $"/select,\"{file}\""); };
                ExportRes.Children.Add(new TextBlock(link) { FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
            }
        }
        catch (Exception ex)
        {
            var err = new TextBlock { Text = "Export failed: " + MainWindow.Friendly(ex), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            err.SetResourceReference(TextBlock.ForegroundProperty, "Err");
            ExportRes.Children.Add(err);
        }
        ExportRes.Visibility = Visibility.Visible;
        ExportBtn.IsEnabled = true; ExportBtn.Content = "Export audio…";
    }
}
