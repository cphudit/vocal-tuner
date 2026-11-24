using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace VocalTuner;

/// <summary>Colour palette. XAML uses the brushes through DynamicResource keys; custom-drawn controls read <see cref="Theme.P"/>.</summary>
public sealed class Palette
{
    public required Color Bg, Surface, Muted, Lane, Border, Border2, Text, Sub, Faint, Off,
        Green, Amber, Red, Err, RollNatural, RollSharp, Shadow;
    public required bool IsDark;

    readonly Dictionary<(Color, byte), SolidColorBrush> cache = new();
    /// <summary>Frozen brush for a colour, optionally with alpha.</summary>
    public SolidColorBrush B(Color c, byte alpha = 255)
    {
        if (!cache.TryGetValue((c, alpha), out var b))
        {
            b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            cache[(c, alpha)] = b;
        }
        return b;
    }
}

public static class Theme
{
    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static readonly Palette Light = new()
    {
        IsDark = false,
        Bg = C("#faf9f7"), Surface = C("#ffffff"), Muted = C("#f1efeb"), Lane = C("#e2dfd8"),
        Border = C("#e5e2db"), Border2 = C("#dedbd4"), Text = C("#1f1e1c"), Sub = C("#8d8a83"),
        Faint = C("#b3b0a8"), Off = C("#d6d3cc"), Green = C("#58a075"), Amber = C("#c9a23f"),
        Red = C("#c25b4e"), Err = C("#b0574a"), RollNatural = C("#f4f2ee"), RollSharp = C("#e9e6df"),
        Shadow = C("#000000"),
    };

    public static readonly Palette Dark = new()
    {
        IsDark = true,
        Bg = C("#0b0b0c"), Surface = C("#141415"), Muted = C("#1b1b1d"), Lane = C("#2e2e31"),
        Border = C("#262628"), Border2 = C("#333336"), Text = C("#ecebe8"), Sub = C("#9a9893"),
        Faint = C("#68665f"), Off = C("#3b3a37"), Green = C("#5db07f"), Amber = C("#d3ae4e"),
        Red = C("#d8695b"), Err = C("#e07b6c"), RollNatural = C("#1c1c1e"), RollSharp = C("#121213"),
        Shadow = C("#000000"),
    };

    public static Palette P { get; private set; } = Dark;
    public static event Action? Changed;

    public static readonly FontFamily Sans = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Mono = new("Consolas");

    public static void Apply(bool dark)
    {
        P = dark ? Dark : Light;
        var r = Application.Current.Resources;
        void Set(string key, Color c) => r[key] = P.B(c);
        Set("Bg", P.Bg); Set("Surface", P.Surface); Set("Muted", P.Muted); Set("Lane", P.Lane);
        Set("BorderB", P.Border); Set("Border2", P.Border2); Set("Text", P.Text); Set("Sub", P.Sub);
        Set("Faint", P.Faint); Set("Off", P.Off); Set("Green", P.Green); Set("Amber", P.Amber);
        Set("Red", P.Red); Set("Err", P.Err);
        r["Hover"] = P.B(P.Text, (byte)(dark ? 18 : 12));
        foreach (Window w in Application.Current.Windows) SetDarkTitleBar(w, dark);
        Changed?.Invoke();
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark (or light) native title bar to match the theme (Windows 10 2004+ / 11).</summary>
    public static void SetDarkTitleBar(Window w, bool dark)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int v = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
        int caption = dark ? 0x000c0b0b : 0x00f7f9fa;          // COLORREF 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));  // DWMWA_CAPTION_COLOR (Win11)
    }

    /// <summary>Hooks a window so its title bar follows the theme from the moment it has a handle.</summary>
    public static void Track(Window w) => w.SourceInitialized += (_, _) => SetDarkTitleBar(w, P.IsDark);
}
