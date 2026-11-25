using System.Collections.Concurrent;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VocalTuner;

/// <summary>
/// Small additive piano-ish tone for finding a starting note (same timbre as the original web version).
/// Built to stay cheap at any device rate (some USB headphones run at 384 kHz): one wavetable lookup and one
/// multiply per sample per note, no locks on the audio thread, a voice limit, and a soft limiter so chords
/// never hard-clip.
/// </summary>
public sealed class PianoSynth : ISampleProvider, IDisposable
{
    sealed class Voice
    {
        public int Midi;
        public double Phase, Inc;          // wavetable position and step (table samples per output sample)
        public double Gain, Decay;         // current level and per-sample decay factor
        public double AttackStep, Peak;    // linear attack until Gain reaches Peak
        public bool Attacking = true;
        public long Left;                  // samples left before the release fade starts
        public double Release = 1;         // 1 = no fade yet; falls to 0 during the release fade
        public double ReleaseStep;
        public long Delay;                 // samples to wait before the note starts (scheduled notes)
        public int Gen;                    // schedule generation (scheduled notes), so a cancel can fade them
    }

    // ---- scheduled notes (warm-up exercises): sample-accurate, on the stream's own clock
    readonly ConcurrentQueue<(double at, int midi, double level, double dur, int gen)> schedIn = new();
    readonly List<(long start, int midi, double level, double dur, int gen)> sched = new();
    volatile int schedGen = 1;
    int seenGen = 1;
    long framesDone;                       // frames rendered since the stream first opened

    /// <summary>Stream clock in seconds (how far rendering has got; playback is ~50 ms behind).</summary>
    public double Now => Interlocked.Read(ref framesDone) / (double)WaveFormat.SampleRate;

    /// <summary>Bumped whenever the output stream is (re)opened; scheduled times are void after that.</summary>
    public int StreamGen { get; private set; }

    /// <summary>Opens the output now (so <see cref="Now"/> runs) without playing anything.</summary>
    public void Open() => EnsureOutput();

    /// <summary>Plays <paramref name="midi"/> at stream time <paramref name="at"/> for <paramref name="dur"/> seconds.</summary>
    public void Schedule(double at, int midi, double volume, double dur) => schedIn.Enqueue((at, midi, Level(volume), dur, schedGen));

    /// <summary>Drops every scheduled note and fades out the ones already sounding.</summary>
    public void CancelScheduled() => schedGen++;

    // timbre: harmonic number and amplitude
    static readonly (int h, double amp)[] Partials = [(1, 1), (2, 0.4), (3, 0.18), (4, 0.09), (5, 0.05)];
    const double Dur = 2.4, Attack = 0.008, FadeOut = 0.03, StealFade = 0.012;
    const int MaxVoices = 16;
    const int TableBits = 12, TableSize = 1 << TableBits;
    static readonly float[] Table = BuildTable();

    readonly List<Voice> voices = new();
    readonly ConcurrentQueue<(int midi, double vol)> pending = new();
    double limiterEnv;                     // peak follower for the soft limiter
    WasapiOut? output;
    readonly object outGate = new();
    public WaveFormat WaveFormat { get; private set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    static float[] BuildTable()
    {
        var t = new float[TableSize + 1];   // +1 guard sample for interpolation
        double norm = 0;
        foreach (var (_, a) in Partials) norm += a;
        for (int i = 0; i <= TableSize; i++)
        {
            double ph = 2 * Math.PI * i / TableSize, s = 0;
            foreach (var (h, a) in Partials) s += a * Math.Sin(h * ph);
            t[i] = (float)(s / norm);       // peak ≈ 1
        }
        return t;
    }

    /// <summary>Note level for a 0..1 volume (the table peaks near 0.87; the limiter catches chords).</summary>
    static double Level(double volume) => Math.Max(0.0001, 0.75 * volume * volume);

    /// <param name="volume">0..1 (UI percentage / 100)</param>
    public void Strike(int midi, double volume)
    {
        EnsureOutput();
        pending.Enqueue((midi, Level(volume)));
    }

    /// <summary>Output device id; null = the Windows default output. Change it with <see cref="SetDevice"/>.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Device the open stream actually plays on (null when closed).</summary>
    public string? OpenDeviceId { get; private set; }

    volatile bool resetVoices;

    /// <summary>
    /// Switch output. <paramref name="id"/> null = Windows default; <paramref name="resolvedId"/> is the device that
    /// choice means right now, so a change of the Windows default also reopens the stream.
    /// </summary>
    public void SetDevice(string? id, string? resolvedId)
    {
        DeviceId = string.IsNullOrEmpty(id) ? null : id;
        WasapiOut? old;
        lock (outGate)
        {
            if (output == null || OpenDeviceId == resolvedId) return;
            old = output; output = null; OpenDeviceId = null;
        }
        resetVoices = true;                    // the new device may run at another rate
        try { old.Stop(); } catch { }
        old.Dispose();
    }

    void EnsureOutput()
    {
        lock (outGate)
        {
            if (output != null) return;
            using var en = new MMDeviceEnumerator();
            MMDevice dev;
            try { dev = DeviceId != null ? en.GetDevice(DeviceId) : en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
            catch { dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }   // chosen device unplugged
            if (dev.State != DeviceState.Active) dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(dev.AudioClient.MixFormat.SampleRate, 2);
            var o = new WasapiOut(dev, AudioClientShareMode.Shared, true, 50);
            o.Init(this);
            o.PlaybackStopped += (s, _) =>
            {
                bool mine;
                lock (outGate) { mine = s == output; if (mine) { output = null; OpenDeviceId = null; } }
                if (mine) (s as WasapiOut)?.Dispose();   // device unplugged: reopen on the next key
            };
            o.Play();
            output = o;
            OpenDeviceId = dev.ID;
            StreamGen++;
        }
    }

    void StartVoice(int midi, double vol, int rate, long delay = 0, double? length = null, int gen = 0)
    {
        // the same key again, or too many notes: fade the old one out quickly instead of cutting it (no click)
        foreach (var v in voices)
            if (v.Midi == midi && v.Release >= 1) Fade(v, StealFade, rate);
        int live = 0;
        foreach (var v in voices) if (v.Release >= 1) live++;
        for (int i = 0; live >= MaxVoices && i < voices.Count; i++)
            if (voices[i].Release >= 1) { Fade(voices[i], StealFade, rate); live--; }

        double f = 440 * Math.Pow(2, (midi - 69) / 12.0);
        double end = Math.Min(0.0006, vol / 2);
        long decaySamples = (long)((Dur - Attack) * rate);
        voices.Add(new Voice
        {
            Midi = midi,
            Inc = f * TableSize / rate,
            Peak = vol,
            AttackStep = vol / Math.Max(1, Attack * rate),
            Decay = Math.Pow(end / vol, 1.0 / decaySamples),
            Left = (long)(Math.Min(Dur, length ?? Dur) * rate),
            Delay = delay,
            Gen = gen,
        });
    }

    static void Fade(Voice v, double seconds, int rate)
    {
        v.Left = 0;
        v.ReleaseStep = Math.Max(v.ReleaseStep, 1.0 / Math.Max(1, seconds * rate));
        if (v.Release >= 1) v.Release = 0.999999;
    }

    // diagnostics (read by the self-test): how often and how much the device asks for, and how long rendering takes
    public long DiagReads; public int DiagMaxFrames, DiagMinFrames = int.MaxValue; public double DiagMaxGapMs, DiagMaxRenderMs;
    readonly System.Diagnostics.Stopwatch diagClock = System.Diagnostics.Stopwatch.StartNew();
    double diagLast = -1;
    public float[]? DiagTap; public int DiagTapPos;                    // left channel of everything handed to the device
    public List<string>? DiagLog; float diagPrevOut, diagPrevRaw;      // sample jumps > 0.2 (finds buffer glitches)

    public int Read(float[] buffer, int offset, int count)
    {
        double t0 = diagClock.Elapsed.TotalMilliseconds;
        if (diagLast >= 0) DiagMaxGapMs = Math.Max(DiagMaxGapMs, t0 - diagLast);
        diagLast = t0; DiagReads++;
        DiagMaxFrames = Math.Max(DiagMaxFrames, count / 2); DiagMinFrames = Math.Min(DiagMinFrames, count / 2);
        try { return ReadCore(buffer, offset, count); }
        finally
        {
            DiagMaxRenderMs = Math.Max(DiagMaxRenderMs, diagClock.Elapsed.TotalMilliseconds - t0);
            if (DiagTap != null)
                for (int i = 0; i < count && DiagTapPos < DiagTap.Length; i += 2) DiagTap[DiagTapPos++] = buffer[offset + i];
        }
    }

    int ReadCore(float[] buffer, int offset, int count)
    {
        // NOT Array.Clear: WasapiOut hands us a byte[] disguised as float[] (NAudio's WaveBuffer), and Array.Clear
        // uses the real element size (1 byte), clearing only a quarter of the block. The leftover audio from the
        // previous block then gets mixed in again, which sounded like a crackling, shorted mic.
        for (int i = offset, e = offset + count; i < e; i++) buffer[i] = 0f;
        int rate = WaveFormat.SampleRate, frames = count / 2;
        if (resetVoices) { resetVoices = false; voices.Clear(); sched.Clear(); limiterEnv = 0; }
        while (pending.TryDequeue(out var p)) StartVoice(p.midi, p.vol, rate);

        // scheduled notes: a cancel bumps the generation, which drops queued notes and fades the sounding ones
        int gen = schedGen;
        if (gen != seenGen)
        {
            seenGen = gen;
            sched.RemoveAll(s => s.gen != gen);
            foreach (var v in voices) if (v.Gen != 0 && v.Gen != gen) { if (v.Delay > 0) { v.Delay = 0; v.Release = 0; v.ReleaseStep = 1; } else Fade(v, 0.06, rate); }
        }
        while (schedIn.TryDequeue(out var q))
            if (q.gen == gen) sched.Add(((long)Math.Round(q.at * rate), q.midi, q.level, q.dur, q.gen));
        long blockStart = framesDone, blockEnd = blockStart + frames;
        for (int si = sched.Count - 1; si >= 0; si--)
        {
            var s = sched[si];
            if (s.start >= blockEnd) continue;
            StartVoice(s.midi, s.level, rate, Math.Max(0, s.start - blockStart), s.dur, s.gen);
            sched.RemoveAt(si);
        }

        for (int vi = voices.Count - 1; vi >= 0; vi--)
        {
            var v = voices[vi];
            double phase = v.Phase, inc = v.Inc, gain = v.Gain, rel = v.Release;
            bool done = false;
            for (int i = 0; i < frames; i++)
            {
                if (v.Delay > 0) { v.Delay--; continue; }
                if (rel <= 0) { done = true; break; }   // cancelled before it started
                if (v.Attacking)
                {
                    gain += v.AttackStep;
                    if (gain >= v.Peak) { gain = v.Peak; v.Attacking = false; }
                }
                else gain *= v.Decay;

                if (v.Left > 0) v.Left--;
                else
                {
                    if (rel >= 1) { rel = 0.999999; if (v.ReleaseStep == 0) v.ReleaseStep = 1.0 / (FadeOut * rate); }
                    rel -= v.ReleaseStep;
                    if (rel <= 0) { done = true; break; }
                }

                int idx = (int)phase;
                double frac = phase - idx;
                double s = Table[idx] + (Table[idx + 1] - Table[idx]) * frac;
                phase += inc;
                if (phase >= TableSize) phase -= TableSize;

                float x = (float)(s * gain * Math.Min(1, rel));
                buffer[offset + 2 * i] += x;
                buffer[offset + 2 * i + 1] += x;
            }
            v.Phase = phase; v.Gain = gain; v.Release = rel;
            if (done) voices.RemoveAt(vi);
        }

        float rawFirst = buffer[offset], rawLast = buffer[offset + count - 2];
        double envIn = limiterEnv;
        // soft limiter: instant attack, ~150 ms release, so loud chords get quieter instead of distorting
        const double Ceil = 0.89;
        double relCoef = Math.Exp(-1.0 / (0.15 * rate));
        double env = limiterEnv;
        for (int i = 0; i < frames; i++)
        {
            int k = offset + 2 * i;
            double a = Math.Abs(buffer[k]);
            env = a > env ? a : env * relCoef;
            if (env > Ceil)
            {
                float g = (float)(Ceil / env);
                buffer[k] *= g; buffer[k + 1] *= g;
            }
        }
        limiterEnv = env < 1e-9 ? 0 : env;
        Interlocked.Add(ref framesDone, frames);
        if (DiagLog != null)
        {
            // look for jumps inside this block and across the boundary with the previous block
            float prev = diagPrevOut;
            for (int i = 0; i < frames; i++)
            {
                float y = buffer[offset + 2 * i];
                if (Math.Abs(y - prev) > 0.2 && DiagLog.Count < 30)
                    DiagLog.Add($"read#{DiagReads} frames={frames} at i={i}: {prev:+0.000;-0.000} -> {y:+0.000;-0.000} | raw first {rawFirst:+0.000;-0.000} last {rawLast:+0.000;-0.000} envIn {envIn:0.000} envOut {env:0.000} voices {voices.Count} prevRawLast {diagPrevRaw:+0.000;-0.000}");
                prev = y;
            }
            diagPrevOut = buffer[offset + count - 2]; diagPrevRaw = rawLast;
        }
        return count;   // keep the stream open (silence when idle) so the next note starts instantly
    }

    public void Dispose()
    {
        WasapiOut? o;
        lock (outGate) { o = output; output = null; }
        if (o != null) { try { o.Stop(); } catch { } o.Dispose(); }
    }

    /// <summary>Offline render for tests: plays <paramref name="hits"/> (time s, midi) at <paramref name="rate"/>.</summary>
    public static float[] Render(int rate, double seconds, IEnumerable<(double t, int midi)> hits, double volume, out double renderMs)
    {
        var syn = new PianoSynth { WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2) };
        var outBuf = new float[(int)(seconds * rate) * 2];
        var queue = new Queue<(double t, int midi)>(hits.OrderBy(h => h.t));
        int block = Math.Max(2, rate / 100) * 2;     // 10 ms blocks, like the device period
        var tmp = new float[block];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int pos = 0; pos < outBuf.Length; pos += block)
        {
            double now = pos / 2.0 / rate;
            while (queue.Count > 0 && queue.Peek().t <= now) syn.pending.Enqueue((queue.Dequeue().midi, Level(volume)));
            int n = Math.Min(block, outBuf.Length - pos);
            syn.Read(tmp, 0, n);
            Array.Copy(tmp, 0, outBuf, pos, n);
        }
        renderMs = sw.Elapsed.TotalMilliseconds;
        return outBuf;
    }
}
