using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Quests;
using ValleyBuddies.Execution;
using ValleyBuddies.Systems;

namespace ValleyBuddies.Bridge;

/// <summary>
/// Harmony patches that handle NPC action interception, daily conversation bypass,
/// concurrent dialogue support, and instant vanilla fallback.
/// Supports both Offline Standalone mode and AliveNpcs Integrated mode.
/// </summary>
public static class DialogueBypassPatches
{
    private static IMonitor? _monitor;
    private static object?   _dialogueManagerInstance;
    private static MethodInfo? _getDialogueStateMethod;
    private static PropertyInfo? _currentNpcProp;
    private static readonly HashSet<string> _dailyAiTalkedPlayers = new(StringComparer.OrdinalIgnoreCase);

    private static readonly PerScreen<string?> _lastActiveItemId = new();
    private static readonly PerScreen<int> _lastActiveItemStack = new();
    private static readonly PerScreen<int> _lastQuestProgress = new();

    public static bool AllowVanillaTalkOnce
    {
        get => OfflineConversationBridge.AllowVanillaTalkOnce;
        set => OfflineConversationBridge.AllowVanillaTalkOnce = value;
    }

    public static void ResetDaily()
    {
        _dailyAiTalkedPlayers.Clear();
    }

    /// <summary>
    /// Ensures that talking to an NPC in AI mode counts toward the daily talk friendship points (+20) exactly like vanilla talk.
    /// </summary>
    public static void AwardDailyTalkFriendshipIfNeeded(NPC npc, Farmer? who)
    {
        if (npc == null) return;
        Farmer player = who ?? Game1.player;
        if (player == null) return;

        string key = $"{player.UniqueMultiplayerID}_{npc.Name}_{Game1.Date?.TotalDays ?? Game1.dayOfMonth}";
        if (_dailyAiTalkedPlayers.Contains(key)) return;
        _dailyAiTalkedPlayers.Add(key);

        try
        {
            if (player.friendshipData.TryGetValue(npc.Name, out var fd))
            {
                if (!fd.TalkedToToday)
                {
                    fd.TalkedToToday = true;
                    player.changeFriendship(20, npc);
                    _monitor?.Log($"Awarded +20 friendship and marked TalkedToToday for {player.Name} with {npc.Name}.", LogLevel.Debug);
                }
            }
            else
            {
                player.changeFriendship(20, npc);
                if (player.friendshipData.TryGetValue(npc.Name, out var fdNew))
                {
                    fdNew.TalkedToToday = true;
                }
                _monitor?.Log($"Awarded initial friendship and marked TalkedToToday for {player.Name} with {npc.Name}.", LogLevel.Debug);
            }
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Error awarding daily AI friendship to {npc.Name}: {ex.Message}", LogLevel.Trace);
        }
    }

    public static void Apply(Harmony harmony, IMonitor monitor)
    {
        _monitor = monitor;
        try
        {
            // 1. Always active patch on NPC.checkAction for dual mode
            var checkAction = AccessTools.Method(typeof(NPC), nameof(NPC.checkAction), new[] { typeof(Farmer), typeof(GameLocation) });
            if (checkAction != null)
            {
                harmony.Patch(
                    original: checkAction,
                    prefix: new HarmonyMethod(typeof(DialogueBypassPatches), nameof(CheckAction_Prefix)),
                    postfix: new HarmonyMethod(typeof(DialogueBypassPatches), nameof(CheckAction_Postfix))
                );
                _monitor.Log("DialogueBypassPatches applied: NPC.checkAction dual-mode handler active.", LogLevel.Debug);
            }

            // 2. AliveNpcs specific patches (if AliveNpcs is present)
            Type? dialoguePatchesType = AccessTools.TypeByName("AliveNpcs.Patches.DialoguePatches");
            if (dialoguePatchesType != null)
            {
                _currentNpcProp = AccessTools.Property(dialoguePatchesType, "CurrentNpc");

                var checkActionPrefix = AccessTools.Method(dialoguePatchesType, "NpcCheckAction_Prefix");
                if (checkActionPrefix != null)
                {
                    harmony.Patch(checkActionPrefix,
                        prefix: new HarmonyMethod(typeof(DialogueBypassPatches), nameof(DialoguePatches_NpcCheckAction_Prefix)));
                }

                var onClosedMethod = AccessTools.Method(dialoguePatchesType, "OnDialogueBoxClosed");
                if (onClosedMethod != null)
                {
                    harmony.Patch(onClosedMethod,
                        postfix: new HarmonyMethod(typeof(DialogueBypassPatches), nameof(DialoguePatches_OnDialogueBoxClosed_Postfix)));
                }
            }

            Type? dialogueManagerType = AccessTools.TypeByName("AliveNpcs.Managers.DialogueManager");
            if (dialogueManagerType != null)
            {
                var canStartMethod = AccessTools.Method(dialogueManagerType, "CanStartDailyDialogue", new[] { typeof(string) });
                if (canStartMethod != null)
                {
                    harmony.Patch(canStartMethod,
                        prefix: new HarmonyMethod(typeof(DialogueBypassPatches), nameof(CanStartDailyDialogue_Prefix)));
                }

                var canResumeMethod = AccessTools.Method(dialogueManagerType, "CanResumeDailyConversation", new[] { typeof(string) });
                if (canResumeMethod != null)
                {
                    harmony.Patch(canResumeMethod,
                        prefix: new HarmonyMethod(typeof(DialogueBypassPatches), nameof(CanResumeDailyConversation_Prefix)));
                }

                _getDialogueStateMethod = AccessTools.Method(dialogueManagerType, "GetNpcDialogueState", new[] { typeof(string) })
                    ?? AccessTools.FirstMethod(dialogueManagerType, m => m.Name == "GetNpcDialogueState");
            }
        }
        catch (Exception ex)
        {
            _monitor.Log($"DialogueBypassPatches note: {ex.Message}", LogLevel.Trace);
        }
    }

    public static bool CheckAction_Prefix(NPC __instance, Farmer who, GameLocation l, ref bool __result)
    {
        try
        {
            if (ModEntry.Config?.EnableMod == false) return true;
            if (__instance == null || who == null) return true;

            // v4.5.3: Si es el cónyuge durmiendo en la cama dentro de la casa y el jugador interactúa con él
            if (__instance.isSleeping.Value && (__instance.currentLocation is StardewValley.Locations.FarmHouse || __instance.currentLocation is StardewValley.Locations.Cabin || __instance.currentLocation?.NameOrUniqueName.Contains("Cabin") == true))
            {
                if (Execution.NpcActionExecutor.Instance != null && Execution.NpcActionExecutor.Instance.TryGetSpouseHomeState(__instance.Name, out var sState))
                {
                    sState.WokeUpByPlayerTonight = true;
                    Execution.NpcActionExecutor.WakeAndExtractNpcFromBed(__instance, __instance.currentLocation, null, who);
                    int v = Game1.random.Next(1, 3);
                    string whisper = Utilities.I18n.Get($"spouse.home.sleep_whisper.v{v}", new { playerName = who.displayName ?? who.Name, npcName = __instance.displayName });
                    ModEntry.ConversationBridge.ShowCompanionBubble(__instance, whisper, 3500);
                    __result = true;
                    return false;
                }
            }

            if (ModEntry.ConversationBridge.Mode == ConversationBridgeMode.OfflineStandalone)
            {
                if (OfflineConversationBridge.AllowVanillaTalkOnce)
                {
                    return true;
                }

                bool isCompanion = NpcActionExecutor.Instance != null && NpcActionExecutor.Instance.IsManagedCompanion(__instance.Name);
                if (isCompanion)
                {
                    if (HasGiftOrQuestForNpc(who, __instance))
                    {
                        return true;
                    }

                    __result = true;
                    DelayedAction.functionAfterDelay(() =>
                    {
                        NpcActionExecutor.Instance?.HandlePlayerInteraction(__instance, who);
                    }, 50);
                    return false;
                }

                // Not a companion: capture interaction telemetry
                _lastActiveItemId.Value = who.ActiveObject?.QualifiedItemId;
                _lastActiveItemStack.Value = who.ActiveObject?.Stack ?? 0;
                _lastQuestProgress.Value = CaptureQuestProgress(who, __instance);
                return true;
            }
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Error in CheckAction_Prefix: {ex}", LogLevel.Trace);
        }

        return true;
    }

    public static void CheckAction_Postfix(NPC __instance, Farmer who, GameLocation l, ref bool __result)
    {
        try
        {
            if (__instance == null || who == null) return;

            if (ModEntry.ConversationBridge.Mode == ConversationBridgeMode.OfflineStandalone)
            {
                int currentStack = who.ActiveObject?.Stack ?? 0;
                string? currentId = who.ActiveObject?.QualifiedItemId;

                bool objectConsumedOrReduced = _lastActiveItemStack.Value > 0 &&
                    (currentId != _lastActiveItemId.Value || currentStack < _lastActiveItemStack.Value);

                int currentQuest = CaptureQuestProgress(who, __instance);
                bool questAdvanced = currentQuest > _lastQuestProgress.Value;

                if (objectConsumedOrReduced || questAdvanced)
                {
                    PostDialogueInviteMonitor.SuppressCurrentInteraction(__instance.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Error in CheckAction_Postfix: {ex}", LogLevel.Trace);
        }
    }

    private static bool HasGiftOrQuestForNpc(Farmer who, NPC npc)
    {
        if (who == null || npc == null) return false;

        if (who.ActiveObject != null)
        {
            var obj = who.ActiveObject;
            if (obj.canBeGivenAsGift())
                return true;

            if (obj.QualifiedItemId == "(O)458" || obj.QualifiedItemId == "(O)460" || obj.QualifiedItemId == "(O)809")
                return true;

            if (who.questLog != null)
            {
                foreach (var q in who.questLog)
                {
                    if (q is ItemDeliveryQuest idq && !idq.completed.Value)
                    {
                        if (string.Equals(idq.target.Value, npc.Name, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(idq.target.Value, npc.displayName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (idq.ItemId.Value == obj.QualifiedItemId || idq.ItemId.Value == obj.ItemId)
                                return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    private static int CaptureQuestProgress(Farmer who, NPC npc)
    {
        int progress = 0;
        try
        {
            if (who.questLog != null)
            {
                foreach (var q in who.questLog)
                {
                    if (q.completed.Value) progress += 1000;
                    if (q is ItemDeliveryQuest idq)
                    {
                        if (string.Equals(idq.target.Value, npc.Name, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(idq.target.Value, npc.displayName, StringComparison.OrdinalIgnoreCase))
                        {
                            progress += 100;
                        }
                    }
                }
            }
            if (Game1.player?.team?.specialOrders != null)
            {
                foreach (var so in Game1.player.team.specialOrders)
                {
                    if (so.objectives != null)
                    {
                        foreach (var obj in so.objectives)
                        {
                            progress += obj.GetCount();
                        }
                    }
                }
            }
        }
        catch { }
        return progress;
    }

    public static void SetDialogueManager(object manager)
    {
        _dialogueManagerInstance = manager;
    }

    public static bool CanStartDailyDialogue_Prefix(string npcName, ref bool __result)
    {
        try
        {
            if (ModEntry.Config?.BypassDailyLimitWhenFollowing != true) return true;

            if (NpcActionExecutor.Instance != null && NpcActionExecutor.Instance.IsManagedCompanion(npcName))
            {
                __result = true;
                return false;
            }
        }
        catch { }
        return true;
    }

    public static bool CanResumeDailyConversation_Prefix(string npcName, ref bool __result)
    {
        try
        {
            if (ModEntry.Config?.BypassDailyLimitWhenFollowing != true) return true;

            if (NpcActionExecutor.Instance != null && NpcActionExecutor.Instance.IsManagedCompanion(npcName))
            {
                __result = true;
                return false;
            }
        }
        catch { }
        return true;
    }

    public static bool DialoguePatches_NpcCheckAction_Prefix(NPC __instance, Farmer who, GameLocation l, ref bool __result)
    {
        try
        {
            if (__instance == null) return true;
            AwardDailyTalkFriendshipIfNeeded(__instance, who);
        }
        catch { }
        return true;
    }

    public static void DialoguePatches_OnDialogueBoxClosed_Postfix()
    {
        try
        {
            NPC? npc = null;
            if (_currentNpcProp != null)
            {
                npc = _currentNpcProp.GetValue(null) as NPC;
            }
            if (npc == null && Game1.currentSpeaker != null)
            {
                npc = Game1.currentSpeaker;
            }
            if (npc == null) return;

            Farmer player = Game1.player;
            AwardDailyTalkFriendshipIfNeeded(npc, player);
            RestoreVanillaDialogue(npc, player);
        }
        catch { }
    }

    /// <summary>
    /// Loads canonical Stardew Valley dialogue for the NPC if empty without clearing daily friendship talk state.
    /// </summary>
    public static void RestoreVanillaDialogue(NPC npc, Farmer? who)
    {
        if (npc == null) return;
        try
        {
            Farmer player = who ?? Game1.player;
            AwardDailyTalkFriendshipIfNeeded(npc, player);

            // Load vanilla canonical dialogue stack if empty
            if (npc.CurrentDialogue == null || npc.CurrentDialogue.Count == 0)
            {
                MethodInfo? loadMethod = AccessTools.Method(typeof(NPC), "loadCurrentDialogue");
                if (loadMethod != null)
                {
                    var st = loadMethod.Invoke(npc, null) as Stack<Dialogue>;
                    if (st != null && st.Count > 0)
                    {
                        npc.CurrentDialogue = st;
                        return;
                    }
                }

                // Fallback from raw character dialogue dict if loadCurrentDialogue produced 0 lines
                var dict = Game1.content.Load<Dictionary<string, string>>($"Characters/Dialogue/{npc.Name}");
                if (dict != null && dict.Count > 0)
                {
                    string dayKey = Game1.shortDayDisplayNameFromDayOfSeason(Game1.dayOfMonth);
                    if (!dict.TryGetValue(dayKey, out var line) || string.IsNullOrEmpty(line))
                    {
                        line = dict.Values.FirstOrDefault();
                    }
                    if (!string.IsNullOrEmpty(line))
                    {
                        npc.CurrentDialogue = new Stack<Dialogue>();
                        npc.CurrentDialogue.Push(new Dialogue(npc, null, line));
                    }
                }
            }
        }
        catch { }
    }

    public static bool IsFreeChatTriggered { get; set; } = false;

    /// <summary>
    /// Resets the daily talked state for the follower in AliveNpcs and initiates conversation.
    /// </summary>
    public static void TriggerFreeChat(NPC npc, Farmer? targetFarmer = null)
    {
        Farmer who = targetFarmer ?? Game1.player;
        if (npc == null || who == null) return;
        var loc = who.currentLocation ?? Game1.currentLocation;
        if (loc == null) return;

        try
        {
            IsFreeChatTriggered = true;
            npc.controller = null;
            npc.Halt();
            npc.faceTowardFarmerForPeriod(3000, 3, false, who);

            who.CanMove = true;
            if (Game1.activeClickableMenu is DialogueBox)
            {
                Game1.exitActiveMenu();
                Game1.dialogueUp = false;
            }

            bool bypassDaily = NpcActionExecutor.Instance?.IsManagedCompanion(npc.Name) == true
                && ModEntry.Config?.BypassDailyLimitWhenFollowing == true;

            AwardDailyTalkFriendshipIfNeeded(npc, who);

            if (bypassDaily)
            {
                if (who != null && who.friendshipData.TryGetValue(npc.Name, out var fd))
                {
                    fd.TalkedToToday = false;
                }

                if (_dialogueManagerInstance != null && _getDialogueStateMethod != null)
                {
                    try
                    {
                        object? state = _getDialogueStateMethod.Invoke(_dialogueManagerInstance, new object[] { npc.Name });
                        if (state != null)
                        {
                            var pTalked = state.GetType().GetProperty("TalkedToday");
                            pTalked?.SetValue(state, false);

                            var pCompletion = state.GetType().GetProperty("CompletionRecorded");
                            pCompletion?.SetValue(state, false);
                        }
                    }
                    catch { }
                }
            }

            RestoreVanillaDialogue(npc, who);

            bool actionHandled = npc.checkAction(who, loc);
            if (!actionHandled && Game1.activeClickableMenu == null && npc.CurrentDialogue != null && npc.CurrentDialogue.Count > 0)
            {
                var d = npc.CurrentDialogue.Pop();
                Game1.activeClickableMenu = new DialogueBox(d);
            }
        }
        catch (Exception ex)
        {
            _monitor?.Log($"TriggerFreeChat error: {ex.Message}", LogLevel.Trace);
            try
            {
                who.CanMove = true;
                if (npc.CurrentDialogue != null && npc.CurrentDialogue.Count > 0)
                {
                    var d = npc.CurrentDialogue.Pop();
                    Game1.activeClickableMenu = new DialogueBox(d);
                }
                else
                {
                    npc.checkAction(who, loc);
                }
            }
            catch { }
        }
        finally
        {
            IsFreeChatTriggered = false;
        }
    }
}
