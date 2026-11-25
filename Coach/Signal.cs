using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VocalTuner.Coach;

/// <summary>Audio loading and small DSP helpers for the analysis (mono, 22050 Hz).</summary>
public static class Sig
{
    public const int Rate = 22050;

    /// <summary>Loads any audio file Windows can decode (WAV, MP3, M4A, FLAC…) as mono at <see cref="Rate"/>.</summary>
    public static float[] LoadFile(string path)
    {
        using var r = new AudioFileReader(path);
        int ch = r.WaveFormat.Channels, rate = r.WaveFormat.SampleRate;
        var all = ReadAll(r, (long)rate * ch * 60 * 20);  // cap at 20 minutes
        return Resample(Downmix(all, ch), rate);
    }

    /// <summary>Mono from interleaved audio. A silent channel (mono mic on one side of a stereo stream) is ignored.</summary>
    public static float[] Downmix(float[] all, int ch)
    {
        if (ch == 1) return all;
        int frames = all.Length / ch;
        var e = new double[ch];
        for (int i = 0; i < frames; i++) for (int c = 0; c < ch; c++) { float v = all[i * ch + c]; e[c] += v * v; }
        double max = e.Max();
        var use = e.Select(x => x >= max * 0.01).ToArray();
        int n = use.Count(u => u);
        var m = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float s = 0;
            for (int c = 0; c < ch; c++) if (use[c]) s += all[i * ch + c];
            m[i] = s / n;
        }
        return m;
    }

    public static float[] Resample(float[] mono, int srcRate)
    {
        if (srcRate == Rate) return mono;
        return ReadAll(new WdlResamplingSampleProvider(new ArrayProvider(mono, srcRate), Rate));
    }

    /// <summary>Scales so the loudest sustained part sits at a fixed level (99.9th percentile of |x| = 0.5).</summary>
    public static void Normalize(float[] x)
    {
        if (x.Length == 0) return;
        int step = Math.Max(1, x.Length / 200000);
        var a = new List<float>();
        for (int i = 0; i < x.Length; i += step) a.Add(Math.Abs(x[i]));
        a.Sort();
        float p = a[(int)(a.Count * 0.999)];
        if (p < 1e-6f) return;
        float g = 0.5f / p;
        for (int i = 0; i < x.Length; i++) x[i] *= g;
    }

    static float[] ReadAll(ISampleProvider s, long max = long.MaxValue)
    {
        var chunks = new List<float[]>();
        var buf = new float[Math.Max(4096, s.WaveFormat.SampleRate * s.WaveFormat.Channels)];
        long total = 0; int n;
        while (total < max && (n = s.Read(buf, 0, buf.Length)) > 0) { chunks.Add(buf.AsSpan(0, n).ToArray()); total += n; }
        var all = new float[total];
        long o = 0;
        foreach (var c in chunks) { Array.Copy(c, 0, all, o, c.Length); o += c.Length; }
        return all;
    }

    sealed class ArrayProvider(float[] data, int rate) : ISampleProvider
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

    // ------------------------------------------------------------------ FFT

    /// <summary>In-place radix-2 FFT (n must be a power of two).</summary>
    public static void Fft(double[] re, double[] im, bool inverse = false)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi;
                    re[a] += xr; im[a] += xi;
                    double t = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = t;
                }
            }
        }
        if (inverse) for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
    }

    static readonly Dictionary<int, double[]> hann = new();
    public static double[] Hann(int n)
    {
        lock (hann)
        {
            if (!hann.TryGetValue(n, out var w))
            {
                w = new double[n];
                for (int i = 0; i < n; i++) w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);
                hann[n] = w;
            }
            return w;
        }
    }

    // ------------------------------------------------------------------ statistics

    public static double Median(IEnumerable<double> xs) => Percentile(xs, 50);

    public static double Percentile(IEnumerable<double> xs, double p)
    {
        var a = xs.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToArray();
        if (a.Length == 0) return double.NaN;
        double pos = p / 100 * (a.Length - 1);
        int i = (int)Math.Floor(pos);
        return i + 1 < a.Length ? a[i] + (a[i + 1] - a[i]) * (pos - i) : a[i];
    }

    public static double Std(IReadOnlyList<double> xs)
    {
        if (xs.Count < 2) return 0;
        double m = xs.Average();
        return Math.Sqrt(xs.Sum(v => (v - m) * (v - m)) / (xs.Count - 1));
    }

    public static double Pearson(double[] a, double[] b)
    {
        double ma = a.Average(), mb = b.Average(), sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < a.Length; i++) { double x = a[i] - ma, y = b[i] - mb; sab += x * y; saa += x * x; sbb += y * y; }
        return sab / (Math.Sqrt(saa * sbb) + 1e-12);
    }
}
