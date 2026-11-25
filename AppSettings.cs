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

    // piano keyboard mapping: semitone above the lowest C on screen (0..24) -> WPF key name ("A", "OemSemicolon", "NumPad1"…)
    public Dictionary<int, string> PianoKeys { get; set; } = DefaultPianoKeys();
    public string OctDownKey { get; set; } = "Z";
    public string OctUpKey { get; set; } = "X";

    // warm-up exercise
    public string WarmEx { get; set; } = "five";            // five = 1-2-3-4-5-4-3-2-1 | arp = 1-3-5-8-5-3-1
    public int WarmStart { get; set; } = 48;                 // lowest starting note (MIDI, C3)
    public int WarmTop { get; set; } = 57;                   // highest starting note (A3)
    public int WarmBpm { get; set; } = 100;
    public bool WarmDown { get; set; } = true;               // come back down after reaching the top
    public bool WarmMelody { get; set; } = true;             // piano plays the notes too (off = starting chord only)

    public static Dictionary<int, string> DefaultPianoKeys() => new()
    {
        [0] = "A", [1] = "W", [2] = "S", [3] = "E", [4] = "D", [5] = "F", [6] = "T", [7] = "G", [8] = "Y",
        [9] = "H", [10] = "U", [11] = "J", [12] = "K", [13] = "O", [14] = "L", [15] = "P", [16] = "OemSemicolon",
    };

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

    // singing analysis (Coach page)
    public string CoachVoice { get; set; } = "tenor";
    public string CoachLang { get; set; } = "en";             // report + Coach page language: en (default) | th
    public string CoachBacking { get; set; } = "";          // optional backing track for analysing outside recordings

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
                if (s.Version < 3) { s.CoachLang = "en"; s.Version = 3; }   // English became the default report language
                return s;
            }
        }
        catch { /* corrupt file: start fresh */ }
        return new AppSettings { Version = 3 };
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
