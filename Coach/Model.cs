namespace VocalTuner.Coach;

/// <summary>Everything one analysis produced. Saved as JSON (history) and rendered as the report.</summary>
public sealed class CoachResult
{
    public int Format = 1;
    public string Id = "";                  // file name stem in the history folder
    public DateTime When;
    public string Source = "";              // file name, or the take's description
    public string Kind = "file";            // "take" | "file"
    public string Voice = "tenor";
    public double Duration;

    public double Hop;                      // seconds per pitch frame
    public int[] PitchCenti = [];           // MIDI × 100 per frame, 0 = no pitch (for the graph)
    public double LoudHop = 0.5;
    public float[] Loud = [];               // voiced loudness per LoudHop window (dB, relative); -200 = silent

    public List<Note> Notes = new();
    public List<Phrase> Phrases = new();
    public List<ZoneStat> Zones = new();
    public int[] ZoneEdges = [];
    public Metrics M = new();
    public KeyInfo Key = new();
    public MusicInfo Music = new();
    public List<Break> Breaks = new();

    public List<Finding> Strengths = new();
    public List<Finding> Weaknesses = new();
    public List<string> Caveats = new();    // ids of situation-specific caveats
}

public sealed class Note
{
    public double T0, T1, CoreT0, CoreT1;   // seconds; core = the settled part
    public double Pitch;                    // measured MIDI (median of the steady part, before the tuning reference)
    public int Target;                      // nearest semitone (after the tuning reference)
    public double Cents;                    // offset from Target
    public int Zone;
    public bool AfterGap, BeforeGap;        // starts a phrase / ends before a breath
    public double StdSlow, StdRaw;          // cents, steady part (slow = vibrato removed)
    public double? Onset;                   // cents of the first 80 ms vs the steady pitch (notes after a gap)
    public double? End, EndStd;             // cents of the last 150 ms vs the steady pitch (long notes before a gap)
    public bool Vibrato;
    public double? VibRate, VibExtent;
    public double Db, Centroid, Hnr, Cpp;
    public double? Jitter, Shimmer;
    public string Flags = "";               // S scoop · A from above · W wobble · D falling end · U unsteady end · K outside the key
    public double Dur => T1 - T0;
}

public sealed class Phrase { public double T0, T1, Db; }

public sealed class Break { public double T; public double From, To; }

public sealed class ZoneStat
{
    public int Zone;
    public int Lo, Hi;                      // MIDI range label (inclusive); Lo = int.MinValue / Hi = int.MaxValue at the ends
    public int Notes;
    public double Sec;
    public double InTunePct = double.NaN, MeanSigned = double.NaN, MeanAbs = double.NaN;
    public double Db = double.NaN, Centroid = double.NaN, Hnr = double.NaN, Cpp = double.NaN;
    public double Jitter = double.NaN, Shimmer = double.NaN;
}

public sealed class Metrics
{
    public int NoteCount;
    public double VoicedSec;
    public double RefCents;                 // tuning reference used (0 = A440)
    public string RefFrom = "a440";         // a440 | backing | recording
    public double InTunePct, ClosePct, MeanSigned, MeanAbs, MedianSigned;
    public double SharpShare, FlatShare;    // share of notes above / below the target
    public double WobblyPct, MedianSlowStd;
    public int OnsetNotes; public double ScoopPct, AbovePct, MedianOnset;
    public int EndNotes; public double EndDropPct, EndUnstablePct;
    public double EndDropEarlyPct = double.NaN, EndDropLatePct = double.NaN;
    public int LongNotes; public double VibratoPct, VibRate = double.NaN, VibExtent = double.NaN;
    public double DynRangeDb, PhraseSpreadDb, LateMinusEarlyDb; public string DynShape = "";
    public double RangeLo, RangeHi, TessLo, TessHi;
    public double Hnr, Cpp, Jitter = double.NaN, Shimmer = double.NaN, Centroid;
    public double PassaggioLo = double.NaN, PassaggioHi = double.NaN;
    public string PassaggioWhy = "";         // quality | breaks | both
}

public sealed class KeyInfo
{
    public int Tonic = -1; public bool Minor; public double Conf;
    public List<OutNote> OutOfKey = new();
    public int BackTonic = -1; public bool BackMinor; public double BackConf;
    public string BackFrom = "";            // track | recording
    public int? Shift;                      // vocal pitch classes = backing + Shift (only when clearly not 0)
    public double ShiftCorr, ZeroCorr;
}

public sealed class OutNote { public int Pc; public int Count; public List<double> Times = new(); }

public sealed class MusicInfo
{
    public bool Detected;
    public double GapDb = double.NaN;       // voice level minus between-phrase level
    public double Flatness = double.NaN;
    public double LowRatio;
    public bool HadBacking;                 // a separate backing track was provided
}

/// <summary>A strength or weakness. Text is generated from Id + values at display time, so the language can switch.</summary>
public sealed class Finding
{
    public string Id = "";
    public double Impact;
    public Dictionary<string, double> V = new();
    public List<double> Times = new();
    public int Zone = -1;
}
