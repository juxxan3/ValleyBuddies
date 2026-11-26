using System;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Menus;
using ValleyBuddies.Bridge;
using ValleyBuddies.Config;
using ValleyBuddies.Execution;
using ValleyBuddies.Utilities;

namespace ValleyBuddies.Systems;

public static class PostDialogueInviteMonitor
{
    private sealed class PendingSession
    {
        public string NpcName { get; set; } = string.Empty;
        public string LocationName { get; set; } = string.Empty;
        public int TicksRemaining { get; set; } = -1;
    }

    private static IMonitor? _monitor;
    private static IModHelper? _helper;

    private static readonly PerScreen<PendingSession?> _pendingSession = new();
    private static readonly PerScreen<string?> _suppressedNpc = new();
    private static readonly PerScreen<int> _suppressUntilTick = new();
    private static bool _isUpdateTickedSubscribed = false;

    private static readonly string[] BlockedTranslationKeyPrefixes = new[]
    {
        "AcceptGift", "AcceptBirthdayGift", "RejectGift", "RejectItem",
        "AcceptBouquet", "RejectBouquet", "RejectMermaidPendant", "RejectRoommateProposal",
        "MovieInvitation", "RejectMovieTicket", "breakUp", "divorced",
        "DumpsterDiveComment", "HitBySlingshot", "Fair_Judg", "WinterStar_", "danceRejection"
    };

    public static void Initialize(IModHelper helper, IMonitor monitor, ActionsConfig config)
    {
        _helper = helper;
        _monitor = monitor;

        helper.Events.Display.MenuChanged += OnMenuChanged;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
    }

    public static void SuppressCurrentInteraction(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return;
        _suppressedNpc.Value = npcName;
        _suppressUntilTick.Value = Game1.ticks + 240;
        _pendingSession.Value = null;
        _monitor?.Log($"[PostDialogueInviteMonitor] Interacción suprimida para {npcName} (regalo/misión/reacción detectada).", LogLevel.Trace);
    }

    private static void OnMenuChanged(object? sender, MenuChangedEventArgs e)
    {
        var cfg = ModEntry.Config;
        if (cfg == null || !cfg.EnablePostDialogueInvitePrompt)
            return;

        if (ModEntry.ConversationBridge.Mode != ConversationBridgeMode.OfflineStandalone)
            return;

        // Stage 1: DialogueBox opened
        if (e.NewMenu is DialogueBox box)
        {
            _pendingSession.Value = null;

            if (box.isQuestion)
                return;

            NPC? speaker = box.characterDialogue?.speaker;
            if (speaker == null)
                return;

            // Check if suppressed by recent ValleyBuddies reaction / gift / quest
            if (!string.IsNullOrEmpty(_suppressedNpc.Value) &&
                string.Equals(_suppressedNpc.Value, speaker.Name, StringComparison.OrdinalIgnoreCase) &&
                Game1.ticks <= _suppressUntilTick.Value)
            {
                _suppressedNpc.Value = null;
                _suppressUntilTick.Value = 0;
                return;
            }

            _suppressedNpc.Value = null;
            _suppressUntilTick.Value = 0;

            // Check if already companion
            if (NpcActionExecutor.Instance != null && NpcActionExecutor.Instance.IsManagedCompanion(speaker.Name))
                return;

            // Check blocked TranslationKeys
            string? translationKey = box.characterDialogue?.TranslationKey;
            if (!string.IsNullOrEmpty(translationKey))
            {
                foreach (string prefix in BlockedTranslationKeyPrefixes)
                {
                    if (translationKey.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                        return;
                }
            }

            // Check eligibility
            if (!ShopDutyDetector.IsNpcEligibleForOfflineRecruitment(speaker, cfg))
                return;

            // Check if direct-click shopkeeper on duty (e.g. Krobus, Dwarf)
            if (ShopDutyDetector.IsDirectClickShopkeeperOnDuty(speaker, speaker.currentLocation))
                return;

            // Valid session started
            _pendingSession.Value = new PendingSession
            {
                NpcName = speaker.Name,
                LocationName = speaker.currentLocation?.NameOrUniqueName ?? string.Empty,
                TicksRemaining = -1
            };
            return;
        }

        // If any non-DialogueBox menu opened, cancel pending session
        if (e.NewMenu != null)
        {
            _pendingSession.Value = null;
            return;
        }

        // Stage 2: DialogueBox closed and no new menu opened
        if (e.OldMenu is DialogueBox && e.NewMenu == null)
        {
            var session = _pendingSession.Value;
            if (session != null && session.TicksRemaining == -1)
            {
                session.TicksRemaining = 2; // Defer 2 ticks
                SubscribeUpdateTicked();
            }
        }
    }

    private static void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        var session = _pendingSession.Value;
        if (session == null)
        {
            CheckUnsubscribeUpdateTicked();
            return;
        }

        session.TicksRemaining--;
        if (session.TicksRemaining > 0)
            return;

        // Session expired / ready to check
        string targetNpcName = session.NpcName;
        string targetLocationName = session.LocationName;
        _pendingSession.Value = null;
        CheckUnsubscribeUpdateTicked();

        // Stage 3: Real-time verification
        if (Game1.activeClickableMenu != null) return;
        if (!Context.IsPlayerFree) return;
        if (Game1.eventUp || Game1.CurrentEvent != null || Game1.isFestival() || Utility.isFestivalDay()) return;

        var loc = Game1.player.currentLocation;
        if (loc == null || !string.Equals(loc.NameOrUniqueName, targetLocationName, StringComparison.OrdinalIgnoreCase))
            return;

        var npc = loc.getCharacterFromName(targetNpcName);
        if (npc == null || npc.isSleeping.Value || (npc.CurrentDialogue != null && npc.CurrentDialogue.Count > 0))
            return;

        float dist = Vector2.Distance(npc.Tile, Game1.player.Tile);
        if (dist > 4.5f)
            return;

        if (ModEntry.Config != null && !ShopDutyDetector.IsNpcEligibleForOfflineRecruitment(npc, ModEntry.Config))
            return;

        if (NpcActionExecutor.Instance != null && NpcActionExecutor.Instance.IsManagedCompanion(npc.Name))
            return;

        // Passed all validations! Show the recruitment prompt
        ShowOfflineInviteQuestion(npc, Game1.player);
    }

    public static void ShowOfflineInviteQuestion(NPC npc, Farmer who)
    {
        if (npc == null || who == null || Game1.currentLocation == null) return;

        var responses = new[]
        {
            new Response("vb_offline_invite_yes", I18n.Get("offline.invite.yes", new { npcName = npc.displayName })),
            new Response("vb_offline_invite_no", I18n.Get("offline.invite.no"))
        };

        Game1.currentLocation.createQuestionDialogue(
            I18n.Get("offline.invite.prompt", new { npcName = npc.displayName }),
            responses,
            (Farmer f, string answer) =>
            {
                if (answer == "vb_offline_invite_yes")
                {
                    NpcActionExecutor.Instance.HandleOfflineInviteConfirmed(npc, f);
                }
            }
        );
    }

    private static void SubscribeUpdateTicked()
    {
        if (!_isUpdateTickedSubscribed && _helper != null)
        {
            _helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            _isUpdateTickedSubscribed = true;
        }
    }

    private static void CheckUnsubscribeUpdateTicked()
    {
        // Unsubscribe if no screen has a pending session
        if (_isUpdateTickedSubscribed && _helper != null && _pendingSession.Value == null)
        {
            _helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
            _isUpdateTickedSubscribed = false;
        }
    }

    private static void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        ResetAllScreens();
    }

    private static void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        ResetAllScreens();
    }

    public static void ResetAllScreens()
    {
        _pendingSession.ResetAllScreens();
        _suppressedNpc.ResetAllScreens();
        _suppressUntilTick.ResetAllScreens();
        if (_isUpdateTickedSubscribed && _helper != null)
        {
            _helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
            _isUpdateTickedSubscribed = false;
        }
    }
}
