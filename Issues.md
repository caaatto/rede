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

## Security Audit (2026-04-04, Runde 7) — Performance-Änderungen Audit

### High
[x] H1: Salt-Reuse bei cached Key — Analysiert: Salt wird in Envelope geschrieben, Decrypt liest Salt aus Envelope und re-derivet. Kein Bug — kryptographisch korrekt (Nonce liefert Uniqueness).
[x] H2: Concurrent Dictionary Modification bei debounced Save — `_profileMutationLock` schützt alle Profile-Dictionary-Mutationen (AddChatMessage, SaveRatchetState, SaveSenderKeyState) + Serialisierung in SaveProfileAsync.
[x] H3: Cached Key nicht gezeroed bei Logout — `ClearCachedKey()` zeroot `_cachedKey64`, `_cachedSalt`, nullt `_cachedPassphrase`. Wird von `FlushAsync()` aufgerufen.

### Medium
[x] M1: Debounced Save verschluckt Exceptions — `OnSaveError` Event + `_savePending = true` für Retry. MainWindow zeigt Fehler als `[WARNING]` System-Message.
[x] M2: `_cachedPassphrase` als Klartext-String — Bekannte .NET-Limitation (immutable strings). Bereits in "Bekannte Einschränkungen" dokumentiert.
[x] M3: BroadcastProfile in Task.Run ohne Exception-Handling — SendMessage hat eigene Error-Handling (OnSystemMessage). Task.Run Exceptions sind non-fatal (einzelne Kontakte können offline sein).

### Low
[x] L1: Source-gen Serializer Compat — `[JsonPropertyName]` Attribute haben Priorität über `PropertyNamingPolicy`. Kein Compat-Problem.

### Nicht gefixt (architekturell / niedrige Priorität)
[x] C5: Race Condition in async Ratchet State Save — Gelöst durch debounced Saves + `_profileMutationLock`. Alle Mutations sind jetzt thread-safe, Saves werden coalesced.
[x] H8: Event-Handler-Akkumulation in Flyouts — `menu.Closed += (_, _) => btn.ContextMenu = null;` in allen 5 PointerPressed-Handlern. ContextMenu wird nach Schließen vom Control entfernt, GC kann aufräumen.
[x] H10: Device Key Injection via MITM — Device-Fingerprint (SHA256 des SigningKey, erste 8 Bytes, Hex-formatiert) wird bei DEVICE_ADDED angezeigt mit Aufforderung zur Out-of-Band-Verifizierung.
[x] H13: SealedSender Domain Separation — `SEALED_SENDER_V1:` Domain-Tag wird in Seal() prepended und in Unseal() verifiziert+gestripped. Sowohl C# (`SealedSender.cs`) als auch JS (`crypto.js`) aktualisiert.
[x] L2: Silent Exception-Swallowing in Avatar/Icon-Loading — `System.Diagnostics.Debug.WriteLine()` in catch-Blöcken von `LoadAvatar()` und `LoadIcon()` (keine externe Dependency).
[x] L3: Fire-and-Forget Saves — Gelöst durch debounced Saves mit Error-Event + FlushAsync bei App-Exit. OnSaveError surfaced Fehler im UI.

## Security Audit (2026-04-05, Runde 8) — Gefixte Findings

### High
[x] H1: NonceTracker in-memory only — Replay-Fenster nach Neustart geschlossen. `NonceTracker.ImportSnapshot()`/`ExportSnapshot()` + neues `Profile.SeenNonces` Feld. `MainWindow.PropagateProfile()` lädt Snapshot beim Login in alle drei Tracker (chat/group/place). `ExportNoncesToProfile()` merged vor jedem `FlushAsync` (Close, Ctrl+Q). 10k Hard-Cap, stale-Eviction (>1h) bei Import und Export.
[x] H2: ProfileEncryption HMAC optional — Leerer/fehlender `envelope.Hmac` ist jetzt hartes Decrypt-Fail (`return null`). Blockt Downgrade-Angriffe auf unauthenticated Legacy-Format. Legacy-HMAC-Salt-Derivation bleibt als einziger Extra-Versuch bei gesetztem HMAC.
[x] H3: Auto-Update Binaries ohne echte Signatur — Ed25519 Detached-Signature-Verifikation via `Sodium.PublicKeyAuth.VerifyDetached` in `UpdateService.VerifyReleaseSignatureAsync()`. `<AssetName>.sig` Asset wird aus GitHub-Release gezogen. `ReleaseSigningPublicKeyB64` Konstante (aktuell leer = Skip mit SHA256-Fallback). Bei gesetztem Public Key ist `.sig` Pflicht, fehlend/ungültig = harter Abort ohne Fallback. Infra komplett, Release-Manager muss nur Keypair generieren und Public Key eintragen.

### Medium
[x] M1: Event-Handler-Akkumulation bei wiederholtem Login — `_store.OnSaveError` war der echte Leak (doppelte WARNING-Toasts nach 2. Login). Fix: `_saveErrorHandler` als Feld, un-/resubscribe in `InitServices`. Zusätzlich `IDisposable` auf `AuthService`, `ChatService`, `ContactService`, `GroupService`, `PlaceService`, `DeviceService` (CallService hatte es schon). `InitServices` ruft `Dispose()` auf allen 7 Services vor Neuanlegen — deterministisches Lifecycle statt GC-Warten.

## Security Audit (2026-04-05, Runde 9) — Zeroable Key Material (v2.16.0-beta)

### Architekturell
[x] A1: Key material als immutable `string` — architekturelles Refactoring auf `byte[]` für alle Secret/Public Keys, Chain Keys, Group/Metadata Keys, Ratchet-State-Felder (RK/CKs/CKr/DHr), Sender-Key ChainKeys, Skipped Message Keys (`MKSKIPPED`) und Device Keys. Betroffen: `Models.cs` (Profile, Contact, DeviceKeys, KeyPairData, OneTimePreKey, ArchivedSignedPreKey, Group, Place), `CryptoService.cs` (alle Sign/Verify/Encrypt/Decrypt Signaturen), `DoubleRatchet.cs` (RatchetState, KeyPairBytes, MkSkippedConverter), `SenderKeys.cs`, `X3dh.cs` (RecipientBundle, OneTimePreKeyBytes, X3dhInitiateResult), `SealedSender.cs`, `ProfileEncryption.cs`, `RedeConnection.cs` (ServerSigningKey), alle Services (Auth, Chat, Contact, Device, Group, Place, GroupCall), `ProfileStore.cs`, `MainWindow.axaml.cs`. Wire-Format bleibt base64 via `Base64BytesJsonConverter` (per-Feld JsonConverter) — JSON auf Disk und Protokoll-Payloads unverändert, volle JS-v1-Kompat. Ephemeral keys in X3DH/DoubleRatchet/ProfileEncryption in `try-finally` Blöcken gezeroed. Key-Vergleiche konsequent auf `CryptographicOperations.FixedTimeEquals` umgestellt (identityKey-Check, OTPK-Lookup, ServerSigningKey TOFU, Device-Key-Validation). `Fingerprint(byte[])` hasht weiterhin die base64-Repräsentation — erhält Bit-Identität mit JS-v1-Fingerprints, die User bereits out-of-band verifiziert haben.

[x] A2: `Profile.ZeroSecrets()` — zeroot `SecretKey`, `SigningSecretKey`, `SignedPreKey.SecretKey`, alle `OneTimePreKeys[].SecretKey`, `PreviousSignedPreKeys[].SecretKey`, `Group.Key`, `Place.MetadataKey` via `CryptographicOperations.ZeroMemory`. Aufgerufen in `MainWindow.OnClosing` und `Window_KeyDown` (Ctrl+Q) nach `FlushAsync`. Prozess-Memory hält nach Logout keine wiederherstellbaren Secrets mehr.

[x] A3: Skipped Message Keys zeroable — `DoubleRatchet.RatchetState.MKSKIPPED` ist jetzt `Dictionary<string, byte[]>` mit Custom `MkSkippedConverter` (serialisiert weiterhin als `{"header:n": "base64"}`). Die bis zu 1000 skipped keys im Heap sind dadurch zeroable, auch wenn Eviction zwischen Sessions nicht implementiert ist (Eviction on logout via `ZeroSecrets` wäre ein Follow-up — die Map lebt aber ohnehin im RatchetState, der in `ProfileEncryption` verschlüsselt auf Disk landet).

## Security Audit (2026-04-05, Runde 10) — Zeroable Passphrase (v2.17.0-beta)

### Architekturell
[x] A1: Passphrase als immutable `string` propagiert — Refactoring auf `byte[]` durch `AuthService`, `ChatService`, `ContactService`, `GroupService`, `PlaceService`, `DeviceService`, `CallService`, `ProfileStore` (15 public Methoden, `_cachedPassphrase` als `byte[]?` mit `FixedTimeEquals`-Vergleich) und `ProfileEncryption.DeriveKey/Encrypt/Decrypt`. Wire-Format und Scrypt-Ableitung unverändert. Zero auf Logout/Close via `CryptographicOperations.ZeroMemory(_auth.Passphrase)` zusätzlich zu `Profile.ZeroSecrets()`.

[x] A2: Passphrase Swap/Hibernation-Leak — `SecureMemory.Lock(byte[])` P/Invoke zu `mlock(2)` (Linux/macOS) und `VirtualLock` (Windows) in `Rede.Core/Crypto/SecureMemory.cs`. Pin via `GCHandle.Pinned`, `SecureHandle`-Disposable für deterministisches `munlock`+Unpin. Silent No-Op wenn Kernel ablehnt (z.B. RLIMIT_MEMLOCK in Container). `MainWindow._passphraseLock` hält den Handle für die aktive Session, wird direkt nach UTF-8-Encoding in `LoginAsync`/`RegisterAsync`/`QuickLoginAsync` gesetzt, Zero-then-Dispose auf Close.

[x] A3: Passphrase-Eingabe materialisiert `string` — `SecureTextBox` Custom Control (`Rede.Desktop/Controls/SecureTextBox.cs`) mit internem mlock'd `byte[4096]` Buffer. `OnTextInput` encodet jede Keystroke direkt UTF-8 in den Buffer — die einzige GC-Exposure ist das 1-2 Zeichen lange `TextInputEventArgs.Text` pro Keystroke (Mikrosekunden-Fenster im Handler). Backspace walkt über UTF-8 Continuation-Bytes und zerot die gedroppten Bytes. Kein Clipboard-Paste (würde unzerobare Strings einführen), keine Selection, kein Cursor. `ExtractPassphrase()` gibt frische `byte[]`-Kopie zurück und zerot den internen Buffer. `LoginView.axaml.cs` ersetzt 4 `TextBox`-Instanzen (Quick/Login/Register/Confirm), `LoginViewModel.Passphrase`/`PassphraseConfirm` gelöscht, Events auf `Action<string, byte[], ...>` umgestellt. Register-Mode vergleicht Passphrase+Confirm via `FixedTimeEquals`. Nach dem Refactor hält der Prozess zu keinem Zeitpunkt eine managed-`string`-Kopie der Passphrase.

## Bekannte Einschränkungen (by design / .NET limitation)
- ~~Key material wird als string (base64) gespeichert~~ — GEFIXT: Alle Secret/Public Keys, Chain Keys, Group/Metadata Keys, Ratchet-State-Felder (RK/CKs/CKr/DHr), Sender-Key ChainKeys und Device Keys sind jetzt `byte[]` im Speicher. Wire-Format (JSON auf Disk, Protokoll) bleibt base64 via `Base64BytesJsonConverter` für JS-v1-Kompat. `Profile.ZeroSecrets()` zeroot SecretKey, SigningSecretKey, SignedPreKey, OneTimePreKeys, PreviousSignedPreKeys, Group.Key, Place.MetadataKey via `CryptographicOperations.ZeroMemory` — aufgerufen auf beiden Close-Pfaden (Window Close, Ctrl+Q) nach `FlushAsync`.
- ~~Skipped Message Keys (bis zu 1000) als base64 Strings im Heap~~ — GEFIXT im gleichen Refactor: `DoubleRatchet.RatchetState.MKSKIPPED` ist jetzt `Dictionary<string, byte[]>` mit Custom-Converter, Keys sind auf dem Heap zeroable.
- ~~NonceTracker ist in-memory~~ — GEFIXT in Runde 8 (H1): Persistiert als `Profile.SeenNonces`, beim Login in alle Tracker importiert, bei Flush exportiert. Replay-Fenster überlebt Neustarts.
- SecureOverwrite ist auf SSDs mit Wear-Leveling nicht effektiv - Full-Disk-Encryption empfohlen (User-Guidance / Release-Notes, kein Code-Fix möglich).
- ~~Auto-Update-Binaries ohne echte Signatur~~ — GEFIXT: Ed25519-Keypair generiert, Public Key in `ReleaseSigningPublicKeyB64` eingetragen (`SPON95u43RxzipArSW1Ntyk9eQ6hHCaf8UJlzOR+vas=`). Secret Key in `/home/amke/Rede/.release-signing-key.secret` (chmod 600). Signing-Script `scripts/sign-release.sh` erzeugt `.sig` Assets. Release-Prozess muss jetzt `REDE.sig` + `REDE.exe.sig` mitliefern — fehlende Signatur = harter Update-Abbruch.
- ~~Sender Key Signatur bindet nicht an Group ID~~ — GEFIXT in v2.17.4-beta: Signatur-Payload jetzt `ciphertext || uint32(messageNumber) || utf8(contextId)` wobei contextId = groupId (Groups) bzw. `place:{placeId}:{channelId}` (Places). Geändert über alle 4 Codebases: `SenderKeys.cs`, `crypto.js`, `index.js` (client), `index.js` (server). Rückwärtskompatibel: Verifikation probiert neues Format zuerst, fällt auf Legacy (ohne contextId) zurück für Nachrichten von älteren Clients.
- ~~ProfileEncryption HMAC ist optional für Legacy-Profile~~ — GEFIXT in Runde 8 (H2): Leerer HMAC = hartes Decrypt-Fail.
- ~~Passphrase wird als string an 5+ Service-Objekte propagiert~~ — GEFIXT in Runde 10: `byte[]` durch alle Services + `ProfileStore` + `ProfileEncryption`, `SecureMemory.Lock` (mlock/VirtualLock) gegen Swap/Hibernation, `SecureTextBox` Custom Control vermeidet managed-string-Materialisierung bei der Eingabe.
- ~~Event-Handler-Akkumulation in InitServices bei wiederholtem Login~~ — GEFIXT in Runde 8 (M1): `_saveErrorHandler` als Feld mit un-/resubscribe + `IDisposable` auf allen Services.
- ~~Double scrypt bei jedem Profile-Save~~ — GEFIXT in Runde 6 (L7): Single 64-Byte Derivation mit Legacy-Fallback.
- ~~Profile wird bei jeder einzelnen Nachricht komplett neu verschlüsselt und geschrieben~~ — GEFIXT in v2.17.3-beta: Chat-History in separater `{hash}.history.enc` Datei. `AddChatMessage` triggert nur noch `SaveChatHistoryDebounced` (nur History-Dict wird verschlüsselt+geschrieben), nicht mehr `SaveProfileDebounced` (gesamtes Profil mit Keys, Contacts, Ratchet States). Migration von eingebetteter History beim ersten Login automatisch. `SaveProfileAsync` schließt ChatHistory per Swap-and-Restore vom Serialisieren aus.
- ~~Fire-and-forget Task.Run Saves ohne Fehlerbehandlung~~ — GEFIXT in Runde 7: Debounced Saves mit OnSaveError Event + FlushAsync bei App-Exit.

## Security Audit (2026-04-06, Runde 11) — Gefixte Findings

### Critical (Server)
[x] C1: pg-store.js fehlende Blob-Funktionen — `blobs` Tabelle im PG-Schema + `addBlob()`, `getBlob()`, `cleanupBlobs()` async Functions + Export. Server crashte bei `BLOB_UPLOAD`/`BLOB_FETCH` mit PostgreSQL-Backend.
[x] C2: TOCTOU Race in PG `addGroupMember`/`addPlaceMember` — Count-Check + Insert jetzt in `withTransaction()` mit `SELECT ... FOR UPDATE`. Verhindert concurrent Bypass der Gruppen-/Place-Größenlimits.

### High (Server)
[x] H1: `isScopeMember()`/`sendToScopeMembers()` sync/async Mismatch — Beide Funktionen `async`, alle `store.getPlace()`/`store.getGroup()` mit `await`. Alle 5 Call-Sites (handleGCallRequestToken, handleGCallAnnounce, handleGCallEnd) awaited. Auf PG-Backend war Membership-Check für Group Calls komplett umgangen (Promise ist truthy).
[x] H4: Redis pub/sub ohne Nachrichtenvalidierung — `KNOWN_ACTIONS` Set, `data.action`/`data.wsId`/`data.message` Type-Checks vor Dispatch. Verhindert Message-Injection über kompromittierte Redis-Instanz.

### High (Client v1)
[x] H5: Fehlende `usedOTPKId` in cli.js X3DH-Payloads — `usedOTPKId: x3dhResult.usedOTPKId` in beiden X3DH-Payloads (cmdSend + ginvite). Ohne dieses Feld wurde der 4. DH-Schritt (One-Time Pre-Key) beim Empfänger übersprungen → reduzierte Forward Secrecy bei CLI-initiierten Sessions.
[x] H6: Server Signing Key nicht validiert in index.js — `AUTH_OK` und `DEVICE_LINK_OK` Handler prüfen jetzt `decodeBase64().length === 32` (Pattern aus cli.js übernommen). Verhindert Pinning eines malformierten Keys.

### High (Client v2)
[x] H7: SRTP ohne Replay-Schutz — RFC 3711 64-Bit Sliding Replay Window in `SrtpSession.Unprotect()`. Duplikate, zu alte Pakete (>63 Positionen) und bereits gesehene Pakete werden rejected. `Array.Clear` → `CryptographicOperations.ZeroMemory` in `Dispose()`.
[x] H8: Attachment-Keys als immutable Strings — `AttachmentInfo.Key`/`Nonce` von `string` auf `byte[]` mit `Base64BytesJsonConverter` umgestellt. `BlobService.UploadAsync()` zeroot lokale Key-Kopie nach Erstellung. `MessageEnvelope.Encode/Decode` konvertiert an der Wire-Boundary. Wire-Format (JSON) bleibt base64.
[x] H9: `ApplyDelete` chatKey-Parsing Bug — `parts[0]` war `"place"` (Prefix), nicht die placeId. Fix: `parts[1]` + `parts.Length >= 3`. Admin-Deletes für Place-Nachrichten funktionierten vorher nie (fail-closed).

### Medium (Server)
[x] M1: PG Nonce-Check TOCTOU — `INSERT ... ON CONFLICT DO NOTHING RETURNING nonce_key` statt SELECT+INSERT. Atomisch: wenn RETURNING leer, ist der Nonce bereits gesehen.
[x] M3: Kein Rate-Limit auf `BLOB_FETCH` — `blobFetchLimits` Map (30/min pro User), Cleanup im Periodic Interval.
[x] M5: Owner kann Place verlassen — `handlePlaceLeave` prüft jetzt `senderId === place.creatorId` und sendet Error. Owner muss Ownership transferieren (noch nicht implementiert — Follow-up).
[x] M6: `activeCount`-Berechnung fehlerhaft — `activeConnectionCount` Counter statt `wss.clients.size - queue.filter(...)`. Inkrement in `setupConnection`, Dekrement im Close-Handler. Queue-Admission-Entscheidung nutzt den Counter.

### Medium (Client v1)
[x] M8: cli.js `_pendingBuffer` überspringt Sealed Messages — `handleSealed(pm)` Route für `SEALED_MESSAGE`/`pm.sealed` vor `handleGM`/`handleDM` in der Buffer-Drain-Loop.
[x] M9: Kein ANSI-Escaping in cli.js — `escapeContent()` Funktion (CSI/OSC/Control-Char-Filter, identisch zu index.js) + Anwendung auf alle untrusted Ausgaben (Sender-Names, Nachrichtentext, Gruppen-Names).

### Medium (Client v2)
[x] M12: DoubleRatchet Backup nicht gezeroed bei Erfolg — `ZeroBackup()` Hilfsfunktion zeroot RK, CKs, CKr, DHs.SecretKey und alle MKSKIPPED-Values. Aufgerufen im Success-Pfad von `Decrypt()`.
[x] M13: `BlobService` unbounded Cache — `MaxCacheEntries = 100`, `_cacheOrder` Queue für LRU-Eviction, evicted Entries werden mit `CryptographicOperations.ZeroMemory` gezeroed.
[x] M14: `SecureTextBox` kein Dispose — `OnDetachedFromVisualTree` Override zeroot `_buffer` via `CryptographicOperations.ZeroMemory`, disposed `_bufferLock`.
[x] M18: Reconnect TOCTOU — `volatile bool _isReconnecting` → `int _isReconnecting` mit `Interlocked.CompareExchange(ref _isReconnecting, 1, 0)` für atomisches Test-and-Set.

### Medium (Services)
[x] M4: `PlaceService.KickFromPlace` ohne lokalen Permission-Check — `HasPermission(place, Profile.UserId, PlaceRole.Admin)` Guard + Self-Kick- und Owner-Kick-Schutz.
[x] M5: `PlaceService.RemoveChannel` ohne lokalen Permission-Check — `HasPermission(place, Profile.UserId, PlaceRole.Admin)` Guard.
[x] M7: `PlaceService.UpdatePlaceProfile` ohne Permission-Check — `HasPermission(place, Profile.UserId, PlaceRole.Admin)` Guard (konsistent mit `UpdateRoleColors`).
[x] M16: `PlaceService.InviteToPlace` — Dokumentiert als intentional (Discord-like Default: jedes Mitglied kann einladen). Server erlaubt es ebenfalls. Kein PlacePermission-Flag vorhanden.
[x] M17: `GroupService.RekeyGroup` ohne Creator-Check — `group.Members[0] != Profile.UserId` Guard (Legacy-Convention: Members[0] = Creator).

### Low (Server)
[x] L2: `gcallTokenLimits` nicht im Periodic Cleanup — Hinzugefügt zum 60s-Cleanup-Interval.
[x] L3: `activeGroupCalls` Stale-Cleanup — Phantom-Participants (disconnected Users) werden entfernt, leere Calls gelöscht, 4h-Timeout. Im selben Cleanup-Interval.
[x] L4: `blobFetchLimits` Cleanup — Hinzugefügt zum 60s-Cleanup-Interval.
[x] L5: `store.cleanupBlobs()` fehlender `await` — Hinzugefügt im Cleanup-Interval.

## Security Audit (2026-04-06, Runde 12) — Gefixte Findings

### Medium (Crypto)
[x] M1: X3dh.Initiate() fehlende Low-Order-Point-Validierung — `IsValidDhPublicKey()` Checks für IdentityKey, SignedPreKey und OneTimePreKey vor DH-Operationen. Respond() hatte bereits Length-Checks, DH() rejectet All-Zeros Output — Initiate fehlte die Subgroup-Check-Schicht.
[x] M2: HKDF.Expand() Intermediate `t` nicht gezeroed — Vorheriger HMAC-Output wird jetzt per `CryptoService.ZeroOut()` gezeroed bevor neuer `t` zugewiesen wird. Finaler `t` nach Loop ebenfalls gezeroed.

### Medium (Server)
[x] M3: gcallPseudonym ohne callId — `callId` wird jetzt in den HMAC-Input gemischt (`PSEUDO:userId:roomName:callId`). Pseudonyme sind damit über verschiedene Call-Sessions hinweg unlinkable, selbst wenn der gleiche Room wiederverwendet wird.

### Medium (Services)
[x] M4: PlaceService.CreateChannel ohne Permission-Check — `HasPermission(place, Profile.UserId, PlaceRole.Admin)` Guard hinzugefügt. Nur Admins+ können Channels erstellen.

### Medium (Crypto, defense-in-depth)
[x] M5: DoubleRatchet.InitSender fehlende Low-Order-Point-Validierung — `IsValidDhPublicKey(recipientDHPub)` Check vor Ratchet-Initialisierung. DhRatchetStep hatte den Check bereits, InitSender fehlte.

### Medium (UI)
[x] M6: Nachrichten aus falscher Conversation in aktiver Chat-View — `OnMessageReceived` und `OnGroupMessageReceived` renderten Nachrichten unabhängig von der selektierten Conversation. Jetzt: `AddIncomingMessage` nur wenn `SelectedConversation` zum Sender/Gruppe passt. History-Persistierung war bereits korrekt (Service-Layer), `LoadChatHistory` zeigt Nachrichten beim Conversation-Wechsel.
[x] M7: `UnsafeRelaxedJsonEscaping` in GroupCallWindow WebView — Erlaubte `<`, `>`, `'` in JSON-String der per `ExecuteScriptAsync` injiziert wird. Ein bösartiger Server-Response (Token/RoomName) konnte aus dem JSON-Literal ausbrechen. Fix: `JavaScriptEncoder.Default` (escapet HTML-sensitive Zeichen).

### Medium (v1 JS Client)
[x] M8: crypto.js fehlende Counter-Limits — `ratchetEncrypt` und `senderKeyEncrypt` hatten kein Limit für `Ns`/`messageNumber`. v2 C# hat `MaxMessageNumber` (1B/10K), v1 JS konnte unbegrenzt weiter senden. Fix: `MAX_RATCHET_MESSAGE_NUMBER = 1_000_000_000`, `MAX_SENDER_KEY_MESSAGE_NUMBER = 10_000` mit throw bei Erschöpfung.

### Analysiert, kein Fix nötig
- Blob-Fetch ohne Zugriffskontrolle — Blobs sind E2EE (AES/nacl.secretbox), blobIds random (128-bit). Explizite ACLs würden erfordern, dass der Server Message-Routing-Metadaten persistiert (wer hat welchen blobId an wen geschickt), was die Sealed-Sender-Privacy untergraben würde. Risiko akzeptiert: Auth + Rate-Limit (30/min) + E2EE + Random-IDs sind ausreichend.
- SRTP uint32 Sequence-Overflow nach ~25h — Kein realistisches Szenario (Calls werden nicht 25h gehalten). Bei Bedarf Reconnect.
- NonceTracker Flooding — Hard-Cap (10k) + Stale-Eviction (>1h) bereits implementiert (Runde 8 H1).
- Delivery Token nicht Connection-gebunden — HMAC(secret, timestamp) ohne userId-Binding, 24h gültig. Risiko niedrig: gestohlenes Token erlaubt nur opaque Sealed Messages (ohne Ratchet-Session nicht entschlüsselbar).
- Place Member-List Client-Desync — Lokale Member-Liste aus E2EE Metadata kann bei Kick-Propagierung verzögert sein. Server-seitige Checks sind autoritativ, E2EE-Schicht bleibt korrekt.

## Security Audit (2026-06-09, Runde 14) — Gefixte Findings

Vollaudit (Server, v2 Crypto, v2 Services/Networking, v1 Client, Protokoll-Sync). Alle Invarianten aus Runden 1–13 wurden geprüft und sind intakt; `shared/protocol.js` und `rede-client/shared/protocol.js` sind identisch. Alle Findings dieser Runde wurden am 2026-06-09 gefixt.

### Critical (Server)
[x] C1: **Invite-Code-Gate auf PG-Backend komplett umgangen** — `server/index.js:976`: `if (!invite.validateAndConsume(inviteCode))` ohne `await`. `pg-store.useInviteCode()` ist async → Rückgabe ist ein Promise (immer truthy) → der Fail-Branch wird NIE genommen. Auf dem Produktions-Federation-Setup (`REDE_DB_BACKEND=pg`) registrierte JEDER beliebige 32-Hex-String einen Account. Regression aus Runde 11 C2 — Call-Site lief indirekt über `invite.js`. GEFIXT: `await` ergänzt (Handler war bereits async).

### High (Server)
[x] H1: **REGISTER faktisch ohne Rate-Limit hinter LB/I2P** — `index.js`: Connections via nginx-LB/I2P bekamen einen zufälligen per-Connection `ipHash` → 5/min-Limit und `MAX_CONNECTIONS_PER_IP` wirkungslos. GEFIXT dreiteilig: (a) hinter Trusted Proxy wird `connId` aus dem ersten `X-Forwarded-For`-Eintrag abgeleitet (Random-Fallback nur noch für I2P ohne XFF), per-IP-Cap greift damit auch hinter dem LB; (b) globaler REGISTER-Limiter 10/min (`REDE_REGISTER_LIMIT` env override) → REGISTER_FAIL „Server busy"; (c) 5 fehlgeschlagene Invite-Versuche pro Socket → `ws.terminate()`.

### High (Client v2)
[x] H2: **Server-Signatur-Enforcement wird beim Login nie scharfgeschaltet** — `RedeConnection.ServerSigningKey` wurde nur aus AUTH_OK/REGISTER_OK gesetzt und nur wenn der Server `serverSigningKey` mitschickte; bis AUTH_OK lief NICHTS durch die Signaturprüfung, ein Weglassen des Felds (oder der Key-Change-Warning-Branch) deaktivierte sie für die ganze Session. GEFIXT: `ArmPinnedServerSigningKey()` in `LoginAsync`/`LoginByHashAsync` direkt nach Profil-Decrypt (vor SendAuth); fehlendes Feld bei vorhandenem Pin → MITM-Warnung + Pin bleibt aktiv; Key-Change-Branch re-armt den ALTEN Pin (fail-closed, nie der neue Key, nie null).
[x] H3: **Place-Metadata von JEDEM Member ohne Autor-Autorisierung akzeptiert** — `PlaceService.HandlePlaceKeyReceived`: `placekey`-Ctrl von beliebigem Kontakt überschrieb MetadataKey, CreatorId, Roles, Bans etc. → Owner-Takeover/Self-Unban/Key-Rotation-DoS durch jedes (auch gekicktes) Mitglied. GEFIXT (nur Autorisierung — Authentizität liefert bereits der Ratchet-DM-Kanal): Placeholder (post-PLACE_INVITE) bleibt TOFU; etablierte Places verlangen Sender = CreatorId ODER Admin laut AKTUELLER lokaler Metadata (Payload wird erst in eine Scratch-Instanz decrypted, bevor State angefasst wird); Key-Rotation, CreatorId-Wechsel und Owner-Demotion verlangen Sender = aktueller Owner. Rejects erzeugen Warnungen.
[x] H4: **SRTP: ein gemeinsamer Master-Key für beide Richtungen + SSRC aus `Random.Shared`** — Keystream-Trennung hing allein am 31-bit-SSRC aus non-CSPRNG; bei Kollision konnte der Relay beide Richtungen per XOR rekonstruieren. GEFIXT: abwärtskompatibler SRTP-v2-Modus — Offer trägt `"v":2` in srtpParams, Callee ackt mit `srtpParams:{"v":2}` im CALL_ANSWER, nur bei Ack aktiv (Legacy-Pfad byte-identisch erhalten). v2 leitet per Richtung getrenntes Material ab: `HKDF(masterKey, salt=masterSalt, info="REDE-SRTPv2:offer"/"answer", 30)` → key[0..16]+salt[16..30]; getrennte Send/Recv-Sessions; SSRC jetzt aus `RandomNumberGenerator` (beide Modi); Buffers gezeroed.

### Medium (Client v2)
[x] M1: **PQ-One-Time-Prekeys unsigniert → PQ-Downgrade durch bösartigen Server** — `X3dh.Initiate` enkapsulierte gegen den PQ-OTPK aus dem Bundle ohne Signaturprüfung. GEFIXT: `GeneratePreKeyBundle` signiert jeden PQ-OTPK mit dem Identity-Signing-Key (gleiche Konstruktion wie `pqSignedPreKeySig`); Upload trägt index-aligniertes `pqOneTimePreKeySigs`-Array, Server normalisiert zu `{key,sig}` im Storage und serviert `pqOneTimePreKeySig` als Geschwisterfeld im PREKEY_BUNDLE (Wire bleibt String — alte Clients unberührt); `Initiate` verifiziert den Sig vor `Encapsulate`, bei FEHLENDEM Sig (alte Uploads) Fallback auf den signaturverifizierten PQ-SPK (unsigniertes Material wird nie benutzt), bei UNGÜLTIGEM Sig Abbruch. Replenishment erzeugt automatisch signierte Bundles.
[x] M2: **SRTP-Auth-Tag deckt ROC nicht ab** (RFC 3711 §4.2) — GEFIXT als Teil von SRTP v2: im v2-Modus wird der 4-Byte-BE-ROC an den HMAC-SHA1-Input angehängt (Protect + Unprotect); Legacy-Modus unverändert für Interop.
[x] M3: **BlobService-Cache nur auf blobId gekeyt** — Peer konnte fremde blobId mit anderem Key referenzieren und bekam den gecachten Klartext des Originals gerendert. GEFIXT: In-Memory-LRU-Key = `blobId + ":" + hex(SHA256(key||nonce))`; Disk-Cache bleibt blobId-keyed (Ciphertext, secretbox failt fail-closed bei falschem Key).

### Medium (Server)
[x] M4: **`store.addBlob` nicht awaited** — Client bekam `BLOB_UPLOAD_OK` auch bei Quota-/DB-Fehler. GEFIXT: `await` + try/catch mit `BLOB_UPLOAD_FAIL` im Fehlerfall.
[x] M5: **Sealed-Sender-Flood-Limit wirkungslos** — `sealedRateLimits` war auf den hinter LB/I2P zufälligen `ipHash` gekeyt. GEFIXT: zusätzlicher per-Empfänger-Limiter (`rlHash('sealed_to:'+to)`, 60/min), bestehender ipHash-Limiter bleibt; neue Map im Periodic Cleanup. (XFF-basierter `ipHash` aus H1a härtet zusätzlich den WSS-Pfad.)

### Low
[x] L1 (Server): `cluster.js` Admin-CLI nutzte hart SQLite-Store + `generateInvite()` ohne await (`[object Promise]`, Invite in falscher DB). GEFIXT: Store via `REDE_DB_BACKEND` ausgewählt, alle Store-/Invite-Aufrufe via `await Promise.resolve(...)`; auch der Worker-`gen-invite`- und `shutdown`-Pfad.
[x] L2 (Server): `dlog` loggte bei `REDE_DEBUG_USER` den displayName. GEFIXT: `register.ok` loggt nur noch `displayNameLen`; alle anderen dlog-Sites auditiert (loggen nur deviceIds/Counts/Close-Codes).
[x] L3 (v2 Crypto): Backup-Clones nicht gezeroed. GEFIXT: `DoubleRatchet.Decrypt` Skipped-Key-Pfad ruft `ZeroBackup(backup)`; `SenderKeys.Decrypt` zeroed `backup.ChainKey` im Success-Pfad.
[x] L4 (v2): Unaufgeforderte USER_LOOKUP_OK konnten Phantom-Kontakte injizieren. GEFIXT: Pending-Lookup-Tracker (ConcurrentDictionary, 60s TTL) in `ContactService`; `AddContact` (einziger Lookup-Sender, per Grep verifiziert) registriert vor dem Send; nicht-korrelierte Antworten werden komplett ignoriert.
[x] L5 (v1): `pendingDeviceDiscovery` unbounded. GEFIXT: Cap 16 Messages/User (drop-oldest, einmaliges Log) + max 32 pending User (index.js; cli.js hat keine Queue).
[x] L6 (v1): Rohe devices-Map gespeichert. GEFIXT: index.js USER_LOOKUP_OK-Refresh validiert jetzt wie cli.js (32-Byte-Keys, nur validiertes Subset); `store.addContact` validiert die initiale devices-Map via neuem `validateDeviceMap()` (wirkt für beide Clients).
[x] L7 (v1): `REDE_AUTO_PASSPHRASE` ohne Warnung. GEFIXT: index.js warnt wie cli.js bei REDE_PASS und löscht die Env-Var nach Gebrauch (`keepEnv` für den Register-Confirm-Doppel-Read).
[x] L8 (v1, beide): OTPK wurde VOR erfolgreichem X3DH-Decrypt konsumiert (OTPK-Drain durch undecryptbare Messages). GEFIXT: neues `store.peekOneTimePreKey()`; Removal+Save erst im Success-Branch und nur wenn der erfolgreiche Versuch den OTPK tatsächlich nutzte. `[otpkSecret, null]`-Fallback-Semantik unverändert.
[x] L9 (v2): `VerifyServerSignature` Regex-Stripping analysiert: provably safe — unescaptes `"serverSig"` kann in einem validen JSON-String-Value nicht vorkommen (Quotes dort sind `\"`), Over-Strip divergiert vom signierten Body und failt fail-closed. Invariante als Kommentar über `StripJsonField` dokumentiert, kein Behavior-Change.

### Funktional (kein Security-Issue)
[x] F1: Attachments > ~750 KB waren hochladbar (MaxBlobSize 10 MB), aber nie abrufbar (1-MB-Frame-Caps + base64 ×1.33). GEFIXT konsistent: `MaxBlobSize = 700_000` mit klarer Fehlermeldung; `RedeConnection.MaxOutgoingSize` 512 KB → 1 MB (passend zum Server-Frame-Cap), damit ein Max-Size-Upload (~933 KB base64) tatsächlich durchpasst. Echte >700-KB-Attachments brauchen künftig Chunking (Follow-up-Feature).
[x] F2: cli.js `ginvite` ohne New-Device-Acceptance-Branch. GEFIXT: Branch aus cmdSend portiert (Signed-Pre-Key-Sig-Verify, Device-Übernahme, `[SECURITY]`-Logs).
[x] F3 (v1): Cert-Pin blockte hart bei jedem Wechsel (Let's-Encrypt-Renewal = Lockout). GEFIXT analog v2-Policy: bei Pin-Mismatch wird `socket.authorized` (CA-Validierung, auch mit rejectUnauthorized:false befüllt) geprüft — CA-trusted → silent Re-Pin + Info-Log; sonst harter Block wie bisher. Self-signed-Server mit stabilem Cert unverändert; Ed25519-Server-Key bleibt der Identitätsanker.

### Bestätigt intakt (Stichproben Runde 14)
- Alle Größencaps (MAX_HEADER_SIZE/MAX_X3DH_SIZE/Sealed 32768), PQ-OTPK-Consumption, FIDO2-2FA-Gate (kein Skip, Challenge-Single-Use, signCount-Monotonie), Auth-Domain-Separation, sämtliche Rate-Limits aus Runden 1–13, Sender-Key-Signaturen mit contextId, Ratchet-Rollback (K2), Padding/Sealed-Sender/Fingerprint-Wire-Kompat C#↔JS, Update-Pfad (Ed25519+SHA256, Downgrade-Block, kein TOCTOU), Blob-Pfad-Traversal-Schutz, FIDO2-Sidecar ohne Klartext-Secrets, Enroll-Order-Invariante, scrypt+PMS-HKDF (kein Passphrase-only-Downgrade), CSPRNG überall außer SSRC (→ H4), FixedTimeEquals durchgängig, Protokoll-Kopien identisch.

## OFFENE Follow-ups (Stand 2026-06-09, nach Runde 14)

Bewusst NICHT in Runde 14 gefixt — brauchen Design/Protokoll-Arbeit. Bei der nächsten Feature-Runde einplanen:

[ ] FU1: **Attachment-Chunking für > 700 KB** — Runde-14-F1 hat die Limits konsistent gemacht (MaxBlobSize 700 KB, passend zu den 1-MB-WS-Frame-Caps), aber größere Anhänge (Fotos!) brauchen ein Chunking-Protokoll (BLOB_UPLOAD/BLOB_DATA in Teilen, Reassembly client-seitig, Hash über das Ganze). Server-seitig ist `MAX_BLOB_SIZE` (10 MB) bereits vorhanden.
[ ] FU2: **Automatischer, owner-gebundener Metadata-Rekey bei Kick/Ban (Audit-F4)** — gekickte/gebannte Place-Member behalten den alten MetadataKey und können bis zur manuellen Rotation weiterhin Gruppen-Call-SFrame-Keys ableiten (`GroupCallService.DeriveSFrameKey` nutzt den MetadataKey) und Metadata mitlesen. Kick/Ban sollte automatisch einen Rekey durch den Owner triggern; die Runde-14-H3-Autorisierung (Rotation nur vom Owner akzeptiert) ist die Voraussetzung dafür und ist deployed.
[ ] FU3: **Delivery-Token-Epoching** — Sealed-Sender-Delivery-Tokens sind 24h gültig und identitätslos; Runde-14-M5 hat das per-Empfänger-Limit ergänzt, aber kürzere Lifetime/rotierende Epochen würden gestohlene Tokens weiter entwerten (Analyse Runde 12: Risiko niedrig, opak ohne Ratchet-Session).
[ ] FU4: **Federation Cross-Node-Delivery-Edge-Cases** — `redis.sendToUser` meldet `sent=true` für Remote-Zustellung bevor `publishToWorker` bestätigt (fire-and-forget) + `getPendingMessages` löscht vor bestätigter Zustellung. Theoretische Silent-Drops, NICHT Ursache des 2026-05-26-Incidents (das waren die Size-Caps). Beide Stellen seit Runde-14-Audit unverändert offen.
[ ] FU5: **Place-Ownership-Transfer** — seit Runde 11 M5 kann der Owner den Place nicht verlassen; Transfer-Mechanismus fehlt weiterhin (jetzt relevanter, weil H3 owner-gebundene Operationen erzwingt).
[ ] FU6: **XFF-Vertrauensgrenze** — `connId` hinter Trusted Proxy nutzt den ERSTEN `X-Forwarded-For`-Eintrag; ein Client hinter einem appendenden Proxy kontrolliert den damit selbst. Backstop ist der globale REGISTER-Limiter (Runde 14 H1b). Sauberer: nginx so konfigurieren, dass es XFF überschreibt statt appended, und nur den letzten Hop lesen.
- Metadata Bandwidth Amplification — Volle Metadata (inkl. Emotes, Bans) bei jedem Admin-Action an alle Members. Inkrementelle Updates wären besser, aber architekturell aufwändig.
- ProfileEncryption Legacy-HMAC Triple-scrypt — Bis zu 3 scrypt-Derivationen bei Legacy-Profilen. Akzeptiert: betrifft nur Migration von sehr alten Profilen.
- SecureTextBox IME-Kompatibilität — Kein IME-Support (CJK-Input). Feature-Gap, kein Security-Issue.

## Security Audit (2026-04-17, Runde 13) — Findings

Fokus auf Änderungen seit Runde 12 (2026-04-06): FIFO ACK-Queue (v2.18.31–v2.18.39), Reaktionen/Edits/Deletes, RNNoise-Install, Passphrase-Change, pkexec-Update.

### High (Client v2)
[x] H1: **`_pendingAck` Multi-Device Misalignment** — `ChatService.SendMessage()` enqueued **einmal** pro Nachricht, aber `SendToDevice()` ruft `_conn.Send(MESSAGE)` einmal **pro Device** auf. Server ACKt jede MESSAGE einzeln (server/index.js:945). Für einen Kontakt mit N Devices kommt also N×ACK, aber nur 1 Enqueue — der 2. ACK dequeuet den Eintrag der **nächsten** Nachricht und stempelt ihr die falsche `msgId` zu. Folgen: Reaktionen/Edits/Deletes gehen an falsche Nachricht, Receiver-Side der FIFO sieht andere msgId als Sender. Fix: entweder pro Device-Send enqueuen, oder ACKs server-seitig pro Device bündeln, oder Tracking-Token im MESSAGE-Payload mitsenden.
[x] H2: **Control-Message Echo klaut Queue-Head** — `ChatService` / `GroupService` / `PlaceService` senden Control-Messages (Reactions/Edits/Deletes) ohne Enqueue, aber Server ACKt/echoet sie trotzdem. Wenn im `_pendingAck` eine reguläre Nachricht wartet, dequeuet der Control-ACK sie irrtümlich → falsche MsgId gestempelt. Race ist rennbar durch: (1) reguläre Text-Nachricht senden, (2) sofort auf älteren Msg reagieren — bevor ACK1 eintrifft klaut ACK2 den Eintrag. Fix: Control-Sends mit einem Sentinel enqueuen (`Msg=null` Marker) oder Control-ACKs serverseitig mit Flag markieren.
[x] H3: **RNNoise Native-Lib ohne Signaturverifikation** — `MainWindow.axaml.cs:2452` lädt `librnnoise.so` / `rnnoise.dll` direkt von GitHub-Release-URL via `HttpClient.GetByteArrayAsync()` und schreibt die Bytes nach `~/.rede/libs/`. Danach wird das Binary via `NativeLibrary.TryLoad()` in den Client-Prozess geladen — **ohne Ed25519-Signatur-Check, ohne SHA256-Hash-Check, ohne Größen-Check**. Ein kompromittiertes Release-Asset oder MITM-Angriff auf `api.github.com` resultiert in Remote Code Execution im REDE-Prozess mit allen Crypto-Keys und dem Mikrofon-Zugriff. `UpdateService` macht für den App-Binary genau das richtige (Ed25519 + SHA256SUMS) — dieselbe Kette muss hier greifen. Fix: Ed25519-Sig-Asset (`librnnoise.so.sig`) + `SHA256SUMS` prüfen; Build entsprechend erweitern (`scripts/sign-release.sh` läuft bereits über Ed25519-Keypair).

### Medium (Client v2)
[x] M1: **`_pendingAck` unbounded** — Weder `ChatService._pendingAck` noch `GroupService._pendingAck` noch `PlaceService._pendingAck` haben einen Cap (kontrastiert mit `_pendingOutgoing` = 100). Bei langem Server-Timeout oder stecken gebliebenen ACKs wächst die Queue monoton und hält harte Referenzen auf `ChatMessage`-Instanzen. Fix: Cap bei 500, ältester Eintrag droppen (mit Log).
[x] M2: **FIFO nicht auf Reconnect geleert** — Weder `_pendingAck` (Core-Layer) noch `PendingAckVms` (UI-Layer in MainViewModel) werden bei Verbindungsabbruch/Reconnect geleert. Nachrichten vor dem Disconnect, die keinen ACK bekommen haben, verbleiben in der Queue; die ersten N ACKs nach Reconnect dequeuen sie — Folge: msgId-Stamp-Misalignment für alle Nachrichten nach dem Reconnect. Fix: `Clear()` im `RedeConnection.OnDisconnected`-Handler für alle drei Services.
[x] M3: **pkexec Shell-Injection via `Environment.ProcessPath`** — `UpdateService.cs:340` baut den Update-Shell-Befehl via String-Interpolation mit Single-Quoting: `mv -f '{currentExe}' '{currentExe}.old' && mv -f '{tempNew}' '{currentExe}'`. Enthält `Environment.ProcessPath` einen Single-Quote (legal auf Linux-Filesystemen), bricht das Quoting auf; weil pkexec mit root-Rechten läuft, wird die Injection als root ausgeführt. `tempNew` ist via `Guid` sicher, `currentExe` nicht. Angriffsvoraussetzung: Angreifer kann REDE an Pfad mit `'` installieren (z.B. AppImage-Loader in kontrolliertem Home). Fix: Pfade via `--` + argv statt Shell-Interpolation übergeben, z.B. Polkit-Policy mit festem Action-Name.

### Low (Client v2)
[x] L1: **MarkdownTextBlock Strikethrough-Regex** — `~~([^~]+?)~~` ist durch den `RegexTimeout = 100ms` (MarkdownTextBlock.cs:27) und `MaxRenderLength = 8192` bereits eingegrenzt. Kein ReDoS-Risiko in Praxis, aber `[^~]+?` + umschließende Literale könnten bei pathologischem Input (`~~~~~~~~~…` mit 8k Zeichen) die 100ms knapp reißen — unkritisch da `RegexMatchTimeoutException` im try/catch landet und fallback auf Plain-Run rendert. AKZEPTIERT, kein Fix nötig (Timeout + Fallback decken den Fall ab; in Runde 14 erneut bestätigt).

### Analysiert, kein Fix nötig
- `ProfileStore.ChangePassphraseAsync` verifiziert selber nicht, aber Caller (`MainWindow.axaml.cs:2346`) prüft via `CryptographicOperations.FixedTimeEquals` gegen `_auth.Passphrase`. OK.
- `SecureTextBox.PasteFromClipboardAsync` — Clipboard-Text als managed string sitzt kurz auf dem Heap; unvermeidlich bei Paste. Window ist µs, kein Issue.
- `Program.cs` Single-Instance-Lock via `FileStream(FileShare.None)` — advisory lock funktioniert auf Linux und Windows. OK.
- `ContactService.RemoveContact` — löscht Contact + Chat-History + Ratchet-States + Sender-Keys atomar im Speicher, dann `FlushAsync`. OK.
- `UpdateService.IsValidExecutable` — Magic-Byte-Check (ELF/PE). Kein SIG-Ersatz, nur Sanity-Check vor Signatur-Verify. OK.

### Bekannte Einschränkungen (nicht gefixt)
- Status-Broadcast O(N²) — Architekturelles Trade-off für Privacy (Server kennt keine Contact-Listen). Rate-Limited 5/min pro User (Runde 6 H5). Kein Fix ohne Subscription-Modell, das den Social Graph leaken würde.
- ~~Sender Keys Legacy-Signatur-Fallback (ohne contextId)~~ — GEFIXT: Legacy-Fallback in `SenderKeys.cs` (C#) und `crypto.js` (JS) entfernt. Alle Clients seit v2.17.3 signieren mit contextId. Pre-v2.17.3 Nachrichten werden nicht mehr akzeptiert.
- ~~Ed25519 Release-Signatur~~ — GEFIXT: Keypair generiert, Public Key eingetragen, Signing-Script erstellt. Siehe oben.
- ~~OTP-Verbrauch vor Decrypt-Verifizierung~~ — GEFIXT: Deferred OTP Consumption. `fetchPreKeyBundle` gibt OTPs zurück ohne sie zu poppen. OTP-Reservierung pro Sender+Target (5min TTL) verhindert, dass verschiedene Sender denselben OTP bekommen. `consumeOTP(userId, deviceId, otpkId)` wird erst in `handleMessage` aufgerufen, wenn ein `x3dh.usedOTPKId` im tatsächlich gesendeten Message vorhanden ist. Abgelaufene Reservierungen werden im 60s-Cleanup recycled. Beide Stores (SQLite `store.js` + PostgreSQL `pg-store.js`) aktualisiert.