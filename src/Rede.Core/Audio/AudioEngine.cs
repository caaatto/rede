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

    // Audio settings
    private volatile float _inputVolume = 1.0f;   // 0.0 - 2.0
    private volatile float _outputVolume = 1.0f;   // 0.0 - 2.0
    private volatile float _noiseGateThreshold = 0.0f; // 0.0 = off, 0.01-0.1 typical
    private int _selectedInputDevice = -1;  // -1 = system default
    private int _selectedOutputDevice = -1; // -1 = system default

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

    /// <summary>
    /// List all available audio devices. Call before Start().
    /// </summary>
    public static List<AudioDeviceInfo> GetDevices()
    {
        var devices = new List<AudioDeviceInfo>();
        try
        {
            PortAudioSharp.PortAudio.Initialize();
            int count = PortAudioSharp.PortAudio.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                var info = PortAudioSharp.PortAudio.GetDeviceInfo(i);
                if (info.maxInputChannels > 0 || info.maxOutputChannels > 0)
                {
                    devices.Add(new AudioDeviceInfo(i, info.name,
                        info.maxInputChannels > 0, info.maxOutputChannels > 0));
                }
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
        _encoder.Bitrate = 24000;
        _encoder.UseInbandFEC = true;
        _encoder.UseDTX = true;
        _encoder.Complexity = 5;

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

            // Noise gate: if RMS below threshold, send silence
            var gate = _noiseGateThreshold;
            if (gate > 0f)
            {
                double sumSq = 0;
                for (int i = 0; i < pcm.Length; i++)
                    sumSq += (double)pcm[i] * pcm[i];
                double rms = Math.Sqrt(sumSq / pcm.Length) / 32768.0;
                if (rms < gate)
                    return PortAudioSharp.StreamCallbackResult.Continue; // Drop silent frame
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

        System.Runtime.InteropServices.Marshal.Copy(pcm, 0, output, (int)frameCount);
        return PortAudioSharp.StreamCallbackResult.Continue;
    }

    public void Dispose()
    {
        Stop();
    }
}
