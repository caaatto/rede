namespace Rede.Core.Audio;

/// <summary>
/// Manages SRTP state for a single call direction (send or receive).
/// Wraps SrtpCrypto with ROC (rollover counter) tracking.
/// </summary>
public class SrtpSession : IDisposable
{
    private readonly byte[] _cipherKey;
    private readonly byte[] _authKey;
    private readonly byte[] _sessionSalt;
    private uint _sendRoc;
    private ushort _lastSendSeq;
    private uint _recvRoc;
    private ushort _lastRecvSeq;
    private bool _firstSend = true;
    private bool _firstRecv = true;

    public SrtpSession(byte[] masterKey, byte[] masterSalt)
    {
        (_cipherKey, _authKey, _sessionSalt) = SrtpCrypto.DeriveSessionKeys(masterKey, masterSalt);
    }

    /// <summary>
    /// Encrypt an outgoing RTP packet. Manages sequence number rollover.
    /// </summary>
    public byte[] Protect(byte[] rtpPacket)
    {
        if (rtpPacket.Length < 12)
            throw new ArgumentException("RTP packet too short");

        ushort seq = (ushort)((rtpPacket[2] << 8) | rtpPacket[3]);

        if (_firstSend)
        {
            _lastSendSeq = seq;
            _firstSend = false;
        }
        else if (seq < _lastSendSeq && _lastSendSeq - seq > 0x8000)
        {
            _sendRoc++;
        }
        _lastSendSeq = seq;

        return SrtpCrypto.Protect(rtpPacket, _cipherKey, _authKey, _sessionSalt, _sendRoc);
    }

    /// <summary>
    /// Decrypt an incoming SRTP packet. Manages ROC estimation for receiver.
    /// Returns null if authentication fails.
    /// </summary>
    public byte[]? Unprotect(byte[] srtpPacket)
    {
        if (srtpPacket.Length < 12 + SrtpCrypto.AuthTagLength)
            return null;

        ushort seq = (ushort)((srtpPacket[2] << 8) | srtpPacket[3]);
        uint estimatedRoc = _recvRoc;

        if (!_firstRecv)
        {
            if (seq < _lastRecvSeq && _lastRecvSeq - seq > 0x8000)
                estimatedRoc = _recvRoc + 1;
            else if (seq > _lastRecvSeq && seq - _lastRecvSeq > 0x8000 && _recvRoc > 0)
                estimatedRoc = _recvRoc - 1;
        }

        var result = SrtpCrypto.Unprotect(srtpPacket, _cipherKey, _authKey, _sessionSalt, estimatedRoc);
        if (result is null) return null;

        // Update ROC on successful decrypt
        if (_firstRecv)
        {
            _lastRecvSeq = seq;
            _recvRoc = estimatedRoc;
            _firstRecv = false;
        }
        else if (estimatedRoc > _recvRoc || (estimatedRoc == _recvRoc && seq > _lastRecvSeq))
        {
            _recvRoc = estimatedRoc;
            _lastRecvSeq = seq;
        }

        return result;
    }

    public void Dispose()
    {
        Array.Clear(_cipherKey);
        Array.Clear(_authKey);
        Array.Clear(_sessionSalt);
    }
}
