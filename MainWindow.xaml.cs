using System.IO;
using Path = System.IO.Path;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace VocalTuner;

public partial class MainWindow : Window
{
    public readonly AppSettings S;
    public readonly AudioEngine Engine = new();
    readonly LiveInput live = new();
    readonly PitchTracker tracker = new();
    readonly PianoSynth synth = new();
    public PianoSynth Synth => synth;
    public PitchTracker Tracker => tracker;
    DeviceWatcher? watcher;
    public DeviceList Devices { get; private set; } = new([], []);
    SettingsWindow? settingsWin;

    // live state
    float livePeak;
    double lastTextAt;
    readonly DispatcherTimer retryTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    readonly DispatcherTimer deviceDebounce = new() { Interval = TimeSpan.FromMilliseconds(600) };
    bool micFailed;

    // recorder state
    public bool Recording { get; private set; }
    public bool Busy { get; private set; }
    readonly DispatcherTimer autoStop = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    string recMode = "none";
    bool closingAfterStop;

    // last take (reviewed in its own window; closing the window keeps the take until the next recording)
    TakeSession? session;
    ReviewWindow? reviewWin;

    public MainWindow(AppSettings settings)
    {
        S = settings;
        InitializeComponent();
        Theme.Track(this);
        var wa = SystemParameters.WorkArea;
        Height = Math.Min(1000, wa.Height - 40);
        Width = Math.Min(860, wa.Width - 40);

        // header
        TabTuner.Checked += (_, _) => SetPage("tuner");
        TabPiano.Checked += (_, _) => SetPage("piano");
        ThemeBtn.Click += (_, _) => { S.Dark = !S.Dark; S.Save(); Theme.Apply(S.Dark); UpdateThemeIcon(); };
        SettingsBtn.Click += (_, _) => { if (settingsWin != null) settingsWin.Close(); else OpenSettings(); };
        RecSummary.Click += (_, _) => OpenSettings();
        PianoOut.Click += (_, _) => OpenSettings();

        // recorder
        RecBtn.Click += (_, _) => ToggleRecording();
        autoStop.Tick += (_, _) => { autoStop.Stop(); _ = StopRecording("track ended"); };
        Engine.BackingEnded += err => Dispatcher.BeginInvoke(() =>
        {
            if (!Recording) return;
            if (err != null) { ShowRecErr("Playback error: " + err); _ = StopRecording(null); return; }
            RecState.Text = "track ended";
            autoStop.Start();
        });
        Engine.RecordError += (src, msg) => Dispatcher.BeginInvoke(() =>
        {
            ShowRecErr($"The {src} stopped: {msg}");
            _ = StopRecording(null);
        });
        Engine.PreviewEnded += () => Dispatcher.BeginInvoke(() => reviewWin?.OnPreviewEnded());
        LastTakeBtn.Click += (_, _) => OpenReview();

        // piano
        PianoVol.Value = S.PianoVol; PianoVolVal.Text = S.PianoVol + "%";
        PianoVol.Changed += v => { S.PianoVol = (int)v; PianoVolVal.Text = (int)v + "%"; PageWarm.SyncVolume(); };
        PianoVol.Committed += _ => S.Save();
        Keyboard.Struck += Strike;
        OctDown.Click += (_, _) => ShiftOct(-1);
        OctUp.Click += (_, _) => ShiftOct(1);
        ShiftOct(0);
        ApplyPianoKeys();
        PianoKeysLink.Click += (_, _) => OpenSettings(pianoKeys: true);

        // live input
        live.RmsThreshold = (float)S.RmsThreshold; live.Clarity = (float)S.Clarity;
        live.Failed += _ => Dispatcher.BeginInvoke(() => RestartListening(TimeSpan.FromSeconds(1)));
        retryTimer.Tick += (_, _) => { retryTimer.Stop(); StartListening(); };
        deviceDebounce.Tick += (_, _) =>
        {
            deviceDebounce.Stop();
            RefreshDevices();
            if (!Recording) RestartListening(TimeSpan.Zero);
        };

        PreviewKeyDown += OnKey;
        // record shortcut: one class handler sees key presses in every window of the app
        EventManager.RegisterClassHandler(typeof(Window), PreviewKeyDownEvent, new KeyEventHandler(OnAnyWindowKey));
        SourceInitialized += (_, _) =>
        {
            hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            ApplyShortcut();
        };
        ApplyShortcut();
        Closing += OnClosing;
        UpdateThemeIcon();
        PageWarm.Init(this);
        TabWarm.Checked += (_, _) => SetPage("warmup");
        string startPage = S.Page is "piano" or "warmup" ? S.Page : "tuner";
        (startPage == "piano" ? TabPiano : startPage == "warmup" ? TabWarm : TabTuner).IsChecked = true;
        SetPage(startPage);
        UpdateRecUi(); UpdateStatus(); UpdateSummary(); ClearNote();

        // Show the window first, then open audio devices.
        ContentRendered += (_, _) =>
        {
            RefreshDevices();
            StartListening();
            watcher = new DeviceWatcher();
            watcher.Changed += () => Dispatcher.BeginInvoke(() => { deviceDebounce.Stop(); deviceDebounce.Start(); });
            CompositionTarget.Rendering += OnFrame;
            if (S.BackFile != null)
            {
                var path = S.BackFile.Path;
                Task.Run(() => { try { return AudioEngine.InspectFile(path); } catch { return null; } })
                    .ContinueWith(t => Dispatcher.BeginInvoke(() => { S.BackFile = t.Result; S.Save(); UpdateSummary(); settingsWin?.Refresh(); }));
            }
        };
    }

    // ------------------------------------------------------------------ pages, theme, status

    void SetPage(string p)
    {
        S.Page = p; S.Save();
        if (p != "warmup") PageWarm.Stop();
        PageTuner.Visibility = p == "tuner" ? Visibility.Visible : Visibility.Collapsed;
        PagePiano.Visibility = p == "piano" ? Visibility.Visible : Visibility.Collapsed;
        PageWarm.Visibility = p == "warmup" ? Visibility.Visible : Visibility.Collapsed;
        Subtitle.Text = p switch
        {
            "piano" => "Find your starting note",
            "warmup" => "Warm up with the piano",
            _ => "Sing, check your pitch, record",
        };
    }

    /// <summary>The piano volume was changed on the Warm-up page: mirror it on the Piano page.</summary>
    public void PianoVolumeChanged() { PianoVol.Value = S.PianoVol; PianoVolVal.Text = S.PianoVol + "%"; }

    void UpdateThemeIcon()
    {
        IconMoon.Visibility = S.Dark ? Visibility.Collapsed : Visibility.Visible;
        IconSun.Visibility = S.Dark ? Visibility.Visible : Visibility.Collapsed;
        ThemeBtn.ToolTip = S.Dark ? "Light mode" : "Dark mode";
    }

    static void Pulse(UIElement el, bool on, double seconds)
    {
        if (!on) { el.BeginAnimation(OpacityProperty, null); el.Opacity = 1; return; }
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(seconds / 2))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
    }

    string statusKey = "";
    void UpdateStatus()
    {
        string key = Recording ? "rec" : live.Running ? "live" : micFailed ? "fail" : "start";
        if (key == statusKey) return;
        statusKey = key;
        LiveDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, key == "rec" ? "Red" : key == "live" ? "Green" : "Off");
        Pulse(LiveDot, key is "rec" or "live", key == "rec" ? 1.2 : 1.6);
        StatusTxt.Text = key switch { "rec" => "recording", "live" => "listening", "fail" => "no mic", _ => "starting…" };
    }

    // ------------------------------------------------------------------ devices & live input

    /// <summary>Calibration / sync key: the mic plus the output device actually in use.</summary>
    public string PairKey => S.MicId + "|" + OutDeviceId;

    /// <summary>The output device the sound goes to right now: the chosen one, or the Windows default.</summary>
    public string OutDeviceId =>
        (Devices.Outputs.FirstOrDefault(d => d.Id == S.OutId && S.OutId != "") ??
         Devices.Outputs.FirstOrDefault(d => d.IsDefault) ?? Devices.Outputs.FirstOrDefault())?.Id ?? "";

    public string? OutDeviceName => Devices.Outputs.FirstOrDefault(d => d.Id == OutDeviceId)?.Name;

    /// <summary>Call after the output choice (or the Windows default) changed.</summary>
    public void ApplyOutput()
    {
        try { synth.SetDevice(S.OutId, OutDeviceId); } catch { }
        UpdateSummary();
    }

    public void RefreshDevices()
    {
        try { Devices = Engine.ListDevices(); } catch { return; }
        string Pick(List<DeviceInfo> list, string cur) =>
            (list.FirstOrDefault(d => d.Id == cur) ?? list.FirstOrDefault(d => d.IsDefault) ?? list.FirstOrDefault())?.Id ?? "";
        S.MicId = Pick(Devices.Inputs, S.MicId);
        // the output choice is kept as is ("" = Windows default); an unplugged choice plays on the default meanwhile
        try { synth.SetDevice(S.OutId, OutDeviceId); } catch { }
        if (Devices.Inputs.Count == 0) ShowRecErr("No microphone found. Connect one; the app picks it up automatically.");
        else if (RecErr.Text.StartsWith("No microphone")) ShowRecErr(null);
        UpdateSummary();
        settingsWin?.Refresh();
    }

    public void StartListening()
    {
        if (live.Running) return;
        retryTimer.Stop();
        try
        {
            if (Environment.GetEnvironmentVariable("VOCALTUNER_LIVE_LOOPBACK") == "1")
                live.StartLoopback(Engine.Device(S.OutId, DataFlow.Render));
            else
                live.Start(Engine.Device(S.MicId, DataFlow.Capture));
            micFailed = false;
            MicErr.Visibility = Visibility.Collapsed;
        }
        catch
        {
            micFailed = true;
            MicErr.Visibility = Visibility.Visible;
            retryTimer.Start();      // keep trying quietly (mic plugged in later, permission fixed…)
        }
        UpdateStatus();
    }

    public void RestartListening(TimeSpan delay)
    {
        live.Stop();
        tracker.Reset(); ClearNote();
        UpdateStatus();
        retryTimer.Stop();
        if (delay <= TimeSpan.Zero) StartListening();
        else { retryTimer.Interval = delay; retryTimer.Start(); }
        retryTimer.Interval = TimeSpan.FromSeconds(4);
    }

    public void DetectionChanged()
    {
        live.RmsThreshold = (float)S.RmsThreshold; live.Clarity = (float)S.Clarity;
        reviewWin?.DrawReview();
    }

    void OnFrame(object? sender, EventArgs e)
    {
        double now = LiveInput.NowMs;
        while (live.TryDequeue(out var r))
        {
            tracker.Process(r.TimeMs, r.Freq, S.Alpha);
            PageWarm.OnPitch(r.TimeMs, r.Freq);
            livePeak = Math.Max(r.Peak, livePeak * 0.9f);
        }

        if (tracker.Voiced && tracker.DispKey is int key)
        {
            double sc = Math.Clamp(tracker.SmoothCents, -50, 50);
            bool doText = now - lastTextAt > 150;
            if (doText) lastTextAt = now;
            ShowNote(key, sc, tracker.Freq, doText);
        }
        else if (noteShown) ClearNote();

        if (!Recording) MicMeter.Level = live.Running ? livePeak : 0;

        var tick = Engine.Tick();
        if (tick is { Recording: true } && Recording)
        {
            // peaks arrive in 20 ms packets but frames are ~16 ms apart: hold and decay instead of flickering to zero
            MicMeter.Level = Math.Max(tick.Mic, MicMeter.Level * 0.9f);
            BackMeter.Level = Math.Max(tick.Back, BackMeter.Level * 0.9f);
            RecTimer.Text = Fmt(tick.Elapsed);
            if (tick.Mic >= 0.99f) RecState.Text = "mic clipping!";
        }
        reviewWin?.OnFrame(tick, now);

        if (S.Page == "tuner")
        {
            Trace.Points = tracker.Trace; Trace.TRight = now; Trace.FocusT = now; Trace.MinBar = S.MinBar;
            Trace.InvalidateVisual();
        }
        if (S.Page == "warmup" || PageWarm.Running) PageWarm.OnFrame(now);
        UpdateStatus();
    }

    bool noteShown;
    void ShowNote(int key, double sc, double freq, bool doText)
    {
        noteShown = true;
        var col = Theme.P.B(Draw.CentsColor(sc));
        string name = Draw.NoteName(key), oct = Draw.Octave(key).ToString();
        if (NoteEl.Text != name) NoteEl.Text = name;
        NoteEl.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        if (OctaveEl.Text != oct) OctaveEl.Text = oct;
        Bar.Cents = sc; Bar.Active = true; Bar.InvalidateVisual();
        PBar.Cents = sc; PBar.Active = true; PBar.InvalidateVisual();
        PNote.Text = name + oct;
        if (doText)
        {
            string ct = (sc >= 0 ? "+" : "") + sc.ToString("0") + " ¢";
            CentsEl.Text = ct; CentsEl.Foreground = col;
            PCents.Text = ct; PCents.Foreground = col;
            HzEl.Text = freq.ToString("0.0") + " Hz";
        }
    }

    void ClearNote()
    {
        noteShown = false;
        NoteEl.Text = "—"; NoteEl.SetResourceReference(TextBlock.ForegroundProperty, "Off");
        OctaveEl.Text = ""; HzEl.Text = "— Hz";
        Bar.Cents = 0; Bar.Active = false; Bar.InvalidateVisual();
        PBar.Cents = 0; PBar.Active = false; PBar.InvalidateVisual();
        PNote.Text = "—";
        foreach (var t in new[] { CentsEl, PCents }) { t.Text = "— ¢"; t.SetResourceReference(TextBlock.ForegroundProperty, "Faint"); }
    }

    // ------------------------------------------------------------------ recording setup summary

    static string ShortName(string? n)
    {
        if (string.IsNullOrEmpty(n)) return "";
        int a = n.IndexOf('('), b = n.LastIndexOf(')');
        return a >= 0 && b > a && b == n.TrimEnd().Length - 1 ? n[(a + 1)..b] : n;
    }

    public void UpdateSummary()
    {
        var t = RecSummaryText.Inlines;
        t.Clear();
        Run Bold(string s) { var r = new Run(s) { FontWeight = FontWeights.Medium }; r.SetResourceReference(TextElement.ForegroundProperty, "Text"); return r; }
        var mic = Devices.Inputs.FirstOrDefault(d => d.Id == S.MicId)?.Name;
        var outN = OutDeviceName;
        PianoOutText.Inlines.Clear();
        PianoOutText.Inlines.Add(new Run("plays on "));
        PianoOutText.Inlines.Add(Bold(outN ?? "no output found"));
        if (S.OutId == "") PianoOutText.Inlines.Add(new Run(" (Windows default)"));
        PianoOutText.Inlines.Add(new Run(" · "));
        PianoOutText.Inlines.Add(new Underline(new Run("change")));
        if (mic != null) { t.Add(new Run("mic ")); t.Add(Bold(ShortName(mic))); t.Add(new Run(" · ")); }
        switch (S.RecMode)
        {
            case "file" when S.BackFile != null: t.Add(new Run("backing ")); t.Add(Bold(S.BackFile.Name)); break;
            case "file":
                var w = new Run("choose a backing track"); w.SetResourceReference(TextElement.ForegroundProperty, "Err"); t.Add(w); break;
            case "system":
                t.Add(new Run("backing ")); t.Add(Bold("system audio"));
                if (outN != null) t.Add(new Run(" from " + ShortName(outN)));
                break;
            default: t.Add(new Run("voice only")); break;
        }
        t.Add(new Run(" · "));
        t.Add(new Underline(new Run("change")));
    }

    public void OpenSettings(bool pianoKeys = false)
    {
        if (settingsWin == null)
        {
            RefreshDevices();
            settingsWin = new SettingsWindow(this) { Owner = this };
            settingsWin.Closed += (_, _) => { settingsWin = null; SettingsBtn.Tag = null; UpdateSummary(); Activate(); };
            settingsWin.Show();
        }
        else settingsWin.Activate();
        if (pianoKeys) settingsWin.ShowPianoKeys();
    }

    public void ShowRecErr(string? msg)
    {
        RecErr.Text = msg ?? "";
        RecErr.Visibility = msg == null ? Visibility.Collapsed : Visibility.Visible;
    }

    public static string Friendly(Exception ex) => ex switch
    {
        System.Runtime.InteropServices.COMException c when (uint)c.HResult == 0x8889000A =>
            "The audio device is being used exclusively by another app. Close it (or disable exclusive mode) and try again.",
        System.Runtime.InteropServices.COMException c when (uint)c.HResult == 0x88890004 =>
            "The audio device was unplugged or disabled.",
        _ => ex.Message,
    };

    public static double ParseTime(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return 0;
        var parts = s.Split(':');
        var nums = new List<double>();
        foreach (var p in parts) { if (!double.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) return 0; nums.Add(d); }
        return nums.Count == 1 ? nums[0] : nums[0] * 60 + nums[1];
    }

    public static string Fmt(double s)
    {
        s = Math.Max(0, s);
        int m = (int)(s / 60); double r = s - m * 60;
        return m + ":" + (r < 10 ? "0" : "") + r.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ record / stop

    void StartRecording()
    {
        ShowRecErr(null);
        if (S.RecMode == "file" && S.BackFile == null)
        {
            ShowRecErr("Choose a backing track file first (or pick \"None\").");
            OpenSettings();
            return;
        }
        // a new recording replaces the last take without asking
        reviewWin?.Close();
        Busy = true; UpdateRecUi();
        var mode = S.RecMode switch { "file" => BackingMode.File, "system" => BackingMode.Loopback, _ => BackingMode.None };
        try
        {
            Engine.StartRecording(S.MicId, S.OutId, mode, S.BackFile?.Path, ParseTime(S.StartAt), S.BackVol / 100f);
        }
        catch (Exception ex)
        {
            Busy = false; UpdateRecUi();
            ShowRecErr("Could not start recording: " + Friendly(ex));
            return;
        }
        Recording = true; Busy = false; recMode = S.RecMode;
        session = null; UpdateLastTake();
        RecState.Text = "";
        if (!live.Running) StartListening();
        UpdateRecUi(); UpdateStatus();
    }

    async Task StopRecording(string? reason)
    {
        if (!Recording) return;
        autoStop.Stop();
        Recording = false; Busy = true;
        RecState.Text = "processing…";
        UpdateRecUi(); UpdateStatus();
        try
        {
            await Engine.StopRecordingAsync();
            double rms = S.RmsThreshold, clarity = S.Clarity;
            var t = await Task.Run(() => Engine.PrepareTake(rms, clarity));
            session = new TakeSession(t, recMode, PairKey);
            string basis = t.SyncBasis != "none" ? t.SyncBasis : recMode;
            if (t.HasBacking && S.Sync.TryGetValue(basis + "-" + PairKey, out var sv)) session.SyncMs = sv;
            RecState.Text = reason ?? "";
            UpdateLastTake();
            OpenReview();
        }
        catch (Exception ex)
        {
            ShowRecErr("Recording failed: " + Friendly(ex));
            RecState.Text = "";
        }
        Busy = false; UpdateRecUi();
        MicMeter.Level = 0; BackMeter.Level = 0;
    }

    void UpdateRecUi()
    {
        if (Recording)
        {
            RecBtn.SetResourceReference(BackgroundProperty, "Bg");
            RecBtn.SetResourceReference(ForegroundProperty, "Text");
            RecBtn.SetResourceReference(BorderBrushProperty, "Red");
            RecIcon.CornerRadius = new CornerRadius(2);
            RecLbl.Text = "Stop";
            RecTimer.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        }
        else
        {
            RecBtn.SetResourceReference(BackgroundProperty, "Text");
            RecBtn.SetResourceReference(ForegroundProperty, "Bg");
            RecBtn.SetResourceReference(BorderBrushProperty, "Text");
            RecIcon.CornerRadius = new CornerRadius(5.5);
            RecLbl.Text = "Record";
            RecTimer.SetResourceReference(TextBlock.ForegroundProperty, "Sub");
        }
        Pulse(RecIcon, Recording, 1.2);
        RecBtn.IsEnabled = !Busy;
        bool showBack = Recording && recMode != "none";
        BackMeterRow.Visibility = showBack ? Visibility.Visible : Visibility.Hidden;
        MetersGrid.ColumnDefinitions[1].Width = new GridLength(showBack ? 22 : 0);
        MetersGrid.ColumnDefinitions[2].Width = showBack ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        settingsWin?.UpdateLock();
    }

    // ------------------------------------------------------------------ review window

    void OpenReview()
    {
        if (session == null) return;
        if (reviewWin != null) { reviewWin.Activate(); return; }
        reviewWin = new ReviewWindow(this, session) { Owner = this };
        reviewWin.Closed += (_, _) => { reviewWin = null; UpdateLastTake(); Activate(); };
        reviewWin.Show();
        UpdateLastTake();
    }

    void UpdateLastTake()
    {
        bool show = session != null && reviewWin == null && !Recording;
        LastTakeBtn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var t = LastTakeText.Inlines;
        t.Clear();
        t.Add(new Run("last take " + Fmt(session!.Take.Duration) + (session.Exported ? " (exported)" : " (not exported)") + " · "));
        t.Add(new Underline(new Run("open review")));
    }

    // ------------------------------------------------------------------ piano

    public void Strike(int midi)
    {
        try { synth.Strike(midi, S.PianoVol / 100.0); } catch { }
        Keyboard.Light(midi);
    }

    void ShiftOct(int d)
    {
        Keyboard.BaseOct = Math.Clamp(Keyboard.BaseOct + d, 1, 5);
        OctLabel.Text = $"C{Keyboard.BaseOct} – C{Keyboard.BaseOct + 2}";
    }

    // computer key -> semitone above the lowest C on screen, plus the octave keys (all from settings)
    readonly Dictionary<Key, int> pianoMap = new();
    Key octDownKey, octUpKey;

    /// <summary>Display text for a stored key name ("OemSemicolon" -> ";", "" -> "").</summary>
    public static string KeyLabel(string? name) => Shortcut.Parse(name).ToString();

    /// <summary>Re-reads the piano key mapping from settings (after it was edited) and updates labels and hint.</summary>
    public void ApplyPianoKeys()
    {
        pianoMap.Clear();
        foreach (var (off, name) in S.PianoKeys)
        {
            var k = Shortcut.Parse(name).Key;
            if (k != Key.None) pianoMap[k] = off;
        }
        octDownKey = Shortcut.Parse(S.OctDownKey).Key;
        octUpKey = Shortcut.Parse(S.OctUpKey).Key;
        Keyboard.LabelFor = off => KeyLabel(S.PianoKeys.GetValueOrDefault(off));
        Keyboard.InvalidateVisual();

        var h = PianoHint.Inlines;
        h.Clear();
        string down = KeyLabel(S.OctDownKey), up = KeyLabel(S.OctUpKey);
        h.Add(new Run("play the keys shown on the piano · "));
        if (down != "" || up != "") h.Add(new Run($"{(down == "" ? "—" : down)} / {(up == "" ? "—" : up)} shift octave · "));
        h.Add(new Underline(new Run("customize keys")));
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        if (S.Page == "warmup" && e.Key == Key.Space && !e.IsRepeat) { PageWarm.Toggle(); e.Handled = true; return; }
        if (S.Page == "piano")
        {
            if (e.IsRepeat) return;
            // Ctrl/Alt/Win combos are left alone so they keep working as shortcuts
            if ((System.Windows.Input.Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return;
            var key = Shortcut.KeyOf(e);
            if (key == Key.None) return;
            if (key == octDownKey) { ShiftOct(-1); e.Handled = true; }
            else if (key == octUpKey) { ShiftOct(1); e.Handled = true; }
            else if (pianoMap.TryGetValue(key, out int off)) { Strike(12 * (Keyboard.BaseOct + 1) + off); e.Handled = true; }
        }
    }

    // ------------------------------------------------------------------ record shortcut

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    const int HotkeyId = 0xB001, WM_HOTKEY = 0x0312;
    const uint MOD_NOREPEAT = 0x4000;
    IntPtr hwnd;
    bool hotkeyActive, shortcutSuspended;

    /// <summary>Why the background shortcut couldn't be registered (null when fine or not requested).</summary>
    public string? HotkeyError { get; private set; }

    /// <summary>Set while the settings box is capturing a new key, so the old shortcut doesn't fire.</summary>
    public bool ShortcutSuspended
    {
        get => shortcutSuspended;
        set { shortcutSuspended = value; ApplyShortcut(); }
    }

    public void ToggleRecording()
    {
        if (Busy) return;
        if (Recording) _ = StopRecording(null); else StartRecording();
    }

    /// <summary>(Re)registers the background hotkey and updates the key hint on the Record button.</summary>
    public void ApplyShortcut()
    {
        if (hotkeyActive) { UnregisterHotKey(hwnd, HotkeyId); hotkeyActive = false; }
        var sc = Shortcut.Parse(S.RecShortcut);
        RecKeyTxt.Text = sc.ToString();
        RecKeyCap.Visibility = sc.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        RecBtn.ToolTip = sc.IsEmpty ? null : $"Record / stop ({sc})";
        if (shortcutSuspended) return;
        HotkeyError = null;
        if (sc.IsEmpty || !S.RecShortcutGlobal || hwnd == IntPtr.Zero) return;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(sc.Key);
        hotkeyActive = RegisterHotKey(hwnd, HotkeyId, (uint)sc.Mods | MOD_NOREPEAT, vk);
        if (!hotkeyActive)
            HotkeyError = $"\"{sc}\" is already taken by another app, so it only works while Vocal Tuner is in front. Pick a different key to use it from other apps.";
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            if (!shortcutSuspended) ToggleRecording();
        }
        return IntPtr.Zero;
    }

    static void OnAnyWindowKey(object sender, KeyEventArgs e)
    {
        if (Application.Current?.MainWindow is MainWindow m) m.HandleShortcutKey(sender, e);
    }

    void HandleShortcutKey(object sender, KeyEventArgs e)
    {
        // the registered background hotkey delivers WM_HOTKEY instead, so only handle the key here without it
        if (shortcutSuspended || hotkeyActive || e.Handled) return;
        if (sender is not (MainWindow or ReviewWindow or SettingsWindow)) return;   // not inside message boxes
        var sc = Shortcut.Parse(S.RecShortcut);
        if (sc.IsEmpty || Shortcut.KeyOf(e) != sc.Key || System.Windows.Input.Keyboard.Modifiers != sc.Mods) return;
        if (sc.Mods == ModifierKeys.None && e.OriginalSource is TextBox) return;    // typing in a field
        e.Handled = true;
        if (!e.IsRepeat) ToggleRecording();
    }

    // ------------------------------------------------------------------ shutdown

    async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (Recording && !closingAfterStop)
        {
            e.Cancel = true;          // finish writing the recording cleanly before exiting
            closingAfterStop = true;
            await Engine.StopRecordingAsync();
            Recording = false;
            Close();
            return;
        }
        CompositionTarget.Rendering -= OnFrame;
        PageWarm.Stop();
        if (hotkeyActive) { UnregisterHotKey(hwnd, HotkeyId); hotkeyActive = false; }
        S.Save();
        live.Dispose();
        synth.Dispose();
        watcher?.Dispose();
        Engine.Dispose();
    }
}
