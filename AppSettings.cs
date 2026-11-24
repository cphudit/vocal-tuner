using System.IO;
using System.Text.Json;

namespace VocalTuner;

/// <summary>Everything the app remembers between runs, stored as JSON in %LOCALAPPDATA%\VocalTuner\settings.json.</summary>
public sealed class AppSettings
{
    public string Page { get; set; } = "tuner";
    public bool Dark { get; set; } = true;

    // pitch detection
    public int Sens { get; set; } = 40;
    public int Noise { get; set; } = 55;
    public int Smooth { get; set; } = 70;
    public int MinBar { get; set; } = 100;

    public int PianoVol { get; set; } = 70;

    // recording setup
    public string RecMode { get; set; } = "file";          // none | file | system
    public string MicId { get; set; } = "";
    public string OutId { get; set; } = "";                // sound output; "" = follow the Windows default
    public int Version { get; set; }                         // settings format; see Load()
    public BackingFileInfo? BackFile { get; set; }
    public string StartAt { get; set; } = "0:00";
    public int BackVol { get; set; } = 100;

    // one key that starts and stops recording; global = also works while another app is in front
    public string RecShortcut { get; set; } = "F9";
    public bool RecShortcutGlobal { get; set; } = true;

    // latency calibration and remembered sync corrections, per "micId|outId" pair
    public Dictionary<string, CalResult> Cal { get; set; } = new();
    public Dictionary<string, double> Sync { get; set; } = new();

    // export
    public string ExportFormat { get; set; } = "wav24";
    public bool Normalize { get; set; } = true;
    public bool AlsoVoice { get; set; }
    public string LastDir { get; set; } = "";

    static string FilePath => Path.Combine(AudioEngine.DataDir, "settings.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Opts) ?? new AppSettings();
                // v2: older versions pinned whatever output was the Windows default at first run; follow Windows again
                if (s.Version < 2) { s.OutId = ""; s.Version = 2; }
                return s;
            }
        }
        catch { /* corrupt file: start fresh */ }
        return new AppSettings { Version = 2 };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AudioEngine.DataDir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }

    // detection parameters derived from the faders (same mapping as the original tuner)
    public double RmsThreshold => 0.05 * Math.Pow(0.08, Sens / 100.0);   // higher sensitivity = lower threshold
    public double Clarity => 0.5 + Noise / 100.0 * 0.4;                  // higher = stricter pitch filter
    public double Alpha => 0.9 - Smooth / 100.0 * 0.8;                   // higher smoothing = slower line
}
