using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace VocalTuner;

/// <summary>
/// Always-on microphone stream for the live tuner (WASAPI shared mode, so the recorder can open the
/// same mic at the same time). Runs pitch detection ~60 times a second on the capture thread and
/// queues the results for the UI.
/// </summary>
public sealed class LiveInput : IDisposable
{
    public readonly record struct Result(double TimeMs, double Freq, float Peak);

    static readonly Stopwatch Clock = Stopwatch.StartNew();
    public static double NowMs => Clock.Elapsed.TotalMilliseconds;

    readonly ConcurrentQueue<Result> results = new();
    WasapiCapture? cap;
    WaveFormat fmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
    int rate, decim = 1, frameLen, hop;          // rate = analysis rate (device rate / decim, at most 48 kHz)
    float acc; int accN;
    readonly float[] ring = new float[1 << 16];
    long written, nextAnalysis;
    Mpm.Workspace ws = new(4096);
    float[] frame = new float[4096], conv = [];
    float hopPeak;
    double[] chanEnergy = new double[8];

    public volatile float RmsThreshold = 0.018f;
    public volatile float Clarity = 0.72f;
    public bool Running => cap != null;
    public string? DeviceName { get; private set; }

    /// <summary>Raised (on a background thread) when the stream stops unexpectedly, e.g. the mic was unplugged.</summary>
    public event Action<string>? Failed;

    public void Start(MMDevice dev) =>
        Start(new WasapiCapture(dev, true, 20) { ShareMode = AudioClientShareMode.Shared }, dev.FriendlyName);

    /// <summary>Debug aid (VOCALTUNER_LIVE_LOOPBACK=1): listen to what the speakers play instead of the mic.</summary>
    public void StartLoopback(MMDevice render) => Start(new WasapiLoopbackCapture(render), render.FriendlyName + " (loopback)");

    void Start(WasapiCapture c, string name)
    {
        Stop();
        fmt = c.WaveFormat;
        // Some devices run at 96–384 kHz. Pitch only needs ≤48 kHz: average groups of samples (a simple
        // low-pass + decimation) so the 85 ms window stays small and cheap.
        decim = Math.Max(1, (int)Math.Ceiling(fmt.SampleRate / 48000.0));
        rate = fmt.SampleRate / decim;
        acc = 0; accN = 0;
        frameLen = (int)Math.Round(rate * 0.0853);      // ~85 ms window: reliable down to E2
        hop = rate / 60;                                // ~60 analyses per second
        ws = new Mpm.Workspace(frameLen);
        frame = new float[frameLen];
        written = 0; nextAnalysis = frameLen; hopPeak = 0;
        chanEnergy = new double[Math.Max(1, fmt.Channels)];
        results.Clear();
        c.DataAvailable += OnData;
        c.RecordingStopped += (s, e) =>
        {
            if (s != cap) return;
            if (e.Exception != null) Failed?.Invoke(e.Exception.Message);
        };
        c.StartRecording();
        cap = c;
        DeviceName = name;
    }

    public void Stop()
    {
        var c = cap;
        cap = null;
        DeviceName = null;
        if (c == null) return;
        try { c.StopRecording(); } catch { }
        try { c.Dispose(); } catch { }
    }

    public long Callbacks, Bytes, Rejected;   // diagnostics

    void OnData(object? sender, WaveInEventArgs e)
    {
        Callbacks++; Bytes += e.BytesRecorded;
        if (sender != cap || e.BytesRecorded <= 0) { Rejected++; return; }
        try { Process(e); } catch (Exception ex) { LastError = ex.ToString(); }
    }
    public string? LastError;
    public string FormatText => fmt.ToString() + $" analysis={rate}Hz decim={decim} frameLen={frameLen} hop={hop}";

    void Process(WaveInEventArgs e)
    {
        int n = AudioEngine.ToFloat(e.Buffer, e.BytesRecorded, fmt, ref conv);
        int ch = Math.Max(1, fmt.Channels), frames = n / ch;

        // Many interfaces put a mono mic on one channel of a stereo stream: average only the live channels.
        double maxE = 0;
        for (int c = 0; c < ch; c++)
        {
            double s = 0;
            for (int i = 0; i < frames; i++) { float v = conv[i * ch + c]; s += v * v; }
            chanEnergy[c] = chanEnergy[c] * 0.8 + s * 0.2;
            if (chanEnergy[c] > maxE) maxE = chanEnergy[c];
        }
        Span<bool> use = stackalloc bool[ch];
        int used = 0;
        for (int c = 0; c < ch; c++) { use[c] = chanEnergy[c] >= maxE * 0.01; if (use[c]) used++; }
        if (used == 0) { use[0] = true; used = 1; }
        float inv = 1f / used;

        int mask = ring.Length - 1;
        for (int i = 0; i < frames; i++)
        {
            float m = 0;
            for (int c = 0; c < ch; c++) if (use[c]) m += conv[i * ch + c];
            m *= inv;
            float a = Math.Abs(m); if (a > hopPeak) hopPeak = a;
            if (decim > 1)
            {
                acc += m;
                if (++accN < decim) continue;
                m = acc / decim; acc = 0; accN = 0;
            }
            ring[written & mask] = m;
            written++;
        }

        double now = NowMs;
        while (written >= nextAnalysis)
        {
            long end = nextAnalysis;
            nextAnalysis += hop;
            if (written - end > ring.Length - frameLen) continue;   // fell behind; skip
            for (int i = 0; i < frameLen; i++) frame[i] = ring[(end - frameLen + i) & mask];
            double f = Mpm.Detect(frame, rate, RmsThreshold, Clarity, ws);
            double t = now - (written - end) * 1000.0 / rate;
            results.Enqueue(new Result(t, f, hopPeak));
            hopPeak = 0;
        }
        while (results.Count > 240) results.TryDequeue(out _);   // UI not draining (minimized)
    }

    public bool TryDequeue(out Result r) => results.TryDequeue(out r);

    public void Dispose() => Stop();
}

/// <summary>Watches for audio devices being plugged/unplugged or the Windows default changing.</summary>
public sealed class DeviceWatcher : IMMNotificationClient, IDisposable
{
    readonly MMDeviceEnumerator enumerator = new();
    public event Action? Changed;
    public DeviceWatcher() => enumerator.RegisterEndpointNotificationCallback(this);
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Changed?.Invoke();
    public void OnDeviceAdded(string pwstrDeviceId) => Changed?.Invoke();
    public void OnDeviceRemoved(string deviceId) => Changed?.Invoke();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => Changed?.Invoke();
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    public void Dispose()
    {
        try { enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        enumerator.Dispose();
    }
}

/// <summary>
/// Turns raw per-frame pitch results into what the tuner shows: a stable note name (switches only after
/// ~10 consistent frames), smoothed cents, and the trace for the piano roll. Port of the original tuner logic.
/// </summary>
public sealed class PitchTracker
{
    public readonly List<RollPoint> Trace = new();
    public int? DispKey;
    public double SmoothCents;
    public double Freq;
    public bool Voiced;

    double lastVoiced = double.NegativeInfinity, dispMidi;
    int? candKey; int candCount, voicedStreak;
    readonly List<double> centsHistory = new();
    double smoothMidi = double.NaN;

    public void Reset()
    {
        Trace.Clear(); centsHistory.Clear();
        DispKey = null; candKey = null; candCount = 0; voicedStreak = 0;
        SmoothCents = 0; smoothMidi = double.NaN; Voiced = false; Freq = 0;
    }

    /// <param name="alpha">pitch-line smoothing (0.1 = slow … 0.9 = fast)</param>
    public void Process(double now, double freq, double alpha)
    {
        if (!double.IsNaN(freq) && freq > 0)
        {
            lastVoiced = now; voicedStreak++;
            double midi = 69 + 12 * Math.Log2(freq / 440.0);
            int key = (int)Math.Round(midi);
            if (DispKey == null) { DispKey = key; dispMidi = key; candKey = null; candCount = 0; }
            else if (key == DispKey) { candKey = null; candCount = 0; }
            else if (key == candKey)
            {
                if (++candCount >= 10) { DispKey = key; dispMidi = key; candKey = null; candCount = 0; centsHistory.Clear(); }
            }
            else { candKey = key; candCount = 1; }
            // cents relative to the DISPLAYED note so the needle stays coherent
            centsHistory.Add((midi - dispMidi) * 100);
            if (centsHistory.Count > 7) centsHistory.RemoveAt(0);
            var sorted = centsHistory.OrderBy(c => c).ToList();
            SmoothCents += (sorted[sorted.Count / 2] - SmoothCents) * 0.12;
            Voiced = true; Freq = freq;
            // smoothed midi for the graph line (snap on real jumps, glide on vibrato)
            if (double.IsNaN(smoothMidi) || Math.Abs(midi - smoothMidi) > 1.5) smoothMidi = midi;
            else smoothMidi += (midi - smoothMidi) * alpha;
            if (voicedStreak >= 3)
                Trace.Add(new RollPoint(now, smoothMidi, (int)Math.Round(smoothMidi), Math.Clamp(SmoothCents, -50, 50)));
        }
        else if (now - lastVoiced > 350)
        {
            if (Voiced || DispKey != null)
            {
                Voiced = false; voicedStreak = 0; smoothMidi = double.NaN;
                DispKey = null; candKey = null; candCount = 0; centsHistory.Clear(); SmoothCents = 0;
            }
            Trace.Add(RollPoint.Gap(now));
        }
        else voicedStreak = 0;

        int drop = 0;
        while (drop < Trace.Count && now - Trace[drop].T >= 8000) drop++;
        if (drop > 0) Trace.RemoveRange(0, drop);
    }
}

/// <summary>A point on a pitch roll: time (ms), MIDI pitch (NaN = gap), nearest note, cents off.</summary>
public readonly record struct RollPoint(double T, double Midi, int Note, double Cents)
{
    public static RollPoint Gap(double t) => new(t, double.NaN, 0, 0);
    public bool IsGap => double.IsNaN(Midi);
}
