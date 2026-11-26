using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using ValleyBuddies.Multiplayer;
using ValleyBuddies.Utilities;

namespace ValleyBuddies.Systems;

public sealed class SavedMailStore
{
    public Dictionary<string, string> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DynamicMailService
{
    public static DynamicMailService? Instance { get; internal set; }

    private const string SaveDataKey = "ValleyBuddies_DynamicMail";
    private readonly IModHelper _helper;
    private readonly Dictionary<string, string> _mailEntries = new(StringComparer.OrdinalIgnoreCase);

    public DynamicMailService(IModHelper helper)
    {
        _helper = helper;
        Instance = this;
    }

    public void ClearState()
    {
        _mailEntries.Clear();
    }

    public void LoadFromSave()
    {
        _mailEntries.Clear();
        if (!Context.IsMainPlayer) return;
        var saved = _helper.Data.ReadSaveData<SavedMailStore>(SaveDataKey);
        if (saved?.Entries != null)
        {
            foreach (var kvp in saved.Entries)
                _mailEntries[kvp.Key] = kvp.Value;
        }
        if (PruneDeliveredMailEntries())
        {
            _helper.Data.WriteSaveData(SaveDataKey, new SavedMailStore { Entries = new Dictionary<string, string>(_mailEntries) });
        }
        InvalidateLocalizedMailCache();
    }

    private bool PruneDeliveredMailEntries()
    {
        if (!Context.IsMainPlayer || _mailEntries.Count == 0) return false;

        var pendingTomorrow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var alreadyReceived = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var farmer in Game1.getAllFarmers())
        {
            if (farmer?.mailForTomorrow != null)
            {
                foreach (var m in farmer.mailForTomorrow)
                {
                    if (!string.IsNullOrWhiteSpace(m)) pendingTomorrow.Add(m);
                }
            }
            if (farmer?.mailbox != null)
            {
                foreach (var m in farmer.mailbox)
                {
                    if (!string.IsNullOrWhiteSpace(m)) pendingTomorrow.Add(m);
                }
            }
            if (farmer?.mailReceived != null)
            {
                foreach (var m in farmer.mailReceived)
                {
                    if (!string.IsNullOrWhiteSpace(m)) alreadyReceived.Add(m);
                }
            }
        }

        bool changed = false;
        foreach (var key in _mailEntries.Keys.ToList())
        {
            if (key.StartsWith("ValleyBuddies_", StringComparison.OrdinalIgnoreCase)
                && !pendingTomorrow.Contains(key)
                && alreadyReceived.Contains(key))
            {
                _mailEntries.Remove(key);
                changed = true;
            }
        }

        const int maxRetainedEntries = 200;
        if (_mailEntries.Count > maxRetainedEntries)
        {
            foreach (var key in _mailEntries.Keys.ToList())
            {
                if (_mailEntries.Count <= maxRetainedEntries) break;
                if (!pendingTomorrow.Contains(key))
                {
                    _mailEntries.Remove(key);
                    changed = true;
                }
            }
        }

        return changed;
    }

    public Dictionary<string, string> GetAllActiveMailEntries() => new(_mailEntries);

    public void MergeSyncedEntriesFromHost(Dictionary<string, string> incoming)
    {
        if (incoming == null || incoming.Count == 0) return;
        foreach (var kvp in incoming)
            _mailEntries[kvp.Key] = kvp.Value;
        InvalidateLocalizedMailCache();
    }

    public void RegisterAndScheduleLetter(
        string mailId,
        string mailBodyWithTitle,
        long targetPlayerId,
        MultiplayerSyncService? syncService)
    {
        if (string.IsNullOrWhiteSpace(mailId) || string.IsNullOrWhiteSpace(mailBodyWithTitle))
            return;

        _mailEntries[mailId] = mailBodyWithTitle;

        if (Context.IsMainPlayer)
        {
            PruneDeliveredMailEntries();
            _helper.Data.WriteSaveData(SaveDataKey, new SavedMailStore { Entries = new Dictionary<string, string>(_mailEntries) });
            syncService?.BroadcastMailEntries(new Dictionary<string, string> { [mailId] = mailBodyWithTitle });
        }

        InvalidateLocalizedMailCache();

        if (syncService != null && targetPlayerId != 0)
            syncService.QueueMailForSpecificPlayer(targetPlayerId, mailId);
        else
            Game1.player.mailForTomorrow.Add(mailId);
    }

    public void QueueLetter(string npcName, string title, string message, string? itemQualifiedId = null, long targetPlayerId = 0, MultiplayerSyncService? syncService = null)
    {
        string safeNpc = SanitizeId(npcName);
        string mailId = $"ValleyBuddies_Letter_{safeNpc}_Y{Game1.year}_{Game1.currentSeason}_D{Game1.dayOfMonth}_{Game1.random.Next(1000, 9999)}";
        string body = message;
        if (!string.IsNullOrWhiteSpace(itemQualifiedId))
        {
            body += $"%item id {itemQualifiedId} 1 %%";
        }
        body += $"[#]{title}";
        RegisterAndScheduleLetter(mailId, body, targetPlayerId != 0 ? targetPlayerId : Game1.player.UniqueMultiplayerID, syncService);
    }

    public void ScheduleSingleSleepoverLetter(string npcName, long hostFarmerId, MultiplayerSyncService? syncService)
    {
        if (!Context.IsMainPlayer || ModEntry.Config?.EnableSleepoverCoffeeLetter == false || string.IsNullOrWhiteSpace(npcName))
            return;

        NPC? npc = Game1.getCharacterFromName(npcName);
        string displayName = npc?.displayName ?? npcName;
        string safeNpc = SanitizeId(npcName);
        string mailId = $"ValleyBuddies_Sleepover_{safeNpc}_Y{Game1.year}_{Game1.currentSeason}_D{Game1.dayOfMonth}";

        // Fix repeatability: remove mailId from mailReceived cache so the letter can be sent again each sleepover
        var host = Game1.GetPlayer(hostFarmerId, onlyOnline: false) ?? Game1.player;
        if (host.mailReceived.Contains(mailId))
            host.mailReceived.Remove(mailId);
        // Also clear from mailForTomorrow to avoid duplicate queuing
        host.mailForTomorrow.RemoveWhere(m => m == mailId || m == mailId + "%&NL&%");

        // Invalidate cache so the new letter body is picked up fresh
        _helper.GameContent.InvalidateCache(asset => asset.NameWithoutLocale.IsEquivalentTo("Data/Mail"));

        string body = BuildSleepoverCoffeeLetter(npcName, displayName);
        RegisterAndScheduleLetter(mailId, body, hostFarmerId, syncService);
    }

    public void ScheduleMorningSleepoverLetters(
        IReadOnlyDictionary<string, long> sleepoverGuests,
        MultiplayerSyncService? syncService)
    {
        if (!Context.IsMainPlayer || ModEntry.Config?.EnableSleepoverCoffeeLetter == false || sleepoverGuests == null || sleepoverGuests.Count == 0)
            return;

        foreach (var kvp in sleepoverGuests)
            ScheduleSingleSleepoverLetter(kvp.Key, kvp.Value, syncService);
    }

    public void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (_mailEntries.Count == 0) return;
        if (e.NameWithoutLocale.IsEquivalentTo("Data/Mail"))
        {
            e.Edit(asset =>
            {
                var data = asset.AsDictionary<string, string>().Data;
                foreach (var kvp in _mailEntries)
                    data[kvp.Key] = kvp.Value;
            });
        }
    }

    private void InvalidateLocalizedMailCache()
    {
        _helper.GameContent.InvalidateCache(asset => asset.NameWithoutLocale.IsEquivalentTo("Data/Mail"));
    }

    private static string SanitizeId(string raw)
    {
        Span<char> buf = stackalloc char[raw.Length];
        int len = 0;
        foreach (char c in raw)
            if (char.IsLetterOrDigit(c) || c == '_') buf[len++] = c;
        return len > 0 ? new string(buf[..len]) : "Friend";
    }

    private static string BuildSleepoverCoffeeLetter(string npcName, string displayName)
    {
        string safeKey = SanitizeId(npcName).ToLowerInvariant();
        string key = $"mail.sleepover.{safeKey}";
        string letterTemplate = I18n.Get(key, new { npcName = displayName });
        if (letterTemplate == key)
        {
            letterTemplate = I18n.Get("mail.sleepover.default", new { npcName = displayName });
        }
        string personalized = letterTemplate.Replace("{{npcName}}", displayName);
        string titleTemplate = I18n.Get("mail.sleepover.title", new { npcName = displayName });
        string title = titleTemplate.Replace("{{npcName}}", displayName);

        return $"{personalized}%item id (O)395 1 %%[#]{title}";
    }
}
