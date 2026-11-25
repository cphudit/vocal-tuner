using System.IO;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VocalTuner;

public enum BackingMode { None, File, Loopback }

public sealed record DeviceInfo(string Id, string Name, bool IsDefault);
public sealed record DeviceList(List<DeviceInfo> Inputs, List<DeviceInfo> Outputs);
public sealed record BackingFileInfo(string Path, string Name, double Duration);
/// <summary>Meter/clock snapshot polled by the UI. Recording: Mic/Back peaks + Elapsed. Preview: PlayPos.</summary>
public sealed record EngineTick(bool Recording, float Mic, float Back, double Elapsed, bool Playing, double PlayPos);
public sealed record CalResult(double FileMs, double? SystemMs, double RoundTripMs, double Confidence);
public sealed record ExportResult(List<string> Files, double GainDb, double PeakDb);

/// <summary>Everything the review panel needs about a finished take.</summary>
public sealed class TakeInfo
{
    public double Duration;
    public int SampleRate;
    public bool HasBacking;
    public double EstOffsetMs;
    public string SyncBasis = "none";     // "system" (aligned through the loopback capture), "file" (clock) or "none"
    public double BucketSec;
    public float[] VoicePeaks = [];
    public float[] BackPeaks = [];
    public double PitchHop;
    public float[] Pitch = [];            // MIDI per hop, NaN = unvoiced
}

/// <summary>
/// Native audio side of the app (WASAPI via NAudio):
///  - records the microphone losslessly (32-bit float, device native rate)
///  - plays a backing file, or records "what you hear" (system loopback)
///  - aligns voice and backing, previews the mix, renders and exports it.
/// Everything here is lossless until the final export.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static double Now => Clock.Elapsed.TotalSeconds;
    static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VocalTuner");
    static string TakeDir => Path.Combine(DataDir, "take");

    readonly MMDeviceEnumerator enumerator = new();

    // Asynchronous notifications. They may fire on any thread.
    public event Action<string?>? BackingEnded;          // error message or null
    public event Action<string, string>? RecordError;    // (source, message)
    public event Action? PreviewEnded;

    // ------------------------------------------------------------------ devices

    public DeviceList ListDevices()
    {
        List<DeviceInfo> List(DataFlow flow)
        {
            string? defId = null;
            try { defId = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia).ID; } catch { }
            return enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
                .Select(d => new DeviceInfo(d.ID, d.FriendlyName, d.ID == defId))
                .ToList();
        }
        return new DeviceList(List(DataFlow.Capture), List(DataFlow.Render));
    }

    internal MMDevice Device(string? id, DataFlow flow)
    {
        if (!string.IsNullOrEmpty(id))
        {
            try { return enumerator.GetDevice(id); } catch { /* unplugged: fall back to default */ }
        }
        return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
    }

    public static BackingFileInfo InspectFile(string path)
    {
        using var r = new AudioFileReader(path);
        return new BackingFileInfo(path, Path.GetFileName(path), r.TotalTime.TotalSeconds);
    }

    // ------------------------------------------------------------------ recording

    sealed class CaptureTrack
    {
        public required IWaveIn Capture;
        public required string Path;
        public required WaveFormat SrcFormat;
        public WaveFileWriter? Writer;
        public int Rate, Channels;
        public long Frames;
        public double StartEst = double.MaxValue;   // Stopwatch time of the first captured frame
        public float Peak;
        public readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    CaptureTrack? mic, loop;
    WasapiOut? player, silenceKeeper;
    AudioFileReader? playerReader;
    float backPeak;
    readonly List<double> playStartEstimates = new();
    BackingMode mode;
    string? backingFile;
    double backingStartAt;
    bool recording;
    public bool IsRecording => recording;

    CaptureTrack StartCapture(IWaveIn cap, string path, bool isMic)
    {
        var fmt = cap.WaveFormat;
        var t = new CaptureTrack { Capture = cap, Path = path, SrcFormat = fmt, Rate = fmt.SampleRate, Channels = fmt.Channels };
        t.Writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(t.Rate, t.Channels));
        float[] conv = Array.Empty<float>();
        cap.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded <= 0 || t.Writer == null) return;
            double now = Now;
            int n = ToFloat(e.Buffer, e.BytesRecorded, t.SrcFormat, ref conv);
            t.Writer.WriteSamples(conv, 0, n);
            float pk = 0;
            for (int i = 0; i < n; i++) { float a = Math.Abs(conv[i]); if (a > pk) pk = a; }
            if (pk > t.Peak) t.Peak = pk;
            t.Frames += n / t.Channels;
            // Wall-clock time of frame 0 = callback time - captured duration. Callbacks can only be late,
            // so the minimum over the first seconds is the best estimate.
            if (t.Frames < t.Rate * 4L)
            {
                double est = now - (double)t.Frames / t.Rate;
                if (est < t.StartEst) t.StartEst = est;
            }
        };
        cap.RecordingStopped += (_, e) =>
        {
            try { t.Writer?.Dispose(); } catch { }
            t.Writer = null;
            t.Stopped.TrySetResult();
            if (e.Exception != null && recording)
                RecordError?.Invoke(isMic ? "microphone" : "system audio", e.Exception.Message);
        };
        cap.StartRecording();
        return t;
    }

    public void StartRecording(string? micId, string? outId, BackingMode backingMode, string? file, double startAt, float backVolume)
    {
        if (recording) throw new InvalidOperationException("Already recording.");
        StopPreview();
        take = null;
        // In file mode the system audio is captured too (not exported) so voice and file can be aligned precisely.
        StartSession(micId, outId, backingMode, file, startAt, backVolume, TakeDir, alsoLoopback: backingMode == BackingMode.File);
    }

    void StartSession(string? micId, string? outId, BackingMode backingMode, string? file, double startAt,
        float backVolume, string dir, bool alsoLoopback)
    {
        Directory.CreateDirectory(dir);
        foreach (var name in new[] { "voice.wav", "system.wav" })
        {
            try { File.Delete(Path.Combine(dir, name)); } catch { }
        }

        mode = backingMode;
        backingFile = file;
        backingStartAt = 0;
        playStartEstimates.Clear();
        backPeak = 0;
        mic = null; loop = null;

        var micDev = Device(micId, DataFlow.Capture);
        try
        {
            var micCap = new WasapiCapture(micDev, true, 20) { ShareMode = AudioClientShareMode.Shared };
            mic = StartCapture(micCap, Path.Combine(dir, "voice.wav"), true);

            if (mode == BackingMode.Loopback || alsoLoopback)
            {
                var outDev = Device(outId, DataFlow.Render);
                var loopCap = new WasapiLoopbackCapture(outDev);
                // WASAPI loopback delivers no packets while nothing is playing, which would squash
                // silent gaps out of the timeline. Playing digital silence keeps the stream continuous.
                silenceKeeper = new WasapiOut(outDev, AudioClientShareMode.Shared, true, 100);
                silenceKeeper.Init(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(loopCap.WaveFormat.SampleRate, 2)));
                silenceKeeper.Play();
                loop = StartCapture(loopCap, Path.Combine(dir, "system.wav"), false);
            }
            if (mode == BackingMode.File)
            {
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) throw new FileNotFoundException("Choose a backing track file first.");
                var outDev = Device(outId, DataFlow.Render);
                playerReader = new AudioFileReader(file) { Volume = backVolume };
                double total = playerReader.TotalTime.TotalSeconds;
                backingStartAt = Math.Clamp(startAt, 0, Math.Max(0, total - 0.5));
                if (backingStartAt > 0) playerReader.CurrentTime = TimeSpan.FromSeconds(backingStartAt);
                var meter = new MeteringSampleProvider(playerReader, Math.Max(256, playerReader.WaveFormat.SampleRate / 50));
                meter.StreamVolume += (_, e) => { foreach (var v in e.MaxSampleValues) if (v > backPeak) backPeak = v; };
                player = new WasapiOut(outDev, AudioClientShareMode.Shared, true, 60);
                player.Init(meter);
                player.PlaybackStopped += (s, e) =>
                {
                    if (recording && s == player)
                        BackingEnded?.Invoke(e.Exception?.Message);
                };
                player.Play();
            }
        }
        catch
        {
            recording = true;
            _ = StopRecordingAsync();
            throw;
        }
        recording = true;
    }

    public void SetBackingVolume(float v)
    {
        if (playerReader != null) playerReader.Volume = Math.Clamp(v, 0f, 2f);
    }

    /// <summary>Called on the UI thread every ~30 ms. Samples the playback clock and returns meter values.</summary>
    public EngineTick? Tick()
    {
        if (recording)
        {
            if (player != null && player.PlaybackState == PlaybackState.Playing)
            {
                try
                {
                    double t0 = Now;
                    long pos = player.GetPosition();
                    double t1 = Now;
                    double sec = pos / (double)player.OutputWaveFormat.AverageBytesPerSecond;
                    if (sec > 0.02 && sec < 4 && playStartEstimates.Count < 200)
                        playStartEstimates.Add((t0 + t1) / 2 - sec);
                }
                catch { }
            }
            float micPk = 0, bkPk = 0;
            if (mic != null) { micPk = mic.Peak; mic.Peak = 0; }
            if (loop != null) { bkPk = loop.Peak; loop.Peak = 0; }
            else if (player != null) { bkPk = backPeak; backPeak = 0; }
            double elapsed = mic != null && mic.Rate > 0 ? (double)mic.Frames / mic.Rate : 0;
            return new EngineTick(true, micPk, bkPk, elapsed, false, 0);
        }
        if (previewOut != null && mix != null && previewOut.PlaybackState == PlaybackState.Playing)
        {
            double pos = Math.Max(0, (double)mix.Pos / mix.WaveFormat.SampleRate - 0.06);
            return new EngineTick(false, 0, 0, 0, true, pos);
        }
        return null;
    }

    public async Task StopRecordingAsync()
    {
        if (!recording) return;
        recording = false;
        try { player?.Stop(); } catch { }
        try { mic?.Capture.StopRecording(); } catch { mic?.Stopped.TrySetResult(); }
        try { loop?.Capture.StopRecording(); } catch { loop?.Stopped.TrySetResult(); }
        var waits = new List<Task>();
        if (mic != null) waits.Add(mic.Stopped.Task);
        if (loop != null) waits.Add(loop.Stopped.Task);
        await Task.WhenAny(Task.WhenAll(waits), Task.Delay(4000));
        try { silenceKeeper?.Stop(); } catch { }
        silenceKeeper?.Dispose(); silenceKeeper = null;
        player?.Dispose(); player = null;
        playerReader?.Dispose(); playerReader = null;
        mic?.Capture.Dispose();
        loop?.Capture.Dispose();
    }

    // ------------------------------------------------------------------ take (in-memory, aligned)

    sealed class Take
    {
        public required float[] Voice;    // mono
        public float[]? Back;             // interleaved stereo
        public int Rate;
        public double EstOffset;          // seconds into Back that line up with voice t=0
    }
    Take? take;

    /// <summary>Loads the recorded take into memory, estimates the sync offset, and analyses pitch.</summary>
    public TakeInfo PrepareTake(double rmsThreshold, double clarity)
    {
        if (mic == null || !File.Exists(mic.Path)) throw new InvalidOperationException("Nothing was recorded.");
        int rate = mic.Rate <= 48000 ? mic.Rate : 48000;
        var voice = LoadVoiceMono(mic.Path, rate);
        if (voice.Length < rate / 10) throw new InvalidOperationException("The recording is too short.");

        float[]? back = null;
        double est = 0;
        string syncBasis = mode == BackingMode.Loopback ? "system" : mode == BackingMode.File ? "file" : "none";
        if (mode == BackingMode.File && backingFile != null)
        {
            using var r = new AudioFileReader(backingFile);
            back = LoadStereo(r, rate);
            est = EstimateFileOffset();
            // Precise path: the system-audio capture made during the take is a digital copy of what was
            // sent to the device. Locate it inside the original file, then chain voice→system→file.
            if (loop != null && File.Exists(loop.Path))
            {
                try
                {
                    using var lr = new WaveFileReader(loop.Path);
                    var loopMono = ToMono(LoadStereo(lr.ToSampleProvider(), rate));
                    double playStart = playStartEstimates.Count > 0 ? Median(playStartEstimates) : loop.StartEst + 0.06;
                    double guess = backingStartAt + (loop.StartEst - playStart);
                    var (f, conf) = AlignLoopToFile(loopMono, ToMono(back), rate, guess);
                    if (conf > 0.6) { est = EstimateLoopOffset() + f; syncBasis = "system"; }
                }
                catch { /* keep the clock-based estimate */ }
            }
        }
        else if (mode == BackingMode.Loopback && loop != null && File.Exists(loop.Path))
        {
            using var r = new WaveFileReader(loop.Path);
            back = LoadStereo(r.ToSampleProvider(), rate);
            est = EstimateLoopOffset();
        }

        take = new Take { Voice = voice, Back = back, Rate = rate, EstOffset = est };

        // waveform overviews
        const int Buckets = 1600;
        double bucketSec = (double)voice.Length / rate / Buckets;
        int bucket = Math.Max(1, (int)(bucketSec * rate));
        var vPeaks = new float[(voice.Length + bucket - 1) / bucket];
        for (int i = 0; i < voice.Length; i++) { float a = Math.Abs(voice[i]); int b = i / bucket; if (a > vPeaks[b]) vPeaks[b] = a; }
        float[] bPeaks = Array.Empty<float>();
        if (back != null)
        {
            int frames = back.Length / 2;
            bPeaks = new float[(frames + bucket - 1) / bucket];
            for (int i = 0; i < frames; i++)
            {
                float a = Math.Max(Math.Abs(back[2 * i]), Math.Abs(back[2 * i + 1]));
                int b = i / bucket; if (a > bPeaks[b]) bPeaks[b] = a;
            }
        }

        var pitch = PitchAnalyzer.Analyze(voice, rate, rmsThreshold, clarity, out double hop);

        for (int i = 0; i < vPeaks.Length; i++) vPeaks[i] = Math.Min(vPeaks[i], 1f);
        for (int i = 0; i < bPeaks.Length; i++) bPeaks[i] = Math.Min(bPeaks[i], 1f);
        return new TakeInfo
        {
            Duration = (double)voice.Length / rate,
            SampleRate = rate,
            HasBacking = back != null,
            EstOffsetMs = Math.Round(est * 1000, 1),
            SyncBasis = syncBasis,
            BucketSec = (double)bucket / rate,
            VoicePeaks = vPeaks,
            BackPeaks = bPeaks,
            PitchHop = hop,
            Pitch = pitch,
        };
    }

    /// <summary>The last take's audio for the singing analysis: mono voice, its rate, and the backing (mono) if any.</summary>
    public (float[] voice, int rate, float[]? back)? TakeAudio() =>
        take == null ? null : (take.Voice, take.Rate, take.Back == null ? null : ToMono(take.Back));

    // Offset = position in the backing (seconds) that lines up with voice t=0, from clock timestamps.
    // Latency the timestamps can't see (device/USB buffers, headphones) is measured by Calibrate().
    const double InputLatency = 0.010, RenderLatency = 0.015;

    double EstimateFileOffset()
    {
        double playStart = playStartEstimates.Count > 0 ? Median(playStartEstimates) : mic!.StartEst + 0.06;
        return backingStartAt + (mic!.StartEst - playStart) - InputLatency;
    }

    double EstimateLoopOffset() => (mic!.StartEst - loop!.StartEst) - RenderLatency - InputLatency;

    // ------------------------------------------------------------------ latency calibration

    static string CalDir => Path.Combine(DataDir, "calibration");

    /// <summary>
    /// Plays a short sequence of noise bursts and records them through the mic (headphones held to the mic,
    /// or speakers), then measures the true round-trip lag. Returns the correction (ms) to add to the
    /// automatic estimate for "audio file" and for "system audio" takes.
    /// </summary>
    public async Task<CalResult> CalibrateAsync(string? micId, string? outId)
    {
        if (recording) throw new InvalidOperationException("Stop recording first.");
        StopPreview();
        Directory.CreateDirectory(CalDir);
        const int R = 48000;
        double[] times = { 0.35, 0.80, 1.12, 1.71, 2.05, 2.62, 2.90 };    // irregular, so no false matches
        var sig = new float[(int)(3.3 * R)];
        var rnd = new Random(1234);
        foreach (var t in times)
        {
            int s0 = (int)(t * R);
            for (int i = 0; i < R * 8 / 1000; i++)
            {
                float env = (float)Math.Sin(Math.PI * i / (R * 8 / 1000.0));
                sig[s0 + i] = 0.8f * env * (float)(rnd.NextDouble() * 2 - 1);
            }
        }
        string clicks = Path.Combine(CalDir, "clicks.wav");
        using (var w = new WaveFileWriter(clicks + ".tmp", WaveFormat.CreateIeeeFloatWaveFormat(R, 1))) w.WriteSamples(sig, 0, sig.Length);
        File.Move(clicks + ".tmp", clicks, true);

        var savedTake = take;
        StartSession(micId, outId, BackingMode.File, clicks, 0, 1f, CalDir, alsoLoopback: true);
        recording = true;
        await Task.Delay(3600);
        await StopRecordingAsync();
        take = savedTake;

        double estFile = EstimateFileOffset();
        double estLoop = loop != null ? EstimateLoopOffset() : double.NaN;
        string voicePath = mic!.Path, loopPath = loop?.Path ?? "";
        int rate = mic.Rate <= 48000 ? mic.Rate : 48000;
        return await Task.Run(() =>
        {
            var voice = LoadVoiceMono(voicePath, rate);
            float[] refFile;
            using (var r = new AudioFileReader(clicks)) refFile = ToMono(LoadStereo(r, rate));
            var (lagF, corrF) = FindLag(voice, refFile, rate, -estFile, 0.8);
            if (corrF < 0.3)
                throw new InvalidOperationException("The microphone didn't pick up the test clicks clearly. Hold the headphones right against the mic (or use speakers), raise the volume, and try again.");
            double fileFix = -lagF - estFile;
            double? sysFix = null;
            if (File.Exists(loopPath) && !double.IsNaN(estLoop))
            {
                using var r = new WaveFileReader(loopPath);
                var refLoop = ToMono(LoadStereo(r.ToSampleProvider(), rate));
                var (lagL, corrL) = FindLag(voice, refLoop, rate, -estLoop, 0.8);
                if (corrL >= 0.3) sysFix = -lagL - estLoop;
            }
            return new CalResult(
                Math.Round(fileFix * 1000),
                sysFix.HasValue ? Math.Round(sysFix.Value * 1000) : null,
                Math.Round(lagF * 1000),
                Math.Round(corrF, 2));
        });
    }

    /// <summary>
    /// Finds F (seconds) such that loop[t] ≈ file[t + F], i.e. the file position at system-capture t=0.
    /// Analyses the loudest 10 s window of the first minute of the capture.
    /// </summary>
    static (double f, double conf) AlignLoopToFile(float[] loop, float[] file, int rate, double guess)
    {
        int W = Math.Min(loop.Length, 10 * rate), stepN = 5 * rate;
        int bestW0 = 0; double bestE = -1;
        for (int w0 = 0; w0 + W <= loop.Length && w0 <= 60 * rate; w0 += stepN)
        {
            double e = 0; for (int i = w0; i < w0 + W; i += 4) e += loop[i] * loop[i];
            if (e > bestE) { bestE = e; bestW0 = w0; }
        }
        var win = loop.AsSpan(bestW0, W).ToArray();
        const double Margin = 1.0;
        int s0 = (int)Math.Max(0, (bestW0 / (double)rate + guess - Margin) * rate);
        int s1 = (int)Math.Min(file.Length, (bestW0 / (double)rate + guess + W / (double)rate + Margin) * rate);
        if (s1 - s0 < rate) return (0, 0);
        var seg = file.AsSpan(s0, s1 - s0).ToArray();
        // win[t] = seg[t + w0 + F - s0]  →  rec[t] = ref[t − L] with L = s0 − w0 − F
        double center = (s0 - bestW0) / (double)rate - guess;
        var (L, conf) = FindLag(win, seg, rate, center, Margin);
        double f = (s0 - bestW0) / (double)rate - L;
        return (f, conf);
    }

    static float[] ToMono(float[] stereo)
    {
        var m = new float[stereo.Length / 2];
        for (int i = 0; i < m.Length; i++) m[i] = (stereo[2 * i] + stereo[2 * i + 1]) * 0.5f;
        return m;
    }

    /// <summary>Finds L (seconds) such that rec[t] ≈ reference[t − L], searching center ± range. Returns (L, normalized correlation).</summary>
    static (double lag, double corr) FindLag(float[] rec, float[] reference, int rate, double center, double range)
    {
        // coarse: 1 ms energy envelopes
        int hop = rate / 1000;
        float[] Env(float[] x)
        {
            var e = new float[x.Length / hop];
            for (int i = 0; i < e.Length; i++) { float s = 0; for (int j = 0; j < hop; j++) { float v = x[i * hop + j]; s += v * v; } e[i] = MathF.Sqrt(s / hop); }
            float mean = e.Average();
            for (int i = 0; i < e.Length; i++) e[i] -= mean;
            return e;
        }
        var er = Env(rec); var ef = Env(reference);
        int lo = (int)Math.Round((center - range) * 1000), hi = (int)Math.Round((center + range) * 1000);
        int bestL = lo; double best = double.NegativeInfinity;
        for (int L = lo; L <= hi; L++)
        {
            double s = 0;
            for (int i = 0; i < ef.Length; i++) { int j = i + L; if (j >= 0 && j < er.Length) s += er[j] * ef[i]; }
            if (s > best) { best = s; bestL = L; }
        }
        // fine: sample-accurate normalized waveform correlation within ±3 ms
        int c0 = bestL * hop, span = 3 * hop;
        int bestS = c0; double bestC = double.NegativeInfinity;
        for (int sft = c0 - span; sft <= c0 + span; sft++)
        {
            double dot = 0, na = 0, nb = 0;
            for (int i = 0; i < reference.Length; i++)
            {
                int j = i + sft; if (j < 0 || j >= rec.Length) continue;
                float a = rec[j], b = reference[i];
                dot += a * b; na += a * a; nb += b * b;
            }
            double c = Math.Abs(dot) / (Math.Sqrt(na * nb) + 1e-12); // abs: some mics invert polarity
            if (c > bestC) { bestC = c; bestS = sft; }
        }
        // confidence: Pearson correlation of the envelopes at the chosen lag
        double sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < ef.Length; i++)
        {
            int j = i + bestL; if (j < 0 || j >= er.Length) continue;
            sab += er[j] * ef[i]; saa += er[j] * er[j]; sbb += ef[i] * ef[i];
        }
        double conf = sab / (Math.Sqrt(saa * sbb) + 1e-12);
        return ((double)bestS / rate, conf);
    }

    static float[] LoadVoiceMono(string path, int rate)
    {
        using var r = new WaveFileReader(path);
        int ch = r.WaveFormat.Channels;
        var all = ReadAll(r.ToSampleProvider());
        int frames = all.Length / ch;
        float[] mono = new float[frames];
        if (ch == 1) Array.Copy(all, mono, frames);
        else
        {
            // Many interfaces put a mono mic on one channel of a stereo stream. Use the live channel(s)
            // instead of averaging in a silent one (which would halve the level).
            double e0 = 0, e1 = 0;
            for (int i = 0; i < frames; i++) { e0 += all[i * ch] * all[i * ch]; e1 += all[i * ch + 1] * all[i * ch + 1]; }
            bool use0 = e0 >= e1 * 0.01, use1 = e1 >= e0 * 0.01;
            for (int i = 0; i < frames; i++)
            {
                float l = all[i * ch], rr = all[i * ch + 1];
                mono[i] = use0 && use1 ? (l + rr) * 0.5f : use0 ? l : rr;
            }
        }
        if (r.WaveFormat.SampleRate == rate) return mono;
        return ReadAll(new WdlResamplingSampleProvider(new ArraySampleProvider(mono, r.WaveFormat.SampleRate, 1), rate));
    }

    static float[] LoadStereo(ISampleProvider src, int rate)
    {
        ISampleProvider s = src.WaveFormat.Channels switch
        {
            1 => new MonoToStereoSampleProvider(src),
            2 => src,
            _ => new MultiplexingSampleProvider(new[] { src }, 2),
        };
        if (s.WaveFormat.SampleRate != rate) s = new WdlResamplingSampleProvider(s, rate);
        return ReadAll(s, maxSamples: (long)rate * 2 * 60 * 30); // cap at 30 minutes
    }

    static float[] ReadAll(ISampleProvider s, long maxSamples = long.MaxValue)
    {
        var chunks = new List<float[]>();
        var buf = new float[s.WaveFormat.SampleRate * s.WaveFormat.Channels];
        long total = 0;
        int n;
        while (total < maxSamples && (n = s.Read(buf, 0, buf.Length)) > 0)
        {
            chunks.Add(buf.AsSpan(0, n).ToArray());
            total += n;
        }
        var all = new float[total];
        long o = 0;
        foreach (var c in chunks) { Array.Copy(c, 0, all, o, c.Length); o += c.Length; }
        return all;
    }

    static double Median(List<double> xs)
    {
        var s = xs.OrderBy(x => x).ToList();
        return s[s.Count / 2];
    }

    // ------------------------------------------------------------------ preview playback

    sealed class MixProvider : ISampleProvider
    {
        readonly float[] voice; readonly float[]? back; readonly int backFrames;
        public WaveFormat WaveFormat { get; }
        public volatile int Pos;
        public volatile float VoiceGain = 1, BackGain = 1;
        public volatile int OffsetFrames;
        public MixProvider(Take t)
        {
            voice = t.Voice; back = t.Back; backFrames = back?.Length / 2 ?? 0;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(t.Rate, 2);
        }
        public int Read(float[] buffer, int offset, int count)
        {
            int pos = Pos;
            int n = Math.Min(count / 2, voice.Length - pos);
            if (n <= 0) return 0;
            float vg = VoiceGain, bg = BackGain; int off = OffsetFrames;
            for (int i = 0; i < n; i++)
            {
                float v = voice[pos + i] * vg, l = v, r = v;
                long bi = (long)pos + i + off;
                if (back != null && bi >= 0 && bi < backFrames) { l += back[2 * bi] * bg; r += back[2 * bi + 1] * bg; }
                buffer[offset + 2 * i] = Math.Clamp(l, -1f, 1f);
                buffer[offset + 2 * i + 1] = Math.Clamp(r, -1f, 1f);
            }
            Pos = pos + n;
            return n * 2;
        }
    }

    MixProvider? mix;
    WasapiOut? previewOut;

    public void PreviewPlay(string? outId, double fromSec, float voiceGain, float backGain, double offsetMs)
    {
        if (take == null) throw new InvalidOperationException("No take to play.");
        StopPreview();
        mix = new MixProvider(take);
        PreviewParams(voiceGain, backGain, offsetMs);
        mix.Pos = (int)Math.Clamp(fromSec * take.Rate, 0, Math.Max(0, take.Voice.Length - 1));
        var outDev = Device(outId, DataFlow.Render);
        var o = new WasapiOut(outDev, AudioClientShareMode.Shared, true, 60);
        o.Init(mix);
        o.PlaybackStopped += (s, e) =>
        {
            if (s != previewOut) return;
            previewOut = null;
            (s as WasapiOut)?.Dispose();
            PreviewEnded?.Invoke();
        };
        previewOut = o;
        o.Play();
    }

    public void PreviewParams(float voiceGain, float backGain, double offsetMs)
    {
        if (mix == null || take == null) return;
        mix.VoiceGain = voiceGain;
        mix.BackGain = backGain;
        mix.OffsetFrames = (int)Math.Round(offsetMs / 1000.0 * take.Rate);
    }

    public void PreviewSeek(double sec)
    {
        if (mix != null && take != null) mix.Pos = (int)Math.Clamp(sec * take.Rate, 0, take.Voice.Length - 1);
    }

    public void StopPreview()
    {
        var o = previewOut;
        previewOut = null;
        if (o != null) { try { o.Stop(); } catch { } o.Dispose(); }
    }

    // ------------------------------------------------------------------ export

    public ExportResult Export(string path, string format, float voiceGain, float backGain, double offsetMs, bool normalize, bool alsoVoice)
    {
        var t = take ?? throw new InvalidOperationException("No take to export.");
        int frames = t.Voice.Length;
        var p = new MixProvider(t) { VoiceGain = voiceGain, BackGain = backGain, OffsetFrames = (int)Math.Round(offsetMs / 1000.0 * t.Rate) };
        // Render without the preview clamp so we can measure true peaks and scale cleanly.
        var outBuf = new float[frames * 2];
        int bFrames = t.Back?.Length / 2 ?? 0;
        for (int i = 0; i < frames; i++)
        {
            float v = t.Voice[i] * voiceGain, l = v, r = v;
            long bi = (long)i + p.OffsetFrames;
            if (t.Back != null && bi >= 0 && bi < bFrames) { l += t.Back[2 * bi] * backGain; r += t.Back[2 * bi + 1] * backGain; }
            outBuf[2 * i] = l; outBuf[2 * i + 1] = r;
        }
        double gainDb = ScaleToTarget(outBuf, normalize, out double peakDb);
        var files = new List<string> { path };
        WriteAudio(path, format, outBuf, 2, t.Rate);

        if (alsoVoice)
        {
            var v = (float[])t.Voice.Clone();
            for (int i = 0; i < v.Length; i++) v[i] *= voiceGain;
            ScaleToTarget(v, normalize, out _);
            string vp = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + " (voice only)" + Path.GetExtension(path));
            WriteAudio(vp, format, v, 1, t.Rate);
            files.Add(vp);
        }
        return new ExportResult(files, Math.Round(gainDb, 1), Math.Round(peakDb, 1));
    }

    /// <summary>Normalizes to -1 dBFS (or only scales down if it would clip). Returns applied gain in dB.</summary>
    static double ScaleToTarget(float[] data, bool normalize, out double peakDb)
    {
        const float Target = 0.8913f; // -1 dBFS
        float peak = 0;
        foreach (var x in data) { float a = Math.Abs(x); if (a > peak) peak = a; }
        float g = 1f;
        if (peak > 1e-6f && (normalize || peak > Target)) g = Math.Min(Target / peak, 31.6f); // max +30 dB
        if (g != 1f) for (int i = 0; i < data.Length; i++) data[i] *= g;
        peakDb = peak > 1e-6f ? 20 * Math.Log10(peak * g) : -120;
        return 20 * Math.Log10(g);
    }

    static void WriteAudio(string path, string format, float[] data, int channels, int rate)
    {
        switch (format)
        {
            case "wav32f":
                using (var w = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, channels))) w.WriteSamples(data, 0, data.Length);
                break;
            case "mp3":
                MediaFoundationApi.Startup();
                var pcm = new SampleToWaveProvider16(new ArraySampleProvider(data, rate, channels));
                MediaFoundationEncoder.EncodeToMp3(pcm, path, 320000);
                break;
            default: // wav24
                using (var w = new WaveFileWriter(path, new WaveFormat(rate, 24, channels))) w.WriteSamples(data, 0, data.Length);
                break;
        }
    }

    // ------------------------------------------------------------------ helpers

    internal static int ToFloat(byte[] buf, int bytes, WaveFormat fmt, ref float[] dst)
    {
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat
                       || (fmt is WaveFormatExtensible x && x.SubFormat == IeeeFloatSubtype);
        int bps = fmt.BitsPerSample / 8;
        int n = bytes / bps;
        if (dst.Length < n) dst = new float[n];
        if (isFloat && bps == 4) Buffer.BlockCopy(buf, 0, dst, 0, n * 4);
        else if (bps == 2) for (int i = 0; i < n; i++) dst[i] = BitConverter.ToInt16(buf, i * 2) / 32768f;
        else if (bps == 3) for (int i = 0, o = 0; i < n; i++, o += 3) dst[i] = (buf[o] | buf[o + 1] << 8 | (sbyte)buf[o + 2] << 16) / 8388608f;
        else if (bps == 4) for (int i = 0; i < n; i++) dst[i] = BitConverter.ToInt32(buf, i * 4) / 2147483648f;
        else throw new NotSupportedException($"Unsupported capture format: {fmt}");
        return n;
    }

    sealed class ArraySampleProvider(float[] data, int rate, int channels) : ISampleProvider
    {
        int pos;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, data.Length - pos);
            if (n <= 0) return 0;
            Array.Copy(data, pos, buffer, offset, n);
            pos += n;
            return n;
        }
    }

    public void Dispose()
    {
        StopPreview();
        if (recording) StopRecordingAsync().Wait(2000);
        enumerator.Dispose();
    }
}
