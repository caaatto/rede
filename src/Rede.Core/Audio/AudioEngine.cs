using System.Collections.Concurrent;

namespace Rede.Core.Audio;

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

    /// <summary>
    /// Fired when an encoded Opus frame is ready to send.
    /// </summary>
    public event Action<byte[]>? OnEncodedFrame;

    public bool IsMuted
    {
        get => _muted;
        set => _muted = value;
    }

    public bool IsRunning => _running;

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

        var inputParams = new PortAudioSharp.StreamParameters
        {
            device = PortAudioSharp.PortAudio.DefaultInputDevice,
            channelCount = Channels,
            sampleFormat = PortAudioSharp.SampleFormat.Int16,
            suggestedLatency = PortAudioSharp.PortAudio.GetDeviceInfo(PortAudioSharp.PortAudio.DefaultInputDevice).defaultLowInputLatency,
        };

        var outputParams = new PortAudioSharp.StreamParameters
        {
            device = PortAudioSharp.PortAudio.DefaultOutputDevice,
            channelCount = Channels,
            sampleFormat = PortAudioSharp.SampleFormat.Int16,
            suggestedLatency = PortAudioSharp.PortAudio.GetDeviceInfo(PortAudioSharp.PortAudio.DefaultOutputDevice).defaultLowOutputLatency,
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

        System.Runtime.InteropServices.Marshal.Copy(pcm, 0, output, (int)frameCount);
        return PortAudioSharp.StreamCallbackResult.Continue;
    }

    public void Dispose()
    {
        Stop();
    }
}
