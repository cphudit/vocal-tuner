using System.IO;
using System.Text.Json;

namespace VocalTuner.Coach;

/// <summary>
/// Every threshold the singing analysis uses, in one place. Defaults live here; to tune them without
/// rebuilding, copy the values into %LOCALAPPDATA%\VocalTuner\coach-config.json (only the fields you
/// want to change are needed).
/// </summary>
public sealed class CoachConfig
{
    // ---- pre-processing / pitch tracking
    public double Clarity = 0.80;            // NSDF peak needed to call a frame voiced (≈ pyin voiced prob > 0.5)
    public double PhraseGapSec = 0.30;        // gaps longer than this separate phrases
    public double MinRunSec = 0.04;           // voiced blips shorter than this are dropped

    // ---- note segmentation
    public double SplitSemitones = 0.5;       // a note ends when the (vibrato-smoothed) pitch moves this far
    public double MinNoteSec = 0.15;
    public double SteadyFrom = 0.30, SteadyTo = 0.85;   // part of a note used for its pitch

    // ---- intonation (cents)
    public double InTune = 20, Close = 35;
    public double TendencyCents = 10;         // mean signed offset that counts as a sharp/flat tendency
    public double ZoneProblemCents = 25;      // mean |offset| in a pitch zone that counts as a problem zone

    // ---- stability, onsets, endings
    public double WobbleCents = 20;           // std of the slow (vibrato-free) pitch in the steady part
    public double OnsetSec = 0.08;
    public double ScoopCents = 40;
    public double LongNoteSec = 0.5;
    public double EndSec = 0.15;
    public double EndDropCents = 50;
    public double EndUnstableCents = 30;

    // ---- vibrato
    public double VibratoMinNoteSec = 0.6;
    public double VibratoMinHz = 3.5, VibratoMaxHz = 8.0;
    public double VibratoMinExtent = 15;      // cents (± amplitude) to count as vibrato
    public double VibratoFastHz = 7.5;
    public double VibratoWideCents = 70;

    // ---- voice quality / registers
    public double BreathyHnrDrop = 3.0;       // dB below the singer's own median
    public double BreathyCppDrop = 1.5;       // dB below the singer's own median
    public double PushLoudDb = 3.0;           // zone louder than the rest by this much
    public double JitterRough = 1.2;          // % (ppq5)
    public double ShimmerRough = 6.0;         // % (apq5)
    public double BreakSemitones = 2.5;       // frame-to-frame jump that counts as a crack / flip

    // ---- dynamics
    public double FlatRangeDb = 6.0;          // p10–p90 of voiced loudness below this = flat
    public double BuildDb = 3.0;

    // ---- music in the recording
    public double MusicGapDb = 22;            // between-phrase level within this many dB of the voice = something is playing

    // ---- feedback
    public int MaxWeaknesses = 5;
    public double FatigueMinSec = 90;         // compare early vs late only for takes at least this long

    static readonly JsonSerializerOptions Json = new() { IncludeFields = true, WriteIndented = true };

    public static string FilePath => Path.Combine(AudioEngine.DataDir, "coach-config.json");

    public static CoachConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<CoachConfig>(File.ReadAllText(FilePath), Json) ?? new CoachConfig();
        }
        catch { /* a broken file falls back to the defaults */ }
        return new CoachConfig();
    }
}

/// <summary>Pitch zones depend on the voice type; the tenor boundaries are shifted for the other voices.</summary>
public static class VoiceTypes
{
    public static readonly (string id, string en, string th, int shift)[] All =
    [
        ("bass", "Bass", "เบส", -5),
        ("baritone", "Baritone", "บาริโทน", -3),
        ("tenor", "Tenor", "เทเนอร์", 0),
        ("alto", "Alto / mezzo", "อัลโต / เมซโซ", 7),
        ("soprano", "Soprano", "โซปราโน", 12),
    ];

    /// <summary>Tenor zones: below A3 | A3–D#4 | E4–F#4 | G4 and up.</summary>
    static readonly int[] TenorEdges = [57, 64, 67];

    public static int[] Edges(string voice)
    {
        int s = All.FirstOrDefault(v => v.id == voice).shift;
        return TenorEdges.Select(e => e + s).ToArray();
    }

    public static int ZoneOf(double midi, int[] edges)
    {
        int z = 0;
        while (z < edges.Length && midi >= edges[z] - 0.5) z++;
        return z;
    }
}
