using System.Security.Cryptography;

namespace Rede.Core.Audio;

/// <summary>
/// Manages SRTP state for a single call direction (send or receive).
/// Wraps SrtpCrypto with ROC (rollover counter) tracking and RFC 3711 replay protection.
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

    // RFC 3711 replay protection — 64-bit sliding window
    private ulong _replayWindow;
    private uint _lastRecvIndex; // full 32-bit index = (ROC << 16) | SEQ

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
    /// Returns null if authentication fails or packet is a replay.
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

        // Compute full 32-bit packet index for replay check
        uint packetIndex = (estimatedRoc << 16) | seq;

        // RFC 3711 replay protection
        if (_firstRecv)
        {
            _lastRecvSeq = seq;
            _recvRoc = estimatedRoc;
            _lastRecvIndex = packetIndex;
            _replayWindow = 0;
            _firstRecv = false;
        }
        else
        {
            if (packetIndex == _lastRecvIndex)
                return null; // Duplicate

            if (packetIndex > _lastRecvIndex)
            {
                uint delta = packetIndex - _lastRecvIndex;
                if (delta < 64)
                    _replayWindow <<= (int)delta;
                else
                    _replayWindow = 0;
                _replayWindow |= 1; // Mark previous highest as seen
                _lastRecvIndex = packetIndex;
            }
            else
            {
                uint delta = _lastRecvIndex - packetIndex;
                if (delta > 63)
                    return null; // Too old
                ulong bit = 1UL << (int)(delta - 1);
                if ((_replayWindow & bit) != 0)
                    return null; // Already seen
                _replayWindow |= bit;
            }

            // Update ROC tracking
            if (estimatedRoc > _recvRoc || (estimatedRoc == _recvRoc && seq > _lastRecvSeq))
            {
                _recvRoc = estimatedRoc;
                _lastRecvSeq = seq;
            }
        }

        return result;
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_cipherKey);
        CryptographicOperations.ZeroMemory(_authKey);
        CryptographicOperations.ZeroMemory(_sessionSalt);
    }
}
