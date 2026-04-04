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

## Security Audit (2026-03-31) — Gefixte Findings

### Critical
[x] C1: Brush.Parse() mit User-Daten ohne Validierung — `ColorHelper.SafeParse()` mit Regex `^#[0-9a-fA-F]{6}$` + try-catch + Fallback. Alle Brush.Parse-Aufrufe in MainViewModel.cs und SettingsViewModel.cs ersetzt.
[x] C2: PlaceService Fake-PlaceKey akzeptiert — `HandlePlaceKeyReceived()` lehnt PlaceKeys für unbekannte Places ab (nur bereits via PLACE_INVITE erstellte Placeholder akzeptiert).

### High
[x] H1: Unbounded Metadata-Größe bei Places — `DecryptMetadata()` prüft `encrypted.Length > MaxMetadataSize * 2` (10MB raw) vor Verarbeitung.
[x] H2: Avatar/Icon-Größe nach Base64-Decode nicht validiert — `LoadAvatar()`, `LoadIcon()`, `LoadAvatarFromBase64()` prüfen `bytes.Length > 256KB` nach Decode.
[x] H3: Unbounded Collections im Profile-Model — `AddChatMessageAsync()` evictet älteste Chat-History wenn >500 Conversations. Per-Chat bereits auf 1000 Messages limitiert.
[x] H4: SRTP Key-Validierung fehlt — `CallService` prüft `srtpKey.Length >= 16` und `srtpSalt.Length >= 14` nach Decode.
[x] H5: Nonce-Länge nicht validiert vor Decrypt — `DoubleRatchet.Decrypt()` und `SenderKeys.Decrypt()` prüfen `nonce.Length != 24`.
[x] H6: Ciphertext-Mindestlänge nicht geprüft — Gleiche Stellen: `ciphertext.Length < 16` → sofort return null.
[x] H7: DH Public Key Länge nicht validiert — `DhRatchetStep()` prüft `dhPub.Length != 32`.
[x] H9: Pending-Outgoing-Queue unbounded — `ChatService.SendMessage()` limitiert Queue auf 100 Messages pro Target.

### Medium
[x] M1: Ephemeral Keys nicht gezeroed bei Exception — `X3dh.Initiate()`, `X3dh.Respond()`, `DoubleRatchet.Encrypt()` mit try-finally Blöcken für alle Secret Keys.
[x] M2: ProfileEncryption.Decrypt zeroed Plaintext-Bytes nicht — `CryptoService.ZeroOut(decrypted)` nach UTF-8 Konvertierung. Auch `hmacValue` in Encrypt gezeroed.
[x] M3: DH-Intermediates nicht gezeroed bei Exception — `DhRatchetStep()` komplett in try-finally mit Zero für alle Intermediates.
[x] M4: Bidi-Override-Zeichen nicht gefiltert — `SanitizeDisplayString()` und `PlaceService.SanitizeMetadataString()` filtern jetzt U+200E-200F, U+202A-202E, U+2066-2069.
[x] M5: Channel-Topic und Category-Name nicht sanitized — `AddCategory()` und `SetChannelTopic()` nutzen `SanitizeMetadataString()` (max 64/200 Chars).
[x] M6: Custom-Status-Text ohne Längenlimit — `SettingsViewModel.OnCustomStatusTextChanged()` truncated auf 128 Chars.
[x] M7: CancellationTokenSource Leak — `DebouncedAudioChange()` ruft `_debounce?.Dispose()` vor Neuerstellung.
[x] M8: Reconnect-Task-Akkumulation — `_isReconnecting` volatile Flag verhindert parallele Reconnect-Tasks.

### Low
[x] L1: System-Messages ohne Längenlimit — `AddSystemMessage()` truncated auf 1000 Chars.

## Security Audit (2026-03-31, Runde 3) — Gefixte Findings

### Critical
[x] C3: Int32 Counter-Wraparound in DoubleRatchet — `MaxMessageNumber = 1_000_000_000` Konstante + Guards vor Ns++/Nr++ und in SkipMessageKeys. Exception erzwingt Session-Reset vor Overflow.
[x] C4: SenderKeys messageNumber Off-by-One — `>= MaxMessageNumber` statt `> MaxMessageNumber` verhindert Encrypt bei exakt 10000 (C-style cast zu uint wäre sonst 2^32-1).

### High
[x] H6: Command-Argument-Längenvalidierung — Group-Name max 64 Chars in `/group` Command-Handler.
[x] H9: DH Header Field Validation — `headerNode["dh"]` auf null/empty geprüft in beiden X3DH- und Ratchet-Message-Handlern in ChatService.

### Medium
[x] M2: Avatar MIME-Type Fallback — Unbekannte Dateiendungen werden jetzt abgelehnt statt still als `image/png` akzeptiert.
[x] M5: Directory Walk Depth Limit — `FindRepoEnv()` (LoginViewModel) und `DetectRepoPath()` (UpdateService) auf max 10 Levels begrenzt.
[x] M9: Bitmap Disposal — `LoadAvatar()` und `LoadIcon()` disposen alte Bitmaps vor Ersetzung (Memory Leak Fix).
[x] M10: PlaceService TryGetProperty — `root.GetProperty("name")` zu `root.TryGetProperty("name", ...)` geändert mit false return bei fehlendem Key.
[x] M11: Proxy-URL-Validierung — `Uri.TryCreate()` Validierung für Proxy-URLs aus .env-Datei.
[x] M12: Avatar-Größe in BroadcastProfile — Base64-Länge >350KB (~256KB decoded) wird vor Broadcast an Kontakte abgelehnt.

## Security Audit (2026-03-31, Runde 4) — Gefixte Findings

### High
[x] H1: AppleScript-Injection in NotificationService — `EscapeAppleScript()` filterte keine Newlines/Control-Chars. Jetzt werden alle Control-Chars (0x00-0x1F, 0x7F) per Regex zu Space ersetzt vor dem Escaping.

### Medium
[x] M1: CustomStatusText Truncation Bug — `OnStatusChanged` wurde nach Truncation auf 128 Chars NICHT aufgerufen. Fix: return nach Truncation (setter re-triggers Handler mit gekürztem Wert).
[x] M2: Base64 Pre-Validation — `LoadAvatar()` und `LoadIcon()` prüfen jetzt `base64.Length > 350_000` VOR `Convert.FromBase64String()` um große Heap-Allokation zu vermeiden.
[x] M3: Ban Reason Längenlimit — `PlaceService.BanUser()` truncated `reason` auf max 200 Chars vor Senden/Speichern.
[x] M4: Emote-Count bei Deserialize — `DecryptMetadata()` begrenzt deserialisierte Emotes auf max 50 (verhindert Metadata-Bloat durch manipulierte E2EE-Daten).
[x] M5: Messages Collection unbounded — `AddIncomingMessage()` entfernt älteste Nachrichten wenn ≥1000 in der In-Memory Display-Collection (OOM-Schutz).

## Security Audit (2026-04-01, Runde 5) — Gefixte Findings

### Medium
[x] M1: Deserialisierte E2EE-Metadata Colors nicht validiert — `DecryptMetadata()` nutzt jetzt `ValidateColor()` (Regex `^#[0-9a-fA-F]{6}$`) für ownerColor, adminColor, memberColor, accentColor. Ungültige Werte fallen auf Defaults zurück.
[x] M2: Per-Emote ImageData unbounded — Emotes mit `ImageData.Length > 87_000` (~64KB decoded) werden bei Deserialization gefiltert.
[x] M3: Bans Dictionary unbounded — Cap auf 1000 Bans + Reason-Truncation auf 200 Chars bei Deserialization.
[x] M4: PlaceChannel Topic/Name unbounded nach Deserialize — Channels werden nach Deserialization sanitized: Name max 64, Topic max 200 Chars.
[x] M5: Contact AccentColor nicht validiert — `OnProfileReceived` Handler prüft jetzt Hex-Format per Regex vor Zuweisung. Ungültige Farben behalten den vorherigen Wert.
[x] M6: PlaceChannel Name bei CreateChannel nicht validiert — `SanitizeMetadataString(name, 64)` vor Channel-Erstellung.
[x] M7: Place IconData unbounded bei Deserialize — `iconData.Length > 350_000` wird abgelehnt.

## Security Audit (2026-04-04, Runde 6) — Gefixte Findings

### Critical
[x] C1: pg-store.js fehlende place_admins/place_bans Tabellen + Funktionen — Schema + async Functions (`addPlaceAdmin`, `removePlaceAdmin`, `isPlaceAdmin`, `addPlaceBan`, `removePlaceBan`, `isPlaceBanned`, `getPlaceBans`) + `getPlace()` returns `admins` array.
[x] C2: index.js sync/async Mismatch mit PG-Backend — Alle Handler-Funktionen `async`, alle `store.*()` Aufrufe mit `await`, Message-Handler-Callback `async`, `deliverPending` async + awaited, Cleanup-Intervals mit `async () =>` Wrapper.
[x] C3: SRTP Binary Relay ohne per-Connection Call-Tracking — `wsActiveCall` WeakMap trackt aktiven Call pro WebSocket. Binary Frames nur relayed wenn `wsActiveCall` gesetzt + Call-Membership verifiziert. 8KB Größenlimit für Binary Frames.
[x] C4: CallService akzeptiert Calls von Nicht-Kontakten — `HandleCallOffer()` prüft `Profile.Contacts.ContainsKey(incomingFrom)` vor Verarbeitung.

### High
[x] H1: `clearPendingPlaceMessages` Argumente vertauscht — Reihenfolge korrigiert zu `(targetUserId, placeId)`.
[x] H2: Call-Signaling forwarded alle JSON-Felder — Whitelist: nur `callId`, `to`, `from`, `fromDeviceId`, `mode`, `sdp`, `candidate`, `srtpParams`, `reason`, `muted`.
[x] H3: `activeCalls` Map unbounded — Cap auf 500 + max 2 aktive Calls pro User.
[x] H4: `userStatuses` Map Memory Leak — Cleanup im 60s-Interval: Entfernt Einträge für User die nicht mehr in `clients` sind.
[x] H5: Status-Broadcast DoS Amplification — Rate Limit 5/Minute pro User für `STATUS_UPDATE`.
[x] H6: Oversized WebSocket Message Frames nicht gedrained — Drain-Loop liest und verwirft Frames bis `EndOfMessage` bei Überschreitung.
[x] H7: `SendAsync()` ohne Outgoing Size Limit — `MaxOutgoingSize` (512KB) Check hinzugefügt, konsistent mit sync `Send()`.
[x] H8: TOFU null-Cert für wss:// über Proxy akzeptiert — Null-Certs nur noch für non-wss:// akzeptiert (Proxy ändert nichts).
[x] H9: CallService DH Header Feld nicht validiert — `string.IsNullOrEmpty(dhVal)` Check vor Header-Konstruktion.
[x] H10: Race auf `_pendingPlaceName` bei schneller Place-Erstellung — `ConcurrentQueue<string>` statt Single-Slot.
[x] H11: `HandlePlaceKeyReceived` MetadataKey Format nicht validiert — Base64 + 32-Byte Längencheck vor Übernahme.
[x] H12: `HandleDeviceLinkFail` Server-Error nicht sanitized — `SanitizeServerError()` angewendet.
[x] H13: Self-Call nicht verhindert — `if (to === senderId) return;` Check in Call-Signaling.

### Medium
[x] M1: `invite.js` hardcoded SQLite Store — Respektiert jetzt `REDE_DB_BACKEND=pg`.
[x] M2: WebSocket/CTS Leak bei Reconnect — `Dispose()` auf alte `_ws`/`_cts` vor Neuerstellung.
[x] M3: Group Creator per `members[0]` Array-Position — `creator_id` Spalte in `groups_` Tabelle + Migration für bestehende DBs + Fallback auf `members[0]`.
[x] M4: Sealed Message Nonce-Validierung fehlt — `validateNonce(sealedPayload.nonce)` hinzugefügt.
[x] M5: Sealed Messages ohne Replay-Schutz — SHA256-Hash aus `ephemeralKey+nonce+ciphertext[:64]` als Nonce-Key in `checkNonce()`.
[x] M6: ProfileEncryption Password Bytes nicht gezeroed — `CryptoService.ZeroOut(password)` nach scrypt.
[x] M7: ProtocolSerializer JSON Depth unbounded — `MaxDepth = 15` in `JsonDocumentOptions`.
[x] M8: LIKE Queries mit User-Input ohne Wildcard-Escaping — `escapeLike()` escaoed `%` und `_` in both stores.
[x] M9: DH Public Key in DhRatchetStep nicht gegen Low-Order Points validiert — `CryptoService.IsValidDhPublicKey()` Check hinzugefügt.
[x] M10: Incoming Messages ohne Längenlimit vor Markdown-Rendering — Truncation auf 8192 Chars in `AddIncomingMessage()` + `UpdateInlines()`.
[x] M11: Stale .tmp Files nach Crash — `EnsureDir()` löscht `*.tmp` beim Start.
[x] M12: .enc Dateien ohne Unix File Permissions — `SetUnixFileMode(UserRead|UserWrite)` nach Rename.
[x] M13: Sealed Envelope Empty-String-Felder — Null/Empty-Check für `ephemeralKey` und `ciphertext` vor Unseal.
[x] M14: Cross-Type Nonce Replay Gap (Sealed→Regular) — Inner Message Nonce nach Unseal auch gegen NonceTracker geprüft.
[x] M15: Deserialisierte Channel Names nicht durch `SanitizeMetadataString` — Jetzt `SanitizeMetadataString()` statt einfacher Truncation.
[x] M16: Categories List unbounded nach Deserialize — Cap auf 100 Einträge.
[x] M17: Place Profile/Role Colors nicht auf Sender-Seite validiert — `ValidateColor()` + IconData-Größencheck vor Distribute.

### Low
[x] L1: Dead Code `_origHandlePlaceInvite` — Entfernt.
[x] L2: MarkdownTextBlock Regex ReDoS — Regex Timeout (100ms) + `RegexMatchTimeoutException` Catch mit Plaintext-Fallback.
[x] L3: OwnAvatarImage Bitmap nicht disposed — `oldBmp?.Dispose()` vor Replacement in `UpdateOwnProfilePanel()`.
[x] L4: SettingsViewModel Avatar Bitmaps nicht disposed — `oldBmp?.Dispose()` in `SetAvatarFromBytes()` und `LoadAvatarFromBase64()`.
[x] L5: `SendBinaryAsync` ohne Size Limit — 8KB Limit für SRTP Packets.
[x] L6: SecureOverwrite vor Rename — Reihenfolge korrigiert (Rename zuerst, dann optional Overwrite).
[x] L7: Double scrypt bei ProfileEncryption — Single 64-Byte Derivation für Encryption + HMAC Key, mit Legacy-Fallback beim Decrypt.

### Crypto
[x] CR1: SenderKeys.Encrypt Chain Key mutiert vor Overflow-Check — Bounds-Check an den Anfang von `Encrypt()` verschoben.
[x] CR2: DoubleRatchet.Encrypt Counter-Check nach State-Mutation — Check an den Anfang verschoben, vor Chain-Key-Derivation.
[x] CR3: HKDF `byte` Loop Counter kann silent wrappen — `int` Counter mit explizitem `> 255` Guard.
[x] CR4: Nur 2 von 7 Curve25519 Low-Order Points — Alle 7 bekannten Small-Subgroup Points in `LowOrderPoints` Liste.

### Nicht gefixt (architekturell / niedrige Priorität)
[ ] C5: Race Condition in async Ratchet State Save — Task.Run Fire-and-Forget kann bei schnellen Nachrichten zu Out-of-Order Writes führen. Erfordert Save-Queue-Architektur.
[ ] H8: Event-Handler-Akkumulation in Flyouts — Context-Menüs werden bei jedem Rechtsklick neu erstellt. Flyout-Instanzen werden vom GC aufgeräumt wenn Flyout geschlossen wird, da keine Referenzen gehalten werden. Nur bei extrem häufigem Öffnen relevant.
[ ] H10: Device Key Injection via MITM — Server könnte theoretisch Phantom-Devices einfügen. Mitigation: Sicherheitswarnung + /confirm Required. Echte Lösung: Out-of-Band Device Verification (QR-Code o.ä.).
[ ] H13: SealedSender Domain Separation — Ephemeral Box-Payload hat keinen Domain-Tag. Erfordert Protokolländerung.
[ ] L2: Silent Exception-Swallowing in Avatar/Icon-Loading — Fehlerhafte Bilder werden korrekt zu null gesetzt. Debug-Logging würde Dependency auf Logging-Framework erfordern.
[ ] L3: Fire-and-Forget Saves — Bekannte Einschränkung, erfordert Save-Queue-Architektur.

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
- ~~Double scrypt bei jedem Profile-Save~~ — GEFIXT in Runde 6 (L7): Single 64-Byte Derivation mit Legacy-Fallback.
- Profile wird bei jeder einzelnen Nachricht komplett neu verschlüsselt und geschrieben - Chat-History sollte separat gespeichert werden.
- Fire-and-forget Task.Run Saves ohne Fehlerbehandlung - Save-Queue mit Ordering wäre robuster.