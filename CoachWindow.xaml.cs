using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using VocalTuner.Coach;

namespace VocalTuner;

/// <summary>The singing report inside the app: same content as the HTML export, with hover details on the pitch graph.</summary>
public partial class CoachWindow : Window
{
    readonly MainWindow M;
    CoachResult r;
    CoachResult? prev;
    string lang;
    string L(string th, string en) => Feedback.L(lang, th, en);

    public CoachWindow(MainWindow main, CoachResult result, CoachResult? previous)
    {
        M = main; r = result; prev = previous;
        lang = M.S.CoachLang == "th" ? "th" : "en";
        InitializeComponent();
        Theme.Track(this);
        (lang == "en" ? LangEn : LangTh).IsChecked = true;
        LangTh.Checked += (_, _) => ChangeLang("th");
        LangEn.Checked += (_, _) => ChangeLang("en");
        ExportBtn.Click += (_, _) => Export();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Build();
    }

    public void Show(CoachResult result, CoachResult? previous)
    {
        r = result; prev = previous;
        ExportRes.Visibility = Visibility.Collapsed;
        Build();
        Scroll.ScrollToTop();
    }

    /// <summary>Called when the language changes on the Coach page.</summary>
    public void SetLang(string l)
    {
        if (l == lang) return;
        (l == "en" ? LangEn : LangTh).IsChecked = true;
    }

    void ChangeLang(string l)
    {
        lang = l;
        if (M.S.CoachLang != l) { M.S.CoachLang = l; M.S.Save(); }
        double off = Scroll.VerticalOffset;
        Build();
        Scroll.ScrollToVerticalOffset(off);
    }

    // ------------------------------------------------------------------ building blocks

    TextBlock Txt(string s, double size = 13.5, string brush = "Text", bool bold = false, Thickness? margin = null)
    {
        var t = new TextBlock { Text = s, FontSize = size, TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.55, Margin = margin ?? new Thickness(0) };
        if (bold) t.FontWeight = FontWeights.SemiBold;
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    void H2(string s) => Body.Children.Add(Txt(s, 15, "Text", true, new Thickness(0, 28, 0, 10)));

    Border Card(UIElement child, Thickness? pad = null)
    {
        var b = new Border { Style = (Style)FindResource("Card"), Padding = pad ?? new Thickness(20, 16, 20, 16), Child = child };
        Body.Children.Add(b);
        return b;
    }

    static StackPanel Stack(params UIElement[] els)
    {
        var s = new StackPanel();
        foreach (var e in els) s.Children.Add(e);
        return s;
    }

    UIElement Bullets(IEnumerable<string> items, string brush = "Text")
    {
        var s = new StackPanel();
        foreach (var it in items)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var dot = Txt("•", 13.5, "Faint"); dot.Width = 16;
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot); row.Children.Add(Txt(it, 13.5, brush));
            s.Children.Add(row);
        }
        return s;
    }

    Grid Table(IEnumerable<(string label, string value)> rows, Func<int, string>? valueBrush = null)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        int i = 0;
        foreach (var (label, value) in rows)
        {
            g.RowDefinitions.Add(new RowDefinition());
            var line = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
            line.SetResourceReference(Border.BorderBrushProperty, "BorderB");
            Grid.SetRow(line, i); Grid.SetColumnSpan(line, 2);
            var a = Txt(label, 13, "Sub", margin: new Thickness(0, 6, 16, 6));
            var b = Txt(value, 13, valueBrush?.Invoke(i) ?? "Text", margin: new Thickness(0, 6, 0, 6));
            b.FontFamily = Theme.Mono; b.TextAlignment = TextAlignment.Right;
            Grid.SetRow(a, i); Grid.SetRow(b, i); Grid.SetColumn(b, 1);
            g.Children.Add(line); g.Children.Add(a); g.Children.Add(b);
            i++;
        }
        return g;
    }

    ChartView Chart(double h, Action<DrawingContext, Size, Palette, double> paint, Func<Size, Point, string?>? hit = null) =>
        new() { Height = h, Paint = paint, Hit = hit, Margin = new Thickness(0, 8, 0, 4) };

    static Border Swatch(string brush)
    {
        var b = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Margin = new Thickness(10, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        b.SetResourceReference(Border.BackgroundProperty, brush);
        return b;
    }

    // ------------------------------------------------------------------ report

    void Build()
    {
        Title = L("รายงานการร้อง · ", "Singing report · ") + Feedback.SourceName(r.Source, lang);
        HeadTitle.Text = Feedback.SourceName(r.Source, lang);
        HeadMeta.Text = $"{Feedback.Time(r.Duration)} · {r.When:yyyy-MM-dd HH:mm} · " + (VoiceTypes.All.FirstOrDefault(v => v.id == r.Voice) is var vt && vt.id != null ? L(vt.th, vt.en) : r.Voice);
        ExportBtn.Content = L("บันทึกเป็น HTML…", "Export HTML…");
        Body.Children.Clear();

        H2(L("สรุป", "Summary"));
        Card(Txt(Feedback.Summary(r, lang), 14.5));
        if (r.Caveats.Contains("music") || r.Caveats.Contains("bleed"))
        {
            var w = Card(Txt(Feedback.CaveatText(r, lang)[0], 13, "Text"));
            w.Margin = new Thickness(0, 10, 0, 0);
            w.SetResourceReference(Border.BorderBrushProperty, "Amber");
        }

        H2(L("จุดแข็ง", "Strengths"));
        var st = new StackPanel();
        foreach (var f in r.Strengths)
        {
            var (t, d) = Feedback.Strength(r, f, lang);
            var head = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            var tick = new Run("✓  "); tick.SetResourceReference(TextElement.ForegroundProperty, "Green");
            head.Inlines.Add(tick); head.Inlines.Add(new Run(t));
            head.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            st.Children.Add(head);
            st.Children.Add(Txt(d, 13, "Sub", margin: new Thickness(22, 2, 0, 10)));
        }
        Card(st);

        // what went wrong (diagnosis) ...
        H2(L("จุดที่ควรแก้ (เรียงตามความสำคัญ)", "Weaknesses (most important first)"));
        var ws = new StackPanel();
        if (r.Weaknesses.Count == 0) ws.Children.Add(Txt(L("ไม่เจอปัญหาใหญ่ในเทคนี้", "No major problems found in this take.")));
        int k = 1;
        foreach (var f in r.Weaknesses)
        {
            var box = new StackPanel { Margin = new Thickness(0, k == 1 ? 0 : 16, 0, 0) };
            box.Children.Add(Txt($"{k++}.  " + Feedback.Title(r, f, lang), 14.5, "Text", true));
            void Field(string label, string text)
            {
                if (text == "") return;
                var t = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(22, 4, 0, 0) };
                var lb = new Run(label + "  ") { FontWeight = FontWeights.SemiBold }; lb.SetResourceReference(TextElement.ForegroundProperty, "Sub");
                t.Inlines.Add(lb); t.Inlines.Add(new Run(text));
                t.SetResourceReference(TextBlock.ForegroundProperty, "Text");
                box.Children.Add(t);
            }
            Field(L("วัดได้", "Measured"), Feedback.Measured(r, f, lang));
            Field(L("ตรงไหน", "Where"), Feedback.Where(r, f, lang));
            Field(L("สาเหตุที่น่าจะเป็น", "Likely cause"), Feedback.Cause(r, f, lang));
            ws.Children.Add(box);
        }
        Card(ws);

        // ... and what to do about it, numbered the same way
        if (r.Weaknesses.Any(f => Feedback.Exercises(r, f, lang).Count > 0))
        {
            H2(L("สิ่งที่ควรฝึก", "What to work on"));
            var wo = new StackPanel();
            k = 1;
            foreach (var f in r.Weaknesses)
            {
                int n = k++;
                var ex = Feedback.Exercises(r, f, lang);
                if (ex.Count == 0) continue;
                wo.Children.Add(Txt($"{n}.  " + Feedback.Title(r, f, lang), 14, "Text", true, new Thickness(0, wo.Children.Count == 0 ? 0 : 14, 0, 2)));
                var bl = (FrameworkElement)Bullets(ex); bl.Margin = new Thickness(22, 0, 0, 0);
                wo.Children.Add(bl);
            }
            Card(wo);
        }

        var plan = Feedback.Plan(r, lang);
        H2(L($"แผนฝึกวันละ {plan.Sum(p => p.min)} นาที", $"Daily practice plan: {plan.Sum(p => p.min)} minutes"));
        Card(Bullets(plan.Select(p => $"{p.min} {L("นาที", "min")} · {p.text}")));

        H2(L("เสียงของคุณบอกอะไร", "What this says about your voice"));
        Card(Bullets(Feedback.Voice(r, lang)));

        if (prev != null)
        {
            H2(L("พัฒนาการเทียบกับครั้งก่อน", "Progress since last time"));
            var d = Feedback.Compare(r, prev, lang);
            var rows = d.Select(x => (x.Label, $"{x.Before} → {x.After}  " + (x.Better > 0 ? L("ดีขึ้น", "better") : x.Better < 0 ? L("แย่ลง", "worse") : L("เท่าเดิม", "same")))).ToList();
            Card(Stack(Txt(L("เทียบกับ ", "Compared with ") + Feedback.SourceName(prev.Source, lang) + " · " + prev.When.ToString("yyyy-MM-dd HH:mm"), 12.5, "Sub", margin: new Thickness(0, 0, 0, 6)),
                Table(rows, i => d[i].Better > 0 ? "Green" : d[i].Better < 0 ? "Err" : "Text")));
        }

        H2(L("กราฟ", "Graphs"));
        var legend = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        legend.Children.Add(Txt(L("กล่อง = โน้ตที่ร้อง สีบอกความตรง", "Boxes = sung notes, coloured by accuracy"), 12, "Sub"));
        legend.Children.Add(Swatch("Green")); legend.Children.Add(Txt("±20¢", 12, "Sub"));
        legend.Children.Add(Swatch("Amber")); legend.Children.Add(Txt("±35¢", 12, "Sub"));
        legend.Children.Add(Swatch("Red")); legend.Children.Add(Txt(L("เกินกว่านั้น", "more"), 12, "Sub"));
        legend.Children.Add(Txt(L("  · กล่องมีขอบ = มีปัญหา · ▼ เสียงแตก · ชี้เมาส์ที่โน้ตเพื่อดูรายละเอียด", "  · outlined = flagged · ▼ crack · hover a note for details"), 12, "Sub"));
        var pitch = Stack(Txt(L("เสียงร้องตามเวลา", "Your pitch over time"), 13.5, "Text", true), legend);
        double rowH = Charts.PitchRowHeight(r);
        for (int row = 0; row < Charts.PitchRows(r); row++)
        {
            int rr = row;
            pitch.Children.Add(Chart(rowH, (dc, sz, p, ppd) => Charts.PitchRow(dc, sz, r, rr, p, ppd), (sz, pt) => Charts.PitchHit(r, rr, sz, pt, lang)));
        }
        Card(pitch);
        Card(Stack(Txt(L("ความคลาดของแต่ละโน้ต (เซนต์)", "How far each note was from its target (cents)"), 13.5, "Text", true),
            Chart(190, (dc, sz, p, ppd) => Charts.Histogram(dc, sz, r, p, ppd, lang)))).Margin = new Thickness(0, 12, 0, 0);
        Card(Stack(Txt(L("ความดังตามเวลา (แถบเทา = ประโยค)", "Loudness over time (grey bands = phrases)"), 13.5, "Text", true),
            Chart(150, (dc, sz, p, ppd) => Charts.Loudness(dc, sz, r, p, ppd)))).Margin = new Thickness(0, 12, 0, 0);
        Card(Stack(Txt(L("คุณภาพเสียงแต่ละช่วง", "Voice quality by pitch zone"), 13.5, "Text", true),
            Chart(200, (dc, sz, p, ppd) => Charts.Zones(dc, sz, r, p, ppd, lang)),
            Txt(L("แท่งจาง = มีโน้ตน้อยกว่า 5 ตัว", "Faded bars = fewer than 5 notes"), 12, "Faint"))).Margin = new Thickness(0, 12, 0, 0);

        H2(L("ข้อจำกัด", "Limits"));
        Card(Bullets(Feedback.CaveatText(r, lang), "Sub"));

        // raw stats last: useful for reference, not for deciding what to practise
        H2(L("ตัวเลขสำคัญ", "Key numbers"));
        Card(Table(Feedback.Numbers(r, lang)));
    }

    // ------------------------------------------------------------------ export

    void Export()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = L("บันทึกรายงาน", "Save report"),
            FileName = $"{Path.GetFileNameWithoutExtension(Feedback.SourceName(r.Source, lang))} - {L("รายงานการร้อง", "singing report")} {r.When:yyyy-MM-dd HH.mm}.html",
            Filter = "HTML|*.html",
            DefaultExt = ".html",
            InitialDirectory = Directory.Exists(M.S.LastDir) ? M.S.LastDir : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        foreach (char c in Path.GetInvalidFileNameChars()) dlg.FileName = dlg.FileName.Replace(c, '_');
        if (dlg.ShowDialog(this) != true) return;
        ExportRes.Inlines.Clear();
        try
        {
            File.WriteAllText(dlg.FileName, HtmlReport.Build(r, prev, lang), new System.Text.UTF8Encoding(false));
            var ok = new Run(L("บันทึกแล้ว  ", "Saved  ")) { FontWeight = FontWeights.SemiBold };
            ok.SetResourceReference(TextElement.ForegroundProperty, "Green");
            ExportRes.Inlines.Add(ok);
            string file = dlg.FileName;
            var open = new Hyperlink(new Run(L("เปิดในเบราว์เซอร์", "Open in browser")));
            open.SetResourceReference(TextElement.ForegroundProperty, "Green");
            open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); } catch { } };
            var folder = new Hyperlink(new Run(L("แสดงในโฟลเดอร์", "Show in folder")));
            folder.SetResourceReference(TextElement.ForegroundProperty, "Sub");
            folder.Click += (_, _) => Process.Start("explorer.exe", $"/select,\"{file}\"");
            ExportRes.Inlines.Add(open); ExportRes.Inlines.Add(new Run("  ·  ")); ExportRes.Inlines.Add(folder);
            ExportRes.Inlines.Add(new Run(L("  ·  พิมพ์เป็น PDF ได้จากเบราว์เซอร์", "  ·  print to PDF from the browser")));
            ExportRes.SetResourceReference(TextBlock.ForegroundProperty, "Sub");
        }
        catch (Exception ex)
        {
            ExportRes.Inlines.Add(new Run(L("บันทึกไม่สำเร็จ: ", "Couldn't save: ") + ex.Message));
            ExportRes.SetResourceReference(TextBlock.ForegroundProperty, "Err");
        }
        ExportRes.Visibility = Visibility.Visible;
    }
}
