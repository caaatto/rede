using System.Reflection;
using System.Runtime.InteropServices;

namespace Rede.Core.Audio;

/// <summary>
/// P/Invoke wrapper for librnnoise — real-time noise suppression.
/// RNNoise processes 480-sample frames at 48kHz (10ms) in float32 format.
/// Gracefully degrades if native library is not available.
/// </summary>
public sealed class RNNoise : IDisposable
{
    private const int RNNoiseFrameSize = 480; // 10ms at 48kHz
    private const string LibName = "rnnoise";

    [DllImport(LibName, EntryPoint = "rnnoise_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rnnoise_create(IntPtr model);

    [DllImport(LibName, EntryPoint = "rnnoise_destroy", CallingConvention = CallingConvention.Cdecl)]
    private static extern void rnnoise_destroy(IntPtr state);

    /// <summary>
    /// Process one frame (480 float samples). Input is modified in-place.
    /// Returns voice activity probability (0.0 - 1.0).
    /// </summary>
    [DllImport(LibName, EntryPoint = "rnnoise_process_frame", CallingConvention = CallingConvention.Cdecl)]
    private static extern float rnnoise_process_frame(IntPtr state, float[] output, float[] input);

    private IntPtr _state;
    private bool _disposed;

    /// <summary>
    /// True if RNNoise native library was loaded successfully.
    /// </summary>
    public static bool IsAvailable { get; }

    static RNNoise()
    {
        // Register a custom resolver so .NET can find the lib in runtimes/{RID}/native/
        // even when the default probing paths don't cover it (e.g. single-file publish).
        NativeLibrary.SetDllImportResolver(typeof(RNNoise).Assembly, (name, asm, searchPath) =>
        {
            if (name != LibName) return IntPtr.Zero;
            // Try default resolution first
            if (NativeLibrary.TryLoad(name, asm, searchPath, out var handle))
                return handle;
            // Try explicit paths relative to the assembly location
            var asmDir = Path.GetDirectoryName(asm.Location) ?? AppContext.BaseDirectory;
            var rid = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64" : "linux-x64";
            var libFile = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "rnnoise.dll" : "librnnoise.so";
            var candidates = new[]
            {
                Path.Combine(asmDir, "runtimes", rid, "native", libFile),
                Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", libFile),
                Path.Combine(asmDir, libFile),
            };
            foreach (var path in candidates)
            {
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out handle))
                    return handle;
            }
            return IntPtr.Zero;
        });

        try
        {
            var state = rnnoise_create(IntPtr.Zero);
            if (state != IntPtr.Zero)
            {
                rnnoise_destroy(state);
                IsAvailable = true;
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch { }
    }

    public RNNoise()
    {
        if (!IsAvailable)
            throw new PlatformNotSupportedException("RNNoise native library not found");
        _state = rnnoise_create(IntPtr.Zero);
        if (_state == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create RNNoise state");
    }

    /// <summary>
    /// Process a 960-sample PCM frame (20ms at 48kHz) in-place.
    /// Splits into two 480-sample RNNoise frames internally.
    /// </summary>
    public void ProcessFrame(short[] pcm, int length)
    {
        if (_disposed || _state == IntPtr.Zero || length < RNNoiseFrameSize) return;

        // RNNoise works with float[-32768, 32768] (not normalized [-1,1])
        var floatBuf = new float[RNNoiseFrameSize];

        // Process in 480-sample chunks
        int offset = 0;
        while (offset + RNNoiseFrameSize <= length)
        {
            // Convert Int16 → float (RNNoise expects [-32768, 32768] range)
            for (int i = 0; i < RNNoiseFrameSize; i++)
                floatBuf[i] = pcm[offset + i];

            rnnoise_process_frame(_state, floatBuf, floatBuf);

            // Convert back to Int16
            for (int i = 0; i < RNNoiseFrameSize; i++)
                pcm[offset + i] = (short)Math.Clamp(floatBuf[i], short.MinValue, short.MaxValue);

            offset += RNNoiseFrameSize;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_state != IntPtr.Zero)
        {
            rnnoise_destroy(_state);
            _state = IntPtr.Zero;
        }
    }
}
