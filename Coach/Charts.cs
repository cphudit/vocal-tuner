using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VocalTuner.Coach;

/// <summary>
/// Report graphs, drawn with plain WPF drawing calls so the same code paints the in-app report
/// (current theme) and the PNGs embedded in the HTML export (light theme).
/// </summary>
public static class Charts
{
    public const double RowSec = 30;
    const double Gutter = 38, Bottom = 18;

    static readonly Typeface Sans = new(Theme.Sans, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    static readonly Typeface SansBold = new(Theme.Sans, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    static readonly Typeface Mono = new(Theme.Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    static FormattedText T(string s, double size, Brush b, double ppd, bool mono = true, bool bold = false) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? SansBold : mono ? Mono : Sans, size, b, ppd);

    public static Color Grade(Palette p, double cents, CoachConfig? c = null)
    {
        double a = Math.Abs(cents), inTune = c?.InTune ?? 20, close = c?.Close ?? 35;
        return a <= inTune ? p.Green : a <= close ? p.Amber : p.Red;
    }

    // ------------------------------------------------------------------ pitch over time

    public static int PitchRows(CoachResult r) => Math.Max(1, (int)Math.Ceiling(r.Duration / RowSec));

    static (int lo, int hi) PitchSpan(CoachResult r)
    {
        int lo = r.Notes.Count > 0 ? r.Notes.Min(n => n.Target) : 48, hi = r.Notes.Count > 0 ? r.Notes.Max(n => n.Target) : 72;
        if (!double.IsNaN(r.M.RangeLo)) lo = Math.Min(lo, (int)r.M.RangeLo);
        if (!double.IsNaN(r.M.RangeHi)) hi = Math.Max(hi, (int)r.M.RangeHi);
        return (lo - 2, hi + 2);
    }

    /// <summary>Height a pitch row needs (lanes of about 8 px).</summary>
    public static double PitchRowHeight(CoachResult r)
    {
        var (lo, hi) = PitchSpan(r);
        return Math.Clamp((hi - lo + 1) * 8, 120, 260) + Bottom;
    }

    public static void PitchRow(DrawingContext dc, Size sz, CoachResult r, int row, Palette p, double ppd)
    {
        var (lo, hi) = PitchSpan(r);
        double t0 = row * RowSec, t1 = t0 + RowSec;
        double plotW = sz.Width - Gutter, plotH = sz.Height - Bottom;
        int lanes = hi - lo + 1;
        double laneH = plotH / lanes;
        double X(double t) => Gutter + (t - t0) / RowSec * plotW;
        double Y(double midi) => (hi + 0.5 - midi) * laneH;

        dc.DrawRectangle(p.B(p.RollNatural), null, new Rect(Gutter, 0, plotW, plotH));
        for (int m = lo; m <= hi; m++)
        {
            bool black = Draw.NoteName(m).Length > 1;
            if (black) dc.DrawRectangle(p.B(p.RollSharp), null, new Rect(Gutter, Y(m) - laneH / 2, plotW, laneH));
            bool label = laneH >= 11 ? !black : m % 12 == 0 || m % 12 == 7;
            if (label)
            {
                var ft = T(Draw.NoteName(m) + Draw.Octave(m), 9.5, p.B(m % 12 == 0 ? p.Sub : p.Faint), ppd);
                dc.DrawText(ft, new Point(Gutter - 6 - ft.Width, Y(m) - ft.Height / 2));
            }
            if (m % 12 == 0) dc.DrawLine(new Pen(p.B(p.Lane), 1), new Point(Gutter, Y(m) + laneH / 2), new Point(sz.Width, Y(m) + laneH / 2));
        }
        // time grid every 5 s
        for (double t = Math.Ceiling(t0 / 5) * 5; t <= t1 + 1e-6; t += 5)
        {
            double x = X(t);
            dc.DrawLine(new Pen(p.B(p.Lane, 140), 1), new Point(x, 0), new Point(x, plotH));
            var ft = T(Feedback.Time(t), 9.5, p.B(p.Faint), ppd);
            dc.DrawText(ft, new Point(Math.Min(x - ft.Width / 2, sz.Width - ft.Width), plotH + 3));
        }
        if (r.Duration < t1) dc.DrawRectangle(p.B(p.Bg, 200), null, new Rect(X(r.Duration), 0, Math.Max(0, sz.Width - X(r.Duration)), plotH));

        // note blocks, coloured by accuracy; flagged notes get an outline
        var flagPen = new Pen(p.B(p.Text, 200), 1.4);
        foreach (var n in r.Notes)
        {
            if (n.T1 < t0 || n.T0 > t1) continue;
            double x0 = Math.Max(Gutter, X(n.T0)), x1 = Math.Min(sz.Width, X(n.T1));
            if (x1 - x0 < 1) continue;
            var rect = new Rect(x0, Y(n.Target) - laneH / 2 + 1, x1 - x0, Math.Max(2, laneH - 2));
            bool flagged = n.Flags.Length > 0;
            dc.DrawRoundedRectangle(p.B(Grade(p, n.Cents), 120), flagged ? flagPen : null, rect, 2, 2);
        }

        // sung pitch
        double refSt = r.M.RefCents / 100;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            bool open = false;
            int i0 = Math.Max(0, (int)(t0 / r.Hop) - 1), i1 = Math.Min(r.PitchCenti.Length, (int)(t1 / r.Hop) + 2);
            for (int i = i0; i < i1; i++)
            {
                int pc = r.PitchCenti[i];
                if (pc == 0) { open = false; continue; }
                double t = i * r.Hop + Frames.N / 2.0 / Sig.Rate;
                var pt = new Point(X(t), Y(pc / 100.0 - refSt));
                if (!open) { g.BeginFigure(pt, false, false); open = true; }
                else g.LineTo(pt, true, false);
            }
        }
        geo.Freeze();
        dc.PushClip(new RectangleGeometry(new Rect(Gutter, 0, plotW, plotH)));
        dc.DrawGeometry(null, new Pen(p.B(p.Text, 210), 1.3) { LineJoin = PenLineJoin.Round }, geo);
        foreach (var b in r.Breaks.Where(b => b.T >= t0 && b.T <= t1))
        {
            double x = X(b.T), y = Y(b.From) - laneH / 2 - 2;
            var tri = new StreamGeometry();
            using (var g = tri.Open()) { g.BeginFigure(new Point(x - 4, y - 6), true, true); g.LineTo(new Point(x + 4, y - 6), true, false); g.LineTo(new Point(x, y), true, false); }
            dc.DrawGeometry(p.B(p.Red), null, tri);
        }
        dc.Pop();
    }

    /// <summary>Tooltip text for the note under the mouse in a pitch row.</summary>
    public static string? PitchHit(CoachResult r, int row, Size sz, Point pt, string lang)
    {
        var (lo, hi) = PitchSpan(r);
        double plotW = sz.Width - Gutter, plotH = sz.Height - Bottom;
        if (pt.X < Gutter || pt.Y > plotH) return null;
        double laneH = plotH / (hi - lo + 1);
        double t = row * RowSec + (pt.X - Gutter) / plotW * RowSec;
        double midi = hi + 0.5 - pt.Y / laneH;
        var n = r.Notes.Where(n => t >= n.T0 - 0.05 && t <= n.T1 + 0.05).OrderBy(n => Math.Abs(n.Target - midi)).FirstOrDefault();
        if (n == null || Math.Abs(n.Target - midi) > 2.5) return null;
        var parts = new List<string>
        {
            $"{Feedback.Note(n.Target)}  {Feedback.Signed(n.Cents)}¢  ·  {Feedback.Time(n.T0)}  ·  {n.Dur:0.00} s",
        };
        foreach (char f in n.Flags.Distinct()) parts.Add(FlagText(f, n, lang));
        return string.Join("\n", parts);
    }

    public static string FlagText(char f, Note n, string lang) => f switch
    {
        'S' => Feedback.L(lang, $"ช้อนเสียงขึ้น ({Feedback.Signed(n.Onset ?? 0)}¢ ตอนเริ่ม)", $"Scoop ({Feedback.Signed(n.Onset ?? 0)}¢ at the start)"),
        'A' => Feedback.L(lang, $"เริ่มสูงกว่าโน้ต ({Feedback.Signed(n.Onset ?? 0)}¢)", $"Starts above ({Feedback.Signed(n.Onset ?? 0)}¢)"),
        'W' => Feedback.L(lang, $"ส่าย ({Feedback.N0(n.StdSlow)}¢)", $"Wanders ({Feedback.N0(n.StdSlow)}¢)"),
        'D' => Feedback.L(lang, $"ท้ายเสียงตก ({Feedback.Signed(n.End ?? 0)}¢)", $"Falling end ({Feedback.Signed(n.End ?? 0)}¢)"),
        'U' => Feedback.L(lang, "ท้ายเสียงสั่น", "Wavering end"),
        'K' => Feedback.L(lang, "อยู่นอกคีย์", "Outside the key"),
        _ => f.ToString(),
    };

    // ------------------------------------------------------------------ cents histogram

    public static void Histogram(DrawingContext dc, Size sz, CoachResult r, Palette p, double ppd, string lang)
    {
        const int Lo = -60, Hi = 60, Bin = 5;
        int nb = (Hi - Lo) / Bin + 1;
        var counts = new int[nb];
        foreach (var n in r.Notes) counts[Math.Clamp((int)Math.Round((n.Cents - Lo) / Bin), 0, nb - 1)]++;
        int max = Math.Max(1, counts.Max());
        double left = 8, plotW = sz.Width - 16, top = 18, plotH = sz.Height - top - 20;
        double bw = plotW / nb;
        double X(double cents) => left + (cents - Lo + Bin / 2.0) / (Hi - Lo + Bin) * plotW;

        dc.DrawLine(new Pen(p.B(p.Lane), 1), new Point(left, top + plotH), new Point(left + plotW, top + plotH));
        foreach (int g in new[] { -35, -20, 20, 35 })
            dc.DrawLine(new Pen(p.B(p.Lane), 1) { DashStyle = DashStyles.Dash }, new Point(X(g), top), new Point(X(g), top + plotH));
        for (int i = 0; i < nb; i++)
        {
            if (counts[i] == 0) continue;
            double c = Lo + i * Bin, h = counts[i] / (double)max * plotH;
            var rect = new Rect(left + i * bw + 1, top + plotH - h, Math.Max(1, bw - 2), h);
            dc.DrawRoundedRectangle(p.B(Grade(p, c)), null, rect, Math.Min(3, bw / 3), Math.Min(3, bw / 3));
            if (counts[i] == max) { var ft = T(max.ToString(), 9.5, p.B(p.Sub), ppd); dc.DrawText(ft, new Point(rect.X + rect.Width / 2 - ft.Width / 2, rect.Y - ft.Height - 1)); }
        }
        foreach (int g in new[] { -50, -35, -20, 0, 20, 35, 50 })
        {
            var ft = T((g > 0 ? "+" : g < 0 ? "−" : "") + Math.Abs(g), 9.5, p.B(p.Faint), ppd);
            dc.DrawText(ft, new Point(X(g) - ft.Width / 2, top + plotH + 3));
        }
        double mean = Math.Clamp(r.M.MeanSigned, Lo, Hi);
        dc.DrawLine(new Pen(p.B(p.Text), 1.5), new Point(X(mean), top - 4), new Point(X(mean), top + plotH));
        var lab = T(Feedback.L(lang, "เฉลี่ย ", "avg ") + Feedback.Signed(r.M.MeanSigned) + "¢", 10, p.B(p.Text), ppd, mono: false);
        dc.DrawText(lab, new Point(Math.Clamp(X(mean) + 4, 0, sz.Width - lab.Width), top - 16));
    }

    // ------------------------------------------------------------------ loudness over time

    public static void Loudness(DrawingContext dc, Size sz, CoachResult r, Palette p, double ppd)
    {
        double left = 34, plotW = sz.Width - left - 6, top = 6, plotH = sz.Height - top - 18;
        const double DbLo = -30;
        double X(double t) => left + t / Math.Max(1, r.Duration) * plotW;
        double Y(double db) => top + (Math.Clamp(db, DbLo, 0) / DbLo) * plotH;
        foreach (int g in new[] { 0, -12, -24 })
        {
            dc.DrawLine(new Pen(p.B(p.Lane), 1), new Point(left, Y(g)), new Point(left + plotW, Y(g)));
            var ft = T(g == 0 ? "0 dB" : g.ToString().Replace("-", "−"), 9.5, p.B(p.Faint), ppd);
            dc.DrawText(ft, new Point(left - 4 - ft.Width, Y(g) - ft.Height / 2));
        }
        double step = r.Duration > 240 ? 60 : r.Duration > 90 ? 30 : r.Duration > 40 ? 10 : 5;
        for (double t = 0; t <= r.Duration; t += step)
        {
            var ft = T(Feedback.Time(t), 9.5, p.B(p.Faint), ppd);
            dc.DrawText(ft, new Point(Math.Clamp(X(t) - ft.Width / 2, left, sz.Width - ft.Width), top + plotH + 3));
        }
        foreach (var ph in r.Phrases)
            dc.DrawRectangle(p.B(p.Muted), null, new Rect(X(ph.T0), top, Math.Max(1, X(ph.T1) - X(ph.T0)), plotH));
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            bool open = false;
            for (int i = 0; i < r.Loud.Length; i++)
            {
                if (r.Loud[i] <= -100) { open = false; continue; }
                var pt = new Point(X((i + 0.5) * r.LoudHop), Y(r.Loud[i]));
                if (!open) { g.BeginFigure(pt, false, false); open = true; } else g.LineTo(pt, true, true);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(p.B(p.Text, 220), 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);
    }

    // ------------------------------------------------------------------ per-zone quality

    public static void Zones(DrawingContext dc, Size sz, CoachResult r, Palette p, double ppd, string lang)
    {
        var zs = r.Zones.Where(z => z.Notes > 0).ToList();
        if (zs.Count == 0) return;
        var panels = new (string title, Func<ZoneStat, double> v, Func<double, string> fmt, double min, double max)[]
        {
            (Feedback.L(lang, "ตรงคีย์ (%)", "On pitch (%)"), z => z.InTunePct, v => Feedback.N0(v), 0, 100),
            (Feedback.L(lang, "ความดัง (dB เทียบค่ากลาง)", "Loudness (dB vs typical)"), z => z.Db, Feedback.Signed, -12, 6),
            (Feedback.L(lang, "ความใส HNR (dB)", "Clarity HNR (dB)"), z => z.Hnr, v => Feedback.N0(v), 0, 30),
            (Feedback.L(lang, "ความสว่าง (Hz)", "Brightness (Hz)"), z => z.Centroid, v => Feedback.N0(v), 0, Math.Max(2500, zs.Max(z => double.IsNaN(z.Centroid) ? 0 : z.Centroid) * 1.1)),
        };
        double gap = 18, pw = (sz.Width - gap * (panels.Length - 1)) / panels.Length;
        double top = 22, bottom = 30, plotH = sz.Height - top - bottom;
        for (int k = 0; k < panels.Length; k++)
        {
            var (title, val, fmt, min, max) = panels[k];
            double x0 = k * (pw + gap);
            var tt = T(title, 10.5, p.B(p.Sub), ppd, mono: false);
            tt.MaxTextWidth = pw; tt.MaxLineCount = 1; tt.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(tt, new Point(x0, 0));
            double Y(double v) => top + plotH - (Math.Clamp(v, min, max) - min) / (max - min) * plotH;
            double baseY = Y(Math.Max(min, Math.Min(0, max)));
            dc.DrawLine(new Pen(p.B(p.Lane), 1), new Point(x0, baseY), new Point(x0 + pw, baseY));
            double bw = pw / zs.Count;
            for (int i = 0; i < zs.Count; i++)
            {
                var z = zs[i];
                double v = val(z);
                double cx = x0 + bw * (i + 0.5), w = Math.Min(26, bw - 6);
                bool thin = z.Notes < 5;
                if (!double.IsNaN(v))
                {
                    double y = Y(v);
                    var rect = new Rect(cx - w / 2, Math.Min(y, baseY), w, Math.Max(1.5, Math.Abs(baseY - y)));
                    dc.DrawRoundedRectangle(p.B(p.Sub, (byte)(thin ? 90 : 220)), null, rect, 3, 3);
                    var vt = T(fmt(v), 9.5, p.B(p.Text), ppd);
                    dc.DrawText(vt, new Point(cx - vt.Width / 2, (v >= 0 || min >= 0 ? rect.Top - vt.Height - 1 : rect.Bottom + 1)));
                }
                string zl = ShortZone(r, z.Zone);
                var zt = T(zl, 9, p.B(p.Faint), ppd);
                dc.DrawText(zt, new Point(cx - zt.Width / 2, top + plotH + 4));
                var nt = T("n=" + z.Notes, 8.5, p.B(p.Faint), ppd);
                dc.DrawText(nt, new Point(cx - nt.Width / 2, top + plotH + 15));
            }
        }
    }

    public static string ShortZone(CoachResult r, int z)
    {
        var e = r.ZoneEdges;
        if (z <= 0) return "<" + Feedback.Note(e[0]);
        if (z >= e.Length) return Feedback.Note(e[^1]) + "+";
        return Feedback.Note(e[z - 1]) + "–" + Feedback.Note(e[z] - 1);
    }

    // ------------------------------------------------------------------ progress across takes

    public static void Trend(DrawingContext dc, Size sz, IReadOnlyList<CoachResult> hist, Palette p, double ppd)
    {
        if (hist.Count == 0) return;
        double left = 34, plotW = sz.Width - left - 30, top = 10, plotH = sz.Height - top - 18;
        double X(int i) => left + (hist.Count == 1 ? plotW / 2 : i * plotW / (hist.Count - 1));
        double Y(double v) => top + plotH - Math.Clamp(v, 0, 100) / 100 * plotH;
        foreach (int g in new[] { 0, 50, 100 })
        {
            dc.DrawLine(new Pen(p.B(p.Lane), 1), new Point(left, Y(g)), new Point(left + plotW, Y(g)));
            var ft = T(g + "%", 9.5, p.B(p.Faint), ppd);
            dc.DrawText(ft, new Point(left - 4 - ft.Width, Y(g) - ft.Height / 2));
        }
        var geo = new StreamGeometry();
        using (var g = geo.Open())
            for (int i = 0; i < hist.Count; i++)
            {
                var pt = new Point(X(i), Y(hist[i].M.InTunePct));
                if (i == 0) g.BeginFigure(pt, false, false); else g.LineTo(pt, true, true);
            }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(p.B(p.Green), 2) { LineJoin = PenLineJoin.Round }, geo);
        for (int i = 0; i < hist.Count; i++)
            dc.DrawEllipse(p.B(p.Green), new Pen(p.B(p.Surface), 2), new Point(X(i), Y(hist[i].M.InTunePct)), 4.5, 4.5);
        var last = hist[^1];
        var lt = T(Feedback.N0(last.M.InTunePct) + "%", 10.5, p.B(p.Text), ppd);
        dc.DrawText(lt, new Point(X(hist.Count - 1) + 8, Y(last.M.InTunePct) - lt.Height / 2));
        var ft2 = T(hist[0].When.ToString("d MMM", CultureInfo.InvariantCulture), 9.5, p.B(p.Faint), ppd);
        dc.DrawText(ft2, new Point(left, top + plotH + 3));
        if (hist.Count > 1)
        {
            var ft3 = T(last.When.ToString("d MMM", CultureInfo.InvariantCulture), 9.5, p.B(p.Faint), ppd);
            dc.DrawText(ft3, new Point(left + plotW - ft3.Width, top + plotH + 3));
        }
    }

    // ------------------------------------------------------------------ export

    /// <summary>Renders a drawing to PNG bytes at 2× for crisp HTML images.</summary>
    public static byte[] Png(double w, double h, Action<DrawingContext> draw, Color bg)
    {
        const double Scale = 2;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, w, h));
            draw(dc);
        }
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(w * Scale), (int)Math.Ceiling(h * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}

/// <summary>Hosts one chart in the app. Repaints with the theme; optional tooltip text from the mouse position.</summary>
public sealed class ChartView : Painted
{
    public Action<DrawingContext, Size, Palette, double>? Paint;
    public Func<Size, Point, string?>? Hit;
    readonly System.Windows.Controls.ToolTip tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };

    public ChartView()
    {
        ToolTip = tip;
        System.Windows.Controls.ToolTipService.SetInitialShowDelay(this, 0);
        tip.Opened += (_, _) => { if (tip.Content == null) tip.IsOpen = false; };
        MouseMove += (_, e) =>
        {
            if (Hit == null) { tip.IsOpen = false; return; }
            var s = Hit(RenderSize, e.GetPosition(this));
            tip.Content = s;
            tip.IsOpen = s != null;
            if (s != null) { tip.HorizontalOffset += 0.01; tip.HorizontalOffset -= 0.01; }
        };
        MouseLeave += (_, _) => tip.IsOpen = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        Paint?.Invoke(dc, RenderSize, Theme.P, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }
}
