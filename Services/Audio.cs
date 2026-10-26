using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Dsp;
using NAudio.Wave;

namespace Isle.Services;

/// Live spectrum of whatever Windows is playing (WASAPI loopback → FFT → log bands with auto-gain).
public sealed class AudioSpectrum
{
    public const int Bands = 12;
    const int FftSize = 2048, FftPow = 11;

    WasapiLoopbackCapture _cap;
    readonly float[] _ring = new float[FftSize];
    int _ringPos, _sinceFft;
    readonly Complex[] _fft = new Complex[FftSize];
    readonly float[] _bands = new float[Bands];
    readonly float[] _peak = Enumerable.Repeat(-60f, Bands).ToArray();
    readonly object _lock = new();
    int _channels = 2, _rate = 48000;
    DateTime _lastData;

    public bool Running => _cap != null;
    /// True when audio packets arrived recently (loopback sends nothing during silence).
    public bool HasSignal => (DateTime.UtcNow - _lastData).TotalMilliseconds < 250;

    DateTime _retryAt;

    public void Start()
    {
        if (_cap != null || DateTime.UtcNow < _retryAt) return;
        try
        {
            _cap = new WasapiLoopbackCapture();
            _channels = _cap.WaveFormat.Channels;
            _rate = _cap.WaveFormat.SampleRate;
            bool isFloat = _cap.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat || _cap.WaveFormat.BitsPerSample == 32;
            _cap.DataAvailable += (_, e) => OnData(e.Buffer, e.BytesRecorded, isFloat);
            var self = _cap;
            _cap.RecordingStopped += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { if (_cap == self) Stop(); });
            _cap.StartRecording();
        }
        catch (Exception ex)
        {
            AppHost.Log("Loopback start: " + ex.Message);
            try { _cap?.Dispose(); } catch { }
            _cap = null;
            _retryAt = DateTime.UtcNow.AddSeconds(10); // e.g. another app holds the device in exclusive mode
        }
    }

    public void Stop()
    {
        var c = _cap;
        _cap = null;
        if (c == null) return;
        try { c.StopRecording(); c.Dispose(); } catch { }
        lock (_lock) Array.Clear(_bands);
    }

    public void Restart() { Stop(); Start(); }

    void OnData(byte[] buf, int count, bool isFloat)
    {
        int bytesPerSample = isFloat ? 4 : 2;
        int frame = bytesPerSample * _channels;
        for (int i = 0; i + frame <= count; i += frame)
        {
            float sum = 0;
            for (int c = 0; c < _channels; c++)
                sum += isFloat ? BitConverter.ToSingle(buf, i + c * 4) : BitConverter.ToInt16(buf, i + c * 2) / 32768f;
            _ring[_ringPos] = sum / _channels;
            _ringPos = (_ringPos + 1) % FftSize;
            if (++_sinceFft >= FftSize / 4) { _sinceFft = 0; Analyze(); }
        }
        _lastData = DateTime.UtcNow;
    }

    void Analyze()
    {
        for (int i = 0; i < FftSize; i++)
        {
            _fft[i].X = _ring[(_ringPos + i) % FftSize] * (float)FastFourierTransform.HannWindow(i, FftSize);
            _fft[i].Y = 0;
        }
        FastFourierTransform.FFT(true, FftPow, _fft);
        double binHz = (double)_rate / FftSize;
        double lo = 50, hi = 14000;
        var result = new float[Bands];
        for (int b = 0; b < Bands; b++)
        {
            double f0 = lo * Math.Pow(hi / lo, (double)b / Bands), f1 = lo * Math.Pow(hi / lo, (double)(b + 1) / Bands);
            int i0 = Math.Max(1, (int)(f0 / binHz)), i1 = Math.Max(i0 + 1, (int)(f1 / binHz));
            double acc = 0;
            for (int i = i0; i < i1 && i < FftSize / 2; i++) acc += Math.Sqrt(_fft[i].X * _fft[i].X + _fft[i].Y * _fft[i].Y);
            double db = 20 * Math.Log10(acc / (i1 - i0) + 1e-9);
            // Per-band auto gain in dB: instant attack, ~5 dB/s release; map the top 30 dB onto the bar height,
            // so quiet and loud tracks both dance through the full range like the iPhone waveform.
            _peak[b] = (float)Math.Max(db, _peak[b] - 0.06);
            double floor = Math.Max(_peak[b] - 30, -70);
            result[b] = (float)Math.Clamp((db - floor) / (_peak[b] - floor + 1e-6), 0, 1);
        }
        lock (_lock) Array.Copy(result, _bands, Bands);
    }

    public void Read(float[] dest)
    {
        lock (_lock) Array.Copy(_bands, dest, Math.Min(dest.Length, Bands));
        if (!HasSignal) Array.Clear(dest);
    }
}

/// Master volume of the default output device + change notifications (also used for the volume HUD).
public sealed class VolumeService : IMMNotificationClient
{
    readonly MMDeviceEnumerator _enum = new();
    MMDevice _device;
    DateTime _selfChange;

    /// (volume 0..1, muted, causedByUs) — raised on a background thread.
    public event Action<float, bool, bool> Changed;
    /// New default output device friendly name — raised on a background thread.
    public event Action<string> DefaultDeviceChanged;

    public VolumeService()
    {
        Bind();
        try { _enum.RegisterEndpointNotificationCallback(this); } catch { }
    }

    void Bind()
    {
        try
        {
            if (_device != null) { _device.AudioEndpointVolume.OnVolumeNotification -= OnNotify; _device.Dispose(); }
            _device = _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _device.AudioEndpointVolume.OnVolumeNotification += OnNotify;
        }
        catch (Exception ex) { AppHost.Log("Volume bind: " + ex.Message); _device = null; }
    }

    void OnNotify(AudioVolumeNotificationData d) =>
        Changed?.Invoke(d.MasterVolume, d.Muted, (DateTime.UtcNow - _selfChange).TotalMilliseconds < 400);

    public string DeviceName { get { try { return _device?.FriendlyName ?? ""; } catch { return ""; } } }

    /// The process currently producing sound on the default device (used to name opaque media-session ids).
    public System.Diagnostics.Process ActiveAudioProcess()
    {
        try
        {
            var mgr = _device?.AudioSessionManager;
            if (mgr == null) return null;
            mgr.RefreshSessions();
            var sessions = mgr.Sessions;
            int me = Environment.ProcessId;
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s.IsSystemSoundsSession || s.State != AudioSessionState.AudioSessionStateActive) continue;
                int pid = (int)s.GetProcessID;
                if (pid == 0 || pid == me) continue;
                var p = System.Diagnostics.Process.GetProcessById(pid);
                // Browsers/Electron play from child processes; walk to the one owning a window.
                var main = System.Diagnostics.Process.GetProcessesByName(p.ProcessName).FirstOrDefault(x => x.MainWindowHandle != IntPtr.Zero);
                return main ?? p;
            }
        }
        catch { }
        return null;
    }

    public float Volume
    {
        get { try { return _device?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0; } catch { return 0; } }
        set { try { _selfChange = DateTime.UtcNow; if (_device != null) _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f); } catch { } }
    }

    public bool Muted
    {
        get { try { return _device?.AudioEndpointVolume.Mute ?? false; } catch { return false; } }
        set { try { _selfChange = DateTime.UtcNow; if (_device != null) _device.AudioEndpointVolume.Mute = value; } catch { } }
    }

    // IMMNotificationClient
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string id)
    {
        if (flow != DataFlow.Render || role != Role.Multimedia) return;
        // COM objects were created on the UI (STA) thread; rebind there, never inside the callback.
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { Bind(); DefaultDeviceChanged?.Invoke(DeviceName); });
    }
    public void OnDeviceStateChanged(string id, DeviceState state) { }
    public void OnDeviceAdded(string id) { }
    public void OnDeviceRemoved(string id) { }
    public void OnPropertyValueChanged(string id, PropertyKey key) { }
}
