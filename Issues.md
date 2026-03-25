# Rede Client  - Issues & Security Audit

## Erledigte Features
[x] Own messages now saved to chat history (both 1:1 and group)
[x] TTL is in days - /ttl 10 deletes messages after 10 days, cleanup runs on login for both sender & receiver
[x] Right-click contact shows context menu with "Invite to #group" for each group + "View fingerprint"
[x] Group invite now sends group key to invitee via ratcheted DM (E2EE key distribution)
[x] Login loading indicator - fixed (race condition: IsLoading was reset in finally block before async completed)
[x] INTERNALID_PATTERN - kein Bug: Server generiert userId (nicht Client). Client sendet displayName, Server antwortet mit userId#xxxx.

## Security Audit (2026-03-25)  - Gefixte Findings

### Crypto Layer
[x] C1/C2: DoubleRatchet.Decrypt - internes State-Rollback bei fehlgeschlagenem Decrypt (Backup vor Mutation, Restore im Catch)
[x] C2: SenderKeys.Decrypt - State-Rollback bei fehlgeschlagenem Decrypt + DeepClone + messageNumber Range-Validierung
[x] H1: CryptoService.Dh() - DH-Output gegen All-Zeros validiert (Low-Order-Point-Angriff)
[x] H4: CryptoService.StripJsonField - Regex für escaped Quotes in Server-Signatur gefixt
[x] H5: X3dh.Respond() - Key-Length-Validierung (32 Bytes) hinzugefügt
[x] M1: X3dh.Initiate() - Key-Length-Validierung für alle Bundle-Keys
[x] M4: Hkdf.Expand() - Counter-Overflow-Check (max 8160 Bytes Output)
[x] M5: MessagePadding.Pad() - Size-Validierung (max 16382 Bytes) verhindert Buffer-Overflow
[x] M5: SenderKeys.Encrypt() - Bounds-Check bei MaxMessageNumber, wirft Exception statt stille Fehler
[x] M6: SealedSender.Unseal() - Ephemeral Key Length + Nonce Length + Ciphertext Validierung
[x] M7: SealedSender.Unseal() - Secret Key nach Benutzung gezeroed
[x] L1: Hkdf.DeriveKey() - PRK (Intermediate Key) nach Expand gezeroed

### Services & Networking
[x] C2: UpdateService - SHA256-Hash-Verifizierung gegen Checksums-Datei aus GitHub Release
[x] C4: GroupService.HandleGroupMessage - Membership-Check vor Akzeptieren von Gruppennachrichten
[x] H2: GroupService.HandleGroupInvite - Server-provided Group Key wird nie akzeptiert (immer lokal generiert)
[x] H3: GroupService - NonceTracker für Gruppen-Nachrichten (Replay-Schutz)
[x] H3: ContactService.ConfirmKeyChange - Speichert und appliziert pending Key Changes (war vorher No-Op)
[x] H5: ChatService.HandleMessage - Nonce jetzt required, nicht optional
[x] H6: ChatService.HandleSealedMessage - Sealed Nonce required (null-guard entfernt)
[x] H7: ChatService._pendingOutgoing - Queue statt Single-Overwrite, alle Nachrichten nach Session-Aufbau gesendet
[x] H9: AuthService - Server-Fehlermeldungen sanitized (HTML, URLs, Control Chars)
[x] M5: GroupService - Leere Member-Liste = Reject (nicht Skip)
[x] M6: GroupService - Sender-Key-State nicht doppelt geladen (Race vermieden)
[x] M6: RedeConnection.SavePinnedCert - Unix File Permissions (0600) auf Cert-Pin-Datei
[x] M7: RedeConnection - TLS null-cert nur für non-wss:// akzeptiert
[x] M8: GroupService.HandleGroupKickOk - Lokale Member-Liste bei Kick aktualisiert
[x] M8: UpdateService.ParseVersion - Pre-Release-Suffixe in Versionsvergleich einbezogen
[x] M9: GroupService.RekeyGroup - TTL=0 für Key-Distribution (offline Members bekommen Key)
[x] M10: ProfileStore.ConfirmContactKeyChange  - Alte Ratchet-States bei Key-Change gelöscht
[x] M7: RedeConnection.Send - Task.Run Wrapper gegen Sync-over-Async Deadlock
[x] L2: ProfileStore.SecureOverwrite - FileStream.Flush(flushToDisk: true) für fsync
[x] L2: ProfileStore.SaveProfileAsync - Atomic Write mit fsync vor Rename
[x] L3: RedeConnection.Send - Outgoing Message Size Limit (512KB)
[x] L5: ProfileStore.MigrateProfile - Archived Signed Pre-Keys nach 30 Tagen expired
[x] H4/H7: NonceTracker.Check - Eviction bei halber Kapazität + Hard-Cap bei Maximum

### UI Layer
[x] H10: MainWindow._pendingDevices - ConcurrentDictionary für Thread-Safety
[x] M4: MainView - CollectionChanged Handler Cleanup in OnUnloaded (Memory Leak Fix)
[x] M13: MainWindow.RegisterAsync - Invite Code + Passphrase Confirm nach Registration gelöscht
[x] M14: MainWindow.RefreshGroups - Gruppen-Namen sanitized (konsistent mit Kontakten)
[x] H1: MainWindow - Passphrase aus Login VM nach Auth gelöscht

## Bekannte Einschränkungen (by design / .NET limitation)
- Key material wird als string (base64) gespeichert - .NET strings sind immutable, können nicht gezeroed werden. Erfordert Refactoring zu byte[] (architekturell).
- Skipped Message Keys (bis zu 1000) als base64 Strings im Heap - nicht zeroed bei Eviction (gleiche .NET string Limitation).
- NonceTracker ist in-memory - nach Neustart können Nachrichten aus der letzten Stunde replayed werden.
- SecureOverwrite ist auf SSDs mit Wear-Leveling nicht effektiv - Full-Disk-Encryption empfohlen.
- Auto-Update-Binaries sind nicht kryptographisch signiert - SHA256-Hash-Verifikation als Mitigation, echte Signierung (minisign/GPG) steht aus.
- Sender Key Signatur bindet nicht an Group ID - Cross-Group-Replay theoretisch möglich. Fix erfordert Protokolländerung (Wire-Compat-Break).
- ProfileEncryption HMAC ist optional für Legacy-Profile - nach Migrationszeitraum sollte HMAC required werden.
- Passphrase wird als string an 5+ Service-Objekte propagiert - .NET SecureString ist deprecated, byte[] erfordert Refactoring.
- Event-Handler-Akkumulation in InitServices bei wiederholtem Login - Services sollten IDisposable implementieren.
- Double scrypt bei jedem Profile-Save (Encryption + HMAC Key) - könnte zu Single 64-Byte Derivation optimiert werden.
- Profile wird bei jeder einzelnen Nachricht komplett neu verschlüsselt und geschrieben - Chat-History sollte separat gespeichert werden.
- Fire-and-forget Task.Run Saves ohne Fehlerbehandlung - Save-Queue mit Ordering wäre robuster.