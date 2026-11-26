using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using StardewModdingAPI;

namespace ValleyBuddies.Bridge;

/// <summary>Locates AliveNpcs internal types by reflection at startup.</summary>
public sealed class ValleyBuddiesReflection
{
    private readonly IMonitor _monitor;

    public bool       IsAvailable             { get; private set; }
    public Type?      GeminiServiceType        { get; private set; }
    public MethodInfo? ParseReactionMethod      { get; private set; }
    public MethodInfo? EvaluateResponseMethod   { get; private set; }

    public ValleyBuddiesReflection(IMonitor monitor) => _monitor = monitor;

    public bool Resolve()
    {
        try
        {
            GeminiServiceType = AccessTools.TypeByName("AliveNpcs.Services.GeminiService");
            if (GeminiServiceType == null) return Fail("AliveNpcs.Services.GeminiService not found");

            ParseReactionMethod = AccessTools.Method(GeminiServiceType, "ParseReactionResponse",
                new[] { typeof(string) });
            if (ParseReactionMethod == null) return Fail("ParseReactionResponse(string) not found");

            EvaluateResponseMethod = GeminiServiceType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "EvaluateResponseAsync");
            if (EvaluateResponseMethod == null) return Fail("EvaluateResponseAsync not found");

            bool hasNpc    = EvaluateResponseMethod.GetParameters().Any(p => p.Name == "npcName");
            bool hasMod    = EvaluateResponseMethod.GetParameters().Any(p => p.Name == "modContext");
            if (!hasNpc || !hasMod) return Fail("EvaluateResponseAsync missing npcName/modContext params");

            IsAvailable = true;
            _monitor.Log("ValleyBuddies bridge resolved.", LogLevel.Debug);
            return true;
        }
        catch (Exception ex)
        {
            return Fail("exception: " + ex.Message);
        }
    }

    /// <summary>
    /// Writes an incoming message into the AliveNpcs Farm Computer Messenger app.
    /// </summary>
    public static void AddMessengerMessage(string npcName, string playerName, string message)
    {
        try
        {
            var modEntryType = AccessTools.TypeByName("AliveNpcs.ModEntry");
            if (modEntryType == null) return;
            var instanceProp = modEntryType.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var instance = instanceProp?.GetValue(null);
            if (instance == null) return;

            var convField = modEntryType.GetField("_conversations", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var convManager = convField?.GetValue(instance);
            if (convManager == null) return;

            var entryType = AccessTools.TypeByName("AliveNpcs.Models.ConversationEntry");
            if (entryType == null) return;

            var entry = Activator.CreateInstance(entryType);
            if (entry == null) return;

            entryType.GetProperty("Day")?.SetValue(entry, StardewValley.Game1.dayOfMonth);
            entryType.GetProperty("Season")?.SetValue(entry, StardewValley.Game1.currentSeason);
            entryType.GetProperty("Year")?.SetValue(entry, StardewValley.Game1.year);
            entryType.GetProperty("ContextType")?.SetValue(entry, "messenger_first");
            entryType.GetProperty("ContextId")?.SetValue(entry, "morning_farewell");
            entryType.GetProperty("ContextName")?.SetValue(entry, "morning_farewell");
            entryType.GetProperty("NpcMessage")?.SetValue(entry, message);
            entryType.GetProperty("PlayerResponse")?.SetValue(entry, "");
            entryType.GetProperty("NpcReaction")?.SetValue(entry, message);
            entryType.GetProperty("Emotion")?.SetValue(entry, "love");
            entryType.GetProperty("FriendshipPointsEarned")?.SetValue(entry, 15);
            entryType.GetProperty("PlayerName")?.SetValue(entry, playerName);
            entryType.GetProperty("IsRemote")?.SetValue(entry, true);

            var addConvMethod = convManager.GetType().GetMethod("AddConversation", new[] { typeof(string), entryType });
            addConvMethod?.Invoke(convManager, new object[] { npcName, entry });
        }
        catch { }
    }

    private bool Fail(string reason)
    {
        IsAvailable = false;
        _monitor.Log($"ValleyBuddies DISABLED: {reason}. AliveNpcs keeps working normally.", LogLevel.Warn);
        return false;
    }
}
