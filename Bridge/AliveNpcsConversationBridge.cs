using System;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using ValleyBuddies.Actions;
using ValleyBuddies.Config;
using ValleyBuddies.Memory;
using ValleyBuddies.Multiplayer;

namespace ValleyBuddies.Bridge;

public sealed class AliveNpcsConversationBridge : IConversationBridge
{
    private IMonitor? _monitor;
    private ValleyBuddiesReflection? _reflection;
    private PromptInjector? _promptInjector;
    private readonly ActionRegistry _registry;
    private readonly ActionContext _context;
    private readonly ActionMemory _memory;
    private readonly Func<string, string> _promptBuilder;
    private readonly MultiplayerSyncService? _multiplayerSync;

    public ConversationBridgeMode Mode => ConversationBridgeMode.AliveNpcsIntegrated;
    public bool IsAliveNpcsActive => _reflection != null && _reflection.IsAvailable;

    public AliveNpcsConversationBridge(
        ActionRegistry registry,
        ActionContext context,
        ActionMemory memory,
        Func<string, string> promptBuilder,
        MultiplayerSyncService? multiplayerSync)
    {
        _registry = registry;
        _context = context;
        _memory = memory;
        _promptBuilder = promptBuilder;
        _multiplayerSync = multiplayerSync;
    }

    public void Initialize(IModHelper helper, IMonitor monitor, Harmony harmony)
    {
        _monitor = monitor;
        try
        {
            _reflection = new ValleyBuddiesReflection(monitor);
            if (!_reflection.Resolve())
            {
                _monitor.Log("AliveNpcs types could not be fully resolved. Falling back.", LogLevel.Warn);
                return;
            }

            _promptInjector = new PromptInjector(monitor, _promptBuilder);
            _promptInjector.TryRegister(helper, helper.ModRegistry.ModID);
            if (_multiplayerSync != null)
            {
                ReactionInterceptor.MultiplayerSync = _multiplayerSync;
            }

            ReactionInterceptor.Initialize(
                monitor,
                _reflection,
                _registry,
                _context,
                _memory,
                _promptBuilder,
                () => _promptInjector.OfficialInjectionConfirmed
            );

            ReactionInterceptor.SetHudText((key, tokens) =>
                tokens != null
                    ? helper.Translation.Get(key, tokens).ToString()
                    : helper.Translation.Get(key).ToString());

            ReactionInterceptor.ApplyPatches(harmony, _reflection, monitor);
            AllNpcsUnlockedPatches.Apply(harmony, monitor);
            GroupTalkPatches.Apply(harmony, monitor);
            ValleyBuddiesMemoryFeed.TryConnect(helper, helper.ModRegistry.ModID, monitor);

            _monitor.Log("AliveNpcsConversationBridge initialized successfully.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _monitor.Log($"Error initializing AliveNpcsConversationBridge: {ex}", LogLevel.Error);
        }
    }

    public void Shutdown()
    {
        _promptInjector?.Dispose();
    }

    public void OpenConversation(NPC npc, Farmer who)
    {
        DialogueBypassPatches.TriggerFreeChat(npc, who);
    }

    public void NotifyCompanionEvent(string eventType, NPC npc, Farmer? owner, string? detail = null)
    {
        try
        {
            if (eventType == "morning_farewell" && npc != null && detail != null)
            {
                _monitor?.Log($"[AliveNpcs MP] ActivityEvent broadcast: {detail}", LogLevel.Info);
            }
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Error in NotifyCompanionEvent: {ex.Message}", LogLevel.Trace);
        }
    }

    public void ShowCompanionBubble(NPC npc, string localizedText, int durationMs = 3000)
    {
        if (npc == null || string.IsNullOrWhiteSpace(localizedText)) return;
        npc.showTextAboveHead(localizedText, duration: durationMs);
    }

    public void SendMemory(string description, int dailyLimit)
    {
        try
        {
            ValleyBuddiesMemoryFeed.Send(description, dailyLimit);
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Error sending memory: {ex.Message}", LogLevel.Trace);
        }
    }

    public void AddMessengerMessage(string npcName, string farmerName, string message)
    {
        try
        {
            ValleyBuddiesReflection.AddMessengerMessage(npcName, farmerName, message);
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Error adding messenger message: {ex.Message}", LogLevel.Trace);
        }
    }

    public void ResetDaily()
    {
        try
        {
            ValleyBuddiesMemoryFeed.ResetDailyCount();
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Error resetting daily feed: {ex.Message}", LogLevel.Trace);
        }
    }

    public void DrainQueue()
    {
        try
        {
            ReactionInterceptor.DrainQueue();
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Error draining reaction queue: {ex.Message}", LogLevel.Trace);
        }
    }
}
