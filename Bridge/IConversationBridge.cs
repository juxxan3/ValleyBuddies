using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using ValleyBuddies.Actions;
using ValleyBuddies.Config;
using ValleyBuddies.Memory;

namespace ValleyBuddies.Bridge;

public enum ConversationBridgeMode
{
    OfflineStandalone,
    AliveNpcsIntegrated
}

public interface IConversationBridge
{
    ConversationBridgeMode Mode { get; }
    bool IsAliveNpcsActive { get; }

    void Initialize(IModHelper helper, IMonitor monitor, Harmony harmony);
    void Shutdown();

    void OpenConversation(NPC npc, Farmer who);
    void NotifyCompanionEvent(string eventType, NPC npc, Farmer? owner, string? detail = null);
    void ShowCompanionBubble(NPC npc, string localizedText, int durationMs = 3000);

    void SendMemory(string description, int dailyLimit);
    void AddMessengerMessage(string npcName, string farmerName, string message);
    void ResetDaily();
    void DrainQueue();
}

public sealed class NullConversationBridge : IConversationBridge
{
    public ConversationBridgeMode Mode => ConversationBridgeMode.OfflineStandalone;
    public bool IsAliveNpcsActive => false;

    public void Initialize(IModHelper helper, IMonitor monitor, Harmony harmony) { }
    public void Shutdown() { }
    public void OpenConversation(NPC npc, Farmer who) { }
    public void NotifyCompanionEvent(string eventType, NPC npc, Farmer? owner, string? detail = null) { }
    public void ShowCompanionBubble(NPC npc, string localizedText, int durationMs = 3000)
    {
        if (npc != null && !string.IsNullOrWhiteSpace(localizedText))
        {
            npc.showTextAboveHead(localizedText, duration: durationMs);
        }
    }
    public void SendMemory(string description, int dailyLimit) { }
    public void AddMessengerMessage(string npcName, string farmerName, string message) { }
    public void ResetDaily() { }
    public void DrainQueue() { }
}

public static class ConversationBridgeFactory
{
    public static IConversationBridge Create(
        IModHelper helper,
        IMonitor monitor,
        Harmony harmony,
        ActionsConfig config,
        string aliveNpcsUniqueId,
        ActionRegistry registry,
        ActionContext context,
        ActionMemory memory,
        Func<string, string> promptBuilder,
        Multiplayer.MultiplayerSyncService multiplayerSync)
    {
        if (config.ForceOfflineMode)
        {
            monitor.Log("Valley Buddies iniciado en Modo Offline (forzado por configuración GMCM).", LogLevel.Info);
            var offline = new OfflineConversationBridge();
            offline.Initialize(helper, monitor, harmony);
            return offline;
        }

        if (!helper.ModRegistry.IsLoaded(aliveNpcsUniqueId))
        {
            monitor.Log("Valley Buddies iniciado en Modo Offline Standalone (AliveNpcs no instalado).", LogLevel.Info);
            var offline = new OfflineConversationBridge();
            offline.Initialize(helper, monitor, harmony);
            return offline;
        }

        try
        {
            return CreateAliveNpcsBridgeIsolated(helper, monitor, harmony, registry, context, memory, promptBuilder, multiplayerSync);
        }
        catch (Exception ex)
        {
            monitor.Log(
                $"AliveNpcs fue detectado pero falló la inicialización del puente ({ex.GetType().Name}: {ex.Message}). Degradando automáticamente a Modo Offline Standalone.",
                LogLevel.Warn);
            var fallback = new OfflineConversationBridge();
            fallback.Initialize(helper, monitor, harmony);
            return fallback;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IConversationBridge CreateIsolated(
        IModHelper helper,
        IMonitor monitor,
        Harmony harmony,
        ActionRegistry registry,
        ActionContext context,
        ActionMemory memory,
        Func<string, string> promptBuilder,
        Multiplayer.MultiplayerSyncService multiplayerSync)
    {
        var bridge = new AliveNpcsConversationBridge(registry, context, memory, promptBuilder, multiplayerSync);
        bridge.Initialize(helper, monitor, harmony);
        monitor.Log("Valley Buddies iniciado en Modo Integrado con AliveNpcs.", LogLevel.Info);
        return bridge;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IConversationBridge CreateAliveNpcsBridgeIsolated(
        IModHelper helper,
        IMonitor monitor,
        Harmony harmony,
        ActionRegistry registry,
        ActionContext context,
        ActionMemory memory,
        Func<string, string> promptBuilder,
        Multiplayer.MultiplayerSyncService multiplayerSync)
    {
        var bridge = new AliveNpcsConversationBridge(registry, context, memory, promptBuilder, multiplayerSync);
        bridge.Initialize(helper, monitor, harmony);
        monitor.Log("Valley Buddies iniciado en Modo Integrado con AliveNpcs.", LogLevel.Info);
        return bridge;
    }
}
