using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VocalTuner;

/// <summary>
/// McLeod Pitch Method (normalized square difference function). Picks the FIRST strong maximum,
/// which is robust against octave errors. Shared by the live tuner and the offline take analysis.
/// </summary>
public static class Mpm
{
    public sealed class Workspace(int frame)
    {
        public readonly float[] Nsdf = new float[frame];
        public readonly double[] Prefix = new double[frame + 1];
    }

    /// <summary>Returns the pitch in Hz (70–1100), or NaN when the frame is too quiet or unclear.</summary>
    public static double Detect(ReadOnlySpan<float> x, int rate, double rmsThreshold, double clarity, Workspace ws)
    {
        int n = x.Length;
        var P = ws.Prefix; var nsdf = ws.Nsdf;
        P[0] = 0;
        for (int i = 0; i < n; i++) P[i + 1] = P[i] + (double)x[i] * x[i];
        if (Math.Sqrt(P[n] / n) < rmsThreshold) return double.NaN;

        int minLag = rate / 1100;
        int maxLag = Math.Min(Math.Min(n - 2, rate / 70), nsdf.Length - 2);
        for (int lag = 0; lag <= maxLag; lag++)
        {
            double acf = Dot(x[..(n - lag)], x[lag..]);
            double m = P[n - lag] + (P[n] - P[lag]);      // Σa² + Σb² from prefix sums
            nsdf[lag] = m > 0 ? (float)(2 * acf / m) : 0;
        }

        // key maxima: one per positive region after the first zero crossing
        int pos = 1;
        while (pos <= maxLag && nsdf[pos] > 0) pos++;
        float highest = 0;
        Span<int> peaks = stackalloc int[128];
        int np = 0;
        float curMax = float.NegativeInfinity; int curPos = -1;
        for (; pos <= maxLag; pos++)
        {
            if (nsdf[pos] > 0) { if (nsdf[pos] > curMax) { curMax = nsdf[pos]; curPos = pos; } }
            else if (curPos > 0)
            {
                if (np < peaks.Length) peaks[np++] = curPos;
                if (curMax > highest) highest = curMax;
                curMax = float.NegativeInfinity; curPos = -1;
            }
        }
        if (curPos > 0 && np < peaks.Length) { peaks[np++] = curPos; if (curMax > highest) highest = curMax; }
        if (np == 0 || highest < clarity) return double.NaN;

        float thresh = 0.9f * highest;
        int t0 = -1;
        for (int k = 0; k < np; k++) if (nsdf[peaks[k]] >= thresh) { t0 = peaks[k]; break; }
        if (t0 < minLag || t0 <= 0 || t0 >= maxLag) return double.NaN;

        double x1 = nsdf[t0 - 1], x2 = nsdf[t0], x3 = nsdf[t0 + 1];
        double aa = (x1 + x3 - 2 * x2) / 2, bb = (x3 - x1) / 2;
        double T = aa != 0 ? t0 - bb / (2 * aa) : t0;
        double f = rate / T;
        return f < 70 || f > 1100 ? double.NaN : f;
    }

    static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
        var acc = Vector<float>.Zero;
        for (int i = 0; i < va.Length; i++) acc += va[i] * vb[i];
        float s = Vector.Dot(acc, Vector<float>.One);
        for (int i = va.Length * Vector<float>.Count; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }
}

/// <summary>
/// Offline pitch tracking of a recorded take, using the same McLeod (NSDF) method as the live tuner.
/// Produces one MIDI value per hop (NaN = unvoiced / no clear pitch).
/// </summary>
public static class PitchAnalyzer
{
    const int Rate = 16000;     // 70–1100 Hz singing range; 16 kHz is plenty and keeps this fast
    const int Frame = 1024;     // 64 ms window: reliable down to ~E2
    const int Hop = 160;        // 10 ms

    public static float[] Analyze(float[] mono, int srcRate, double rmsThreshold, double clarity, out double hopSec)
    {
        hopSec = (double)Hop / Rate;
        float[] x = srcRate == Rate ? mono : Resample(mono, srcRate);
        int count = Math.Max(0, (x.Length - Frame) / Hop + 1);
        var midi = new float[count];
        Parallel.For(0, count, () => new Mpm.Workspace(Frame), (i, _, ws) =>
        {
            midi[i] = Detect(x, i * Hop, ws, rmsThreshold, clarity);
            return ws;
        }, _ => { });
        return Clean(midi);
    }

    static float[] Resample(float[] mono, int srcRate)
    {
        var src = new BufferProvider(mono, srcRate);
        var rs = new WdlResamplingSampleProvider(src, Rate);
        var outList = new List<float>((int)((long)mono.Length * Rate / srcRate) + Rate);
        var buf = new float[8192];
        int n;
        while ((n = rs.Read(buf, 0, buf.Length)) > 0) outList.AddRange(buf.AsSpan(0, n).ToArray());
        return outList.ToArray();
    }

    static float Detect(float[] x, int start, Mpm.Workspace ws, double rmsThreshold, double clarity)
    {
        double f = Mpm.Detect(x.AsSpan(start, Frame), Rate, rmsThreshold, clarity, ws);
        return double.IsNaN(f) ? float.NaN : (float)(69 + 12 * Math.Log2(f / 440.0));
    }

    /// <summary>Removes single-frame octave glitches and very short voiced blips.</summary>
    static float[] Clean(float[] m)
    {
        int n = m.Length;
        var o = new float[n];
        Span<float> win = stackalloc float[5];
        for (int i = 0; i < n; i++)
        {
            if (float.IsNaN(m[i])) { o[i] = float.NaN; continue; }
            int k = 0;
            for (int j = Math.Max(0, i - 2); j <= Math.Min(n - 1, i + 2); j++) if (!float.IsNaN(m[j])) win[k++] = m[j];
            var s = win[..k];
            s.Sort();
            o[i] = s[k / 2];
        }
        // drop voiced runs shorter than 40 ms
        for (int i = 0; i < n;)
        {
            if (float.IsNaN(o[i])) { i++; continue; }
            int j = i;
            while (j < n && !float.IsNaN(o[j])) j++;
            if (j - i < 4) for (int k = i; k < j; k++) o[k] = float.NaN;
            i = j;
        }
        return o;
    }

    sealed class BufferProvider(float[] data, int rate) : ISampleProvider
    {
        int pos;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);
        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, data.Length - pos);
            if (n <= 0) return 0;
            Array.Copy(data, pos, buffer, offset, n);
            pos += n;
            return n;
        }
    }
}
