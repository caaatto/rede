using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Rede.Desktop.Views;

public partial class BootView : UserControl
{
    private readonly TextBlock _bootText;
    private readonly ScrollViewer _scroller;
    private readonly StringBuilder _buffer = new();

    public event Action? OnBootComplete;
    public event Action<string>? OnFailComplete;

    public BootView()
    {
        AvaloniaXamlLoader.Load(this);
        _bootText = this.FindControl<TextBlock>("BootText")!;
        _scroller = this.FindControl<ScrollViewer>("BootScroller")!;
    }

    public async Task RunBootSequence(string userId, bool isNewUser, string transport, string serverUrl)
    {
        _buffer.Clear();

        // Logo animation: REDE -> glitch -> R3D#
        var logoRede = new[]
        {
            " ____   _____  ____   _____",
            "|  _ \\ | ____||  _ \\ | ____|",
            "| |_) ||  _|  | | | ||  _|  ",
            "|  _ < | |___ | |_| || |___ ",
            "|_| \\_\\|_____||____/ |_____|",
        };
        var logoR3Dh = new[]
        {
            " ____   _____  ____     _  _",
            "|  _ \\ |___ / |  _ \\  _| || |",
            "| |_) |  |_ \\ | | | ||_  ..  |",
            "|  _ <  ___) || |_| ||_      |",
            "|_| \\_\\|____/ |____/   |_||_|",
        };

        // Show REDE
        foreach (var line in logoRede)
            AppendLine(line);
        await Delay(600);

        // Glitch transition
        const string glitchChars = "@#$%&*!=+~<>/?";
        const int frames = 8;
        var logoStartLine = _buffer.ToString().Split('\n').Length - logoRede.Length - 1;

        for (var f = 0; f < frames; f++)
        {
            var progress = (double)f / frames;
            var lines = _buffer.ToString().Split('\n');

            for (var l = 0; l < logoRede.Length; l++)
            {
                var src = logoRede[l];
                var dst = logoR3Dh[l];
                var len = Math.Max(src.Length, dst.Length);
                var sb = new StringBuilder(len);
                for (var c = 0; c < len; c++)
                {
                    var srcCh = c < src.Length ? src[c] : ' ';
                    var dstCh = c < dst.Length ? dst[c] : ' ';
                    if (Random.Shared.NextDouble() < progress)
                        sb.Append(dstCh);
                    else if (Random.Shared.NextDouble() < 0.3)
                        sb.Append(glitchChars[Random.Shared.Next(glitchChars.Length)]);
                    else
                        sb.Append(srcCh);
                }
                lines[logoStartLine + l] = sb.ToString();
            }

            var glitched = string.Join('\n', lines);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _bootText.Text = glitched;
                _scroller.ScrollToEnd();
            });
            await Delay(80);
        }

        // Final R3D3
        {
            var lines = _buffer.ToString().Split('\n');
            for (var l = 0; l < logoR3Dh.Length; l++)
                lines[logoStartLine + l] = logoR3Dh[l];
            _buffer.Clear();
            _buffer.Append(string.Join('\n', lines));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _bootText.Text = _buffer.ToString();
                _scroller.ScrollToEnd();
            });
        }
        await Delay(200);

        AppendLine("");
        AppendLine("============================================");
        await TypeLine($"[{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}] SYSTEM INIT");
        AppendLine("============================================");
        AppendLine("");

        await StatusLine("CRYPTO  ", "X3DH + Double Ratchet + XSalsa20-Poly1305");
        await StatusLine("ENTROPY ", $"{RandomHex(8)} ................ OK");
        await StatusLine("KEYSTORE", "scrypt(N=2^20,r=8,p=1) ......... SEALED");
        await StatusLine("PREKEYS ", "Signed pre-key + 20 OTPKs ...... READY");

        AppendLine("");

        var isI2P = transport.Equals("i2p", StringComparison.OrdinalIgnoreCase);
        var isTor = transport.Equals("tor", StringComparison.OrdinalIgnoreCase);

        if (isI2P)
        {
            await StatusLine("NETWORK ", "I2P GARLIC ROUTING ............. INIT");
            await StatusLine("SOCKS5  ", "127.0.0.1:14447 ................ BOUND");
            await StatusLine("TUNNELS ", "in=3 out=3 hops=3 ............. BUILD");
        }
        else if (isTor)
        {
            await StatusLine("NETWORK ", "TOR ONION ROUTING .............. INIT");
            await StatusLine("SOCKS5  ", "127.0.0.1:9050 ................ BOUND");
            await StatusLine("CIRCUIT ", "3-hop circuit .................. BUILD");
        }
        else
        {
            await StatusLine("NETWORK ", "WSS/TLS DIRECT ................ INIT");
            await StatusLine("TLS     ", "ECDHE-P256 + AES-256-GCM ...... HANDSHAKE");
        }

        var displayUrl = serverUrl.Length > 40 ? serverUrl[..40] + "..." : serverUrl;
        await StatusLine("ENDPOINT", displayUrl);

        AppendLine("");

        if (isNewUser)
        {
            await StatusLine("IDENTITY", "GENERATING NEW KEYPAIR ......... WAIT");
            await HexDump(3);
            await StatusLine("REGISTER", $"{userId} .................. NEW");
        }
        else
        {
            await StatusLine("IDENTITY", $"{userId} .............. DECRYPT");
            await HexDump(2);
            await StatusLine("AUTH    ", "CHALLENGE-RESPONSE ............. SIGN");
        }

        AppendLine("");
        AppendLine("[!] NO LOGS  [!] NO METADATA  [!] NO TRACES");
        AppendLine("[!] E2EE + PERFECT FORWARD SECRECY");

        AppendLine("");
        AppendLine("============================================");
        await TypeLine(">> SYSTEM READY :: ENTERING SECURE CHANNEL <<");
        AppendLine("============================================");

        await Delay(600);
        OnBootComplete?.Invoke();
    }

    public async Task RunFailSequence(string error)
    {
        AppendLine("");
        AppendLine("============================================");
        await TypeLine(">> ABORT :: CONNECTION FAILED <<");
        AppendLine("============================================");
        AppendLine("");
        await StatusLine("ERROR   ", error);
        AppendLine("");

        // Glitch the screen
        var glitchChars = "@#$%&*!=+~<>/?|\\";
        var currentText = _buffer.ToString();
        for (var f = 0; f < 6; f++)
        {
            var chars = currentText.ToCharArray();
            var corruptions = 20 + Random.Shared.Next(30);
            for (var i = 0; i < corruptions; i++)
            {
                var pos = Random.Shared.Next(chars.Length);
                if (chars[pos] != '\n' && chars[pos] != ' ')
                    chars[pos] = glitchChars[Random.Shared.Next(glitchChars.Length)];
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _bootText.Text = new string(chars);
            });
            await Delay(60);
        }

        // Restore then fade
        await Dispatcher.UIThread.InvokeAsync(() => _bootText.Text = currentText);
        await Delay(800);
        OnFailComplete?.Invoke(error);
    }

    private void AppendLine(string text)
    {
        _buffer.AppendLine(text);
        Dispatcher.UIThread.Post(Render);
    }

    /// <summary>Paint the buffer — unless the security gate overlay currently owns the screen
    /// (boot writes still accumulate in _buffer; the gate's decrypt reveals them at the end).</summary>
    private void Render()
    {
        if (_gated) return;
        _bootText.Text = _buffer.ToString();
        _scroller.ScrollToEnd();
    }

    private async Task TypeLine(string text)
    {
        var line = new StringBuilder();
        foreach (var ch in text)
        {
            line.Append(ch);
            var current = _buffer + line.ToString();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_gated) return;
                _bootText.Text = current;
                _scroller.ScrollToEnd();
            });
            await Delay(2);
        }
        _buffer.AppendLine(line.ToString());
        Dispatcher.UIThread.Post(Render);
    }

    private async Task StatusLine(string label, string value)
    {
        var dots = new StringBuilder();
        var dotCount = 2 + Random.Shared.Next(3);
        for (var i = 0; i < dotCount; i++)
        {
            dots.Append('.');
            await Delay(40 + Random.Shared.Next(80));
        }
        AppendLine($"  [{label}] {dots} {value}");
    }

    private async Task HexDump(int lines)
    {
        for (var i = 0; i < lines; i++)
        {
            var addr = (i * 16).ToString("x8");
            var hex = RandomHex(16);
            var formatted = string.Join(" ", Split(hex, 2));
            AppendLine($"  {addr}  {formatted}");
            await Delay(20);
        }
    }

    private static string RandomHex(int bytes)
    {
        var buf = new byte[bytes];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToHexString(buf).ToLowerInvariant();
    }

    private static string[] Split(string s, int chunkSize)
    {
        var count = (s.Length + chunkSize - 1) / chunkSize;
        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            var start = i * chunkSize;
            var len = Math.Min(chunkSize, s.Length - start);
            result[i] = s.Substring(start, len);
        }
        return result;
    }

    /// <summary>
    /// Update queue position display during boot. Called from MainWindow when server is full.
    /// </summary>
    public void UpdateQueueStatus(int position, int total)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Replace or append queue line
            var queueLine = $"  [QUEUE   ] ... Position {position}/{total} - waiting for slot";
            var text = _buffer.ToString();
            var idx = text.IndexOf("  [QUEUE   ]", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var end = text.IndexOf('\n', idx);
                if (end < 0) end = text.Length;
                _buffer.Remove(idx, end - idx);
                _buffer.Insert(idx, queueLine);
            }
            else
            {
                _buffer.AppendLine(queueLine);
            }
            _bootText.Text = _buffer.ToString();
            _scroller.ScrollToEnd();
        });
    }

    public void ShowQueueAdmitted()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var text = _buffer.ToString();
            var idx = text.IndexOf("  [QUEUE   ]", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var end = text.IndexOf('\n', idx);
                if (end < 0) end = text.Length;
                _buffer.Remove(idx, end - idx);
                _buffer.Insert(idx, "  [QUEUE   ] ... ADMITTED - connecting");
            }
            _bootText.Text = _buffer.ToString();
            _scroller.ScrollToEnd();
        });
    }

    // ---- Server-2FA security gate (the SECOND touch) -------------------------------------------
    // The first touch unlocked the local profile; the server then demands a hardware assertion
    // before AUTH_OK. That happens while THIS boot view is on screen, so we take the display over
    // with an ASCII "encryption" lock animation, hold it until the user taps the key, then play a
    // matrix-style decrypt back into the normal boot log before the app enters. While _gated is set
    // the normal boot writes keep filling _buffer but don't paint — the gate owns the screen.

    private bool _gated;
    private TaskCompletionSource? _gateDone;        // completes when the decrypt animation finishes
    private TaskCompletionSource<bool>? _unlock;    // signalled by ResolveSecurityGate(ok)

    /// <summary>Awaited by MainWindow before swapping to the main view, so the decrypt plays fully.</summary>
    public Task SecurityGateTask => _gateDone?.Task ?? Task.CompletedTask;

    private static readonly string[] LockLocked =
    {
        "    _____",
        "   /     \\",
        "  |       |",
        "  |       |",
        " _|_______|_",
        "|           |",
        "|    ___    |",
        "|   |   |   |",
        "|   |___|   |",
        "|     |     |",
        "|___________|",
    };

    private static readonly string[] LockOpen =
    {
        "    _____",
        "   /     \\",
        "   \\      |",
        "          |",
        " _________|_",
        "|           |",
        "|    ___    |",
        "|   |   |   |",
        "|   |_ _|   |",
        "|     o     |",
        "|___________|",
    };

    /// <summary>Begin the encryption lock overlay and wait for the second touch.</summary>
    public void BeginSecurityGate() => Dispatcher.UIThread.Post(() =>
    {
        if (_gated) return;
        _unlock = new TaskCompletionSource<bool>();
        _gateDone = new TaskCompletionSource();
        _ = RunSecurityGate();
    });

    /// <summary>Touch done: ok=true plays the unlock + decrypt; ok=false drops the overlay (caller
    /// then runs the fail sequence).</summary>
    public void ResolveSecurityGate(bool ok) => Dispatcher.UIThread.Post(() => _unlock?.TrySetResult(ok));

    private async Task RunSecurityGate()
    {
        var snapshot = _buffer.ToString();
        _gated = true;
        var tick = 0;

        // 1) Locked + encryption rain, looping until the key is tapped (or the assertion fails).
        while (!(_unlock?.Task.IsCompleted ?? true))
        {
            var frame = ComposeGateFrame(LockLocked, "AWAITING HARDWARE SECURITY KEY", blinkOn: (tick / 4) % 2 == 0);
            await Dispatcher.UIThread.InvokeAsync(() => { _bootText.Text = frame; _scroller.ScrollToEnd(); });
            await Delay(90);
            tick++;
        }

        var ok = _unlock?.Task.Result ?? false;
        if (ok)
        {
            // 2) Shackle springs open.
            for (var i = 0; i < 3; i++)
            {
                var frame = ComposeGateFrame(LockOpen, "ACCESS GRANTED — DECRYPTING", blinkOn: true);
                await Dispatcher.UIThread.InvokeAsync(() => _bootText.Text = frame);
                await Delay(130);
            }
            // 3) Matrix decrypt: scramble resolves back into the real boot log.
            var target = snapshot.Replace("\r", "").Split('\n');
            const int dframes = 12;
            for (var f = 0; f <= dframes; f++)
            {
                var display = DecryptFrame(target, (double)f / dframes);
                await Dispatcher.UIThread.InvokeAsync(() => { _bootText.Text = display; _scroller.ScrollToEnd(); });
                await Delay(55);
            }
        }

        _gated = false;
        await Dispatcher.UIThread.InvokeAsync(() => { _bootText.Text = _buffer.ToString(); _scroller.ScrollToEnd(); });
        _gateDone?.TrySetResult();
    }

    private static string ComposeGateFrame(string[] lockArt, string caption, bool blinkOn)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("            >> SESSION ENCRYPTED <<");
        sb.AppendLine("      " + caption);
        sb.AppendLine();
        foreach (var l in lockArt)
            sb.AppendLine("            " + l);
        sb.AppendLine();
        for (var r = 0; r < 3; r++)
            sb.AppendLine("      " + RainLine(30));
        sb.AppendLine();
        sb.AppendLine(blinkOn ? "            [ TOUCH KEY TO DECRYPT ]" : "");
        return sb.ToString();
    }

    private static string RainLine(int len)
    {
        const string chars = "01010110:.x#$%/\\<>";
        var sb = new StringBuilder(len);
        for (var i = 0; i < len; i++)
            sb.Append(chars[Random.Shared.Next(chars.Length)]);
        return sb.ToString();
    }

    private static string DecryptFrame(string[] target, double progress)
    {
        const string glitch = "01@#$%&*!=+~<>/?";
        var sb = new StringBuilder();
        foreach (var line in target)
        {
            foreach (var ch in line)
            {
                if (ch == ' ') { sb.Append(' '); continue; }
                sb.Append(Random.Shared.NextDouble() < progress ? ch : glitch[Random.Shared.Next(glitch.Length)]);
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static Task Delay(int ms) => Task.Delay(ms);
}
