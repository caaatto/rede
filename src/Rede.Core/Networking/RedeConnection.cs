using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rede.Core.Crypto;
using Rede.Core.Protocol;

namespace Rede.Core.Networking;

/// <summary>
/// WebSocket client with server signature verification, TOFU cert pinning, and auto-reconnect.
/// Mirrors: RedeConnection class in network.js
/// </summary>
public class RedeConnection : IDisposable
{
    private static readonly string CertPinFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rede", ".cert_pin");

    private readonly string _serverUrl;
    private readonly ProxySettings _proxySettings;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Action<JsonObject>> _handlers = new();
    private string? _pinnedCertFingerprint;

    public byte[]? ServerSigningKey { get; set; }
    public bool ShouldReconnect { get; set; } = true;
    public int ReconnectDelay { get; set; } = 2000;
    private int _reconnectAttempts;
    private int _isReconnecting; // M8: Guard against concurrent reconnect tasks (0=false, 1=true)

    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action? OnReconnecting;
    public event Action<string>? OnError;
    public event Action<int, int>? OnQueuePosition; // position, total
    public event Action? OnQueueAdmit;

    private bool _isConnected;
    public bool IsConnected => _isConnected;

    /// <summary>
    /// The active transport: "I2P", "Tor", or "Direct".
    /// </summary>
    public string Transport => _proxySettings.UseI2P ? "I2P" : (_proxySettings.UseTor ? "Tor" : "Direct");

    public RedeConnection(string serverUrl, ProxySettings? proxySettings = null)
    {
        _serverUrl = serverUrl;
        _proxySettings = proxySettings ?? new ProxySettings();

        if (_proxySettings.UseI2P)
            ReconnectDelay = 5000;

        _pinnedCertFingerprint = LoadPinnedCert();
        ValidateUrl();
    }

    private string? LoadPinnedCert()
    {
        try
        {
            if (File.Exists(CertPinFile))
            {
                var pins = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CertPinFile));
                return pins?.GetValueOrDefault(_serverUrl);
            }
        }
        catch { }
        return null;
    }

    private void SavePinnedCert(string fingerprint)
    {
        try
        {
            Dictionary<string, string> pins = new();
            if (File.Exists(CertPinFile))
            {
                var existing = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CertPinFile));
                if (existing is not null) pins = existing;
            }
            pins[_serverUrl] = fingerprint;
            var dir = Path.GetDirectoryName(CertPinFile)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(CertPinFile, JsonSerializer.Serialize(pins, new JsonSerializerOptions { WriteIndented = true }));
            // M6: Restrict cert pin file permissions on Unix
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(CertPinFile, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch { }
            }
        }
        catch { }
    }

    private void ValidateUrl()
    {
        if (_proxySettings.UseI2P && !_serverUrl.Contains(".i2p"))
            OnError?.Invoke("[WARNING] Using I2P but server address is not a .i2p address.");

        if (!_proxySettings.UseI2P && !_proxySettings.UseTor &&
            !_serverUrl.StartsWith("wss://") &&
            !_serverUrl.Contains("localhost") && !_serverUrl.Contains("127.0.0.1"))
        {
            OnError?.Invoke("[WARNING] Connecting over unencrypted WebSocket to a remote host!");
        }

        if (_proxySettings.UseTor && !_serverUrl.Contains(".onion"))
            OnError?.Invoke("[WARNING] Using Tor without .onion address. Exit node can see your traffic.");
    }

    public void On(string messageType, Action<JsonObject> handler)
    {
        _handlers.AddOrUpdate(messageType, handler, (_, _) => handler);
    }

    public async Task ConnectAsync()
    {
        // Block plain ws:// to non-localhost (unless tunneled)
        if (_serverUrl.StartsWith("ws://") && !_proxySettings.UseI2P && !_proxySettings.UseTor)
        {
            var uri = new Uri(_serverUrl);
            if (uri.Host != "localhost" && uri.Host != "127.0.0.1" && uri.Host != "::1")
                throw new InvalidOperationException("Refusing unencrypted ws:// to remote host. Use wss:// or I2P/Tor.");
        }

        // Check proxy reachability first (throws with user-friendly message)
        if (_proxySettings.UseI2P || _proxySettings.UseTor)
        {
            var proxyUrl = _proxySettings.UseI2P ? _proxySettings.I2PProxy : _proxySettings.TorProxy;
            await CheckSocksProxyAsync(proxyUrl); // throws InvalidOperationException if unreachable
        }

        // M2: Dispose previous WS and CTS to prevent resource leak on reconnect
        _cts?.Cancel();
        _cts?.Dispose();
        try { _ws?.Dispose(); } catch { }

        _cts = new CancellationTokenSource();
        _ws = new ClientWebSocket();

        // TLS cert validation via TOFU pinning
        bool TofuValidation(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
        {
            // M7: Only accept null cert for non-TLS connections (not for wss:// even through proxy)
            if (cert is null)
                return !_serverUrl.StartsWith("wss://");
            var fp = cert.GetCertHashString(HashAlgorithmName.SHA256);
            if (_pinnedCertFingerprint is null)
            {
                _pinnedCertFingerprint = fp;
                SavePinnedCert(fp);
                OnError?.Invoke("[TOFU] First connection — certificate pinned. Verify with server admin!");
                return true;
            }
            if (_pinnedCertFingerprint != fp)
            {
                OnError?.Invoke($"[SECURITY] Server certificate CHANGED! Possible MITM. Connection blocked. Delete ~/.rede/.cert_pin to re-pin.");
                return false;
            }
            return true;
        }

        if (_serverUrl.StartsWith("wss://"))
        {
            _ws.Options.RemoteCertificateValidationCallback = TofuValidation;
        }

        var timeout = _proxySettings.UseI2P ? 90000 : (_proxySettings.UseTor ? 60000 : 15000);
        using var connectCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, connectCts.Token);

        try
        {
            if (_proxySettings.UseI2P || _proxySettings.UseTor)
            {
                var proxyUrl = _proxySettings.UseI2P ? _proxySettings.I2PProxy : _proxySettings.TorProxy;
                var normalizedUrl = proxyUrl.Replace("socks5h://", "socks5://");
                var handler = new SocketsHttpHandler
                {
                    Proxy = new WebProxy(normalizedUrl),
                    UseProxy = true,
                };
                handler.SslOptions.RemoteCertificateValidationCallback = TofuValidation;
                var invoker = new HttpMessageInvoker(handler);
                await _ws.ConnectAsync(new Uri(_serverUrl), invoker, linked.Token);
            }
            else
            {
                await _ws.ConnectAsync(new Uri(_serverUrl), linked.Token);
            }

            _isConnected = true;
            _reconnectAttempts = 0; // M5: Reset backoff on successful connect
            OnConnected?.Invoke();

            _ = Task.Run(() => ReceiveLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            _isConnected = false;
            throw new InvalidOperationException($"Connection failed: {ex.Message}", ex);
        }
    }

    private const int MaxOutgoingSize = 512 * 1024; // L3: 512KB outgoing limit

    public bool Send(string type, JsonObject? payload = null)
    {
        if (_ws?.State != WebSocketState.Open)
            return false;

        var msg = ProtocolSerializer.CreateClientMessage(type, payload);
        var bytes = Encoding.UTF8.GetBytes(msg);

        // L3: Reject oversized outgoing messages
        if (bytes.Length > MaxOutgoingSize)
        {
            OnError?.Invoke("[WARNING] Outgoing message too large — dropped.");
            return false;
        }

        try
        {
            // M7: Use Task.Run to avoid sync-over-async deadlock on UI thread
            Task.Run(async () =>
                await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                    _cts?.Token ?? CancellationToken.None)
            ).GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Send a binary frame (for SRTP audio packets in secure voice mode).
    /// </summary>
    public async Task<bool> SendBinaryAsync(byte[] data)
    {
        if (_ws?.State != WebSocketState.Open)
            return false;
        if (data.Length > 8192) return false; // SRTP packets should be well under 8KB
        try
        {
            await _ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true,
                _cts?.Token ?? CancellationToken.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Event fired when a binary frame is received (SRTP audio packets).
    /// </summary>
    public event Action<byte[]>? OnBinaryMessage;

    public async Task<bool> SendAsync(string type, JsonObject? payload = null)
    {
        if (_ws?.State != WebSocketState.Open)
            return false;

        var msg = ProtocolSerializer.CreateClientMessage(type, payload);
        var bytes = Encoding.UTF8.GetBytes(msg);

        // H7: Outgoing size limit (same as sync Send)
        if (bytes.Length > MaxOutgoingSize)
        {
            OnError?.Invoke("[WARNING] Outgoing message too large — dropped.");
            return false;
        }

        try
        {
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                _cts?.Token ?? CancellationToken.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private const int MaxMessageSize = 1024 * 1024; // H1: 1MB max message size

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var messageBuffer = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                messageBuffer.SetLength(0);
                WebSocketReceiveResult result;
                bool oversized = false;

                // Read complete message (may arrive in multiple frames)
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        goto exit;
                    messageBuffer.Write(buffer, 0, result.Count);
                    // H1: Check message size limit
                    if (messageBuffer.Length > MaxMessageSize)
                    {
                        oversized = true;
                        break;
                    }
                } while (!result.EndOfMessage);

                if (oversized)
                {
                    // H6: Drain remaining frames of oversized message to prevent frame desync
                    while (!result.EndOfMessage)
                    {
                        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Close) goto exit;
                    }
                    OnError?.Invoke("[SECURITY] Oversized message dropped.");
                    messageBuffer.SetLength(0);
                    continue;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    var data = messageBuffer.ToArray();
                    try { OnBinaryMessage?.Invoke(data); }
                    catch (Exception ex) { OnError?.Invoke($"Binary handler error: {ex.Message}"); }
                    continue;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var raw = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                    var msg = ProtocolSerializer.Parse(raw);
                    if (msg is null) continue;

                    // Verify server signature if we have a pinned key
                    if (ServerSigningKey is not null)
                    {
                        var sigNode = msg["serverSig"];
                        if (sigNode is null)
                        {
                            OnError?.Invoke("[SECURITY] Missing server signature! Message dropped.");
                            continue;
                        }
                        if (!CryptoService.VerifyServerSignature(raw, ServerSigningKey))
                        {
                            OnError?.Invoke("[SECURITY] Invalid server signature! Message dropped.");
                            continue;
                        }
                    }

                    var type = ProtocolSerializer.GetType(msg);

                    // Queue messages — fire events directly (no handler registration needed)
                    if (type == Msg.QueuePosition)
                    {
                        var pos = ProtocolSerializer.GetInt(msg, "position");
                        var total = ProtocolSerializer.GetInt(msg, "total");
                        OnQueuePosition?.Invoke(pos, total);
                        continue;
                    }
                    if (type == Msg.QueueAdmit)
                    {
                        OnQueueAdmit?.Invoke();
                        continue;
                    }

                    if (type is not null && _handlers.TryGetValue(type, out var handler))
                    {
                        try { handler(msg); }
                        catch (Exception ex) { OnError?.Invoke($"Handler error [{type}]: {ex.Message}"); }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }

        exit:
        _isConnected = false;
        OnDisconnected?.Invoke();

        if (ShouldReconnect && Interlocked.CompareExchange(ref _isReconnecting, 1, 0) == 0)
        {
            OnReconnecting?.Invoke();
            // M5: Exponential backoff — 2s, 4s, 8s, 16s, 32s, cap 60s
            var delay = Math.Min(ReconnectDelay * (1 << Math.Min(_reconnectAttempts, 5)), 60000);
            _reconnectAttempts++;
            _ = Task.Run(async () =>
            {
                await Task.Delay(delay);
                try { await ConnectAsync(); }
                catch { /* retry on next interval */ }
                finally { Interlocked.Exchange(ref _isReconnecting, 0); }
            });
        }
    }

    private static async Task CheckSocksProxyAsync(string proxyUrl)
    {
        var normalized = proxyUrl.Replace("socks5h://", "http://").Replace("socks5://", "http://");
        var uri = new Uri(normalized);
        var host = uri.Host;
        var port = uri.Port;

        using var cts = new CancellationTokenSource(3000);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, cts.Token);
        }
        catch
        {
            var transport = proxyUrl.Contains("4447") ? "i2pd" : "Tor";
            throw new InvalidOperationException(
                $"{transport} läuft nicht. Starte {transport} und versuche es erneut.\n({host}:{port} nicht erreichbar)");
        }
    }

    public void Disconnect()
    {
        ShouldReconnect = false;
        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                Send(Msg.SessionEnd);
                _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch { }
        }
        _cts?.Cancel();
    }

    public void Dispose()
    {
        Disconnect();
        _ws?.Dispose();
        _cts?.Dispose();
    }
}
