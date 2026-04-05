using System.Runtime.InteropServices;

namespace Rede.Core.Crypto;

/// <summary>
/// Best-effort OS-level memory locking for long-lived secrets (passphrase,
/// cached scrypt key). Pins the managed byte[] so the GC can't move it, then
/// asks the kernel not to swap the backing page to disk.
///
/// This is defense-in-depth against swap/hibernation leakage — it does NOT
/// protect against a local attacker with ptrace/memory-read capability. The
/// lock silently no-ops if the OS refuses (e.g. RLIMIT_MEMLOCK exceeded in a
/// container); callers should still ZeroMemory the buffer on disposal.
///
/// Page granularity: mlock/VirtualLock round up to the OS page size (typically
/// 4096 bytes), so even a 32-byte secret pins a whole page. Keep the number of
/// concurrently locked buffers small.
/// </summary>
public static class SecureMemory
{
    [DllImport("libc", SetLastError = true)]
    private static extern int mlock(IntPtr addr, nuint len);

    [DllImport("libc", SetLastError = true)]
    private static extern int munlock(IntPtr addr, nuint len);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualLock(IntPtr lpAddress, nuint dwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualUnlock(IntPtr lpAddress, nuint dwSize);

    /// <summary>
    /// Pin <paramref name="buffer"/> and ask the kernel not to swap it. The
    /// returned handle must be disposed to release the pin; ZeroMemory the
    /// buffer BEFORE disposal so the last content on the physical frame is
    /// zeros.
    /// </summary>
    public static SecureHandle Lock(byte[] buffer)
    {
        if (buffer.Length == 0)
            return new SecureHandle(default, IntPtr.Zero, 0, false);

        var gch = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        var addr = gch.AddrOfPinnedObject();
        var len = (nuint)buffer.Length;
        bool locked;
        try
        {
            locked = OperatingSystem.IsWindows()
                ? VirtualLock(addr, len)
                : mlock(addr, len) == 0;
        }
        catch
        {
            locked = false;
        }
        return new SecureHandle(gch, addr, len, locked);
    }

    public sealed class SecureHandle : IDisposable
    {
        private GCHandle _gch;
        private readonly IntPtr _addr;
        private readonly nuint _len;
        private readonly bool _locked;
        private bool _disposed;

        internal SecureHandle(GCHandle gch, IntPtr addr, nuint len, bool locked)
        {
            _gch = gch;
            _addr = addr;
            _len = len;
            _locked = locked;
        }

        /// <summary>True if the kernel accepted the lock request.</summary>
        public bool IsLocked => _locked;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_locked && _addr != IntPtr.Zero)
            {
                try
                {
                    if (OperatingSystem.IsWindows()) VirtualUnlock(_addr, _len);
                    else munlock(_addr, _len);
                }
                catch { }
            }
            if (_gch.IsAllocated) _gch.Free();
        }
    }
}
