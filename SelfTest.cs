using System.IO;
﻿using System.Text.Json;
using NAudio.Wave;

namespace VocalTuner;

/// <summary>
/// Headless check of the native audio path: `VocalTuner.exe --selftest &lt;outDir&gt;`.
/// Generates a test backing track, records a few seconds in each backing mode on the default
/// devices, renders/exports the mix, and writes a JSON report to outDir\selftest.json.
/// </summary>
static class SelfTest
{
    /// <summary>`--pianotest outDir`: renders a stress pattern offline, old vs new synth: CPU time, clipping, clicks.</summary>
    public static int PianoTest(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var hits = new List<(double t, int midi)>();
        int[] scale = [60, 62, 64, 65, 67, 69, 71, 72];
        for (int i = 0; i < 24; i++) hits.Add((i * 0.125, scale[i % 8]));                 // fast run, 8 notes/s
        for (int c = 0; c < 3; c++) foreach (var m in new[] { 48, 60, 64, 67, 72, 76 }) hits.Add((3.0 + c * 0.5, m)); // 6-note chords
        for (int i = 0; i < 12; i++) hits.Add((5.0 + i * 0.08, 69));                       // hammering one key
        for (int i = 0; i < 30; i++) hits.Add((6.5 + i * 0.03, 36 + i * 2));                 // 30-note glissando: voice limit
        const double secs = 10, vol = 1.0;
        var lines = new List<string>();
        foreach (int rate in new[] { 48000, 384000 })
        {
            double oldMs = OldRender(rate, secs, hits, vol, out int oldClipped, out float oldPeak);
            var buf = PianoSynth.Render(rate, secs, hits, vol, out double newMs);
            float peak = 0; double maxD2 = 0; int clipped = 0;
            for (int i = 4; i < buf.Length; i += 2)
            {
                float x = buf[i]; peak = Math.Max(peak, Math.Abs(x)); if (Math.Abs(x) >= 0.999f) clipped++;
                maxD2 = Math.Max(maxD2, Math.Abs(x - 2 * buf[i - 2] + buf[i - 4]));
            }
            lines.Add($"{rate} Hz: old {oldMs / secs / 10:0.0}% of real time (peak {oldPeak:0.00}, {oldClipped} clipped samples) | " +
                      $"new {newMs / secs / 10:0.00}% of real time (peak {peak:0.00}, {clipped} clipped, max 2nd-diff {maxD2:0.0000})");
            {
                using var w = new WaveFileWriter(Path.Combine(outDir, $"piano_stress_{rate / 1000}k.wav"), WaveFormat.CreateIeeeFloatWaveFormat(rate, 2));
                w.WriteSamples(buf, 0, buf.Length);
            }
        }
        File.WriteAllLines(Path.Combine(outDir, "pianotest.txt"), lines);
        return 0;
    }

    /// <summary>`--pianolive outDir`: plays the stress pattern on the real default output and records it back via loopback.</summary>
    public static int PianoLive(string outDir, string? devName = null)
    {
        Directory.CreateDirectory(outDir);
        using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        var dev = PickRender(en, devName);
        using var cap = new NAudio.Wave.WasapiLoopbackCapture(dev);
        var fmt = cap.WaveFormat; var got = new List<float>(); int ch = fmt.Channels;
        cap.DataAvailable += (_, e) =>
        {
            int n = e.BytesRecorded / 4;
            for (int i = 0; i < n; i += ch) got.Add(BitConverter.ToSingle(e.Buffer, i * 4));
        };
        cap.StartRecording();
        using var synth = new PianoSynth { DeviceId = dev.ID, DiagTap = new float[fmt.SampleRate * 12], DiagLog = new() };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hits = new List<(double t, int midi)>();
        int[] scale = [60, 62, 64, 65, 67, 69, 71, 72];
        for (int i = 0; i < 24; i++) hits.Add((0.3 + i * 0.125, scale[i % 8]));
        for (int c = 0; c < 3; c++) foreach (var m in new[] { 48, 60, 64, 67, 72, 76 }) hits.Add((3.3 + c * 0.5, m));
        for (int i = 0; i < 12; i++) hits.Add((5.3 + i * 0.08, 69));
        for (int i = 0; i < 30; i++) hits.Add((6.8 + i * 0.03, 36 + i * 2));
        foreach (var (t, m) in hits.OrderBy(h => h.t))
        {
            while (sw.Elapsed.TotalSeconds < t) Thread.Sleep(1);
            synth.Strike(m, 0.7);
        }
        while (sw.Elapsed.TotalSeconds < 9.5) Thread.Sleep(10);
        cap.StopRecording(); Thread.Sleep(200);
        var a = got.ToArray(); int rate = fmt.SampleRate;
        using (var w = new WaveFileWriter(Path.Combine(outDir, "pianolive.wav"), WaveFormat.CreateIeeeFloatWaveFormat(rate, 1)))
            w.WriteSamples(a, 0, a.Length);
        int start = Array.FindIndex(a, x => Math.Abs(x) > 1e-4);
        // clicks: the 2nd difference of a clean piano at this rate stays tiny (offline render: ~0.016 at 384 kHz);
        // a buffer glitch is a step, which shows up as a big 2nd difference. Group spikes into 10 ms events.
        double thr = Math.Max(0.03, 0.0155 * Math.Pow(384000.0 / rate, 2) * 2);
        int clipped = 0, zeroGaps = 0, zeroRun = 0; double maxD2 = 0; float peak = 0;
        var events = new List<double>(); int lastEv = -rate;
        for (int i = Math.Max(2, start); i < a.Length; i++)
        {
            float x = a[i]; peak = Math.Max(peak, Math.Abs(x)); if (Math.Abs(x) >= 0.999f) clipped++;
            if (x == 0 && Math.Abs(a[i - 1]) > 1e-3) zeroRun = 1; else if (x == 0 && zeroRun > 0) zeroRun++; else { if (zeroRun >= 8) zeroGaps++; zeroRun = 0; }
            double d2 = Math.Abs(x - 2 * a[i - 1] + a[i - 2]);
            maxD2 = Math.Max(maxD2, d2);
            if (d2 > thr && i - lastEv > rate / 100) { events.Add(i / (double)rate); lastEv = i; }
        }
        var lines = new List<string>
        {
            $"{dev.FriendlyName} {rate} Hz {fmt.Channels}ch: captured {a.Length / (double)rate:0.0}s, sound from {start / (double)rate:0.00}s",
            $"peak {peak:0.00}, clipped {clipped}, silent gaps {zeroGaps}, max 2nd-diff {maxD2:0.0000}, click events (>{thr:0.000}) {events.Count}",
            "click times: " + string.Join(" ", events.Take(40).Select(t => t.ToString("0.000"))),
            $"synth: reads {synth.DiagReads}, frames/read {synth.DiagMinFrames}..{synth.DiagMaxFrames}, max gap between reads {synth.DiagMaxGapMs:0.0} ms, max render {synth.DiagMaxRenderMs:0.00} ms",
        };
        using (var w = new WaveFileWriter(Path.Combine(outDir, "pianolive_synth.wav"), WaveFormat.CreateIeeeFloatWaveFormat(rate, 1)))
            w.WriteSamples(synth.DiagTap!, 0, synth.DiagTapPos);
        lines.AddRange(synth.DiagLog!);
        File.WriteAllLines(Path.Combine(outDir, "pianolive.txt"), lines);
        return 0;
    }

    static NAudio.CoreAudioApi.MMDevice PickRender(NAudio.CoreAudioApi.MMDeviceEnumerator en, string? name) =>
        string.IsNullOrEmpty(name) ? en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia)
        : en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active).First(d => d.FriendlyName.Contains(name));

    sealed class Sine(WaveFormat fmt) : IWaveProvider
    {
        double ph;
        public WaveFormat WaveFormat => fmt;
        public int Read(byte[] buf, int off, int count)
        {
            int ch = fmt.Channels, frames = count / (4 * ch);
            for (int i = 0; i < frames; i++)
            {
                float x = (float)(0.3 * Math.Sin(ph)); ph += 2 * Math.PI * 440 / fmt.SampleRate; if (ph > 2 * Math.PI) ph -= 2 * Math.PI;
                for (int c = 0; c < ch; c++) BitConverter.TryWriteBytes(new Span<byte>(buf, off + (i * ch + c) * 4, 4), x);
            }
            return frames * 4 * ch;
        }
    }

    /// <summary>`--outtest outDir`: a pure 440 Hz sine through different WasapiOut setups, checked via loopback.</summary>
    public static int OutTest(string outDir, string? devName = null)
    {
        Directory.CreateDirectory(outDir);
        var lines = new List<string>();
        using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        var dev = PickRender(en, devName);
        var mix = dev.AudioClient.MixFormat;
        lines.Add($"{dev.FriendlyName}: mix {mix.Encoding} {mix.SampleRate} Hz {mix.Channels}ch {mix.BitsPerSample}bit ({mix.GetType().Name})");
        var cases = new (string name, WaveFormat fmt, bool ev, int lat)[]
        {
            ("float stereo, event, 50ms", WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, 2), true, 50),
            ("float stereo, push, 50ms", WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, 2), false, 50),
            ("exact mix format, event, 50ms", mix, true, 50),
            ("exact mix format, push, 100ms", mix, false, 100),
            ("48k float, event, 50ms", WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), true, 50),
        };
        foreach (var (name, fmt, ev, lat) in cases)
        {
            try
            {
                using var cap = new NAudio.Wave.WasapiLoopbackCapture(dev);
                var cf = cap.WaveFormat; var got = new List<float>();
                cap.DataAvailable += (_, e) => { int n = e.BytesRecorded / 4; for (int i = 0; i < n; i += cf.Channels) got.Add(BitConverter.ToSingle(e.Buffer, i * 4)); };
                cap.StartRecording();
                using (var o = new WasapiOut(dev, NAudio.CoreAudioApi.AudioClientShareMode.Shared, ev, lat))
                {
                    o.Init(new Sine(fmt)); o.Play(); Thread.Sleep(1500); o.Stop();
                }
                cap.StopRecording(); Thread.Sleep(150);
                var a = got.ToArray(); int rate = cf.SampleRate;
                int s0 = Array.FindIndex(a, x => Math.Abs(x) > 0.01) + rate / 10, s1 = a.Length - rate / 10;
                double expect = 0.3 * Math.Pow(2 * Math.PI * 440 / rate, 2), maxD2 = 0; int spikes = 0, last = -rate;
                for (int i = Math.Max(2, s0); i < s1; i++)
                {
                    double d2 = Math.Abs(a[i] - 2 * a[i - 1] + a[i - 2]); maxD2 = Math.Max(maxD2, d2);
                    if (d2 > expect * 20 + 1e-4 && i - last > rate / 1000) { spikes++; last = i; }
                }
                lines.Add($"{name}: captured {(s1 - s0) / (double)rate:0.00}s, clicks {spikes}, max 2nd-diff {maxD2:0.00000} (clean ≈ {expect:0.00000})");
                if (name.StartsWith("float stereo, event"))
                    using (var w = new WaveFileWriter(Path.Combine(outDir, "sine_event.wav"), WaveFormat.CreateIeeeFloatWaveFormat(rate, 1))) w.WriteSamples(a, 0, a.Length);
            }
            catch (Exception ex) { lines.Add($"{name}: ERROR {ex.Message}"); }
            Thread.Sleep(300);
        }
        File.WriteAllLines(Path.Combine(outDir, "outtest.txt"), lines);
        return 0;
    }

    /// <summary>The previous synth's per-sample math (pow + 5 sines per voice, hard clip), for comparison.</summary>
    static double OldRender(int rate, double secs, List<(double t, int midi)> hits, double volume, out int clipped, out float peak)
    {
        (int h, double amp)[] partials = [(1, 1), (2, 0.4), (3, 0.18), (4, 0.09), (5, 0.05)];
        const double Dur = 2.4, Attack = 0.008;
        var voices = new List<(double f, double vol, long pos)>();
        var q = new Queue<(double t, int midi)>(hits.OrderBy(h => h.t));
        int frames = rate / 100; var buf = new float[frames * 2];
        clipped = 0; peak = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (long p = 0; p < (long)(secs * rate); p += frames)
        {
            while (q.Count > 0 && q.Peek().t <= p / (double)rate) { var h = q.Dequeue(); voices.Add((440 * Math.Pow(2, (h.midi - 69) / 12.0), Math.Max(0.0001, 0.6 * volume * volume), 0)); }
            Array.Clear(buf);
            for (int v = voices.Count - 1; v >= 0; v--)
            {
                var vo = voices[v]; double end = Math.Min(0.0006, vo.vol / 2);
                for (int i = 0; i < frames; i++)
                {
                    double t = (vo.pos + i) / (double)rate; if (t >= Dur) break;
                    double g = t < Attack ? vo.vol * t / Attack : vo.vol * Math.Pow(end / vo.vol, (t - Attack) / (Dur - Attack));
                    double s = 0; foreach (var (hh, amp) in partials) s += amp * Math.Sin(2 * Math.PI * vo.f * hh * t);
                    buf[2 * i] += (float)(g * s); buf[2 * i + 1] += (float)(g * s);
                }
                vo.pos += frames; voices[v] = vo;
                if (vo.pos / (double)rate >= Dur) voices.RemoveAt(v);
            }
            for (int i = 0; i < buf.Length; i += 2) { float a = Math.Abs(buf[i]); peak = Math.Max(peak, a); if (a > 1) clipped++; }
        }
        return sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>Diagnostics: LiveInput on the default output's loopback while the piano synth plays A3.</summary>
    public static int LiveTest(string outFile)
    {
        var lines = new List<string>();
        using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        var render = en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
        lines.Add("render: " + render.FriendlyName);
        var live = new LiveInput();
        live.StartLoopback(render);
        using var synth = new PianoSynth();
        Thread.Sleep(300);
        synth.Strike(57, 0.7);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int n = 0, voiced = 0; float pk = 0; var freqs = new List<double>();
        var tracker = new PitchTracker();
        while (sw.ElapsedMilliseconds < 1500)
        {
            while (live.TryDequeue(out var r))
            {
                n++; pk = Math.Max(pk, r.Peak);
                if (!double.IsNaN(r.Freq)) { voiced++; if (freqs.Count < 10) freqs.Add(Math.Round(r.Freq, 1)); }
                tracker.Process(r.TimeMs, r.Freq, 0.34);
            }
            Thread.Sleep(10);
        }
        lines.Add($"callbacks={live.Callbacks} bytes={live.Bytes} rejected={live.Rejected} running={live.Running} fmt={live.FormatText}");
        lines.Add($"error={live.LastError}");
        live.Stop();
        lines.Add($"results={n} voiced={voiced} peak={pk:0.000} firstFreqs=[{string.Join(", ", freqs)}] dispKey={tracker.DispKey}");
        File.WriteAllLines(outFile, lines);
        return 0;
    }

    /// <summary>Diagnostics: peak levels of one and of two simultaneous shared-mode captures of the default mic.</summary>
    public static int MicTest(string outFile)
    {
        var lines = new List<string>();
        float Run(params NAudio.CoreAudioApi.WasapiCapture[] caps)
        {
            var peaks = new float[caps.Length];
            var silentFlags = new int[caps.Length];
            for (int k = 0; k < caps.Length; k++)
            {
                int idx = k; var c = caps[k]; float[] conv = [];
                c.DataAvailable += (_, e) =>
                {
                    int n = AudioEngine.ToFloat(e.Buffer, e.BytesRecorded, c.WaveFormat, ref conv);
                    for (int i = 0; i < n; i++) peaks[idx] = Math.Max(peaks[idx], Math.Abs(conv[i]));
                };
                c.StartRecording();
            }
            Thread.Sleep(1500);
            foreach (var c in caps) { c.StopRecording(); }
            Thread.Sleep(200);
            foreach (var c in caps) c.Dispose();
            lines.Add(string.Join(" | ", peaks.Select((p, i) => $"cap{i} peak={p:0.00000}")));
            return peaks.Max();
        }
        using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        foreach (var d in en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active))
        {
            var v = d.AudioEndpointVolume;
            lines.Add($"device '{d.FriendlyName}': mute={v.Mute} volume={v.MasterVolumeLevelScalar:0.00} format={d.AudioClient.MixFormat}");
        }
        NAudio.CoreAudioApi.WasapiCapture New(bool ev) =>
            new(en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Multimedia), ev, 20)
            { ShareMode = NAudio.CoreAudioApi.AudioClientShareMode.Shared };
        lines.Add("one capture (event):"); Run(New(true));
        lines.Add("two captures (event+event):"); Run(New(true), New(true));
        lines.Add("one capture again:"); Run(New(true));
        lines.Add("LiveInput + capture:");
        var live = new LiveInput();
        live.Start(en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Multimedia));
        Thread.Sleep(300);
        Run(New(true));
        float lp = 0; while (live.TryDequeue(out var r)) lp = Math.Max(lp, r.Peak);
        lines.Add($"live peak={lp:0.00000}");
        live.Stop();
        File.WriteAllLines(outFile, lines);
        return 0;
    }

    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var log = new List<object>();
        int failures = 0;
        void Note(string step, object? data = null, string? error = null)
        {
            log.Add(new { step, ok = error == null, error, data });
            if (error != null) failures++;
        }

        // 1) test backing track: 4 s of a simple melody at 44.1 kHz stereo (forces resampling to the mic rate)
        string backing = Path.Combine(outDir, "test-backing.wav");
        using (var w = new WaveFileWriter(backing, WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)))
        {
            double[] notes = { 220, 247, 262, 294, 330, 294, 262, 247 };
            var buf = new float[44100 * 2 / 2];
            for (int n = 0; n < notes.Length; n++)
            {
                for (int i = 0; i < buf.Length / 2; i++)
                {
                    double t = (double)i / 44100;
                    float env = (float)Math.Min(1, Math.Min(t * 50, (0.5 - t) * 50));
                    float s = 0.3f * env * (float)Math.Sin(2 * Math.PI * notes[n] * t);
                    buf[2 * i] = s; buf[2 * i + 1] = s;
                }
                w.WriteSamples(buf, 0, buf.Length);
            }
        }

        // 2) pitch analyser on the known melody (expected MIDI 57,59,60,62,64,62,60,59)
        try
        {
            using var r = new AudioFileReader(backing);
            var all = new float[r.Length / 4];
            int got = r.Read(all, 0, all.Length);
            var mono = new float[got / 2];
            for (int i = 0; i < mono.Length; i++) mono[i] = all[2 * i];
            var midi = PitchAnalyzer.Analyze(mono, 44100, 0.018, 0.72, out double hop);
            var perNote = Enumerable.Range(0, 8).Select(n =>
            {
                var seg = midi.Skip((int)((n * 0.5 + 0.1) / hop)).Take((int)(0.3 / hop)).Where(m => !float.IsNaN(m)).ToList();
                return seg.Count == 0 ? -1 : Math.Round(seg.Average(), 2);
            }).ToList();
            double[] expect = { 57, 59, 60, 62, 64, 62, 60, 59 };
            bool okPitch = perNote.Zip(expect).All(p => Math.Abs(p.First - p.Second) < 0.1);
            Note("pitch-analyzer", new { perNote }, okPitch ? null : "pitch mismatch");
        }
        catch (Exception ex) { Note("pitch-analyzer", null, ex.ToString()); }

        // 2b) live detector + tracker (48 kHz, 85 ms window, ~60 Hz) on a sung-like melody with vibrato
        try
        {
            const int R = 48000;
            double[] notesHz = { 220, 247, 262, 294, 330, 294, 262, 247 };
            var sig = new float[R * 4];
            var rnd = new Random(7);
            double phase = 0;
            for (int i = 0; i < sig.Length; i++)
            {
                double t = (double)i / R; int n = Math.Min(7, (int)(t / 0.5));
                double f = notesHz[n] * Math.Pow(2, 0.15 / 12 * Math.Sin(2 * Math.PI * 5.5 * t));   // ±15 cent vibrato
                phase += 2 * Math.PI * f / R;
                sig[i] = (float)(0.2 * Math.Sin(phase) + 0.08 * Math.Sin(2 * phase) + 0.002 * (rnd.NextDouble() * 2 - 1));
            }
            var ws = new Mpm.Workspace(4096);
            var tracker = new PitchTracker();
            var shown = new List<int>();
            int frame = (int)Math.Round(R * 0.0853), hop = R / 60;
            for (int end = frame, k = 0; end <= sig.Length; end += hop, k++)
            {
                double fHz = Mpm.Detect(sig.AsSpan(end - frame, frame), R, 0.018, 0.72, ws);
                tracker.Process(end * 1000.0 / R, fHz, 0.34);
                double tSec = (double)end / R;
                if (Math.Abs(tSec % 0.5 - 0.45) < 0.5 / 60) shown.Add(tracker.DispKey ?? -1);   // near the end of each note
            }
            int[] expect = { 57, 59, 60, 62, 64, 62, 60, 59 };
            bool ok = shown.Count == 8 && shown.SequenceEqual(expect);
            Note("live-detector", new { shown, traceLen = tracker.Trace.Count }, ok ? null : "live note mismatch");
        }
        catch (Exception ex) { Note("live-detector", null, ex.ToString()); }

        using var engine = new AudioEngine();
        try { Note("devices", engine.ListDevices()); } catch (Exception ex) { Note("devices", null, ex.ToString()); }

        foreach (var mode in new[] { BackingMode.File, BackingMode.Loopback, BackingMode.None })
        {
            string tag = mode.ToString().ToLowerInvariant();
            try
            {
                engine.StartRecording(null, null, mode, backing, 0, 0.2f);
                var meters = new List<object?>();
                var until = DateTime.UtcNow.AddSeconds(4.2);
                while (DateTime.UtcNow < until) { Thread.Sleep(30); var m = engine.Tick(); if (meters.Count < 400) meters.Add(m); }
                engine.StopRecordingAsync().Wait();
                var take = engine.PrepareTake(0.018, 0.72);
                Note("record-" + tag, new
                {
                    duration = take.Duration,
                    sampleRate = take.SampleRate,
                    hasBacking = take.HasBacking,
                    estOffsetMs = take.EstOffsetMs,
                    syncBasis = take.SyncBasis,
                    pitchFrames = take.Pitch.Length,
                    voicedFrames = take.Pitch.Count(p => !float.IsNaN(p)),
                    maxVoicePeak = take.VoicePeaks.DefaultIfEmpty(0).Max(),
                    maxBackPeak = take.BackPeaks.DefaultIfEmpty(0).Max(),
                    meterSamples = meters.Count(m => m != null),
                });

                // preview a moment of playback
                engine.PreviewPlay(null, 0, 1, 1, take.EstOffsetMs);
                Thread.Sleep(500);
                var tick = engine.Tick();
                engine.StopPreview();
                Note("preview-" + tag, tick);

                foreach (var fmt in new[] { "wav24", "mp3" })
                {
                    string path = Path.Combine(outDir, $"take-{tag}.{(fmt == "mp3" ? "mp3" : "wav")}");
                    var r = engine.Export(path, fmt, 1, 1, take.EstOffsetMs, true, mode != BackingMode.None);
                    Note($"export-{tag}-{fmt}", new { result = r, bytes = new FileInfo(path).Length });
                }
            }
            catch (Exception ex)
            {
                Note("record-" + tag, null, ex.ToString());
                try { engine.StopRecordingAsync().Wait(); } catch { }
            }
        }

        File.WriteAllText(Path.Combine(outDir, "selftest.json"),
            JsonSerializer.Serialize(new { failures, log }, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        return failures == 0 ? 0 : 1;
    }
}

