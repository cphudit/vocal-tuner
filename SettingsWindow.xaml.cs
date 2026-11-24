using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VocalTuner;

/// <summary>Separate pop-up window: recording setup + pitch-detection parameters. Changes apply immediately.</summary>
public partial class SettingsWindow : Window
{
    readonly MainWindow M;
    AppSettings S => M.S;
    bool loading;

    static readonly Dictionary<string, string> ModeHints = new()
    {
        ["none"] = "Records just your voice.",
        ["file"] = "The track plays through the sound output above while you sing. The export uses the original file (not a re-recording), so the backing stays perfectly clean. Wear headphones so the music doesn't leak into the mic.",
        ["system"] = "Records everything playing on the sound output above: start your karaoke in any app (YouTube, Spotify, a video…), then press Record. Wear headphones so the music doesn't leak into the mic, and mute notification sounds.",
    };

    public SettingsWindow(MainWindow main)
    {
        M = main;
        InitializeComponent();
        Theme.Track(this);

        MicSel.SelectionChanged += id =>
        {
            if (loading) return;
            S.MicId = id; S.Save(); ShowCal();
            if (!M.Recording) M.RestartListening(TimeSpan.Zero);
            M.UpdateSummary();
        };
        OutSel.SelectionChanged += id => { if (loading) return; S.OutId = id; S.Save(); M.ApplyOutput(); ShowCal(); };
        ModeNone.Checked += (_, _) => SetMode("none");
        ModeFile.Checked += (_, _) => SetMode("file");
        ModeSystem.Checked += (_, _) => SetMode("system");
        PickBtn.Click += (_, _) => PickFile();
        StartAt.Text = S.StartAt;
        StartAt.LostKeyboardFocus += (_, _) => CommitStartAt();
        StartAt.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitStartAt(); e.Handled = true; } };
        BackVolSl.Value = S.BackVol;
        BackVolVal.Text = S.BackVol + "%";
        BackVolSl.Changed += v =>
        {
            S.BackVol = (int)v; BackVolVal.Text = (int)v + "%";
            if (M.Recording) M.Engine.SetBackingVolume((float)(v / 100));
        };
        BackVolSl.Committed += _ => S.Save();
        CalBtn.Click += async (_, _) => await Calibrate();
        DoneBtn.Click += (_, _) => Close();
        PreviewKeyDown += (_, e) =>
        {
            if (Capturing) { CaptureKey(e); return; }
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };

        // keyboard shortcut
        KeyBtn.Click += (_, _) => { if (Capturing) EndCapture(); else BeginCapture(); };
        KeyBtn.LostKeyboardFocus += (_, _) => { if (Capturing) EndCapture(); };
        Deactivated += (_, _) => { if (Capturing) EndCapture(); };
        Closed += (_, _) => { if (Capturing) EndCapture(); };
        RoutedEventHandler onGlobal = (_, _) =>
        {
            if (loading || S.RecShortcutGlobal == (KeyGlobal.IsChecked == true)) return;
            S.RecShortcutGlobal = KeyGlobal.IsChecked == true; S.Save(); M.ApplyShortcut(); ShowShortcut();
        };
        KeyGlobal.Checked += onGlobal; KeyGlobal.Unchecked += onGlobal;

        Body.MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 140);
        BuildFaders();
        Refresh();
        UpdateLock();
    }

    /// <summary>Re-reads devices and settings into the controls.</summary>
    public void Refresh()
    {
        loading = true;
        string Label(DeviceInfo d) => d.Name + (d.IsDefault ? "  (default)" : "");
        MicSel.SetItems(M.Devices.Inputs.Select(d => (d.Id, Label(d))), S.MicId);
        var def = M.Devices.Outputs.FirstOrDefault(d => d.IsDefault);
        var outs = new List<(string, string)> { ("", "Windows default" + (def != null ? "  ·  " + def.Name : "")) };
        outs.AddRange(M.Devices.Outputs.Select(d => (d.Id, d.Name)));
        OutSel.SetItems(outs, S.OutId);
        (S.RecMode switch { "none" => ModeNone, "system" => ModeSystem, _ => ModeFile }).IsChecked = true;
        loading = false;
        ApplyMode();
        ShowFile();
        ShowCal();
        ShowShortcut();
    }

    // ------------------------------------------------------------------ keyboard shortcut

    /// <summary>True while the shortcut box is waiting for a key press.</summary>
    public bool Capturing { get; private set; }

    void BeginCapture()
    {
        Capturing = true;
        M.ShortcutSuspended = true;        // let the current shortcut reach this box instead of toggling recording
        KeyBtn.Content = "Press a key…";
        KeyBtn.SetResourceReference(BorderBrushProperty, "Text");
        KeyHint.Text = "Esc cancels · Backspace removes the shortcut";
        KeyBtn.Focus();
    }

    void EndCapture()
    {
        Capturing = false;
        KeyBtn.ClearValue(BorderBrushProperty);
        M.ShortcutSuspended = false;       // re-registers the (possibly new) shortcut
        ShowShortcut();
    }

    void CaptureKey(KeyEventArgs e)
    {
        e.Handled = true;
        var key = Shortcut.KeyOf(e);
        var mods = Keyboard.Modifiers;
        if (Shortcut.IsModifier(key))
        {
            // live preview of the modifiers being held
            var held = new Shortcut(Key.A, mods).ToString();
            KeyBtn.Content = held.Length > 1 ? held[..^1] + "…" : "Press a key…";
            return;
        }
        if (e.IsRepeat) return;
        if (mods == ModifierKeys.None && key == Key.Escape) { EndCapture(); return; }
        if (mods == ModifierKeys.None && key is Key.Back or Key.Delete) { S.RecShortcut = ""; S.Save(); EndCapture(); return; }
        if (mods == ModifierKeys.None && key is Key.Tab or Key.Return) return;   // keep those for moving around the window
        S.RecShortcut = new Shortcut(key, mods).Serialize();
        S.Save();
        EndCapture();
    }

    void ShowShortcut()
    {
        var sc = Shortcut.Parse(S.RecShortcut);
        if (!Capturing) KeyBtn.Content = sc.IsEmpty ? "Not set" : sc.ToString();
        if (!Capturing) KeyHint.Text = sc.IsEmpty ? "click the box, then press a key (e.g. F9 or Ctrl+R)" : "same key starts and stops · click to change";
        KeyGlobal.IsChecked = S.RecShortcutGlobal;
        KeyGlobal.IsEnabled = !sc.IsEmpty;

        string? warn = null; string color = "Amber";
        if (!sc.IsEmpty && S.RecShortcutGlobal && M.HotkeyError != null) { warn = M.HotkeyError; color = "Err"; }
        else if (!sc.IsEmpty && S.RecShortcutGlobal && sc.BlocksTyping)
            warn = $"While Vocal Tuner is open, \"{sc}\" won't type in other apps. Use a function key (F1–F12) or add Ctrl/Alt to avoid that.";
        KeyWarn.Text = warn ?? "";
        KeyWarn.SetResourceReference(TextBlock.ForegroundProperty, color);
        KeyWarn.Visibility = warn != null ? Visibility.Visible : Visibility.Collapsed;
    }

    public void UpdateLock()
    {
        bool locked = M.Recording || M.Busy;
        SetupRows.IsEnabled = !locked;
        SetupRows.Opacity = locked ? 0.5 : 1;
        LockedNote.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
    }

    void SetMode(string m)
    {
        if (loading || M.Recording) return;
        S.RecMode = m; S.Save();
        ApplyMode();
        M.UpdateSummary();
    }

    void ApplyMode()
    {
        string m = S.RecMode;
        var file = m == "file" ? Visibility.Visible : Visibility.Collapsed;
        var back = m == "none" ? Visibility.Collapsed : Visibility.Visible;
        FileRow.Visibility = file; StartRow.Visibility = file; VolRow.Visibility = file;
        CalRow.Visibility = back;
        ModeHint.Text = ModeHints.GetValueOrDefault(m, "");
    }

    void ShowFile()
    {
        var f = S.BackFile;
        FileName.Text = f != null ? f.Name + "  ·  " + MainWindow.Fmt(f.Duration)[..^2] : "No file chosen";
        FileName.ToolTip = f?.Path;
        FileName.SetResourceReference(TextBlock.ForegroundProperty, f != null ? "Text" : "Faint");
    }

    async void PickFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a backing track",
            Filter = "Audio files|*.mp3;*.wav;*.m4a;*.aac;*.flac;*.wma;*.aif;*.aiff|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        string path = dlg.FileName;
        try
        {
            S.BackFile = await Task.Run(() => AudioEngine.InspectFile(path));
            S.Save();
            ShowFile();
            M.UpdateSummary();
            M.ShowRecErr(null);
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(this, "Can't open that file", "Could not open that file: " + ex.Message);
        }
    }

    void CommitStartAt()
    {
        double sec = Math.Max(0, MainWindow.ParseTime(StartAt.Text));
        int m = (int)(sec / 60), s = (int)(sec % 60);
        StartAt.Text = $"{m}:{s:00}";
        S.StartAt = StartAt.Text; S.Save();
    }

    void ShowCal()
    {
        if (S.Cal.TryGetValue(M.PairKey, out var cal))
        {
            CalTxt.Text = $"calibrated · round trip {cal.RoundTripMs} ms";
            CalTxt.SetResourceReference(TextBlock.ForegroundProperty, "Green");
        }
        else
        {
            CalTxt.Text = "not calibrated (your voice may sit slightly late)";
            CalTxt.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        }
    }

    async Task Calibrate()
    {
        if (M.Recording) return;
        if (!ConfirmDialog.Ask(this, "Latency calibration",
                "Hold your headphones against the microphone (or switch to speakers) and keep the room quiet. A few clicks will play for about 3 seconds.",
                "Start")) return;
        CalBtn.IsEnabled = false;
        CalTxt.Text = "listening for clicks…";
        CalTxt.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        try
        {
            var r = await M.Engine.CalibrateAsync(S.MicId, S.OutId);
            S.Cal[M.PairKey] = r;
            S.Sync["file-" + M.PairKey] = r.FileMs;
            if (r.SystemMs is double sys) S.Sync["system-" + M.PairKey] = sys;
            S.Save();
            ShowCal();
        }
        catch (Exception ex)
        {
            CalTxt.Text = MainWindow.Friendly(ex);
            CalTxt.SetResourceReference(TextBlock.ForegroundProperty, "Err");
        }
        CalBtn.IsEnabled = true;
    }

    void BuildFaders()
    {
        var defs = new (string label, string unit, double max, double dflt, Func<int> get, Action<int> set)[]
        {
            ("Mic sensitivity", "%", 100, 40, () => S.Sens, v => S.Sens = v),
            ("Noise filter", "%", 100, 55, () => S.Noise, v => S.Noise = v),
            ("Smoothing", "%", 100, 70, () => S.Smooth, v => S.Smooth = v),
            ("Min bar length", "ms", 400, 100, () => S.MinBar, v => S.MinBar = v),
        };
        foreach (var d in defs)
        {
            var val = new TextBlock { Style = (Style)FindResource("Mono"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Text = d.get() + d.unit };
            var fader = new VFader { Max = d.max, Default = d.dflt, Value = d.get(), Margin = new Thickness(0, 10, 0, 10), HorizontalAlignment = HorizontalAlignment.Center };
            var lbl = new TextBlock { Text = d.label, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
            fader.Changed += v =>
            {
                d.set((int)v); val.Text = (int)v + d.unit;
                S.Save();
                M.DetectionChanged();
            };
            var col = new StackPanel();
            col.Children.Add(val); col.Children.Add(fader); col.Children.Add(lbl);
            Faders.Children.Add(col);
        }
    }
}

/// <summary>Small themed message box (the Win32 one ignores the dark theme).</summary>
public static class ConfirmDialog
{
    public static bool Ask(Window owner, string title, string message, string okText) => Show(owner, title, message, okText, "Cancel");
    public static void Info(Window owner, string title, string message) => Show(owner, title, message, "OK", null);

    static bool Show(Window owner, string title, string message, string okText, string? cancelText)
    {
        var w = new Window
        {
            Title = title, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.Height, Width = 420, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            UseLayoutRounding = true,
        };
        w.SetResourceReference(Control.BackgroundProperty, "Surface");
        w.SetResourceReference(Control.ForegroundProperty, "Text");
        Theme.Track(w);
        var msg = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20, Margin = new Thickness(24, 22, 24, 20) };
        var ok = new Button { Content = okText, Style = (Style)Application.Current.Resources["Pill"], Padding = new Thickness(20, 8, 20, 8), FontSize = 13, IsDefault = true, Margin = new Thickness(10, 0, 0, 0) };
        bool result = false;
        ok.Click += (_, _) => { result = true; w.Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (cancelText != null)
        {
            var cancel = new Button { Content = cancelText, Style = (Style)Application.Current.Resources["Secondary"], IsCancel = true };
            cancel.Click += (_, _) => w.Close();
            buttons.Children.Add(cancel);
        }
        buttons.Children.Add(ok);
        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(20, 12, 20, 12), Child = buttons };
        footer.SetResourceReference(Border.BorderBrushProperty, "BorderB");
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer); root.Children.Add(msg);
        w.Content = root;
        w.ShowDialog();
        return result;
    }
}
