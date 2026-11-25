using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VocalTuner.Coach;

namespace VocalTuner;

/// <summary>Coach page: analyse the last take or any recording, choose voice type and report language, and see past analyses.</summary>
public partial class CoachView : UserControl
{
    MainWindow? M;
    AppSettings S => M!.S;
    string Lang => S.CoachLang == "th" ? "th" : "en";
    string L(string th, string en) => Feedback.L(Lang, th, en);
    List<CoachResult> history = new();
    bool busy, loaded;
    CoachWindow? reportWin;

    public CoachView() => InitializeComponent();

    public void Init(MainWindow main)
    {
        M = main;
        VoiceSel.SelectionChanged += v => { S.CoachVoice = v; S.Save(); };
        (Lang == "th" ? LangTh : LangEn).IsChecked = true;
        LangTh.Checked += (_, _) => SetLang("th");
        LangEn.Checked += (_, _) => SetLang("en");
        TakeBtn.Click += async (_, _) => await AnalyzeTake();
        FileBtn.Click += async (_, _) => await AnalyzeFile();
        BackBtn.Click += (_, _) => PickBacking();
        BackClear.Click += (_, _) => { S.CoachBacking = ""; S.Save(); Texts(); };
        Trend.Paint = (dc, sz, p, ppd) => Charts.Trend(dc, sz, history.TakeLast(30).ToList(), p, ppd);
        Texts();
    }

    void SetLang(string l)
    {
        S.CoachLang = l; S.Save();
        Texts(); BuildHistory();
        reportWin?.SetLang(l);
    }

    void Texts()
    {
        if (M == null) return;
        IntroTitle.Text = L("ฟีดแบ็กการร้องเพลง", "Feedback on your singing");
        IntroTxt.Text = L("วิเคราะห์ความตรงของโน้ต การช้อนเสียง ความนิ่ง ท้ายประโยค vibrato คุณภาพเสียง และความดัง แล้วบอกว่าควรฝึกอะไร ไม่ต้องรู้ว่าเป็นเพลงอะไร",
            "Checks pitch accuracy, scoops, steadiness, phrase endings, vibrato, tone and dynamics, then tells you what to practise. It doesn't need to know the song.");
        TakeBtn.Content = L("วิเคราะห์เทคล่าสุด", "Analyze last take");
        FileBtn.Content = L("วิเคราะห์ไฟล์เสียง…", "Analyze a recording…");
        VoiceLbl.Text = L("ประเภทเสียง", "Voice type");
        LangLbl.Text = L("ภาษารายงาน", "Report language");
        BackLbl.Text = L("เพลงประกอบ", "Backing track");
        VoiceSel.SetItems(VoiceTypes.All.Select(v => (v.id, Lang == "th" ? v.th : v.en)), S.CoachVoice);
        bool hasBack = S.CoachBacking != "" && File.Exists(S.CoachBacking);
        BackTxt.Text = hasBack ? Path.GetFileName(S.CoachBacking) : L("ไม่มี · เลือกไฟล์…", "none · choose…");
        BackClearTxt.Text = L("เอาออก", "remove");
        BackClear.Visibility = hasBack ? Visibility.Visible : Visibility.Collapsed;
        BackHint.Text = L("ไม่บังคับ ใช้กับไฟล์ที่อัดจากที่อื่น: ใส่ดนตรีของเพลงนั้นเพื่อเช็กคีย์และจูนเทียบกับเสียงร้อง (เทคที่อัดในแอปใช้ดนตรีของเทคเองอยู่แล้ว)",
            "Optional, for recordings made elsewhere: the song's music, so the key and tuning can be checked against your voice. Takes recorded here already use their own backing.");
        HistTitle.Text = L("ประวัติการวิเคราะห์", "PAST ANALYSES");
        TrendTitle.Text = L("ตรงคีย์ (±20 เซนต์) แต่ละครั้ง", "On pitch (±20 cents), each analysis");
        HistEmpty.Text = L("ยังไม่มี ผลการวิเคราะห์แต่ละครั้งจะเก็บไว้ที่นี่ เพื่อดูพัฒนาการ", "Nothing yet. Every analysis is kept here so you can track your progress.");
        TipsTxt.Text = L("อัดให้ได้ผลแม่น: ใส่หูฟังให้ไมค์ได้ยินแค่เสียงร้อง · ไม่ใส่เอฟเฟกต์/รีเวิร์บ · ไฟล์ WAV 44.1 หรือ 48 kHz · ไมค์ห่างปากประมาณ 15–20 ซม.",
            "For accurate results: wear headphones so the mic hears only you · no effects or reverb · WAV at 44.1 or 48 kHz · mic about 15–20 cm from your mouth.");
        Refresh();
    }

    /// <summary>Enables "Analyze last take" when there is one, and reloads the history the first time the page is shown.</summary>
    public void Refresh()
    {
        if (M == null) return;
        TakeBtn.IsEnabled = !busy && M.Session != null && !M.Recording;
        TakeBtn.ToolTip = M.Session == null ? L("ยังไม่มีเทค อัดเสียงในหน้า Tuner ก่อน", "No take yet. Record one on the Tuner page.") : null;
        FileBtn.IsEnabled = !busy;
        if (!loaded) { loaded = true; history = History.LoadAll(); BuildHistory(); }
    }

    void Status(string? msg, bool err = false)
    {
        StatusTxt.Text = msg ?? "";
        StatusTxt.Visibility = msg == null ? Visibility.Collapsed : Visibility.Visible;
        StatusTxt.SetResourceReference(TextBlock.ForegroundProperty, err ? "Err" : "Sub");
    }

    string StepText(string step) => step switch
    {
        "load" => L("กำลังโหลดเสียง…", "Loading audio…"),
        "pitch" => L("กำลังจับโน้ต…", "Tracking pitch…"),
        "key" => L("กำลังเช็กคีย์…", "Checking the key…"),
        "shape" => L("กำลังดูการเริ่ม/จบโน้ตและ vibrato…", "Looking at note starts, endings and vibrato…"),
        "quality" => L("กำลังวัดคุณภาพเสียง…", "Measuring tone…"),
        _ => step,
    };

    // ------------------------------------------------------------------ analysis

    public async Task AnalyzeTake()
    {
        if (M?.Session == null || busy) return;
        var audio = M.Engine.TakeAudio();
        if (audio == null) return;
        var (voice, rate, back) = audio.Value;
        string source = M.RecModeName == "file" && S.BackFile != null ? S.BackFile.Name
            : M.RecModeName == "system" ? "take:system" : "take:voice";
        await Analyze(() => (Sig.Resample(voice, rate), back == null ? null : Sig.Resample(back, rate)), source, "take");
    }

    async Task AnalyzeFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L("เลือกไฟล์เสียงร้อง", "Choose a singing recording"),
            Filter = L("ไฟล์เสียง", "Audio files") + "|*.wav;*.mp3;*.m4a;*.flac;*.aac;*.wma;*.aiff|" + L("ทุกไฟล์", "All files") + "|*.*",
            InitialDirectory = Directory.Exists(S.LastDir) ? S.LastDir : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        string path = dlg.FileName, back = S.CoachBacking;
        bool useBack = back != "" && File.Exists(back) && !string.Equals(back, path, StringComparison.OrdinalIgnoreCase);
        await Analyze(() => (Sig.LoadFile(path), useBack ? Sig.LoadFile(back) : null), Path.GetFileName(path), "file");
    }

    void PickBacking()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L("เลือกไฟล์ดนตรีของเพลง", "Choose the song's backing track"),
            Filter = L("ไฟล์เสียง", "Audio files") + "|*.wav;*.mp3;*.m4a;*.flac;*.aac;*.wma;*.aiff|" + L("ทุกไฟล์", "All files") + "|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        S.CoachBacking = dlg.FileName; S.Save(); Texts();
    }

    async Task Analyze(Func<(float[] voice, float[]? back)> load, string source, string kind)
    {
        busy = true; Refresh();
        Status(StepText("load"));
        string voiceType = S.CoachVoice;
        try
        {
            var r = await Task.Run(() =>
            {
                var (v, b) = load();
                return Pipeline.Run(v, b, source, kind, voiceType, step => Dispatcher.BeginInvoke(() => Status(StepText(step))));
            });
            try { History.Save(r); } catch { /* the report still opens */ }
            history.Add(r);
            BuildHistory();
            Status(null);
            Open(r);
        }
        catch (Exception ex)
        {
            Status(Feedback.FriendlyError(ex, Lang), err: true);
        }
        finally { busy = false; Refresh(); }
    }

    void Open(CoachResult r)
    {
        var prev = History.Previous(r, history);
        if (reportWin != null) { reportWin.Show(r, prev); reportWin.Activate(); return; }
        reportWin = new CoachWindow(M!, r, prev) { Owner = M };
        reportWin.Closed += (_, _) => reportWin = null;
        reportWin.Show();
    }

    // ------------------------------------------------------------------ history

    void BuildHistory()
    {
        HistList.Children.Clear();
        HistEmpty.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistCount.Text = history.Count > 0 ? history.Count.ToString() : "";
        TrendBox.Visibility = history.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        Trend.InvalidateVisual();
        foreach (var r in history.AsEnumerable().Reverse().Take(50))
        {
            var row = new DockPanel();
            var del = new Button { Style = (Style)FindResource("Icon"), Width = 28, Height = 28, ToolTip = L("ลบรายการนี้", "Delete this analysis"), Margin = new Thickness(8, 0, 0, 0) };
            var x = new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,0 L8,8 M8,0 L0,8"), StrokeThickness = 1.5, Width = 9, Height = 9, Stretch = Stretch.Uniform };
            x.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Faint");
            del.Content = x;
            var item = r;
            del.Click += (_, _) =>
            {
                if (!ConfirmDialog.Ask(Window.GetWindow(this), L("ลบผลวิเคราะห์", "Delete analysis"),
                    L($"ลบผลวิเคราะห์ของ \"{Feedback.SourceName(item.Source, Lang)}\" ({item.When:d MMM HH:mm})? ลบแล้วกู้คืนไม่ได้", $"Delete the analysis of \"{Feedback.SourceName(item.Source, Lang)}\" ({item.When:d MMM HH:mm})? This can't be undone."),
                    L("ลบ", "Delete"))) return;
                try { History.Delete(item.Id); } catch { }
                history.Remove(item); BuildHistory();
            };
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);

            var pct = new TextBlock { Text = Feedback.N0(r.M.InTunePct) + "%", FontFamily = Theme.Mono, FontSize = 13, Width = 52, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            pct.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            DockPanel.SetDock(pct, Dock.Right);
            row.Children.Add(pct);
            var date = new TextBlock { Text = r.When.ToString("d MMM  HH:mm", System.Globalization.CultureInfo.InvariantCulture), FontFamily = Theme.Mono, FontSize = 12, Width = 110, VerticalAlignment = VerticalAlignment.Center };
            date.SetResourceReference(TextBlock.ForegroundProperty, "Sub");
            row.Children.Add(date);
            var src = new TextBlock { Text = Feedback.SourceName(r.Source, Lang) + "  ·  " + Feedback.Time(r.Duration), FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            src.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            row.Children.Add(src);

            var open = new Button { Style = (Style)FindResource("MenuRow"), Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = L("เปิดรายงาน", "Open the report") };
            open.Loaded += (_, _) => { if (VisualTreeHelper.GetChildrenCount(open) > 0 && VisualTreeHelper.GetChild(open, 0) is Border bd && bd.Child is ContentPresenter cp) cp.HorizontalAlignment = HorizontalAlignment.Stretch; };
            open.Click += (_, _) => Open(item);
            HistList.Children.Add(open);
        }
    }
}
