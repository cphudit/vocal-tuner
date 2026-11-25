using System.Globalization;

namespace VocalTuner.Coach;

/// <summary>
/// Turns the measurements into strengths, ranked weaknesses, a practice plan and caveats.
/// Findings are stored as ids + numbers; all wording (Thai / English) is produced here at display time.
/// </summary>
public static class Feedback
{
    // ================================================================== rules

    public static void Build(CoachResult r, CoachConfig c)
    {
        r.Strengths.Clear(); r.Weaknesses.Clear(); r.Caveats.Clear();
        var m = r.M;
        var w = new List<Finding>();
        var s = new List<Finding>();
        Finding F(string id, double impact) => new() { Id = id, Impact = Math.Round(impact, 3) };
        List<double> Times(IEnumerable<Note> ns, Func<Note, double> worst, int max = 6) =>
            ns.OrderByDescending(worst).Take(max).Select(n => Math.Round(n.T0, 2)).OrderBy(t => t).ToList();

        // whole vocal shifted against the music: the most important thing to fix
        if (r.Key.Shift is int shift)
        {
            var f = F("shift", 0.97); f.V["shift"] = shift; f.V["corr"] = r.Key.ShiftCorr;
            w.Add(f);
        }

        // sharp / flat tendency
        bool tendency = false;
        if (Math.Abs(m.MeanSigned) >= c.TendencyCents && Math.Max(m.SharpShare, m.FlatShare) >= 60)
        {
            bool sharp = m.MeanSigned > 0;
            var f = F(sharp ? "sharp" : "flat", Math.Clamp(Math.Abs(m.MeanSigned) / 30, 0.25, 0.9));
            f.V["mean"] = m.MeanSigned; f.V["share"] = sharp ? m.SharpShare : m.FlatShare;
            f.Times = Times(r.Notes.Where(n => sharp ? n.Cents > c.InTune : n.Cents < -c.InTune), n => Math.Abs(n.Cents));
            w.Add(f); tendency = true;
        }

        // problem pitch zones (with what probably causes them)
        var zones = r.Zones.Where(z => z.Notes >= 5).ToList();
        double medHnr = Sig.Median(zones.Select(z => z.Hnr)), medCpp = Sig.Median(zones.Select(z => z.Cpp));
        var zoneFindings = new List<Finding>();
        foreach (var z in zones)
        {
            var others = zones.Where(o => o != z).ToList();
            double otherDb = others.Count > 0 ? others.Max(o => o.Db) : double.NaN;
            bool breathy = (!double.IsNaN(z.Hnr) && medHnr - z.Hnr >= c.BreathyHnrDrop) || (!double.IsNaN(z.Cpp) && medCpp - z.Cpp >= c.BreathyCppDrop);
            bool quieter = others.Count > 0 && z.Db <= others.Average(o => o.Db) - 4;
            bool loudest = others.Count > 0 && z.Db >= otherDb + c.PushLoudDb * 0.5 && z.Db >= others.Average(o => o.Db) + c.PushLoudDb;
            bool pitchProblem = z.MeanAbs >= c.ZoneProblemCents || z.InTunePct <= m.InTunePct - 20;
            string? id = null;
            if (pitchProblem && z.MeanSigned >= 12 && loudest) id = "zone_push";
            else if (pitchProblem && z.MeanSigned <= -12 && (breathy || quieter)) id = "zone_mix";
            else if (pitchProblem) id = "zone_pitch";
            else if (breathy && (quieter || z.Zone >= 2)) id = "zone_breathy";
            if (id == null) continue;
            var f = F(id, id == "zone_breathy" ? 0.42 : Math.Clamp(z.MeanAbs / 40, 0.3, 0.92));
            f.Zone = z.Zone;
            f.V["abs"] = z.MeanAbs; f.V["signed"] = z.MeanSigned; f.V["intune"] = z.InTunePct; f.V["db"] = z.Db;
            f.V["hnr"] = z.Hnr - medHnr; f.V["cpp"] = z.Cpp - medCpp; f.V["n"] = z.Notes;
            f.Times = Times(r.Notes.Where(n => n.Zone == z.Zone), n => Math.Abs(n.Cents));
            zoneFindings.Add(f);
        }
        w.AddRange(zoneFindings.OrderByDescending(f => f.Impact).Take(2));

        // overall accuracy (only when nothing more specific explains it)
        if (m.InTunePct < 60 && !tendency && zoneFindings.Count(f => f.Id != "zone_breathy") == 0)
        {
            var f = F("pitch", Math.Clamp((75 - m.InTunePct) / 50, 0.25, 0.9));
            f.V["intune"] = m.InTunePct; f.V["close"] = m.ClosePct; f.V["abs"] = m.MeanAbs;
            f.Times = Times(r.Notes.Where(n => Math.Abs(n.Cents) > c.Close), n => Math.Abs(n.Cents));
            w.Add(f);
        }

        if (m.OnsetNotes >= 5 && m.ScoopPct >= 25)
        {
            var f = F("scoop", Math.Clamp(m.ScoopPct / 60, 0.3, 0.85)); f.V["pct"] = m.ScoopPct; f.V["median"] = m.MedianOnset;
            f.Times = Times(r.Notes.Where(n => n.Flags.Contains('S')), n => -(n.Onset ?? 0));
            w.Add(f);
        }
        else if (m.OnsetNotes >= 5 && m.AbovePct >= 25)
        {
            var f = F("above", Math.Clamp(m.AbovePct / 80, 0.2, 0.5)); f.V["pct"] = m.AbovePct;
            f.Times = Times(r.Notes.Where(n => n.Flags.Contains('A')), n => n.Onset ?? 0);
            w.Add(f);
        }

        if (r.Notes.Count(n => n.Dur >= 0.3) >= 5 && m.WobblyPct >= 20)
        {
            var f = F("wobble", Math.Clamp(m.WobblyPct / 50, 0.3, 0.8)); f.V["pct"] = m.WobblyPct; f.V["std"] = m.MedianSlowStd;
            f.Times = Times(r.Notes.Where(n => n.Flags.Contains('W')), n => n.StdSlow);
            w.Add(f);
        }

        if (m.EndNotes >= 4 && (m.EndDropPct >= 25 || m.EndUnstablePct >= 30))
        {
            bool fatigue = !double.IsNaN(m.EndDropLatePct) && m.EndDropLatePct - m.EndDropEarlyPct >= 20;
            var f = F(fatigue ? "endings_late" : "endings", Math.Clamp(Math.Max(m.EndDropPct, m.EndUnstablePct * 0.8) / 55, 0.3, 0.85));
            f.V["drop"] = m.EndDropPct; f.V["unstable"] = m.EndUnstablePct;
            if (fatigue) { f.V["early"] = m.EndDropEarlyPct; f.V["late"] = m.EndDropLatePct; }
            f.Times = Times(r.Notes.Where(n => n.Flags.Contains('D') || n.Flags.Contains('U')), n => -(n.End ?? 0));
            w.Add(f);
        }

        var vib = r.Notes.Where(n => n.Vibrato).ToList();
        if (vib.Count >= 3 && !double.IsNaN(m.VibExtent) && m.VibExtent > c.VibratoWideCents)
        {
            var f = F("vib_wide", 0.4); f.V["extent"] = m.VibExtent; f.V["rate"] = m.VibRate;
            f.Times = Times(vib, n => n.VibExtent ?? 0); w.Add(f);
        }
        else if (vib.Count >= 3 && !double.IsNaN(m.VibRate) && m.VibRate > c.VibratoFastHz)
        {
            var f = F("vib_fast", 0.38); f.V["rate"] = m.VibRate; f.V["extent"] = m.VibExtent;
            f.Times = Times(vib, n => n.VibRate ?? 0); w.Add(f);
        }

        if (r.Breaks.Count >= 2)
        {
            var f = F("breaks", Math.Clamp(r.Breaks.Count / 6.0, 0.35, 0.8)); f.V["n"] = r.Breaks.Count;
            f.V["at"] = Math.Round(Sig.Median(r.Breaks.Select(b => b.From)));
            f.Times = r.Breaks.Take(6).Select(b => b.T).ToList();
            w.Add(f);
        }

        foreach (var o in r.Key.OutOfKey.OrderByDescending(o => o.Count).Take(1))
        {
            var f = F("outkey", Math.Clamp(o.Count / (double)r.Notes.Count * 4, 0.3, 0.6));
            f.V["pc"] = o.Pc; f.V["n"] = o.Count; f.V["tonic"] = r.Key.Tonic; f.V["minor"] = r.Key.Minor ? 1 : 0;
            f.Times = o.Times.Take(6).ToList();
            w.Add(f);
        }

        if (m.DynShape == "flat" && r.Duration >= 45)
        {
            var f = F("dyn_flat", 0.3); f.V["range"] = m.DynRangeDb; w.Add(f);
        }

        bool strain = (!double.IsNaN(m.Jitter) && m.Jitter >= c.JitterRough) || (!double.IsNaN(m.Shimmer) && m.Shimmer >= c.ShimmerRough);
        if (strain)
        {
            var f = F("rough", 0.34); f.V["jitter"] = m.Jitter; f.V["shimmer"] = m.Shimmer; w.Add(f);
            r.Caveats.Add("strain");
        }

        if (m.LongNotes >= 4 && m.VibratoPct < 15)
        {
            var f = F("no_vibrato", 0.15); f.V["pct"] = m.VibratoPct; w.Add(f);
        }

        r.Weaknesses = w.Where(f => f.Impact >= 0.15).OrderByDescending(f => f.Impact).Take(c.MaxWeaknesses).ToList();
        // a style tip only fills a free slot, it never pushes out a real problem
        if (r.Weaknesses.Count > 3) r.Weaknesses.RemoveAll(f => f.Id == "no_vibrato");

        // ---------------------------------------------------------------- strengths
        Finding S(string id, double score) => new() { Id = id, Impact = Math.Round(score, 3) };
        if (m.InTunePct >= 60) { var f = S("s_pitch", m.InTunePct / 100); f.V["intune"] = m.InTunePct; f.V["close"] = m.ClosePct; s.Add(f); }
        else if (m.ClosePct >= 75) { var f = S("s_close", m.ClosePct / 150); f.V["close"] = m.ClosePct; s.Add(f); }
        var bestZone = zones.Where(z => z.InTunePct >= 60 && z.InTunePct >= m.InTunePct + 8).OrderByDescending(z => z.InTunePct).FirstOrDefault();
        if (bestZone != null) { var f = S("s_zone", 0.55 + (bestZone.InTunePct - m.InTunePct) / 100); f.Zone = bestZone.Zone; f.V["intune"] = bestZone.InTunePct; s.Add(f); }
        if (r.Notes.Count(n => n.Dur >= 0.3) >= 5 && m.WobblyPct <= 10) { var f = S("s_steady", 0.6 + (10 - m.WobblyPct) / 40); f.V["pct"] = m.WobblyPct; s.Add(f); }
        if (m.OnsetNotes >= 5 && m.ScoopPct <= 10 && m.AbovePct <= 15) { var f = S("s_onset", 0.58 + (10 - m.ScoopPct) / 40); f.V["pct"] = m.ScoopPct; s.Add(f); }
        if (m.EndNotes >= 4 && m.EndDropPct <= 10 && m.EndUnstablePct <= 20) { var f = S("s_endings", 0.57 + (10 - m.EndDropPct) / 40); f.V["pct"] = m.EndDropPct; s.Add(f); }
        if (vib.Count >= 3 && m.VibratoPct >= 30 && m.VibRate is >= 4.5 and <= 7 && m.VibExtent is >= 20 and <= 60)
        { var f = S("s_vibrato", 0.6); f.V["rate"] = m.VibRate; f.V["extent"] = m.VibExtent; f.V["pct"] = m.VibratoPct; s.Add(f); }
        if (m.DynShape is "builds" or "varied" && m.DynRangeDb >= 8 && r.Duration >= 45) { var f = S("s_dyn", 0.5); f.V["range"] = m.DynRangeDb; f.V["builds"] = m.DynShape == "builds" ? 1 : 0; s.Add(f); }
        if (m.RangeHi - m.RangeLo >= 15) { var f = S("s_range", 0.45); f.V["lo"] = m.RangeLo; f.V["hi"] = m.RangeHi; s.Add(f); }
        if (r.Key.BackTonic >= 0 && r.Key.Shift == null && r.Key.ZeroCorr >= 0.6) { var f = S("s_key", 0.62); s.Add(f); }
        if (r.Breaks.Count == 0 && r.Notes.Count(n => n.Zone >= 2) >= 5) { var f = S("s_nobreak", 0.48); s.Add(f); }
        if (s.Count == 0) { var f = S("s_done", 0.1); f.V["n"] = r.Notes.Count; f.V["lo"] = m.RangeLo; f.V["hi"] = m.RangeHi; s.Add(f); }
        r.Strengths = s.OrderByDescending(f => f.Impact).Take(4).ToList();

        // ---------------------------------------------------------------- caveats for this take
        if (r.Music.Detected && !r.Music.HadBacking) r.Caveats.Add("music");
        if (r.Music.Detected && r.Music.HadBacking) r.Caveats.Add("bleed");
        if (r.Duration < 45) r.Caveats.Add("short");
        if (r.Notes.Count < 20) r.Caveats.Add("fewnotes");
        if (m.RefFrom == "a440" && r.Key.BackTonic < 0) r.Caveats.Add("a440");
    }

    // ================================================================== text helpers

    public static string L(string lang, string th, string en) => lang == "th" ? th : en;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static string N0(double v) => double.IsNaN(v) ? "–" : Math.Round(v).ToString("0", Inv);
    public static string N1(double v) => double.IsNaN(v) ? "–" : v.ToString("0.0", Inv);
    public static string Signed(double v) => double.IsNaN(v) ? "–" : (Math.Round(v) > 0 ? "+" : Math.Round(v) < 0 ? "−" : "") + Math.Abs(Math.Round(v)).ToString("0", Inv);
    public static string Note(double midi) => double.IsNaN(midi) ? "–" : Draw.NoteName((int)Math.Round(midi)) + Draw.Octave((int)Math.Round(midi));
    public static string Pc(int pc) => Draw.Names[((pc % 12) + 12) % 12];
    public static string Time(double t) { int s = (int)Math.Floor(t); return $"{s / 60}:{s % 60:00}"; }
    public static string Times(IEnumerable<double> ts) => string.Join(", ", ts.Select(Time));

    /// <summary>Recorded takes are stored as "take:system" / "take:voice" (older analyses: English or Thai text).</summary>
    public static string SourceName(string src, string lang) => src switch
    {
        "take:system" or "Recorded take · system audio" or "เทคที่อัด · เสียงในเครื่อง" => L(lang, "เทคที่อัด · เสียงในเครื่อง", "Recorded take · system audio"),
        "take:voice" or "Recorded take · voice only" or "เทคที่อัด · เสียงร้องอย่างเดียว" => L(lang, "เทคที่อัด · เสียงร้องอย่างเดียว", "Recorded take · voice only"),
        _ => src,
    };

    public static string KeyName(int tonic, bool minor, string lang) =>
        tonic < 0 ? "–" : Pc(tonic) + (minor ? L(lang, " ไมเนอร์", " minor") : L(lang, " เมเจอร์", " major"));

    public static string ZoneName(CoachResult r, int zone, string lang)
    {
        var e = r.ZoneEdges;
        if (e.Length == 0) return "";
        if (zone <= 0) return L(lang, "ต่ำกว่า ", "below ") + Note(e[0]);
        if (zone >= e.Length) return Note(e[^1]) + L(lang, " ขึ้นไป", " and up");
        return Note(e[zone - 1]) + "–" + Note(e[zone] - 1);
    }

    public static string ZoneRole(int zone, string lang) => zone switch
    {
        0 => L(lang, "ช่วงต่ำ", "low"),
        1 => L(lang, "ช่วงกลาง", "middle"),
        2 => L(lang, "ช่วงรอยต่อเสียง", "passaggio area"),
        _ => L(lang, "ช่วงสูง", "high"),
    };

    static string ZoneFull(CoachResult r, int z, string lang) => ZoneName(r, z, lang) + " (" + ZoneRole(z, lang) + ")";

    public static string FriendlyError(Exception ex, string lang) => ex is CoachException ce ? ce.Code switch
    {
        "short" => L(lang, "ไฟล์สั้นเกินไป ต้องยาวอย่างน้อย 3 วินาที", "The recording is too short. It needs at least 3 seconds."),
        "nonotes" => L(lang, "ไม่เจอเสียงร้องที่จับโน้ตได้ ลองเช็กว่าไฟล์มีเสียงร้องชัด ๆ และไมค์ไม่ได้ปิดเสียงอยู่", "No sung notes were found. Check that the file has clear singing and that the mic wasn't muted."),
        _ => ce.Code,
    } : L(lang, "วิเคราะห์ไม่สำเร็จ: ", "Analysis failed: ") + ex.Message;

    // ================================================================== weaknesses

    public static string Title(CoachResult r, Finding f, string lang)
    {
        double V(string k) => f.V.TryGetValue(k, out var v) ? v : double.NaN;
        return f.Id switch
        {
            "shift" => V("shift") < 0
                ? L(lang, $"ทั้งเพลงร้องต่ำกว่าดนตรี {N0(-V("shift"))} ครึ่งเสียง", $"The whole vocal is {N0(-V("shift"))} semitone(s) below the music")
                : L(lang, $"ทั้งเพลงร้องสูงกว่าดนตรี {N0(V("shift"))} ครึ่งเสียง", $"The whole vocal is {N0(V("shift"))} semitone(s) above the music"),
            "sharp" => L(lang, "มักร้องเพี้ยนสูง (ชาร์ป)", "Tends to sing sharp"),
            "flat" => L(lang, "มักร้องเพี้ยนต่ำ (แฟลต)", "Tends to sing flat"),
            "zone_push" => L(lang, $"ช่วง {ZoneName(r, f.Zone, lang)} ร้องสูงเกินและดังที่สุด", $"Sharp and loudest at {ZoneName(r, f.Zone, lang)}"),
            "zone_mix" => L(lang, $"ช่วง {ZoneName(r, f.Zone, lang)} เสียงต่ำลงและบางลง", $"Flat and thinner at {ZoneName(r, f.Zone, lang)}"),
            "zone_pitch" => L(lang, $"ช่วง {ZoneName(r, f.Zone, lang)} เพี้ยนบ่อยกว่าช่วงอื่น", $"Pitch is less accurate at {ZoneName(r, f.Zone, lang)}"),
            "zone_breathy" => L(lang, $"ช่วง {ZoneName(r, f.Zone, lang)} เสียงมีลมปนมากขึ้น", $"The tone gets breathy at {ZoneName(r, f.Zone, lang)}"),
            "pitch" => L(lang, "ความตรงของโน้ตโดยรวมยังไม่นิ่ง", "Overall pitch accuracy"),
            "scoop" => L(lang, "ช้อนเสียงขึ้นตอนเริ่มโน้ต (scoop)", "Scooping up into notes"),
            "above" => L(lang, "เริ่มโน้ตสูงไปแล้วค่อยลง", "Starting notes above the pitch"),
            "wobble" => L(lang, "โน้ตยาวยังส่าย ไม่นิ่ง", "Held notes wander"),
            "endings" => L(lang, "ท้ายประโยคเสียงตก/ไม่นิ่ง", "Phrase endings fall or waver"),
            "endings_late" => L(lang, "ท้ายประโยคเสียงตก โดยเฉพาะช่วงท้ายเพลง", "Phrase endings fall, especially late in the song"),
            "vib_wide" => L(lang, "vibrato กว้างเกินไป", "Vibrato is very wide"),
            "vib_fast" => L(lang, "vibrato เร็วเกินไป", "Vibrato is fast"),
            "breaks" => L(lang, $"เสียงแตก/พลิกกลางโน้ต (แถว {Note(V("at"))})", $"Voice cracks or flips (around {Note(V("at"))})"),
            "outkey" => L(lang, $"โน้ต {Pc((int)V("pc"))} หลุดคีย์บ่อย", $"{Pc((int)V("pc"))} keeps falling outside the key"),
            "dyn_flat" => L(lang, "ความดังเท่า ๆ กันทั้งเพลง", "Loudness stays the same all the way through"),
            "rough" => L(lang, "เสียงมีความหยาบ/สั่นในระดับรอบคลื่นสูง", "Signs of a rough or strained tone"),
            "no_vibrato" => L(lang, "โน้ตยาวยังไม่มี vibrato (เป็นเรื่องสไตล์)", "No vibrato on long notes (a style choice)"),
            _ => f.Id,
        };
    }

    public static string Measured(CoachResult r, Finding f, string lang)
    {
        double V(string k) => f.V.TryGetValue(k, out var v) ? v : double.NaN;
        var m = r.M;
        return f.Id switch
        {
            "shift" => L(lang,
                $"โน้ตที่ร้องเข้ากับคอร์ดของดนตรีได้ดีที่สุดเมื่อเลื่อน {Signed(V("shift"))} ครึ่งเสียง (ความเข้ากัน {N0(V("corr") * 100)}% เทียบกับ {N0(r.Key.ZeroCorr * 100)}% ถ้าไม่เลื่อน) คีย์เสียงร้อง {KeyName(r.Key.Tonic, r.Key.Minor, lang)} · คีย์ดนตรี {KeyName(r.Key.BackTonic, r.Key.BackMinor, lang)}",
                $"Your notes match the music's chords best when shifted {Signed(V("shift"))} semitone(s) ({N0(V("corr") * 100)}% match vs {N0(r.Key.ZeroCorr * 100)}% unshifted). Vocal key {KeyName(r.Key.Tonic, r.Key.Minor, lang)} · music key {KeyName(r.Key.BackTonic, r.Key.BackMinor, lang)}."),
            "sharp" or "flat" => L(lang,
                $"เฉลี่ยทุกโน้ต {Signed(V("mean"))} เซนต์ และ {N0(V("share"))}% ของโน้ตอยู่{(f.Id == "sharp" ? "สูง" : "ต่ำ")}กว่าโน้ตที่ควรเป็น (100 เซนต์ = ครึ่งเสียง)",
                $"Average offset {Signed(V("mean"))} cents; {N0(V("share"))}% of notes sit {(f.Id == "sharp" ? "above" : "below")} the target (100 cents = one semitone)."),
            "zone_push" or "zone_mix" or "zone_pitch" => L(lang,
                $"{N0(V("n"))} โน้ตในช่วงนี้ คลาดเฉลี่ย {N0(V("abs"))} เซนต์ (ทิศทาง {Signed(V("signed"))}) ตรงคีย์ ±20 เซนต์แค่ {N0(V("intune"))}% เทียบกับ {N0(m.InTunePct)}% ทั้งเพลง · ความดัง {Signed(V("db"))} dB เทียบค่ากลางของคุณ",
                $"{N0(V("n"))} notes here miss by {N0(V("abs"))} cents on average (direction {Signed(V("signed"))}); only {N0(V("intune"))}% within ±20 cents vs {N0(m.InTunePct)}% overall · loudness {Signed(V("db"))} dB vs your typical level."),
            "zone_breathy" => L(lang,
                $"ความใสของเสียง (HNR) {Signed(V("hnr"))} dB และ CPP {Signed(V("cpp"))} dB เทียบกับช่วงอื่นของคุณ · ความดัง {Signed(V("db"))} dB",
                $"Clarity (HNR) {Signed(V("hnr"))} dB and CPP {Signed(V("cpp"))} dB compared with your other zones · loudness {Signed(V("db"))} dB."),
            "pitch" => L(lang,
                $"ตรงคีย์ (±20 เซนต์) {N0(V("intune"))}% ของโน้ต · ใกล้เคียง (±35) {N0(V("close"))}% · คลาดเฉลี่ย {N0(V("abs"))} เซนต์",
                $"{N0(V("intune"))}% of notes within ±20 cents · {N0(V("close"))}% within ±35 · average miss {N0(V("abs"))} cents."),
            "scoop" => L(lang,
                $"{N0(V("pct"))}% ของโน้ตต้นประโยคเริ่มต่ำกว่าโน้ตจริงเกิน 40 เซนต์ แล้วค่อยไถขึ้น",
                $"{N0(V("pct"))}% of phrase-start notes begin more than 40 cents below the note and slide up."),
            "above" => L(lang,
                $"{N0(V("pct"))}% ของโน้ตต้นประโยคเริ่มสูงกว่าโน้ตจริงเกิน 40 เซนต์",
                $"{N0(V("pct"))}% of phrase-start notes begin more than 40 cents above the note."),
            "wobble" => L(lang,
                $"{N0(V("pct"))}% ของโน้ตที่ยาวเกิน 0.3 วินาที ส่ายขึ้นลงช้า ๆ เกิน 20 เซนต์ (ไม่นับ vibrato)",
                $"{N0(V("pct"))}% of notes longer than 0.3 s drift by more than 20 cents (vibrato excluded)."),
            "endings" or "endings_late" => L(lang,
                $"โน้ตท้ายประโยคที่ยาว: ตกลงเกิน 50 เซนต์ {N0(V("drop"))}% · สั่นไม่นิ่ง {N0(V("unstable"))}%" + (f.Id == "endings_late" ? $" · ช่วงต้น-กลางเพลงตก {N0(V("early"))}% แต่ช่วงท้ายเพลงตก {N0(V("late"))}%" : ""),
                $"Long phrase-end notes: {N0(V("drop"))}% fall more than 50 cents, {N0(V("unstable"))}% waver." + (f.Id == "endings_late" ? $" Early/middle of the song {N0(V("early"))}%, last third {N0(V("late"))}%." : "")),
            "vib_wide" => L(lang,
                $"vibrato กว้าง ±{N0(V("extent"))} เซนต์ ที่ {N1(V("rate"))} ครั้ง/วินาที (ทั่วไปอยู่ราว ±20–60 เซนต์)",
                $"Vibrato swings ±{N0(V("extent"))} cents at {N1(V("rate"))} Hz (typical: about ±20–60 cents)."),
            "vib_fast" => L(lang,
                $"vibrato {N1(V("rate"))} ครั้ง/วินาที (ทั่วไปอยู่ราว 4.5–7) กว้าง ±{N0(V("extent"))} เซนต์",
                $"Vibrato at {N1(V("rate"))} Hz (typical: about 4.5–7 Hz), ±{N0(V("extent"))} cents."),
            "breaks" => L(lang,
                $"เจอ {N0(V("n"))} ครั้งที่เสียงกระโดดหลุดจากโน้ตที่ค้างอยู่แล้วกลับมาภายในไม่ถึงครึ่งวินาที",
                $"{N0(V("n"))} times the pitch jumped away from a held note and came back within half a second."),
            "outkey" => L(lang,
                $"คีย์ที่ประมาณจากเสียงร้องคือ {KeyName((int)V("tonic"), V("minor") > 0, lang)} แต่มี {Pc((int)V("pc"))} ที่ไม่อยู่ในคีย์ {N0(V("n"))} โน้ต",
                $"The estimated key of your singing is {KeyName((int)V("tonic"), V("minor") > 0, lang)}, but {Pc((int)V("pc"))}, which is outside it, appears {N0(V("n"))} times."),
            "dyn_flat" => L(lang,
                $"ความดังของเสียงร้องต่างกันแค่ {N1(V("range"))} dB (ช่วง 10–90%) และแต่ละประโยคดังพอ ๆ กัน",
                $"Your loudness varies by only {N1(V("range"))} dB (10th–90th percentile) and phrases are similarly loud."),
            "rough" => L(lang,
                $"jitter {N1(V("jitter"))}% · shimmer {N1(V("shimmer"))}% (ค่าประมาณจากการร้องเพลง ไม่ใช่การตรวจทางการแพทย์)",
                $"Jitter {N1(V("jitter"))}% · shimmer {N1(V("shimmer"))}% (estimates from singing, not a medical measurement)."),
            "no_vibrato" => L(lang,
                $"มี vibrato แค่ {N0(V("pct"))}% ของโน้ตที่ยาวเกิน 0.6 วินาที",
                $"Only {N0(V("pct"))}% of notes longer than 0.6 s have vibrato."),
            _ => "",
        };
    }

    public static string Where(CoachResult r, Finding f, string lang)
    {
        var parts = new List<string>();
        if (f.Zone >= 0) parts.Add(L(lang, "ช่วงเสียง ", "Pitch zone ") + ZoneFull(r, f.Zone, lang));
        if (f.Times.Count > 0) parts.Add(L(lang, "เช่นที่ ", "e.g. at ") + Times(f.Times));
        if (f.Id is "shift" or "sharp" or "flat" or "dyn_flat" or "rough" && f.Times.Count == 0) parts.Add(L(lang, "ทั้งเพลง", "the whole take"));
        return string.Join(" · ", parts);
    }

    public static string Cause(CoachResult r, Finding f, string lang) => f.Id switch
    {
        "shift" => L(lang, "น่าจะเริ่มผิดโน้ตตั้งแต่ต้น หรือคีย์ของคาราโอเกะถูกปรับไว้ แล้วร้องตามความจำของต้นฉบับ",
            "You probably started on the wrong note, or the karaoke key was changed and you sang it the way you remember the original."),
        "sharp" => L(lang, "มักเกิดจากดันลมแรงเกินไปหรือเกร็งเวลาตื่นเต้น โดยเฉพาะโน้ตสูง",
            "Usually too much breath pressure or tension, especially on higher notes and when excited."),
        "flat" => L(lang, "มักเกิดจากลมไม่พยุง เหนื่อย หรือได้ยินเสียงตัวเองไม่ชัด (เสียงในหูฟังเบาหรือดังเกินไป)",
            "Usually not enough breath support, tiredness, or not hearing yourself well (headphone mix too loud or too quiet)."),
        "zone_push" => L(lang, "ตรงช่วงรอยต่อเสียงมักดันลมและความดังขึ้นไปช่วย ทำให้เสียงชาร์ปและแข็ง",
            "Probably pushing more breath and volume to get through this area, which pulls the pitch sharp."),
        "zone_mix" => L(lang, "เสียงผสม (mix) ยังเชื่อมไม่ติด เลยเปลี่ยนไปใช้เสียงที่บางและมีลม ทำให้ไม่ถึงโน้ต",
            "The mix isn't connected yet, so the voice turns thin and breathy and doesn't quite reach the note."),
        "zone_pitch" => L(lang, "ช่วงนี้ยังไม่คุ้น หรือเปลี่ยนการใช้เสียงตรงนี้ เลยหาโน้ตไม่แม่น",
            "This part of your range is less familiar, or your voice changes gear here, so notes land less precisely."),
        "zone_breathy" => L(lang, "สายเสียงปิดไม่สนิทตอนขึ้นสูง (เสียงผสมยังเชื่อมไม่ติด) ถ้าตั้งใจให้มีลมเพื่ออารมณ์เพลงก็ไม่เป็นไร",
            "The vocal folds aren't closing fully as you go up (mix not connected yet). If you meant a breathy sound for the mood, that's fine."),
        "pitch" => L(lang, "อาจยังไม่ได้ยินโน้ตในหัวก่อนร้อง หรือยังจำทำนองได้ไม่แม่น",
            "You may not be hearing the note in your head before singing it, or the melody isn't memorised precisely yet."),
        "scoop" => L(lang, "เริ่มเสียงก่อนจะ 'ได้ยิน' โน้ตในหัว เป็นสไตล์ป๊อปได้ถ้าตั้งใจ แต่ถ้าเป็นทุกโน้ตจะฟังดูไม่มั่นใจ",
            "Starting the sound before you hear the note in your head. It can be a pop style choice, but on every note it sounds unsure."),
        "above" => L(lang, "ตั้งโน้ตสูงไว้ก่อนแล้วปล่อยลง มักเกิดจากเกร็งคอตอนเริ่ม",
            "Aiming high and settling down, often from throat tension at the start."),
        "wobble" => L(lang, "ลมไม่สม่ำเสมอหรือเกร็ง ทำให้คุมโน้ตค้างไม่ได้",
            "Uneven airflow or tension makes it hard to hold a note steady."),
        "endings" => L(lang, "ลมหมดก่อนจบประโยค หรือปล่อยการพยุงลมก่อนโน้ตจบ",
            "Running out of breath, or letting go of breath support before the note finishes."),
        "endings_late" => L(lang, "เหนื่อย/ลมเริ่มหมดตอนท้ายเพลง ซึ่งเจอบ่อยในเพลงยาว",
            "Fatigue or breath running low late in the song, which is common in long songs."),
        "vib_wide" => L(lang, "ใช้ลมหรือกรามมากเกินไปในการทำ vibrato หรือเหนื่อย",
            "Too much breath or jaw involved in the vibrato, or tiredness."),
        "vib_fast" => L(lang, "มักเกิดจากความเกร็งที่คอหรือลิ้น",
            "Usually tension in the throat or tongue."),
        "breaks" => L(lang, "ตรงรอยต่อเสียง แบกน้ำหนักเสียงจากช่วงล่างขึ้นไปมากเกินไป แล้วเสียงพลิกเอง",
            "Carrying too much chest weight up into the register change, so the voice flips."),
        "outkey" => L(lang, "อาจจำทำนองผิด หรือเพลงจริงมีโน้ตนอกคีย์อยู่แล้ว ลองเทียบกับต้นฉบับ",
            "Maybe the melody is remembered wrong, or the song really uses that note. Check against the original."),
        "dyn_flat" => L(lang, "ร้องด้วยแรงเท่ากันทั้งเพลง ทำให้ท่อนฮุกไม่ต่างจากท่อนเวิร์ส",
            "The same effort all the way through, so the chorus doesn't lift above the verse."),
        "rough" => L(lang, "อาจเกร็ง เหนื่อย หรือเสียงล้า",
            "Possibly tension, tiredness or a tired voice."),
        "no_vibrato" => L(lang, "ร้องเสียงตรงเป็นสไตล์ป๊อปได้ ถ้าอยากได้ vibrato ต้องผ่อนคลายตอนค้างโน้ต",
            "Straight tone is a fine pop style; vibrato needs a relaxed sustain if you want it."),
        _ => "",
    };

    public static List<string> Exercises(CoachResult r, Finding f, string lang)
    {
        string zone = f.Zone >= 0 ? ZoneName(r, f.Zone, lang) : "";
        string first = f.Times.Count > 0 ? Time(f.Times[0]) : "";
        return f.Id switch
        {
            "shift" => [
                L(lang, "ก่อนเริ่มร้อง ฮัมโน้ตแรกไปพร้อมอินโทร จนรู้สึกว่าเข้ากับดนตรี", "Before you start, hum the first note along with the intro until it fits the music."),
                L(lang, "ใช้หน้า Piano หาโน้ตแรกของเพลง แล้วเช็กว่าคาราโอเกะไม่ได้ถูกปรับคีย์", "Find the first note on the Piano page, and check that the karaoke key wasn't changed."),
            ],
            "sharp" => [
                L(lang, "ร้องประโยคที่ชาร์ปด้วยความดังประมาณ 70% เปิดหน้า Tuner ไว้ ให้ขึ้นสีเขียวก่อนค่อยเพิ่มเสียง", "Sing the sharp phrases at about 70% volume with the Tuner page open; get them green before adding volume."),
                L(lang, "ไซเรนเสียง 'งึ' (ng) ขึ้นลงเบา ๆ 1 นาที เพื่อคลายความเกร็ง", "Soft 'ng' sirens up and down for a minute to release tension."),
            ],
            "flat" => [
                L(lang, "ตอนร้องให้คิดว่าโน้ต 'อยู่สูงขึ้นไปนิด' และยกเพดานอ่อนเหมือนเริ่มหาว", "Think of each note as slightly higher and lift the soft palate, like the start of a yawn."),
                L(lang, "เช็กหูฟังให้ได้ยินเสียงตัวเองชัด แล้วร้องโน้ตยาวกับหน้า Tuner ให้ขึ้นสีเขียว", "Make sure you can hear yourself in the headphones, then hold notes on the Tuner page until they're green."),
            ],
            "zone_push" => [
                L(lang, $"ร้องท่อนที่อยู่ช่วง {zone} ด้วยความดังประมาณ 70% เปิด Tuner ไว้ ให้โน้ตเข้าก่อนค่อยเพิ่มเสียง", $"Sing the phrases around {zone} at about 70% volume with the Tuner; land the pitch first, then add volume."),
                L(lang, $"ลิปทริล (เป่าปากให้สั่น brrr) หรือไถเสียง 'งึ' ผ่านช่วง {zone} ขึ้นลง 1–2 นาที", $"Lip trills or 'ng' slides through {zone}, up and down, for 1–2 minutes."),
            ],
            "zone_mix" => [
                L(lang, $"ไถเสียง 'เน' (nay) หรือ 'มัม' (mum) ผ่านช่วง {zone} โดยพยายามให้เสียงเหมือนเดิมตลอด", $"'Nay' or 'mum' slides through {zone}, keeping the tone the same all the way."),
                L(lang, "ใช้หน้า Warm-up แบบ 5 โน้ต ด้วยเสียง 'เน' ให้คีย์ขึ้นไปถึงช่วงนี้", "Use the Warm-up page (5-note scale) on 'nay', letting the key climb into this area."),
            ],
            "zone_pitch" => [
                L(lang, $"เล่นโน้ตในช่วง {zone} บนหน้า Piano แล้วร้องตามทีละโน้ตค้างไว้ 3 วินาที", $"Play notes in {zone} on the Piano page and match each one, holding 3 seconds."),
                L(lang, "ใช้หน้า Warm-up ให้คีย์ขึ้นไปถึงช่วงนี้ ร้องช้า ๆ ที่ 70–80 BPM", "Use the Warm-up page with the key climbing into this area, slowly at 70–80 BPM."),
            ],
            "zone_breathy" => [
                L(lang, $"ไถเสียง 'เน' (nay) หรือ 'มัม' (mum) ผ่านช่วง {zone} ให้เสียงชัดไม่มีลมรั่ว", $"'Nay' or 'mum' slides through {zone} with a clear, non-airy tone."),
                L(lang, "ฮัม 'อึม' ให้รู้สึกสั่นที่ริมฝีปาก แล้วเปิดเป็นสระ 'อา' โดยคงความชัดไว้", "Hum 'mm' until your lips buzz, then open to 'ah' keeping the same clear tone."),
            ],
            "pitch" => [
                L(lang, "ร้องช้า ๆ ทีละประโยคกับหน้า Tuner ค้างแต่ละโน้ตให้เป็นสีเขียวก่อนไปโน้ตถัดไป", "Sing each phrase slowly with the Tuner, holding every note until it turns green."),
                L(lang, "ก่อนร้องแต่ละประโยค ฮัมในใจหรือเล่นโน้ตแรกบน Piano ก่อน", "Before each phrase, hear the first note in your head or play it on the Piano."),
            ],
            "scoop" => [
                L(lang, "ก่อนเข้าแต่ละประโยค 'ได้ยิน' โน้ตแรกในหัวก่อน แล้วเริ่มตรงโน้ตเลย", "Hear the first note in your head before each phrase, then start right on it."),
                L(lang, "ใช้หน้า Warm-up ร้องแบบสตักกาโต ('ฮะ ฮะ ฮะ' สั้น ๆ) ให้แต่ละโน้ตเริ่มตรงทันที", "Use the Warm-up page staccato ('ha ha ha', short) so each note starts on pitch."),
            ],
            "above" => [
                L(lang, "เริ่มโน้ตด้วยเสียง 'ม' หรือ 'น' เบา ๆ เพื่อวางโน้ตก่อนเปิดเป็นสระ", "Start notes with a gentle 'm' or 'n' to set the pitch before the vowel."),
                L(lang, "ร้องสตักกาโตบนหน้า Warm-up แล้วดูกราฟว่าเริ่มตรงโน้ต", "Staccato on the Warm-up page, watching that each note starts on target."),
            ],
            "wobble" => [
                L(lang, "ค้างโน้ตเดียว 8 จังหวะด้วยเสียง 'อู' เปิด Tuner ไว้ให้เส้นนิ่งอยู่สีเขียว", "Hold one note for 8 beats on 'oo' with the Tuner, keeping the line flat and green."),
                L(lang, "ฝึกลม: ปล่อยเสียง 'ซซซ' ให้เท่ากันตลอด 20 วินาที", "Breath: a steady 'sss' for 20 seconds without surges."),
            ],
            "endings" or "endings_late" => [
                L(lang, "จดจุดหายใจในเนื้อเพลง แล้วหายใจตรงนั้นทุกครั้ง", "Mark breath points in the lyrics and breathe there every time."),
                L(lang, "ฝึกค้างโน้ตท้ายประโยคให้ครบจังหวะกับ Tuner ให้เส้นอยู่สีเขียวจนจบ แล้วค่อยหยุดลม", "Hold phrase-end notes to their full length on the Tuner, staying green to the end, then stop the air."),
            ],
            "vib_wide" => [
                L(lang, "ค้างโน้ตเสียงตรง 2 จังหวะ แล้วค่อยปล่อยให้ vibrato เบา ๆ เกิดเอง", "Hold a straight tone for 2 beats, then let a small vibrato appear by itself."),
                L(lang, "ผ่อนกรามและลิ้น ไม่ใช้ท้องกระตุกเพื่อทำ vibrato", "Relax the jaw and tongue; don't pulse from the stomach."),
            ],
            "vib_fast" => [
                L(lang, "ฝึกสั่นโน้ตช้า ๆ ตามเมโทรนอม 4 → 5 → 6 ครั้งต่อวินาที", "Practise slow, controlled pulses with a metronome: 4 → 5 → 6 per second."),
                L(lang, "หาวเบา ๆ ก่อนร้องเพื่อคลายคอและโคนลิ้น", "A gentle yawn before singing to release the throat and tongue root."),
            ],
            "breaks" => [
                L(lang, "ลิปทริลหรือไซเรน 'งึ' ผ่านจุดที่เสียงแตก ขึ้นลงช้า ๆ ด้วยความดังปานกลาง", "Lip trills or 'ng' sirens through the break, slowly, at medium volume."),
                L(lang, "ร้องสเกล 5 โน้ต ด้วย 'เน' (nay) ให้คร่อมจุดนี้ แล้วลดน้ำหนักเสียงตอนขึ้น", "5-note scales on 'nay' across this spot, lightening the voice as you go up."),
            ],
            "outkey" => [
                L(lang, $"ฟังต้นฉบับท่อนที่ {first} แล้วเล่นโน้ตนั้นบนหน้า Piano เทียบกับที่ร้อง", $"Listen to the original at {first} and compare the note on the Piano page with what you sang."),
            ],
            "dyn_flat" => [
                L(lang, "ตั้งเป้าในเนื้อเพลง: ท่อนเวิร์ส 60% · ท่อนฮุก 90%", "Plan it in the lyrics: verse at 60%, chorus at 90%."),
                L(lang, "ร้องประโยคเดียวกัน 3 ระดับ เบา · กลาง · ดัง โดยให้โน้ตยังตรง", "Sing the same phrase at three levels, soft · medium · strong, keeping it in tune."),
            ],
            "rough" => [
                L(lang, "พักเสียง ดื่มน้ำ และลดความดังลงในการซ้อมช่วงนี้", "Rest your voice, drink water, and sing softer for a while."),
                L(lang, "ถ้าเสียงแหบ เจ็บ หรือเป็นนานหลายวัน ควรปรึกษาครูสอนร้องหรือผู้เชี่ยวชาญด้านเสียง", "If you feel hoarse or sore, or it lasts for days, talk to a singing teacher or voice professional."),
            ],
            "no_vibrato" => [
                L(lang, "ค้างโน้ตยาวแล้วผ่อนคลายลมช่วงครึ่งหลังของโน้ต ให้ vibrato เกิดเอง", "On long notes, relax into the second half and let vibrato appear."),
            ],
            _ => [],
        };
    }

    // ================================================================== strengths

    public static (string title, string detail) Strength(CoachResult r, Finding f, string lang)
    {
        double V(string k) => f.V.TryGetValue(k, out var v) ? v : double.NaN;
        return f.Id switch
        {
            "s_pitch" => (L(lang, "ร้องตรงโน้ตเป็นส่วนใหญ่", "Mostly on pitch"),
                L(lang, $"{N0(V("intune"))}% ของโน้ตอยู่ในระยะ ±20 เซนต์ และ {N0(V("close"))}% อยู่ใน ±35", $"{N0(V("intune"))}% of notes within ±20 cents and {N0(V("close"))}% within ±35.")),
            "s_close" => (L(lang, "หาโน้ตเจอเกือบทุกครั้ง", "You find the right notes"),
                L(lang, $"{N0(V("close"))}% ของโน้ตอยู่ใกล้โน้ตที่ถูก (±35 เซนต์) เหลือแค่ขัดเกลาให้ตรงขึ้น", $"{N0(V("close"))}% of notes land within ±35 cents; it's about polishing, not finding them.")),
            "s_zone" => (L(lang, $"ช่วง {ZoneName(r, f.Zone, lang)} แม่นที่สุด", $"Strongest at {ZoneName(r, f.Zone, lang)}"),
                L(lang, $"ตรงคีย์ {N0(V("intune"))}% ในช่วงนี้", $"{N0(V("intune"))}% on pitch in this zone.")),
            "s_steady" => (L(lang, "โน้ตยาวนิ่ง", "Steady held notes"),
                L(lang, $"มีแค่ {N0(V("pct"))}% ของโน้ตยาวที่ส่าย", $"Only {N0(V("pct"))}% of longer notes drift.")),
            "s_onset" => (L(lang, "เริ่มโน้ตตรง ไม่ช้อนเสียง", "Clean note starts"),
                L(lang, $"ต้นประโยคช้อนเสียงแค่ {N0(V("pct"))}%", $"Only {N0(V("pct"))}% of phrase starts scoop.")),
            "s_endings" => (L(lang, "จบประโยคได้นิ่ง", "Phrase endings held"),
                L(lang, $"โน้ตท้ายประโยคตกแค่ {N0(V("pct"))}%", $"Only {N0(V("pct"))}% of phrase endings fall.")),
            "s_vibrato" => (L(lang, "vibrato เป็นธรรมชาติ", "Natural vibrato"),
                L(lang, $"{N1(V("rate"))} ครั้ง/วินาที กว้าง ±{N0(V("extent"))} เซนต์ ใน {N0(V("pct"))}% ของโน้ตยาว", $"{N1(V("rate"))} Hz, ±{N0(V("extent"))} cents, on {N0(V("pct"))}% of long notes.")),
            "s_dyn" => (L(lang, "ใช้ความดังเบาเล่าเรื่อง", "Uses dynamics"),
                L(lang, $"ช่วงความดัง {N0(V("range"))} dB" + (V("builds") > 0 ? " และเพลงค่อย ๆ ไต่ระดับขึ้น" : ""), $"{N0(V("range"))} dB of dynamic range" + (V("builds") > 0 ? ", and the song builds." : "."))),
            "s_range" => (L(lang, "ใช้ช่วงเสียงกว้าง", "Wide range in use"),
                L(lang, $"จาก {Note(V("lo"))} ถึง {Note(V("hi"))}", $"From {Note(V("lo"))} to {Note(V("hi"))}.")),
            "s_key" => (L(lang, "อยู่คีย์เดียวกับดนตรี", "In the same key as the music"),
                L(lang, $"โน้ตที่ร้องเข้ากับคีย์ {KeyName(r.Key.BackTonic, r.Key.BackMinor, lang)} ของดนตรี", $"Your notes fit the music's {KeyName(r.Key.BackTonic, r.Key.BackMinor, lang)}.")),
            "s_nobreak" => (L(lang, "ขึ้นสูงได้โดยเสียงไม่แตก", "No cracks going up"),
                L(lang, "ไม่เจอเสียงพลิก/แตกกลางโน้ตเลย", "No flips or cracks were found in held notes.")),
            "s_done" => (L(lang, "ร้องจบทั้งเทค", "A full take to work from"),
                L(lang, $"วิเคราะห์ได้ {N0(V("n"))} โน้ต ช่วง {Note(V("lo"))}–{Note(V("hi"))}", $"{N0(V("n"))} notes analysed, {Note(V("lo"))}–{Note(V("hi"))}.")),
            _ => (f.Id, ""),
        };
    }

    // ================================================================== summary, plan, voice, caveats

    public static string Summary(CoachResult r, string lang)
    {
        var m = r.M;
        var parts = new List<string>
        {
            L(lang,
                $"เทคนี้ยาว {Time(r.Duration)} วิเคราะห์ได้ {m.NoteCount} โน้ต ร้องตรงคีย์ (±20 เซนต์) {N0(m.InTunePct)}% และใกล้เคียง (±35) {N0(m.ClosePct)}%",
                $"In this {Time(r.Duration)} take, {N0(m.InTunePct)}% of {m.NoteCount} notes were on pitch (±20 cents) and {N0(m.ClosePct)}% were close (±35)."),
        };
        if (r.Strengths.Count > 0)
            parts.Add(L(lang, $"จุดเด่น: {Strength(r, r.Strengths[0], lang).title}", $"Your strongest point: {Strength(r, r.Strengths[0], lang).title.ToLowerInvariant()}."));
        var top = r.Weaknesses.Where(f => f.Id != "no_vibrato").Take(2).Select(f => Title(r, f, lang)).ToList();
        if (top.Count > 0)
            parts.Add(L(lang, "เรื่องที่ควรแก้ก่อน: " + string.Join(" และ ", top), "Work on first: " + string.Join("; ", top) + "."));
        else
            parts.Add(L(lang, "ไม่เจอปัญหาใหญ่ ลองเพิ่มความยากด้วยเพลงที่สูงขึ้นหรือเร็วขึ้น", "No major problems found. Try a higher or faster song to stretch yourself."));
        if (r.Caveats.Contains("music"))
            parts.Add(L(lang, "มีเสียงดนตรีปนในไฟล์ ตัวเลขจึงคลาดได้ ครั้งหน้าใส่หูฟังตอนอัด", "Music was heard in the recording, so treat the numbers as rough; wear headphones next time."));
        else
            parts.Add(L(lang, $"แผนฝึกด้านล่างใช้เวลาประมาณ {PlanMinutes(r)} นาทีต่อวัน", $"The practice plan below takes about {PlanMinutes(r)} minutes a day."));
        return string.Join(" ", parts);
    }

    static int PlanMinutes(CoachResult r) => 2 + Math.Min(3, r.Weaknesses.Count(f => f.Id != "no_vibrato")) * 2 + 2;

    public static List<(int min, string text)> Plan(CoachResult r, string lang)
    {
        var plan = new List<(int, string)>
        {
            (2, L(lang, "วอร์มอัพ: ลิปทริล 30 วินาที แล้วใช้หน้า Warm-up สเกล 5 โน้ตในช่วงที่สบาย", "Warm up: 30 s of lip trills, then the Warm-up page's 5-note scale in a comfortable range.")),
        };
        foreach (var f in r.Weaknesses.Where(f => f.Id != "no_vibrato").Take(3))
        {
            var ex = Exercises(r, f, lang);
            if (ex.Count > 0) plan.Add((2, Title(r, f, lang) + " — " + ex[0]));
        }
        var t = r.Weaknesses.SelectMany(f => f.Times).FirstOrDefault(-1);
        plan.Add((2, t >= 0
            ? L(lang, $"เอาไปใช้: ร้องท่อนที่ {Time(t)} ช้า ๆ กับหน้า Tuner แล้วร้องความเร็วจริง อัดใหม่แล้ววิเคราะห์เทียบ", $"Apply it: sing the phrase at {Time(t)} slowly with the Tuner, then at full speed. Record again and compare.")
            : L(lang, "เอาไปใช้: ร้องท่อนที่ยากที่สุดของเพลง อัดใหม่แล้ววิเคราะห์เทียบ", "Apply it: sing the hardest phrase of the song, record again and compare.")));
        return plan;
    }

    public static List<string> Voice(CoachResult r, string lang)
    {
        var m = r.M;
        var l = new List<string>
        {
            L(lang, $"ช่วงเสียงที่ใช้ในเทคนี้: {Note(m.RangeLo)}–{Note(m.RangeHi)} · ร้องอยู่บ่อยที่สุดช่วง {Note(m.TessLo)}–{Note(m.TessHi)}",
                $"Range used in this take: {Note(m.RangeLo)}–{Note(m.RangeHi)} · most of the singing sits at {Note(m.TessLo)}–{Note(m.TessHi)}."),
        };
        var zs = r.Zones.Where(z => z.Notes >= 5).ToList();
        if (zs.Count >= 2)
        {
            var best = zs.OrderByDescending(z => z.InTunePct).First();
            l.Add(L(lang, $"ช่วงที่แข็งแรงที่สุด: {ZoneFull(r, best.Zone, lang)} ตรงคีย์ {N0(best.InTunePct)}%",
                $"Strongest zone: {ZoneFull(r, best.Zone, lang)}, {N0(best.InTunePct)}% on pitch."));
        }
        if (!double.IsNaN(m.PassaggioLo))
        {
            string why = m.PassaggioWhy switch
            {
                "breaks" => L(lang, "เพราะเสียงพลิก/แตกบ่อยแถวนี้", "because cracks cluster here"),
                "both" => L(lang, "เพราะคุณภาพเสียงเปลี่ยนและเสียงพลิกแถวนี้", "because the tone changes and cracks cluster here"),
                _ => L(lang, "เพราะเสียงเริ่มมีลมหรือบางลงตรงนี้", "because the tone gets airier or thinner here"),
            };
            l.Add(L(lang, $"รอยต่อเสียง (passaggio) น่าจะอยู่แถว {Note(m.PassaggioLo)}–{Note(m.PassaggioHi)} {why}",
                $"Your likely passaggio (where the voice changes gear) is around {Note(m.PassaggioLo)}–{Note(m.PassaggioHi)}, {why}."));
        }
        else
            l.Add(L(lang, "ไม่เจอจุดที่เสียงเปลี่ยนชัด ๆ ในโน้ตที่ร้องในเทคนี้", "No clear register change was found in the notes you sang."));
        string vt = VoiceTypes.All.FirstOrDefault(v => v.id == r.Voice) is var t && t.id != null ? L(lang, t.th, t.en) : r.Voice;
        l.Add(L(lang, $"โซนต่าง ๆ ในรายงานนี้ใช้ช่วงของเสียง{vt} (เปลี่ยนได้ในหน้า Coach)",
            $"Pitch zones in this report use {vt} ranges (change it on the Coach page)."));
        return l;
    }

    public static List<string> CaveatText(CoachResult r, string lang)
    {
        var l = new List<string>();
        foreach (var c in r.Caveats)
            l.Add(c switch
            {
                "music" => L(lang, "ไฟล์นี้มีเสียงดนตรีปน การจับโน้ตอาจคลาด แนะนำให้อัดโดยใส่หูฟัง ให้ไมค์ได้ยินแค่เสียงร้อง (แอปนี้ไม่มีตัวแยกเสียงร้องออกจากดนตรี)",
                    "There is music in this recording, so pitch tracking may be off. Record with headphones so the mic hears only you (this app has no vocal separation)."),
                "bleed" => L(lang, "ไมค์ได้ยินเสียงดนตรีด้วย (น่าจะเปิดลำโพง) ใส่หูฟังจะได้ผลแม่นกว่า", "The mic also picked up the music (speakers?). Headphones give more accurate results."),
                "short" => L(lang, "เทคสั้น ตัวเลขบางอย่าง (ความเหนื่อย ความดังทั้งเพลง) จึงยังบอกได้ไม่มาก", "This is a short take, so some measures (fatigue, song-wide dynamics) say little."),
                "fewnotes" => L(lang, "มีโน้ตให้วิเคราะห์น้อย เปอร์เซ็นต์จึงเปลี่ยนได้ง่าย", "Only a few notes were analysed, so the percentages can swing a lot."),
                "a440" => L(lang, "ไม่มีดนตรีให้เทียบ จึงวัดกับจูนมาตรฐาน (A4 = 440 Hz) ถ้าร้องเปล่าแล้วต่ำหรือสูงกว่าทั้งเพลงเท่า ๆ กัน หูคนฟังอาจไม่รู้สึกว่าเพี้ยน",
                    "There was no music to compare with, so notes are measured against standard tuning (A4 = 440 Hz). Singing alone, a constant offset may not sound wrong to a listener."),
                "strain" => L(lang, "ค่า jitter/shimmer เป็นตัวบ่งชี้คร่าว ๆ จากการร้องเพลง ไม่ใช่การวินิจฉัย ถ้าเจ็บหรือเสียงแหบนาน ควรพักและปรึกษาผู้เชี่ยวชาญ",
                    "Jitter/shimmer are rough indicators from singing, not a diagnosis. If it hurts or hoarseness lasts, rest and see a professional."),
                _ => c,
            });
        l.Add(L(lang, "โปรแกรมไม่รู้ว่าเป็นเพลงอะไร จึงวัดเทียบกับโน้ตที่ใกล้ที่สุด ถ้าร้องผิดโน้ตไปทั้งตัว (เช่นผิดไปครึ่งเสียงเต็ม) จะนับว่า 'ตรง' โน้ตที่ร้องจริง",
            "The program doesn't know the song, so it measures against the nearest note. A wrong note sung perfectly in tune counts as in tune."));
        l.Add(L(lang, "ตัดสินอารมณ์ ความไพเราะของเสียง เนื้อร้อง และการออกเสียงไม่ได้",
            "It can't judge emotion, beauty of tone, lyrics or pronunciation."));
        l.Add(L(lang, "เสียงลม การช้อนเสียง หรือการปล่อยเสียงตกตอนจบ อาจเป็นสไตล์ที่ตั้งใจ ลองเช็กว่าตั้งใจหรือเปล่า",
            "Breathiness, scoops and falling endings can be intentional style. Check whether they were."));
        return l;
    }

    // ================================================================== key numbers

    public static List<(string label, string value)> Numbers(CoachResult r, string lang)
    {
        var m = r.M;
        var rows = new List<(string, string)>
        {
            (L(lang, "โน้ตที่วิเคราะห์", "Notes analysed"), $"{m.NoteCount} · " + L(lang, $"ร้อง {N0(m.VoicedSec)} วินาที", $"{N0(m.VoicedSec)} s sung")),
            (L(lang, "ตรงคีย์ (±20 เซนต์)", "On pitch (±20 cents)"), N0(m.InTunePct) + "%"),
            (L(lang, "ใกล้เคียง (±35 เซนต์)", "Close (±35 cents)"), N0(m.ClosePct) + "%"),
            (L(lang, "คลาดเฉลี่ย", "Average miss"), N0(m.MeanAbs) + L(lang, " เซนต์", " cents")),
            (L(lang, "ทิศทางเฉลี่ย (+ สูง / − ต่ำ)", "Average direction (+ sharp / − flat)"), Signed(m.MeanSigned) + L(lang, " เซนต์", " cents")),
            (L(lang, "จูนที่ใช้เทียบ", "Tuning reference"), m.RefFrom == "a440" ? "A4 = 440 Hz"
                : L(lang, $"ตามดนตรี ({Signed(m.RefCents)} เซนต์จาก A440)", $"from the music ({Signed(m.RefCents)} cents from A440)")),
            (L(lang, "คีย์ (จากเสียงร้อง)", "Key (from your singing)"), r.Key.Tonic >= 0 ? KeyName(r.Key.Tonic, r.Key.Minor, lang) + (r.Key.Conf < 0.6 ? L(lang, " (ไม่ชัด)", " (unclear)") : "") : "–"),
        };
        if (r.Key.BackTonic >= 0)
            rows.Add((L(lang, "คีย์ของดนตรี", "Key of the music"), KeyName(r.Key.BackTonic, r.Key.BackMinor, lang)
                + (r.Key.Shift is int s ? L(lang, $" · เสียงร้องเลื่อน {Signed(s)}", $" · vocal shifted {Signed(s)}") : L(lang, " · ตรงกับเสียงร้อง", " · matches the vocal"))));
        rows.Add((L(lang, "ช้อนเสียงต้นประโยค", "Scoops at phrase starts"), m.OnsetNotes > 0 ? $"{N0(m.ScoopPct)}% " + L(lang, $"จาก {m.OnsetNotes} โน้ต", $"of {m.OnsetNotes}") : "–"));
        rows.Add((L(lang, "โน้ตยาวที่ส่าย", "Wandering held notes"), N0(m.WobblyPct) + "%"));
        rows.Add((L(lang, "ท้ายประโยคตก", "Falling phrase endings"), m.EndNotes > 0 ? $"{N0(m.EndDropPct)}% " + L(lang, $"จาก {m.EndNotes} โน้ต", $"of {m.EndNotes}")
            + (!double.IsNaN(m.EndDropLatePct) ? L(lang, $" (ช่วงท้ายเพลง {N0(m.EndDropLatePct)}%)", $" (last third {N0(m.EndDropLatePct)}%)") : "") : "–"));
        rows.Add(("Vibrato", m.LongNotes > 0 ? $"{N0(m.VibratoPct)}% " + L(lang, "ของโน้ตยาว", "of long notes")
            + (!double.IsNaN(m.VibRate) ? $" · {N1(m.VibRate)} Hz · ±{N0(m.VibExtent)}" + L(lang, " เซนต์", " cents") : "") : "–"));
        rows.Add((L(lang, "ช่วงเสียงที่ใช้", "Range used"), $"{Note(m.RangeLo)}–{Note(m.RangeHi)} · " + L(lang, $"ส่วนใหญ่ {Note(m.TessLo)}–{Note(m.TessHi)}", $"mostly {Note(m.TessLo)}–{Note(m.TessHi)}")));
        rows.Add((L(lang, "ช่วงความดัง (10–90%)", "Dynamic range (10–90%)"), N1(m.DynRangeDb) + " dB · " + m.DynShape switch
        {
            "builds" => L(lang, "ค่อย ๆ ดังขึ้น", "builds"),
            "fades" => L(lang, "ค่อย ๆ เบาลง", "fades"),
            "flat" => L(lang, "เท่า ๆ กัน", "flat"),
            _ => L(lang, "มีขึ้นลง", "varied"),
        }));
        rows.Add((L(lang, "ความใสของเสียง (HNR · CPP)", "Clarity (HNR · CPP)"), $"{N1(m.Hnr)} dB · {N1(m.Cpp)} dB"));
        rows.Add(("Jitter · shimmer", double.IsNaN(m.Jitter) ? "–" : $"{N1(m.Jitter)}% · {N1(m.Shimmer)}%"));
        rows.Add((L(lang, "เสียงแตก/พลิก", "Cracks / flips"), r.Breaks.Count.ToString(Inv)));
        return rows;
    }

    // ================================================================== progress

    public sealed record Delta(string Label, string Before, string After, int Better);   // Better: 1 improved, -1 worse, 0 same

    public static List<Delta> Compare(CoachResult now, CoachResult prev, string lang)
    {
        var l = new List<Delta>();
        void Add(string label, double a, double b, bool higherIsBetter, string unit, double same)
        {
            if (double.IsNaN(a) || double.IsNaN(b)) return;
            int better = Math.Abs(b - a) < same ? 0 : (b > a) == higherIsBetter ? 1 : -1;
            l.Add(new Delta(label, N0(a) + unit, N0(b) + unit, better));
        }
        var p = prev.M; var n = now.M;
        Add(L(lang, "ตรงคีย์ (±20 เซนต์)", "On pitch (±20 cents)"), p.InTunePct, n.InTunePct, true, "%", 3);
        Add(L(lang, "คลาดเฉลี่ย", "Average miss"), p.MeanAbs, n.MeanAbs, false, L(lang, " เซนต์", " cents"), 2);
        if (Math.Abs(p.MeanSigned) >= 3 || Math.Abs(n.MeanSigned) >= 3)
        {
            int better = Math.Abs(Math.Abs(n.MeanSigned) - Math.Abs(p.MeanSigned)) < 2 ? 0 : Math.Abs(n.MeanSigned) < Math.Abs(p.MeanSigned) ? 1 : -1;
            l.Add(new Delta(L(lang, "ทิศทางเฉลี่ย", "Average direction"), Signed(p.MeanSigned), Signed(n.MeanSigned), better));
        }
        if (p.OnsetNotes >= 5 && n.OnsetNotes >= 5) Add(L(lang, "ช้อนเสียงต้นประโยค", "Scoops"), p.ScoopPct, n.ScoopPct, false, "%", 5);
        Add(L(lang, "โน้ตยาวที่ส่าย", "Wandering held notes"), p.WobblyPct, n.WobblyPct, false, "%", 5);
        if (p.EndNotes >= 4 && n.EndNotes >= 4) Add(L(lang, "ท้ายประโยคตก", "Falling endings"), p.EndDropPct, n.EndDropPct, false, "%", 5);
        if (p.LongNotes >= 4 && n.LongNotes >= 4) Add(L(lang, "โน้ตยาวที่มี vibrato", "Long notes with vibrato"), p.VibratoPct, n.VibratoPct, true, "%", 5);
        Add(L(lang, "ช่วงความดัง", "Dynamic range"), p.DynRangeDb, n.DynRangeDb, true, " dB", 1);
        Add(L(lang, "เสียงแตก/พลิก", "Cracks / flips"), prev.Breaks.Count, now.Breaks.Count, false, "", 1);
        if (Math.Round(p.RangeHi) != Math.Round(n.RangeHi))
            l.Add(new Delta(L(lang, "โน้ตสูงสุดที่ใช้", "Highest note used"), Note(p.RangeHi), Note(n.RangeHi), 0));
        return l;
    }
}
