using System.Net.Http;
using System.Text.Json;

namespace Rede.Core.Services;

/// <summary>
/// Imports Discord server structure, message history, and emotes into a Rede Place.
/// Requires a Discord bot token with MESSAGE_CONTENT intent or a user token.
/// </summary>
public class DiscordImportService
{
    private const string ApiBase = "https://discord.com/api/v10";
    private const string CdnBase = "https://cdn.discordapp.com";
    private const int MaxEmoteSize = 64 * 1024; // 64KB
    private const int MaxEmotes = 50;
    private const int MessageBatchSize = 100;

    public event Action<string>? OnStatus;
    public event Action<string>? OnError;

    /// <summary>
    /// Fetched Discord server data — ready to be imported into a Place.
    /// </summary>
    public class DiscordServerData
    {
        public string Name { get; set; } = "";
        public string? IconUrl { get; set; }
        public byte[]? IconData { get; set; }
        public string? IconMimeType { get; set; }
        public List<DiscordCategory> Categories { get; set; } = new();
        public List<DiscordChannel> Channels { get; set; } = new();
        public List<DiscordEmote> Emotes { get; set; } = new();
    }

    public class DiscordCategory
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int Position { get; set; }
    }

    public class DiscordChannel
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Topic { get; set; }
        public string? CategoryId { get; set; }
        public int Position { get; set; }
        public int Type { get; set; } // 0=text, 2=voice, 4=category, 5=announcement
        public List<DiscordMessage> Messages { get; set; } = new();
    }

    public class DiscordMessage
    {
        public string Id { get; set; } = "";
        public string Author { get; set; } = "";
        public string Content { get; set; } = "";
        public DateTimeOffset Timestamp { get; set; }
        public string? ReferencedMessageId { get; set; }
        public string? ReferencedMessagePreview { get; set; }
        public string? ReferencedMessageAuthor { get; set; }
    }

    public class DiscordEmote
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Animated { get; set; }
        public byte[]? ImageData { get; set; }
        public string MimeType { get; set; } = "image/png";
    }

    /// <summary>
    /// Fetch all data from a Discord server. Call this first, then ImportToPlace().
    /// </summary>
    public async Task<DiscordServerData?> FetchServerAsync(string token, string guildId, bool includeMessages = true)
    {
        using var http = CreateClient(token);

        // 1. Fetch guild info
        OnStatus?.Invoke("Fetching server info...");
        JsonElement guild;
        try
        {
            var json = await http.GetStringAsync($"{ApiBase}/guilds/{guildId}");
            guild = JsonDocument.Parse(json).RootElement;
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Failed to fetch guild: {ex.Message}");
            return null;
        }

        var data = new DiscordServerData
        {
            Name = guild.GetProperty("name").GetString() ?? "Imported",
        };

        // Guild icon
        if (guild.TryGetProperty("icon", out var iconEl) && iconEl.GetString() is string iconHash)
        {
            var ext = iconHash.StartsWith("a_") ? "gif" : "png";
            data.IconUrl = $"{CdnBase}/icons/{guildId}/{iconHash}.{ext}?size=256";
            data.IconMimeType = ext == "gif" ? "image/gif" : "image/png";
            try
            {
                data.IconData = await http.GetByteArrayAsync(data.IconUrl);
                if (data.IconData.Length > 256 * 1024)
                    data.IconData = null; // too large
            }
            catch { data.IconData = null; }
        }

        // 2. Fetch channels
        OnStatus?.Invoke("Fetching channels...");
        try
        {
            var json = await http.GetStringAsync($"{ApiBase}/guilds/{guildId}/channels");
            var channels = JsonDocument.Parse(json).RootElement;

            foreach (var ch in channels.EnumerateArray())
            {
                var type = ch.GetProperty("type").GetInt32();
                var name = ch.GetProperty("name").GetString() ?? "";
                var id = ch.GetProperty("id").GetString() ?? "";
                var position = ch.TryGetProperty("position", out var posEl) ? posEl.GetInt32() : 0;

                if (type == 4) // Category
                {
                    data.Categories.Add(new DiscordCategory
                    {
                        Id = id,
                        Name = name,
                        Position = position,
                    });
                }
                else if (type == 0 || type == 5) // Text or Announcement
                {
                    var topic = ch.TryGetProperty("topic", out var topicEl) ? topicEl.GetString() : null;
                    var parentId = ch.TryGetProperty("parent_id", out var pEl) ? pEl.GetString() : null;

                    data.Channels.Add(new DiscordChannel
                    {
                        Id = id,
                        Name = name,
                        Topic = topic,
                        CategoryId = parentId,
                        Position = position,
                        Type = type,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Failed to fetch channels: {ex.Message}");
            return null;
        }

        data.Categories.Sort((a, b) => a.Position.CompareTo(b.Position));
        data.Channels.Sort((a, b) => a.Position.CompareTo(b.Position));
        OnStatus?.Invoke($"Found {data.Categories.Count} categories, {data.Channels.Count} text channels.");

        // 3. Fetch emotes
        OnStatus?.Invoke("Fetching emotes...");
        try
        {
            var json = await http.GetStringAsync($"{ApiBase}/guilds/{guildId}/emojis");
            var emojis = JsonDocument.Parse(json).RootElement;
            int emoteCount = 0;

            foreach (var e in emojis.EnumerateArray())
            {
                if (emoteCount >= MaxEmotes) break;

                var eid = e.GetProperty("id").GetString() ?? "";
                var ename = e.GetProperty("name").GetString() ?? "";
                var animated = e.TryGetProperty("animated", out var animEl) && animEl.GetBoolean();

                var ext = animated ? "gif" : "png";
                var mime = animated ? "image/gif" : "image/png";

                byte[]? imgData = null;
                try
                {
                    imgData = await http.GetByteArrayAsync($"{CdnBase}/emojis/{eid}.{ext}?size=64");
                    if (imgData.Length > MaxEmoteSize)
                        imgData = null;
                }
                catch { }

                if (imgData is not null)
                {
                    data.Emotes.Add(new DiscordEmote
                    {
                        Id = eid,
                        Name = ename,
                        Animated = animated,
                        ImageData = imgData,
                        MimeType = mime,
                    });
                    emoteCount++;
                }
            }
            OnStatus?.Invoke($"Fetched {data.Emotes.Count} emotes.");
        }
        catch (Exception ex)
        {
            OnStatus?.Invoke($"Emote fetch failed (continuing): {ex.Message}");
        }

        // 4. Fetch message history per channel
        if (includeMessages)
        {
            foreach (var ch in data.Channels)
            {
                OnStatus?.Invoke($"Fetching messages from #{ch.Name}...");
                try
                {
                    await FetchChannelMessagesAsync(http, ch);
                    OnStatus?.Invoke($"  #{ch.Name}: {ch.Messages.Count} messages");
                }
                catch (Exception ex)
                {
                    OnStatus?.Invoke($"  #{ch.Name}: failed ({ex.Message})");
                }

                // Rate limit courtesy
                await Task.Delay(500);
            }
        }

        OnStatus?.Invoke($"Discord import ready: \"{data.Name}\" — {data.Channels.Count} channels, {data.Emotes.Count} emotes, {data.Channels.Sum(c => c.Messages.Count)} messages total.");
        return data;
    }

    /// <summary>
    /// Import fetched Discord data into a new Place.
    /// Creates the place, waits for server confirmation, then populates channels/categories/emotes/messages.
    /// </summary>
    public async Task ImportToPlaceAsync(DiscordServerData data, PlaceService placeService, ChatService? chatService)
    {
        if (placeService.Profile is null) return;

        // Step 1: Create the place
        OnStatus?.Invoke($"Creating place \"{data.Name}\"...");
        var tcs = new TaskCompletionSource<string?>();

        void OnChanged()
        {
            // Find the newly created place by name
            var places = placeService.GetPlaces();
            if (places is null) return;
            foreach (var (id, p) in places)
            {
                if (p.Name == data.Name && p.Channels.Count <= 1)
                {
                    tcs.TrySetResult(id);
                    return;
                }
            }
        }

        placeService.OnPlacesChanged += OnChanged;
        placeService.CreatePlace(data.Name);

        // Wait for place creation (max 15s)
        var placeId = await WaitWithTimeout(tcs.Task, 15000);
        placeService.OnPlacesChanged -= OnChanged;

        if (placeId is null)
        {
            OnError?.Invoke("Place creation timed out.");
            return;
        }

        OnStatus?.Invoke($"Place created (id: {placeId[..8]}...)");

        // Step 2: Set place icon if available
        if (data.IconData is not null)
        {
            var iconBase64 = Convert.ToBase64String(data.IconData);
            placeService.UpdatePlaceProfile(placeId, null, iconBase64, data.IconMimeType, chatService);
            await Task.Delay(300);
        }

        // Step 3: Create categories
        foreach (var cat in data.Categories)
        {
            OnStatus?.Invoke($"Creating category \"{cat.Name}\"...");
            placeService.AddCategory(placeId, cat.Name, chatService);
            await Task.Delay(200);
        }

        // Build category ID → name lookup
        var catLookup = data.Categories.ToDictionary(c => c.Id, c => c.Name);

        // Step 4: Create channels (skip "general" — already exists)
        var channelIdMap = new Dictionary<string, string>(); // discord channel id → rede channel id

        // Map the default "general" channel
        var places2 = placeService.GetPlaces();
        if (places2 is not null && places2.TryGetValue(placeId, out var placeObj))
        {
            var existingGeneral = placeObj.Channels.FirstOrDefault(c => c.Value.Name == "general");
            if (existingGeneral.Key is not null)
            {
                // Map first Discord channel to existing general, or just keep it
                var firstDiscord = data.Channels.FirstOrDefault(c => c.Name == "general");
                if (firstDiscord is not null)
                    channelIdMap[firstDiscord.Id] = existingGeneral.Key;
            }
        }

        foreach (var ch in data.Channels)
        {
            if (channelIdMap.ContainsKey(ch.Id)) continue; // already mapped

            OnStatus?.Invoke($"Creating channel #{ch.Name}...");
            placeService.CreateChannel(placeId, ch.Name, chatService);
            await Task.Delay(300);

            // Find the newly created channel
            places2 = placeService.GetPlaces();
            if (places2 is not null && places2.TryGetValue(placeId, out var p))
            {
                var newCh = p.Channels.FirstOrDefault(c => c.Value.Name == ch.Name);
                if (newCh.Key is not null)
                    channelIdMap[ch.Id] = newCh.Key;
            }
        }

        // Step 5: Assign categories to channels
        foreach (var ch in data.Channels)
        {
            if (ch.CategoryId is not null && catLookup.TryGetValue(ch.CategoryId, out var catName)
                && channelIdMap.TryGetValue(ch.Id, out var redeChId))
            {
                placeService.SetChannelCategory(placeId, redeChId, catName, chatService);
                await Task.Delay(100);
            }
        }

        // Step 6: Set channel topics
        foreach (var ch in data.Channels)
        {
            if (!string.IsNullOrEmpty(ch.Topic) && channelIdMap.TryGetValue(ch.Id, out var redeChId))
            {
                placeService.SetChannelTopic(placeId, redeChId, ch.Topic, chatService);
                await Task.Delay(100);
            }
        }

        // Step 7: Import emotes
        foreach (var emote in data.Emotes)
        {
            if (emote.ImageData is null) continue;
            OnStatus?.Invoke($"Adding emote :{emote.Name}:...");
            placeService.AddEmote(placeId, emote.Name, emote.ImageData, emote.MimeType, chatService);
            await Task.Delay(200);
        }

        // Step 8: Import message history (as formatted text from importing user)
        int totalMsgs = 0;
        foreach (var ch in data.Channels)
        {
            if (ch.Messages.Count == 0) continue;
            if (!channelIdMap.TryGetValue(ch.Id, out var redeChId)) continue;

            OnStatus?.Invoke($"Importing {ch.Messages.Count} messages into #{ch.Name}...");

            // Send messages in chronological order, batched as formatted text
            foreach (var batch in BatchMessages(ch.Messages, 20))
            {
                var formatted = FormatMessageBatch(batch);
                placeService.SendChannelMessage(placeId, redeChId, formatted);
                totalMsgs += batch.Count;
                await Task.Delay(300);
            }
        }

        OnStatus?.Invoke($"Import complete: \"{data.Name}\" — {channelIdMap.Count} channels, {data.Emotes.Count} emotes, {totalMsgs} messages imported.");
    }

    private static string FormatMessageBatch(List<DiscordMessage> messages)
    {
        var lines = new List<string>();
        foreach (var msg in messages)
        {
            if (string.IsNullOrWhiteSpace(msg.Content)) continue;
            var time = msg.Timestamp.ToString("yyyy-MM-dd HH:mm");
            if (msg.ReferencedMessagePreview is not null)
                lines.Add($"> _{msg.ReferencedMessageAuthor ?? "?"}: {msg.ReferencedMessagePreview}_");
            lines.Add($"[{time}] *{msg.Author}*: {msg.Content}");
        }
        return string.Join("\n", lines);
    }

    private static IEnumerable<List<DiscordMessage>> BatchMessages(List<DiscordMessage> messages, int batchSize)
    {
        for (int i = 0; i < messages.Count; i += batchSize)
        {
            yield return messages.GetRange(i, Math.Min(batchSize, messages.Count - i));
        }
    }

    private async Task FetchChannelMessagesAsync(HttpClient http, DiscordChannel channel, int maxMessages = 0)
    {
        string? before = null;
        int totalFetched = 0;

        while (maxMessages <= 0 || totalFetched < maxMessages)
        {
            var url = $"{ApiBase}/channels/{channel.Id}/messages?limit={MessageBatchSize}";
            if (before is not null)
                url += $"&before={before}";

            var json = await http.GetStringAsync(url);
            var messages = JsonDocument.Parse(json).RootElement;
            int count = 0;

            foreach (var m in messages.EnumerateArray())
            {
                var content = m.TryGetProperty("content", out var cEl) ? cEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(content)) continue;

                var author = "Unknown";
                if (m.TryGetProperty("author", out var aEl))
                {
                    if (aEl.TryGetProperty("global_name", out var gnEl) && gnEl.GetString() is string gn)
                        author = gn;
                    else if (aEl.TryGetProperty("username", out var unEl) && unEl.GetString() is string un)
                        author = un;
                }

                var timestamp = m.TryGetProperty("timestamp", out var tEl)
                    ? DateTimeOffset.Parse(tEl.GetString()!)
                    : DateTimeOffset.UtcNow;

                var msgId = m.GetProperty("id").GetString() ?? "";

                // Parse reply reference
                string? refMsgId = null;
                string? refPreview = null;
                string? refAuthor = null;
                if (m.TryGetProperty("message_reference", out var refEl)
                    && refEl.TryGetProperty("message_id", out var refIdEl))
                {
                    refMsgId = refIdEl.GetString();
                }
                if (m.TryGetProperty("referenced_message", out var refMsg) && refMsg.ValueKind == JsonValueKind.Object)
                {
                    var refContent = refMsg.TryGetProperty("content", out var rcEl) ? rcEl.GetString() : null;
                    refPreview = refContent is not null && refContent.Length > 100 ? refContent[..100] : refContent;
                    if (refMsg.TryGetProperty("author", out var raEl))
                    {
                        if (raEl.TryGetProperty("global_name", out var rgnEl) && rgnEl.GetString() is string rgn)
                            refAuthor = rgn;
                        else if (raEl.TryGetProperty("username", out var runEl) && runEl.GetString() is string run)
                            refAuthor = run;
                    }
                }

                channel.Messages.Add(new DiscordMessage
                {
                    Id = msgId,
                    Author = author,
                    Content = content,
                    Timestamp = timestamp,
                    ReferencedMessageId = refMsgId,
                    ReferencedMessagePreview = refPreview,
                    ReferencedMessageAuthor = refAuthor,
                });

                before = msgId;
                count++;
            }

            totalFetched += count;

            // Less than batch size means we've reached the beginning
            if (count < MessageBatchSize) break;

            // Rate limit courtesy
            await Task.Delay(500);
        }

        // Reverse so oldest first
        channel.Messages.Reverse();
    }

    private static async Task<string?> WaitWithTimeout(Task<string?> task, int timeoutMs)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeoutMs));
        return completed == task ? await task : null;
    }

    private static HttpClient CreateClient(string token)
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "Rede-DiscordImport");
        // Support both bot tokens and user tokens
        if (token.StartsWith("Bot "))
            http.DefaultRequestHeaders.Add("Authorization", token);
        else
            http.DefaultRequestHeaders.Add("Authorization", $"Bot {token}");
        http.Timeout = TimeSpan.FromSeconds(30);
        return http;
    }
}
