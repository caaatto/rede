using System.Collections.Concurrent;

namespace Rede.Core.Audio;

public record AudioDeviceInfo(int Index, string Name, bool IsInput, bool IsOutput);

/// <summary>
/// Audio capture/playback engine using PortAudioSharp with Opus encoding via Concentus.
/// Captures 48kHz mono 20ms frames, encodes to Opus, decodes incoming Opus frames for playback.
/// </summary>
public class AudioEngine : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 1;
    public const int FrameDurationMs = 20;
    public const int FrameSize = SampleRate * FrameDurationMs / 1000; // 960 samples

    private Concentus.IOpusEncoder? _encoder;
    private Concentus.IOpusDecoder? _decoder;

    private PortAudioSharp.Stream? _inputStream;
    private PortAudioSharp.Stream? _outputStream;

    private readonly ConcurrentQueue<byte[]> _playbackQueue = new();
    private volatile bool _running;
    private volatile bool _muted;

    // Echo cancellation: store last output frame for spectral subtraction
    private short[]? _lastOutputFrame;

    // Audio settings
    private volatile float _inputVolume = 1.0f;   // 0.0 - 2.0
    private volatile float _outputVolume = 1.0f;   // 0.0 - 2.0
    private volatile float _noiseGateThreshold = 0.0f; // 0.0 = off, 0.01-0.1 typical
    private volatile bool _noiseSuppression;
    private volatile bool _autoInputSensitivity = true;
    private volatile bool _autoGainControl;
    private volatile bool _echoCancellation = true;
    private int _selectedInputDevice = -1;  // -1 = system default
    private int _selectedOutputDevice = -1; // -1 = system default

    private RNNoise? _rnnoise;

    // Auto input sensitivity: adaptive noise gate based on ambient noise floor
    private double _ambientNoiseFloor = 0.005;  // starts low, adapts upward
    private int _silentFrameCount;
    private const int AdaptionFrames = 50; // ~1s of silence to adapt

    // AGC state
    private float _agcGain = 1.0f;
    private const float AgcTargetRms = 0.15f; // target RMS level (normalized)
    private const float AgcMaxGain = 4.0f;
    private const float AgcMinGain = 0.25f;
    private const float AgcAttack = 0.01f;   // fast response
    private const float AgcRelease = 0.002f; // slow release

    // Current input level (for UI meter)
    private volatile float _currentInputLevelDb = -100f;

    /// <summary>Current mic input level in dB (for UI level meter). Range: -100 to 0.</summary>
    public float CurrentInputLevelDb => _currentInputLevelDb;

    /// <summary>
    /// Fired when an encoded Opus frame is ready to send.
    /// </summary>
    public event Action<byte[]>? OnEncodedFrame;

    public bool IsMuted
    {
        get => _muted;
        set => _muted = value;
    }

    public float InputVolume
    {
        get => _inputVolume;
        set => _inputVolume = Math.Clamp(value, 0f, 2f);
    }

    public float OutputVolume
    {
        get => _outputVolume;
        set => _outputVolume = Math.Clamp(value, 0f, 2f);
    }

    public float NoiseGateThreshold
    {
        get => _noiseGateThreshold;
        set => _noiseGateThreshold = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>
    /// Enable/disable RNNoise noise suppression. Only works if native library is available.
    /// </summary>
    public bool NoiseSuppression
    {
        get => _noiseSuppression;
        set
        {
            _noiseSuppression = value;
            if (value && _rnnoise is null && RNNoise.IsAvailable)
            {
                try { _rnnoise = new RNNoise(); } catch { _rnnoise = null; }
            }
            else if (!value)
            {
                _rnnoise?.Dispose();
                _rnnoise = null;
            }
        }
    }

    /// <summary>
    /// Automatically adjust input sensitivity based on ambient noise level.
    /// When enabled, the noise gate threshold adapts to the environment.
    /// </summary>
    public bool AutoInputSensitivity
    {
        get => _autoInputSensitivity;
        set => _autoInputSensitivity = value;
    }

    /// <summary>
    /// Automatic gain control - normalizes mic volume to a consistent level.
    /// </summary>
    public bool AutoGainControl
    {
        get => _autoGainControl;
        set => _autoGainControl = value;
    }

    /// <summary>
    /// Echo cancellation - reduces feedback from speakers picked up by mic.
    /// Simple spectral subtraction approach.
    /// </summary>
    public bool EchoCancellation
    {
        get => _echoCancellation;
        set => _echoCancellation = value;
    }

    /// <summary>
    /// Whether RNNoise native library is available on this platform.
    /// </summary>
    public static bool IsNoiseSuppressionAvailable => RNNoise.IsAvailable;

    public int SelectedInputDevice
    {
        get => _selectedInputDevice;
        set => _selectedInputDevice = value;
    }

    public int SelectedOutputDevice
    {
        get => _selectedOutputDevice;
        set => _selectedOutputDevice = value;
    }

    public bool IsRunning => _running;

    private volatile bool _monitorOnly;
    private PortAudioSharp.Stream? _monitorStream;

    /// <summary>
    /// Start input-only monitoring for the level meter (no encoding, no call).
    /// Used by Settings to show live mic level. Call StopMonitor() when leaving Settings.
    /// </summary>
    public void StartMonitor()
    {
        if (_running || _monitorOnly) return;
        try
        {
            PortAudioSharp.PortAudio.Initialize();
            var inputDev = _selectedInputDevice >= 0 ? _selectedInputDevice : PortAudioSharp.PortAudio.DefaultInputDevice;
            var inputParams = new PortAudioSharp.StreamParameters
            {
                device = inputDev,
                channelCount = Channels,
                sampleFormat = PortAudioSharp.SampleFormat.Int16,
                suggestedLatency = PortAudioSharp.PortAudio.GetDeviceInfo(inputDev).defaultLowInputLatency,
            };

            // Initialize RNNoise if enabled (for accurate level display after suppression)
            if (_noiseSuppression && _rnnoise is null && RNNoise.IsAvailable)
            {
                try { _rnnoise = new RNNoise(); } catch { _rnnoise = null; }
            }

            _monitorOnly = true;
            _monitorStream = new PortAudioSharp.Stream(
                inParams: inputParams, outParams: null,
                sampleRate: SampleRate, framesPerBuffer: (uint)FrameSize,
                streamFlags: PortAudioSharp.StreamFlags.ClipOff,
                callback: MonitorCallback, userData: IntPtr.Zero);
            _monitorStream.Start();
        }
        catch { _monitorOnly = false; }
    }

    /// <summary>Stop input monitoring (called when leaving Settings).</summary>
    public void StopMonitor()
    {
        _monitorOnly = false;
        try { _monitorStream?.Stop(); } catch { }
        try { _monitorStream?.Dispose(); } catch { }
        _monitorStream = null;
        _currentInputLevelDb = -100f;
        try { PortAudioSharp.PortAudio.Terminate(); } catch { }
    }

    private PortAudioSharp.StreamCallbackResult MonitorCallback(
        IntPtr input, IntPtr output, uint frameCount,
        ref PortAudioSharp.StreamCallbackTimeInfo timeInfo,
        PortAudioSharp.StreamCallbackFlags statusFlags, IntPtr userData)
    {
        if (!_monitorOnly) return PortAudioSharp.StreamCallbackResult.Complete;
        try
        {
            var pcm = new short[frameCount];
            System.Runtime.InteropServices.Marshal.Copy(input, pcm, 0, (int)frameCount);

            // Apply input volume
            var vol = _inputVolume;
            if (vol != 1.0f)
            {
                for (int i = 0; i < pcm.Length; i++)
                    pcm[i] = (short)Math.Clamp(pcm[i] * vol, short.MinValue, short.MaxValue);
            }

            // RNNoise if enabled
            if (_noiseSuppression && _rnnoise is not null)
            {
                try { _rnnoise.ProcessFrame(pcm, pcm.Length); } catch { }
            }

            // Compute RMS for level meter
            double sumSq = 0;
            for (int i = 0; i < pcm.Length; i++)
                sumSq += (double)pcm[i] * pcm[i];
            double rms = Math.Sqrt(sumSq / pcm.Length) / 32768.0;
            _currentInputLevelDb = rms > 0 ? (float)Math.Max(-100.0, 20.0 * Math.Log10(rms)) : -100f;
        }
        catch { }
        return PortAudioSharp.StreamCallbackResult.Continue;
    }

    /// <summary>
    /// List all available audio devices. Call before Start().
    /// </summary>
    public static List<AudioDeviceInfo> GetDevices()
    {
        var devices = new List<AudioDeviceInfo>();
        try
        {
            PortAudioSharp.PortAudio.Initialize();

            // PortAudio enumerates every physical device once PER host API (Windows: MME,
            // DirectSound, WASAPI, WDM-KS; Linux: ALSA, Pulse, JACK), which floods the picker
            // with duplicates and odd virtual entries. Restrict to the default host API — the one
            // the OS default device belongs to — so each device appears exactly once.
            int defaultHostApi = -1;
            try
            {
                int defIn = PortAudioSharp.PortAudio.DefaultInputDevice;
                int defOut = PortAudioSharp.PortAudio.DefaultOutputDevice;
                if (defIn >= 0 && defIn != PortAudioSharp.PortAudio.NoDevice)
                    defaultHostApi = PortAudioSharp.PortAudio.GetDeviceInfo(defIn).hostApi;
                else if (defOut >= 0 && defOut != PortAudioSharp.PortAudio.NoDevice)
                    defaultHostApi = PortAudioSharp.PortAudio.GetDeviceInfo(defOut).hostApi;
            }
            catch { }

            int count = PortAudioSharp.PortAudio.DeviceCount;
            var seen = new HashSet<string>();
            for (int i = 0; i < count; i++)
            {
                var info = PortAudioSharp.PortAudio.GetDeviceInfo(i);
                if (info.maxInputChannels <= 0 && info.maxOutputChannels <= 0) continue;
                // Only the default host API (unless we couldn't determine it, then keep all).
                if (defaultHostApi >= 0 && info.hostApi != defaultHostApi) continue;
                // De-dup safety net by name + direction.
                var key = $"{info.name}|{info.maxInputChannels > 0}|{info.maxOutputChannels > 0}";
                if (!seen.Add(key)) continue;
                devices.Add(new AudioDeviceInfo(i, info.name,
                    info.maxInputChannels > 0, info.maxOutputChannels > 0));
            }
            PortAudioSharp.PortAudio.Terminate();
        }
        catch { }
        return devices;
    }

    public void Start()
    {
        if (_running) return;

        PortAudioSharp.PortAudio.Initialize();

        _encoder = Concentus.OpusCodecFactory.CreateEncoder(SampleRate, Channels, Concentus.Enums.OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = 96000;
        _encoder.UseInbandFEC = true;
        _encoder.UseDTX = true;
        _encoder.Complexity = 8;

        _decoder = Concentus.OpusCodecFactory.CreateDecoder(SampleRate, Channels);

        var inputDev = _selectedInputDevice >= 0 ? _selectedInputDevice : PortAudioSharp.PortAudio.DefaultInputDevice;
        var outputDev = _selectedOutputDevice >= 0 ? _selectedOutputDevice : PortAudioSharp.PortAudio.DefaultOutputDevice;

        var inputParams = new PortAudioSharp.StreamParameters
        {
            device = inputDev,
            channelCount = Channels,
            sampleFormat = PortAudioSharp.SampleFormat.Int16,
            suggestedLatency = PortAudioSharp.PortAudio.GetDeviceInfo(inputDev).defaultLowInputLatency,
        };

        var outputParams = new PortAudioSharp.StreamParameters
        {
            device = outputDev,
            channelCount = Channels,
            sampleFormat = PortAudioSharp.SampleFormat.Int16,
            suggestedLatency = PortAudioSharp.PortAudio.GetDeviceInfo(outputDev).defaultLowOutputLatency,
        };

        // Initialize RNNoise if enabled
        if (_noiseSuppression && _rnnoise is null && RNNoise.IsAvailable)
        {
            try { _rnnoise = new RNNoise(); } catch { _rnnoise = null; }
        }

        _running = true;

        _inputStream = new PortAudioSharp.Stream(
            inParams: inputParams, outParams: null,
            sampleRate: SampleRate, framesPerBuffer: (uint)FrameSize,
            streamFlags: PortAudioSharp.StreamFlags.ClipOff,
            callback: InputCallback, userData: IntPtr.Zero);

        _outputStream = new PortAudioSharp.Stream(
            inParams: null, outParams: outputParams,
            sampleRate: SampleRate, framesPerBuffer: (uint)FrameSize,
            streamFlags: PortAudioSharp.StreamFlags.ClipOff,
            callback: OutputCallback, userData: IntPtr.Zero);

        _inputStream.Start();
        _outputStream.Start();
    }

    public void Stop()
    {
        _running = false;

        try { _inputStream?.Stop(); } catch { }
        try { _outputStream?.Stop(); } catch { }
        try { _inputStream?.Dispose(); } catch { }
        try { _outputStream?.Dispose(); } catch { }
        _inputStream = null;
        _outputStream = null;

        _encoder = null;
        _decoder = null;

        _rnnoise?.Dispose();
        _rnnoise = null;

        try { PortAudioSharp.PortAudio.Terminate(); } catch { }
    }

    /// <summary>
    /// Queue an incoming encoded Opus frame for playback.
    /// </summary>
    public void QueuePlayback(byte[] opusFrame)
    {
        // Limit queue to ~500ms to prevent growing delay
        while (_playbackQueue.Count > 25)
            _playbackQueue.TryDequeue(out _);
        _playbackQueue.Enqueue(opusFrame);
    }

    private PortAudioSharp.StreamCallbackResult InputCallback(
        IntPtr input, IntPtr output, uint frameCount,
        ref PortAudioSharp.StreamCallbackTimeInfo timeInfo,
        PortAudioSharp.StreamCallbackFlags statusFlags, IntPtr userData)
    {
        if (!_running) return PortAudioSharp.StreamCallbackResult.Complete;
        if (_muted || _encoder is null) return PortAudioSharp.StreamCallbackResult.Continue;

        try
        {
            var pcm = new short[frameCount];
            System.Runtime.InteropServices.Marshal.Copy(input, pcm, 0, (int)frameCount);

            // Apply input volume
            var vol = _inputVolume;
            if (vol != 1.0f)
            {
                for (int i = 0; i < pcm.Length; i++)
                    pcm[i] = (short)Math.Clamp(pcm[i] * vol, short.MinValue, short.MaxValue);
            }

            // Compute RMS for metering, gating, and AGC
            double sumSq = 0;
            for (int i = 0; i < pcm.Length; i++)
                sumSq += (double)pcm[i] * pcm[i];
            double rms = Math.Sqrt(sumSq / pcm.Length) / 32768.0;

            // Update dB meter for UI (clamp to -100..0)
            _currentInputLevelDb = rms > 0 ? (float)Math.Max(-100.0, 20.0 * Math.Log10(rms)) : -100f;

            // Auto input sensitivity: adapt noise floor to ambient noise
            if (_autoInputSensitivity)
            {
                if (rms < _ambientNoiseFloor * 1.5)
                {
                    _silentFrameCount++;
                    if (_silentFrameCount > AdaptionFrames)
                        _ambientNoiseFloor = _ambientNoiseFloor * 0.95 + rms * 0.05;
                }
                else
                {
                    _silentFrameCount = 0;
                }
                // Gate at 2x ambient floor
                if (rms < _ambientNoiseFloor * 2.0)
                    return PortAudioSharp.StreamCallbackResult.Continue;
            }
            else
            {
                // Manual noise gate
                var gate = _noiseGateThreshold;
                if (gate > 0f && rms < gate)
                    return PortAudioSharp.StreamCallbackResult.Continue;
            }

            // AGC: normalize mic volume to consistent level
            if (_autoGainControl && rms > 0.001)
            {
                float targetGain = (float)(AgcTargetRms / rms);
                targetGain = Math.Clamp(targetGain, AgcMinGain, AgcMaxGain);
                float rate = targetGain > _agcGain ? AgcAttack : AgcRelease;
                _agcGain += (targetGain - _agcGain) * rate;
                if (_agcGain != 1.0f)
                {
                    for (int i = 0; i < pcm.Length; i++)
                        pcm[i] = (short)Math.Clamp(pcm[i] * _agcGain, short.MinValue, short.MaxValue);
                }
            }

            // Echo cancellation: subtract scaled output signal from input
            if (_echoCancellation && _lastOutputFrame is not null)
            {
                var echoRef = _lastOutputFrame;
                int echoLen = Math.Min(pcm.Length, echoRef.Length);
                for (int i = 0; i < echoLen; i++)
                    pcm[i] = (short)Math.Clamp(pcm[i] - echoRef[i] * 0.3, short.MinValue, short.MaxValue);
            }

            // RNNoise: neural noise suppression (after volume + gate + AGC + echo cancel, before encode)
            if (_noiseSuppression && _rnnoise is not null)
            {
                try { _rnnoise.ProcessFrame(pcm, pcm.Length); } catch { }
            }

            var encoded = new byte[4000]; // Max Opus frame
            int len = _encoder.Encode(pcm.AsSpan(), (int)frameCount, encoded.AsSpan(), encoded.Length);
            if (len > 0)
            {
                var frame = new byte[len];
                Buffer.BlockCopy(encoded, 0, frame, 0, len);
                OnEncodedFrame?.Invoke(frame);
            }
        }
        catch { }

        return PortAudioSharp.StreamCallbackResult.Continue;
    }

    private PortAudioSharp.StreamCallbackResult OutputCallback(
        IntPtr input, IntPtr output, uint frameCount,
        ref PortAudioSharp.StreamCallbackTimeInfo timeInfo,
        PortAudioSharp.StreamCallbackFlags statusFlags, IntPtr userData)
    {
        if (!_running)
            return PortAudioSharp.StreamCallbackResult.Complete;

        var pcm = new short[frameCount];

        if (_decoder is not null && _playbackQueue.TryDequeue(out var opusFrame))
        {
            try
            {
                _decoder.Decode(opusFrame.AsSpan(), pcm.AsSpan(), (int)frameCount, false);
            }
            catch
            {
                Array.Clear(pcm); // Silence on decode error
            }
        }

        // Apply output volume
        var outVol = _outputVolume;
        if (outVol != 1.0f)
        {
            for (int i = 0; i < pcm.Length; i++)
                pcm[i] = (short)Math.Clamp(pcm[i] * outVol, short.MinValue, short.MaxValue);
        }

        // Store output frame for echo cancellation reference
        if (_echoCancellation)
            _lastOutputFrame = (short[])pcm.Clone();

        System.Runtime.InteropServices.Marshal.Copy(pcm, 0, output, (int)frameCount);
        return PortAudioSharp.StreamCallbackResult.Continue;
    }

    public void Dispose()
    {
        Stop();
    }
}
