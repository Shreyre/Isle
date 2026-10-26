using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Isle.Services;

public sealed record RadarMatch(string Title, string Artist, string Album, string CoverUrl, string ShazamUrl, string SpotifyUrl, string YouTubeUrl);

/// Identifies whatever the PC is playing: records desktop audio, fingerprints it and asks Shazam.
public static class MusicRadar
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    public static async Task<RadarMatch> IdentifyAsync(IProgress<string> status, CancellationToken ct)
    {
        status?.Report("Listening…");
        WasapiLoopbackCapture capture;
        try { capture = new WasapiLoopbackCapture(); }
        catch (Exception ex) { AppHost.Log("Radar capture: " + ex.Message); status?.Report("Audio capture unavailable"); return null; }

        var format = capture.WaveFormat;
        var buffer = new MemoryStream();
        capture.DataAvailable += (_, e) => { lock (buffer) buffer.Write(e.Buffer, 0, e.BytesRecorded); };
        try
        {
            capture.StartRecording();
            // Query early (5 s) for a fast answer, then once more with the full ~11 s window.
            foreach (int wait in new[] { 5, 6 })
            {
                await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                byte[] raw;
                lock (buffer) raw = buffer.ToArray();
                short[] pcm = To16kMono(raw, format);
                if (pcm.Length < 16000 * 2 || Rms(pcm) < 30) continue;
                status?.Report("Identifying…");
                var match = await QueryAsync(pcm, ct);
                if (match != null) return match;
                status?.Report("Listening…");
            }
            byte[] all;
            lock (buffer) all = buffer.ToArray();
            var final = To16kMono(all, format);
            status?.Report(final.Length < 16000 * 2 || Rms(final) < 30 ? "No audio playing" : "No match found");
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { AppHost.Log("Radar: " + ex.Message); status?.Report("Couldn't reach Shazam"); return null; }
        finally
        {
            try { capture.StopRecording(); } catch { }
            capture.Dispose();
        }
    }

    internal static async Task<RadarMatch> QueryAsync(short[] pcm16k, CancellationToken ct) => Parse(await PostAsync(pcm16k, ct));

    internal static async Task<string> PostAsync(short[] pcm16k, CancellationToken ct)
    {
        var (uri, ms) = ShazamSignature.Generate(pcm16k);
        string tz = TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana : "Europe/Paris";
        string body = JsonSerializer.Serialize(new
        {
            timezone = tz,
            signature = new { uri, samplems = ms },
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            context = new { },
            geolocation = new { },
        });
        string url = $"https://amp.shazam.com/discovery/v5/en/US/android/-/tag/{Guid.NewGuid().ToString().ToUpperInvariant()}/{Guid.NewGuid().ToString().ToUpperInvariant()}"
                   + "?sync=true&webv3=true&sampling=true&connected=&shazamapiversion=v3&sharehub=true&video=v3";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("User-Agent", "Dalvik/2.1.0 (Linux; U; Android 10; Pixel 4 Build/QQ3A.200805.001)");
        req.Content.Headers.TryAddWithoutValidation("Content-Language", "en_US");
        using var res = await Http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    internal static RadarMatch Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("matches", out var matches) || matches.ValueKind != JsonValueKind.Array || matches.GetArrayLength() == 0
            || !root.TryGetProperty("track", out var t)) return null;

        string title = Str(t, "title"), artist = Str(t, "subtitle");
        string cover = t.TryGetProperty("images", out var im) ? Str(im, "coverarthq") ?? Str(im, "coverart") : null;
        string album = null, spotify = null;
        if (t.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Array)
            foreach (var s in sections.EnumerateArray())
                if (s.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Array)
                    foreach (var x in md.EnumerateArray())
                        if (Str(x, "title") == "Album") album ??= Str(x, "text");
        if (t.TryGetProperty("hub", out var hub) && hub.TryGetProperty("providers", out var providers) && providers.ValueKind == JsonValueKind.Array)
            foreach (var p in providers.EnumerateArray())
                if (Str(p, "type") == "SPOTIFY" && p.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
                    foreach (var a in actions.EnumerateArray())
                        spotify ??= SpotifyWeb(Str(a, "uri"));

        string q = Uri.EscapeDataString($"{title} {artist}".Trim());
        return new RadarMatch(title, artist, album, cover, Str(t, "url"),
            spotify ?? $"https://open.spotify.com/search/{q}", $"https://www.youtube.com/results?search_query={q}");
    }

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // spotify:track:ID -> https://open.spotify.com/track/ID ; spotify:search:Q -> web search (works without the desktop app)
    static string SpotifyWeb(string uri)
    {
        if (uri == null || !uri.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = uri.Split(':', 3);
        if (parts.Length < 3) return null;
        return parts[1] == "search"
            ? "https://open.spotify.com/search/" + Uri.EscapeDataString(Uri.UnescapeDataString(parts[2]))
            : $"https://open.spotify.com/{parts[1]}/{parts[2]}";
    }

    static double Rms(short[] s)
    {
        double sum = 0;
        foreach (short v in s) sum += (double)v * v;
        return s.Length == 0 ? 0 : Math.Sqrt(sum / s.Length);
    }

    /// Decodes the capture's mix format (float or 16/24/32-bit PCM, any channel count) to 16 kHz mono s16.
    internal static short[] To16kMono(byte[] raw, WaveFormat fmt)
    {
        int ch = Math.Max(1, fmt.Channels), bytes = fmt.BitsPerSample / 8, frame = ch * bytes;
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat || (fmt is WaveFormatExtensible ext && ext.SubFormat == FloatSubFormat);
        int frames = raw.Length / frame;
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < ch; c++)
            {
                int o = f * frame + c * bytes;
                sum += isFloat ? BitConverter.ToSingle(raw, o)
                     : bytes == 2 ? BitConverter.ToInt16(raw, o) / 32768f
                     : bytes == 3 ? ((raw[o] << 8) | (raw[o + 1] << 16) | (raw[o + 2] << 24)) / 2147483648f
                     : BitConverter.ToInt32(raw, o) / 2147483648f;
            }
            mono[f] = sum / ch;
        }

        ISampleProvider src = new ArrayProvider(mono, fmt.SampleRate);
        if (fmt.SampleRate != 16000) src = new WdlResamplingSampleProvider(src, 16000);
        var output = new List<short>((int)((long)frames * 16000 / Math.Max(1, fmt.SampleRate)) + 1024);
        var buf = new float[8192];
        int n;
        while ((n = src.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < n; i++) output.Add((short)Math.Clamp(buf[i] * 32767f, -32768f, 32767f));
        return output.ToArray();
    }

    sealed class ArrayProvider : ISampleProvider
    {
        readonly float[] _data;
        int _pos;
        public ArrayProvider(float[] data, int rate) { _data = data; WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1); }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _data.Length - _pos);
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }
}

/// Shazam audio fingerprint (port of the SongRec / shazamio signature generator) for 16 kHz mono s16 audio.
public static class ShazamSignature
{
    const int Rate = 16000;
    static readonly double[] Hann = Enumerable.Range(1, 2048).Select(n => 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / 2049)).ToArray();
    static readonly int[] NeighborOffsets = { -10, -7, -4, -3, 1, 2, 5, 8 };
    static readonly int[] TimeOffsets = { -53, -45, 165, 172, 179, 186, 193, 200, 214, 221, 228, 235, 242, 249 };
    static readonly int[] SpreadOffsets = { -1, -3, -6 };

    static int Mod(int x) => ((x % 256) + 256) % 256;

    public static (string uri, int sampleMs) Generate(short[] samples)
    {
        var bands = new SortedDictionary<int, List<(int pass, int mag, int bin)>>();
        var ring = new short[2048];
        int ringPos = 0;
        var fft = new double[256][];
        var spread = new double[256][];
        for (int i = 0; i < 256; i++) { fft[i] = new double[1025]; spread[i] = new double[1025]; }
        int fftPos = 0, spreadPos = 0, spreadWritten = 0;
        var re = new double[2048];
        var im = new double[2048];

        for (int start = 0; start + 128 <= samples.Length; start += 128)
        {
            // FFT of the latest 2048 samples (oldest first), Hann-windowed
            Array.Copy(samples, start, ring, ringPos, 128);
            ringPos = (ringPos + 128) % 2048;
            for (int i = 0; i < 2048; i++) { re[i] = ring[(ringPos + i) % 2048] * Hann[i]; im[i] = 0; }
            Fft(re, im);
            var last = fft[fftPos];
            for (int k = 0; k <= 1024; k++) last[k] = Math.Max((re[k] * re[k] + im[k] * im[k]) / (1 << 17), 1e-10);
            fftPos = (fftPos + 1) % 256;

            // Peak spreading: frequency (3 bins), then time (into earlier spread outputs, chained max)
            var s = spread[spreadPos];
            Array.Copy(last, s, 1025);
            for (int p = 0; p < 1025; p++)
            {
                if (p < 1023) s[p] = Math.Max(s[p], Math.Max(s[p + 1], s[p + 2]));
                double max = s[p];
                foreach (int f in SpreadOffsets)
                {
                    var former = spread[Mod(spreadPos + f)];
                    max = former[p] = Math.Max(former[p], max);
                }
            }
            spreadPos = (spreadPos + 1) % 256;
            spreadWritten++;

            if (spreadWritten >= 46) RecognizePeaks(fft[Mod(fftPos - 46)], spread[Mod(spreadPos - 49)], spread, spreadPos, spreadWritten - 46, bands);
        }

        return (Encode(bands, samples.Length), (int)(samples.Length * 1000L / Rate));
    }

    static void RecognizePeaks(double[] f46, double[] s49, double[][] spread, int spreadPos, int fftNumber,
        SortedDictionary<int, List<(int, int, int)>> bands)
    {
        for (int bin = 10; bin < 1015; bin++)
        {
            double v = f46[bin];
            if (v < 1.0 / 64 || v < s49[bin - 1]) continue;

            double maxNeighbor = 0;
            foreach (int o in NeighborOffsets) maxNeighbor = Math.Max(s49[bin + o], maxNeighbor);
            if (v <= maxNeighbor) continue;

            double maxOther = maxNeighbor;
            foreach (int o in TimeOffsets) maxOther = Math.Max(spread[Mod(spreadPos + o)][bin - 1], maxOther);
            if (v <= maxOther) continue;

            double mag = Math.Log(Math.Max(1.0 / 64, v)) * 1477.3 + 6144;
            double before = Math.Log(Math.Max(1.0 / 64, f46[bin - 1])) * 1477.3 + 6144;
            double after = Math.Log(Math.Max(1.0 / 64, f46[bin + 1])) * 1477.3 + 6144;
            double variation1 = mag * 2 - before - after;
            double variation2 = (after - before) * 32 / variation1;
            double correctedBin = bin * 64 + variation2;
            double hz = correctedBin * (16000.0 / 2 / 1024 / 64);

            int band = hz > 250 && hz < 520 ? 0 : hz > 520 && hz < 1450 ? 1 : hz > 1450 && hz < 3500 ? 2 : hz > 3500 && hz < 5500 ? 3 : -1;
            if (band < 0) continue;
            if (!bands.TryGetValue(band, out var list)) bands[band] = list = new();
            list.Add((fftNumber, (int)mag, (int)correctedBin));
        }
    }

    static string Encode(SortedDictionary<int, List<(int pass, int mag, int bin)>> bands, int numberSamples)
    {
        var contents = new MemoryStream();
        var cw = new BinaryWriter(contents);
        foreach (var (band, peaks) in bands)
        {
            var pb = new MemoryStream();
            var pw = new BinaryWriter(pb);
            int pass = 0;
            foreach (var pk in peaks)
            {
                if (pk.pass - pass >= 255) { pw.Write((byte)0xFF); pw.Write(pk.pass); pass = pk.pass; }
                pw.Write((byte)(pk.pass - pass));
                pw.Write((ushort)pk.mag);
                pw.Write((ushort)pk.bin);
                pass = pk.pass;
            }
            pw.Flush();
            cw.Write(0x60030040 + band);
            cw.Write((int)pb.Length);
            cw.Write(pb.ToArray());
            cw.Write(new byte[(4 - pb.Length % 4) % 4]);
        }
        cw.Flush();
        byte[] body = contents.ToArray();

        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0xcafe2580u);                 // magic1
        w.Write(0u);                          // crc32 (patched below)
        w.Write((uint)(body.Length + 8));     // size minus header
        w.Write(0x94119c00u);                 // magic2
        w.Write(0u); w.Write(0u); w.Write(0u);
        w.Write(3u << 27);                    // sample rate id: 16000 Hz
        w.Write(0u); w.Write(0u);
        w.Write((uint)(numberSamples + Rate * 0.24));
        w.Write((uint)((15 << 19) + 0x40000));
        w.Write(0x40000000u);
        w.Write((uint)(body.Length + 8));
        w.Write(body);
        w.Flush();
        byte[] bin = ms.ToArray();
        BitConverter.GetBytes(Crc32(bin, 8)).CopyTo(bin, 4);
        return "data:audio/vnd.shazam.sig;base64," + Convert.ToBase64String(bin);
    }

    static uint Crc32(byte[] data, int start)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = start; i < data.Length; i++)
        {
            crc ^= data[i];
            for (int b = 0; b < 8; b++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }

    /// In-place iterative radix-2 FFT (forward).
    static void Fft(double[] re, double[] im)
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
            double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    double ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }
}
