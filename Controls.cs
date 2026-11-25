using Path = System.Windows.Shapes.Path;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace VocalTuner;

static class Draw
{
    public static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    public static string NoteName(int midi) => Names[((midi % 12) + 12) % 12];
    public static int Octave(int midi) => (int)Math.Floor(midi / 12.0) - 1;
    public static Color CentsColor(double c)
    {
        double a = Math.Abs(c);
        return a <= 5 ? Theme.P.Green : a <= 15 ? Theme.P.Amber : Theme.P.Red;
    }

    static readonly Typeface MonoFace = new(Theme.Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    public static FormattedText Text(string s, double size, Brush b, Visual v, bool mono = true) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            mono ? MonoFace : new Typeface(Theme.Sans, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, b, VisualTreeHelper.GetDpi(v).PixelsPerDip);

    public static Geometry Rounded(Rect r, double rad) => new RectangleGeometry(r, rad, rad);
}

/// <summary>Base class for custom-drawn elements: repaints when the theme changes.</summary>
public abstract class Painted : FrameworkElement
{
    protected Painted()
    {
        Loaded += (_, _) => Theme.Changed += InvalidateVisual;
        Unloaded += (_, _) => Theme.Changed -= InvalidateVisual;
    }
}

public enum TargetGrade { Pending, Good, Close, Off, Missed }

/// <summary>A note to sing on the pitch roll: times in ms (same clock as the trace), MIDI note, result.</summary>
public readonly record struct RollTarget(double T0, double T1, int Midi, TargetGrade Grade, bool Active);

/// <summary>Scrolling piano roll: note lanes, held-note blocks coloured by accuracy, and the pitch line.</summary>
public sealed class PitchRoll : Painted
{
    public IReadOnlyList<RollPoint> Points = Array.Empty<RollPoint>();
    public double TRight, FocusT, MinBar = 100;
    public double WindowMs { get; set; } = 8000;
    public double? PlayT;
    /// <summary>Notes to sing (warm-up): drawn as boxes behind the pitch line.</summary>
    public IReadOnlyList<RollTarget>? Targets;
    /// <summary>When set, the view centres on this note instead of following the pitch line.</summary>
    public double? FocusMidi;
    /// <summary>Draw the held-note blocks coloured by accuracy (off when targets do the grading).</summary>
    public bool ShowNoteBlocks { get; set; } = true;
    double? viewMid;
    public void ResetView() => viewMid = null;

    protected override void OnRender(DrawingContext dc)
    {
        var P = Theme.P;
        double W = ActualWidth, H = ActualHeight;
        if (W < 10 || H < 10) return;
        dc.PushClip(Draw.Rounded(new Rect(0, 0, W, H), 12));
        dc.DrawRectangle(P.B(P.Muted), null, new Rect(0, 0, W, H));
        var pts = Points;

        int SPAN = Math.Max(10, (int)Math.Round(14 * (H / W) * (1280.0 / 720)));
        double? focus = null;
        for (int i = pts.Count - 1; i >= 0; i--)
        {
            var p = pts[i];
            if (p.T > FocusT) continue;
            if (!p.IsGap) { focus = p.Midi; break; }
        }
        if (focus == null && PlayT != null)
            foreach (var p in pts) if (!p.IsGap && p.T >= FocusT) { focus = p.Midi; break; }
        if (FocusMidi is double fx) focus = fx;
        viewMid ??= focus ?? 57;
        double mid = viewMid.Value;
        if (focus is double fm)
        {
            if (Math.Abs(fm - mid) > SPAN) mid = fm;
            mid += (fm - mid) * 0.04;
            if (fm > mid + SPAN / 2.0 - 2) mid = fm - SPAN / 2.0 + 2;
            if (fm < mid - SPAN / 2.0 + 2) mid = fm + SPAN / 2.0 - 2;
        }
        viewMid = mid;

        double laneH = H / SPAN, topMidi = mid + SPAN / 2.0;
        double Y(double m) => (topMidi - m) * laneH;
        double X(double t) => W - (TRight - t) / WindowMs * W;

        int lo = (int)Math.Floor(mid - SPAN / 2.0) - 1, hi = (int)Math.Ceiling(mid + SPAN / 2.0) + 1;
        for (int n = lo; n <= hi; n++)
        {
            bool sharp = Draw.NoteName(n).Contains('#');
            dc.DrawRectangle(P.B(sharp ? P.RollSharp : P.RollNatural), null, new Rect(0, Y(n + 0.5), W, laneH + 0.5));
        }

        // target boxes (warm-up): outline while coming up / being sung, then filled by how close you were
        if (Targets != null)
            foreach (var tg in Targets)
            {
                double x0 = X(tg.T0), x1 = X(tg.T1);
                if (x1 < 0 || x0 > W) continue;
                var r = new Rect(x0 + 1, Y(tg.Midi + 0.5) + 1, Math.Max(2, x1 - x0 - 2), Math.Max(2, laneH - 2));
                var (fill, edge) = tg.Grade switch
                {
                    TargetGrade.Good => (P.B(P.Green, 70), P.B(P.Green)),
                    TargetGrade.Close => (P.B(P.Amber, 70), P.B(P.Amber)),
                    TargetGrade.Off => (P.B(P.Red, 60), P.B(P.Red)),
                    TargetGrade.Missed => (P.B(P.Faint, 30), P.B(P.Faint)),
                    _ => (P.B(P.Text, tg.Active ? (byte)40 : (byte)16), P.B(P.Sub)),
                };
                dc.DrawRoundedRectangle(fill, new Pen(edge, tg.Active ? 2 : 1), r, 4, 4);
            }

        // note blocks: consecutive points on the same held note, coloured by average deviation
        double segStart = 0, segEnd = 0, sum = 0; int segNote = int.MinValue, cnt = 0;
        void Flush()
        {
            if (segNote != int.MinValue && segEnd - segStart >= MinBar && cnt > 0)
            {
                double avg = sum / cnt;
                var (c, a) = avg <= 5 ? (P.Green, (byte)140) : avg <= 15 ? (P.Amber, (byte)140) : (P.Red, (byte)128);
                double x0 = X(segStart);
                dc.DrawRectangle(P.B(c, a), null, new Rect(x0, Y(segNote + 0.5), Math.Max(1.5, X(segEnd) - x0), laneH));
            }
            segNote = int.MinValue; sum = 0; cnt = 0;
        }
        if (ShowNoteBlocks)
        {
            foreach (var p in pts)
            {
                if (p.IsGap) { Flush(); continue; }
                if (p.Note != segNote) { Flush(); segStart = p.T; segNote = p.Note; }
                segEnd = p.T; sum += Math.Abs(p.Cents); cnt++;
            }
            Flush();
        }

        // pitch line: break on gaps or jumps instead of drawing vertical drops
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            RollPoint? prev = null;
            foreach (var p in pts)
            {
                if (p.IsGap) { prev = null; continue; }
                var pt = new Point(X(p.T), Y(p.Midi));
                if (prev is not RollPoint q || p.T - q.T > 400 || Math.Abs(p.Midi - q.Midi) > 1.2) g.BeginFigure(pt, false, false);
                else g.LineTo(pt, true, false);
                prev = p;
            }
        }
        geo.Freeze();
        var pen = new Pen(P.B(P.Text), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(null, pen, geo);

        if (PlayT is double pt0) dc.DrawRectangle(P.B(P.Red), null, new Rect(Math.Round(X(pt0)) - 1, 0, 2, H));

        // note labels (naturals) on the left
        double fs = Math.Max(8, laneH * 0.45);
        for (int n = lo; n <= hi; n++)
        {
            string name = Draw.NoteName(n);
            if (name.Contains('#')) continue;
            double top = Y(n + 0.5);
            dc.DrawRectangle(P.B(P.Bg), null, new Rect(0, top + 0.5, 38, laneH - 1));
            var ft = Draw.Text(name + Draw.Octave(n), fs, P.B(P.Sub), this);
            dc.DrawText(ft, new Point(7, top + (laneH - ft.Height) / 2));
        }
        dc.Pop();
    }
}

/// <summary>Horizontal cents bar with the in-tune zone and a coloured needle.</summary>
public sealed class TunerBar : Painted
{
    public double Cents;      // -50..50
    public bool Active;
    protected override void OnRender(DrawingContext dc)
    {
        var P = Theme.P; double W = ActualWidth, H = ActualHeight;
        if (W < 4 || H < 4) return;
        double x = W * (0.5 + Math.Clamp(Cents, -50, 50) / 100);
        var needle = P.B(Active ? Draw.CentsColor(Cents) : P.Off);
        if (H < 30)
        {
            // compact bar (piano page): thin track, full-height centre mark and needle
            double th = Math.Round(H * 0.6), ty = (H - th) / 2;
            dc.DrawRoundedRectangle(P.B(P.Lane), null, new Rect(0, ty, W, th), th / 2, th / 2);
            dc.DrawRoundedRectangle(P.B(P.Faint), null, new Rect(W / 2 - 1, 0, 2, H), 1, 1);
            dc.DrawRoundedRectangle(needle, null, new Rect(x - 3, 0, 6, H), 3, 3);
            return;
        }
        dc.PushClip(Draw.Rounded(new Rect(0, 0, W, H), 12));
        dc.DrawRectangle(P.B(P.Muted), null, new Rect(0, 0, W, H));
        double tick = H * 0.34, mark = H * 0.16, nIn = H * 0.09;     // insets scale with height (64 px design)
        foreach (var f in new[] { 0.1, 0.3, 0.7, 0.9 }) dc.DrawRectangle(P.B(P.Border2), null, new Rect(Math.Round(W * f), tick, 1, H - 2 * tick));
        dc.DrawRectangle(P.B(P.Green, 26), null, new Rect(W * 0.45, 0, W * 0.1, H));
        dc.DrawRoundedRectangle(P.B(P.Faint), null, new Rect(W / 2 - 1, mark, 2, H - 2 * mark), 1, 1);
        dc.DrawRoundedRectangle(needle, null, new Rect(x - 2, nIn, 4, H - 2 * nIn), 2, 2);
        dc.Pop();
    }
}

/// <summary>Peak level bar on a -60..0 dBFS scale.</summary>
public sealed class LevelMeter : Painted
{
    float level;
    public bool Backing { get; set; }
    public float Level { get => level; set { if (Math.Abs(value - level) > 1e-4) { level = value; InvalidateVisual(); } } }
    protected override void OnRender(DrawingContext dc)
    {
        var P = Theme.P; double W = ActualWidth, H = ActualHeight;
        dc.DrawRoundedRectangle(P.B(P.Lane), null, new Rect(0, 0, W, H), H / 2, H / 2);
        double db = level > 0 ? 20 * Math.Log10(level) : -90;
        double w = Math.Clamp((db + 60) / 60, 0, 1) * W;
        if (w < 1) return;
        var c = level >= 0.99f ? P.Red : level >= 0.7f ? P.Amber : Backing ? P.Sub : P.Green;
        dc.DrawRoundedRectangle(P.B(c), null, new Rect(0, 0, w, H), H / 2, H / 2);
    }
}

/// <summary>Horizontal slider: drag, click, arrow keys, double-click to reset. Fill grows from zero (or min).</summary>
public sealed class HSlider : Painted
{
    public double Min { get; set; }
    public double Max { get; set; } = 100;
    public double Step { get; set; } = 1;
    public double Default { get; set; }
    double value;
    public double Value { get => value; set { this.value = Math.Clamp(value, Min, Max); InvalidateVisual(); } }
    public event Action<double>? Changed;     // while dragging
    public event Action<double>? Committed;   // on release / key / reset

    public HSlider() { Height = 26; Focusable = true; Cursor = Cursors.Hand; FocusVisualStyle = null; }

    void Set(double v, bool fire = true)
    {
        v = Math.Clamp(Math.Round(v / Step) * Step, Min, Max);
        if (v == value) return;
        value = v; InvalidateVisual();
        if (fire) Changed?.Invoke(v);
    }
    double FromX(double x) => Min + Math.Clamp((x - 8) / Math.Max(1, ActualWidth - 16), 0, 1) * (Max - Min);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ClickCount == 2) { Set(Default); Committed?.Invoke(value); e.Handled = true; return; }
        CaptureMouse(); Set(FromX(e.GetPosition(this).X)); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) Set(FromX(e.GetPosition(this).X)); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { if (IsMouseCaptured) { ReleaseMouseCapture(); Committed?.Invoke(value); } }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        int d = e.Key is Key.Right or Key.Up ? 1 : e.Key is Key.Left or Key.Down ? -1 : 0;
        if (d != 0) { Set(value + d * Step); Committed?.Invoke(value); e.Handled = true; }
    }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) => InvalidateVisual();
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var P = Theme.P; double W = ActualWidth, H = ActualHeight, cy = H / 2;
        if (W < 20 || H < 10) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));   // hit area
        double x0 = 8, x1 = W - 8;
        dc.DrawRoundedRectangle(P.B(P.Lane), null, new Rect(x0, cy - 2, x1 - x0, 4), 2, 2);
        double zero = Min < 0 && Max > 0 ? 0 : Min;
        double px = x0 + (value - Min) / (Max - Min) * (x1 - x0), zx = x0 + (zero - Min) / (Max - Min) * (x1 - x0);
        dc.DrawRoundedRectangle(P.B(P.Green), null, new Rect(Math.Min(px, zx), cy - 2, Math.Abs(px - zx), 4), 2, 2);
        if (IsKeyboardFocused) dc.DrawRoundedRectangle(null, new Pen(P.B(P.Green), 1.5), new Rect(px - 11, cy - 14, 22, 28), 7, 7);
        dc.DrawRoundedRectangle(P.B(P.Text), null, new Rect(px - 8, cy - 11, 16, 22), 5, 5);
    }
}

/// <summary>Vertical fader (detection settings). Double-click resets.</summary>
public sealed class VFader : Painted
{
    public double Min { get; set; }
    public double Max { get; set; } = 100;
    public double Default { get; set; }
    double value;
    public double Value { get => value; set { this.value = Math.Clamp(value, Min, Max); InvalidateVisual(); } }
    public event Action<double>? Changed;
    public VFader() { Width = 44; Height = 150; Cursor = Cursors.Hand; }

    void SetFromY(double y)
    {
        double v = Math.Round(Min + Math.Clamp(1 - (y - 8) / (ActualHeight - 16), 0, 1) * (Max - Min));
        if (v != value) { value = v; InvalidateVisual(); Changed?.Invoke(v); }
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { value = Default; InvalidateVisual(); Changed?.Invoke(value); return; }
        CaptureMouse(); SetFromY(e.GetPosition(this).Y);
    }
    protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) SetFromY(e.GetPosition(this).Y); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

    protected override void OnRender(DrawingContext dc)
    {
        var P = Theme.P; double W = ActualWidth, H = ActualHeight, cx = W / 2;
        if (W < 10 || H < 20) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));
        double y0 = 8, y1 = H - 8, y = y1 - (value - Min) / (Max - Min) * (y1 - y0);
        dc.DrawRoundedRectangle(P.B(P.Lane), null, new Rect(cx - 2, y0, 4, y1 - y0), 2, 2);
        dc.DrawRoundedRectangle(P.B(P.Green), null, new Rect(cx - 2, y, 4, y1 - y), 2, 2);
        dc.DrawRoundedRectangle(P.B(P.Text), null, new Rect(cx - 18, y - 8, 36, 16), 5, 5);
    }
}

/// <summary>Waveform overview of a take (backing lane on top when present). Click/drag to seek.</summary>
public sealed class WaveView : Painted
{
    public TakeInfo? Take;
    public double OffsetSec, Pos;
    public event Action<double>? Seek;    // fraction 0..1
    public WaveView() { Cursor = Cursors.Hand; }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { CaptureMouse(); Fire(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) Fire(e); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();
    void Fire(MouseEventArgs e) => Seek?.Invoke(Math.Clamp(e.GetPosition(this).X / ActualWidth, 0, 1));

    protected override void OnRender(DrawingContext dc)
    {
        var P = Theme.P; double W = ActualWidth, H = ActualHeight;
        if (W < 10 || H < 10) return;
        dc.PushClip(Draw.Rounded(new Rect(0, 0, W, H), 10));
        dc.DrawRectangle(P.B(P.Muted), null, new Rect(0, 0, W, H));
        var t = Take;
        if (t == null || t.Duration <= 0) { dc.Pop(); return; }
        int lanes = t.HasBacking ? 2 : 1; double laneH = H / lanes;
        int cols = Math.Max(1, (int)W);
        void Peaks(float[] peaks, double shift, double top, Color c)
        {
            if (peaks.Length == 0) return;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                double mid = top + laneH / 2, amp = laneH / 2 - 3;
                for (int px = 0; px < cols; px++)
                {
                    double t0 = (double)px / cols * t.Duration + shift, t1 = (double)(px + 1) / cols * t.Duration + shift;
                    int i0 = Math.Max(0, (int)Math.Floor(t0 / t.BucketSec)), i1 = Math.Min(peaks.Length, (int)Math.Ceiling(t1 / t.BucketSec));
                    float pk = 0; for (int i = i0; i < i1; i++) if (peaks[i] > pk) pk = peaks[i];
                    double h = Math.Max(pk > 0 ? 0.5 : 0, Math.Sqrt(pk) * amp);   // sqrt: makes quiet singing visible
                    if (h <= 0) continue;
                    double x = px * W / cols;
                    g.BeginFigure(new Point(x, mid - h), true, true);
                    g.LineTo(new Point(x + W / cols, mid - h), false, false);
                    g.LineTo(new Point(x + W / cols, mid + h), false, false);
                    g.LineTo(new Point(x, mid + h), false, false);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(P.B(c), null, geo);
        }
        if (t.HasBacking)
        {
            Peaks(t.BackPeaks, OffsetSec, 0, P.Faint);
            dc.DrawRectangle(P.B(P.Lane), null, new Rect(0, laneH - 0.5, W, 1));
        }
        Peaks(t.VoicePeaks, 0, (lanes - 1) * laneH, P.Text);
        var sub = P.B(P.Sub);
        if (t.HasBacking) dc.DrawText(Draw.Text("backing", 10, sub, this), new Point(6, 4));
        dc.DrawText(Draw.Text("voice", 10, sub, this), new Point(6, (lanes - 1) * laneH + 4));
        dc.DrawRectangle(P.B(P.Red), null, new Rect(Math.Round(Pos / t.Duration * W) - 1, 0, 2, H));
        dc.Pop();
    }
}

/// <summary>Two-octave (+1 key) piano with computer-keyboard hints. Raises <see cref="Struck"/> on click.</summary>
public sealed class PianoKeyboard : Painted
{
    int baseOct = 3;
    public int BaseOct { get => baseOct; set { baseOct = value; InvalidateVisual(); } }

    /// <summary>Computer-key label for a key, by semitone above the lowest C shown (0..24).</summary>
    public Func<int, string>? LabelFor;

    int? selected;
    /// <summary>Key (semitone above the lowest C) drawn with a highlight ring, e.g. while assigning it a computer key.</summary>
    public int? SelectedOffset { get => selected; set { selected = value; InvalidateVisual(); } }
    public event Action<int>? Struck;
    readonly Dictionary<int, DateTime> lit = new();
    readonly System.Windows.Threading.DispatcherTimer fade = new() { Interval = TimeSpan.FromMilliseconds(60) };

    public PianoKeyboard()
    {
        Cursor = Cursors.Hand;
        fade.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;
            foreach (var k in lit.Where(kv => (now - kv.Value).TotalMilliseconds > 280).Select(kv => kv.Key).ToList()) lit.Remove(k);
            if (lit.Count == 0) fade.Stop();
            InvalidateVisual();
        };
    }

    public void Light(int midi) { lit[midi] = DateTime.UtcNow; fade.Start(); InvalidateVisual(); }

    static readonly int[] WhiteSemis = [0, 2, 4, 5, 7, 9, 11];
    static readonly (int i, int semi)[] Blacks = [(0, 1), (1, 3), (3, 6), (4, 8), (5, 10)];

    IEnumerable<(int midi, Rect r, bool black)> Keys()
    {
        double W = ActualWidth, H = ActualHeight, w = W / 15;
        int k = 0;
        for (int o = 0; o < 2; o++) foreach (var s in WhiteSemis) yield return (12 * (baseOct + 1 + o) + s, new Rect(k++ * w, 0, w, H), false);
        yield return (12 * (baseOct + 3), new Rect(k * w, 0, w, H), false);
        for (int o = 0; o < 2; o++)
            foreach (var (i, semi) in Blacks)
            {
                double bw = w * 0.6;
                yield return (12 * (baseOct + 1 + o) + semi, new Rect((o * 7 + i + 1) * w - bw / 2, 0, bw, H * 0.58), true);
            }
    }

    string KbFor(int midi) { int off = midi - 12 * (baseOct + 1); return LabelFor?.Invoke(off) ?? ""; }

    // label shrunk to fit narrow keys ("Num 1", "F10"…)
    FormattedText KbText(string s, double size, Brush b, double maxW)
    {
        var t = Draw.Text(s, size, b, this);
        while (t.Width > maxW && size > 7) { size -= 1; t = Draw.Text(s, size, b, this); }
        return t;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        var keys = Keys().ToList();
        var hit = keys.Where(k => k.black).FirstOrDefault(k => k.r.Contains(p));
        if (hit.r.IsEmpty || !hit.black) hit = keys.Where(k => !k.black).FirstOrDefault(k => k.r.Contains(p));
        if (!hit.r.IsEmpty) Struck?.Invoke(hit.midi);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var P = Theme.P; bool dark = P.IsDark;
        var white = P.B(dark ? Color.FromRgb(0xd9, 0xd6, 0xcf) : Colors.White);
        var whiteOn = P.B(dark ? Color.FromRgb(0x9c, 0xd0, 0xae) : Color.FromRgb(0xdc, 0xed, 0xe3));
        var edge = new Pen(P.B(dark ? Color.FromRgb(0x16, 0x17, 0x1a) : Color.FromRgb(0xde, 0xdb, 0xd4)), 1);
        var black = P.B(dark ? Color.FromRgb(0x0b, 0x0b, 0x0d) : Color.FromRgb(0x26, 0x25, 0x1f));
        var kbW = P.B(dark ? Color.FromRgb(0x6b, 0x68, 0x62) : Color.FromRgb(0xb3, 0xb0, 0xa8));
        var nmW = P.B(dark ? Color.FromRgb(0x3a, 0x38, 0x34) : Color.FromRgb(0x8d, 0x8a, 0x83));
        var kbB = P.B(Color.FromRgb(0x9a, 0x96, 0x8e));
        if (dark) dc.DrawRoundedRectangle(P.B(Color.FromRgb(0x16, 0x17, 0x1a)), null, new Rect(-6, -6, ActualWidth + 12, ActualHeight + 12), 8, 8);
        foreach (var (midi, r, isBlack) in Keys())
        {
            if (!isBlack)
            {
                dc.DrawGeometry(lit.ContainsKey(midi) ? whiteOn : white, edge, Draw.Rounded(r, 5));
                var nm = Draw.Text(Draw.NoteName(midi) + Draw.Octave(midi), 11, nmW, this, false);
                dc.DrawText(nm, new Point(r.X + (r.Width - nm.Width) / 2, r.Bottom - 12 - nm.Height));
                var kb = KbText(KbFor(midi), 11, kbW, r.Width - 4);
                dc.DrawText(kb, new Point(r.X + (r.Width - kb.Width) / 2, r.Bottom - 16 - nm.Height - kb.Height));
            }
        }
        foreach (var (midi, r, isBlack) in Keys())
        {
            if (!isBlack) continue;
            dc.DrawRoundedRectangle(lit.ContainsKey(midi) ? P.B(P.Green) : black, null, r, 4, 4);
            var kb = KbText(KbFor(midi), 10, kbB, r.Width - 2);
            dc.DrawText(kb, new Point(r.X + (r.Width - kb.Width) / 2, r.Bottom - 8 - kb.Height));
        }
        if (selected is int sel)
            foreach (var (midi, r, _) in Keys())
                if (midi - 12 * (baseOct + 1) == sel)
                    dc.DrawRoundedRectangle(P.B(P.Green, 40), new Pen(P.B(P.Green), 2.5), new Rect(r.X + 1.5, r.Y + 1.5, Math.Max(0, r.Width - 3), Math.Max(0, r.Height - 3)), 5, 5);
    }
}

/// <summary>Themed drop-down list (the stock ComboBox can't follow the dark theme without a large template).</summary>
public sealed class Dropdown : Button
{
    readonly TextBlock label = new() { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly Popup popup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, VerticalOffset = 4 };
    readonly StackPanel list = new();
    List<(string value, string text)> items = new();
    public string? SelectedValue { get; private set; }
    public event Action<string>? SelectionChanged;

    public Dropdown()
    {
        Style = (Style)Application.Current.Resources["Secondary"];
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Padding = new Thickness(12, 7, 10, 7);
        var chevron = new Path
        {
            Data = Geometry.Parse("M0,0 L4.5,4.5 L9,0"), StrokeThickness = 1.6, Width = 10, Height = 6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 1, 0, 0),
        };
        chevron.SetResourceReference(Shape.StrokeProperty, "Sub");
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        label.FontWeight = FontWeights.Normal;
        var grid = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(chevron, Dock.Right);
        grid.Children.Add(chevron); grid.Children.Add(label);
        Content = grid;
        // the template centres content; make it stretch
        Loaded += (_, _) => { if (VisualTreeHelper.GetParent(grid) is ContentPresenter cp) cp.HorizontalAlignment = HorizontalAlignment.Stretch; };

        var border = new Border
        {
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(4),
            Child = new ScrollViewer { Content = list, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.25 },
            Margin = new Thickness(0, 0, 8, 10),
        };
        border.SetResourceReference(Border.BackgroundProperty, "Surface");
        border.SetResourceReference(TextElement.ForegroundProperty, "Text");
        border.SetResourceReference(Border.BorderBrushProperty, "Border2");
        popup.Child = border;
        popup.PlacementTarget = this;
        Click += (_, _) =>
        {
            border.MinWidth = ActualWidth;
            popup.IsOpen = !popup.IsOpen;
        };
    }

    public void SetItems(IEnumerable<(string value, string text)> its, string? selected)
    {
        items = its.ToList();
        list.Children.Clear();
        foreach (var (v, t) in items)
        {
            var row = new Button { Style = (Style)Application.Current.Resources["MenuRow"], Content = new TextBlock { Text = t, FontSize = 13 } };
            string val = v;
            row.Click += (_, _) => { popup.IsOpen = false; Select(val, true); };
            list.Children.Add(row);
        }
        Select(selected, false);
    }

    public void Select(string? value, bool fire)
    {
        var it = items.FirstOrDefault(i => i.value == value);
        if (it.value == null && items.Count > 0) it = items[0];
        SelectedValue = it.value;
        label.Text = it.text ?? "";
        foreach (Button b in list.Children)
            ((TextBlock)b.Content).FontWeight = ((TextBlock)b.Content).Text == it.text ? FontWeights.SemiBold : FontWeights.Normal;
        if (fire && it.value != null) SelectionChanged?.Invoke(it.value);
    }
}
