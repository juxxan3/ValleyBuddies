using System;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;

namespace ValleyBuddies.Bridge;

public sealed class OfflineConversationBridge : IConversationBridge
{
    private IMonitor? _monitor;

    public ConversationBridgeMode Mode => ConversationBridgeMode.OfflineStandalone;
    public bool IsAliveNpcsActive => false;

    public static bool AllowVanillaTalkOnce { get; set; } = false;

    public void Initialize(IModHelper helper, IMonitor monitor, Harmony harmony)
    {
        _monitor = monitor;
        _monitor.Log("[OfflineConversationBridge] Modo Offline Standalone inicializado.", LogLevel.Debug);
    }

    public void Shutdown()
    {
    }

    public void OpenConversation(NPC npc, Farmer who)
    {
        if (npc == null || who == null) return;
        try
        {
            AllowVanillaTalkOnce = true;
            DialogueBypassPatches.RestoreVanillaDialogue(npc, who);
            var loc = who.currentLocation ?? npc.currentLocation ?? Game1.currentLocation;
            npc.checkAction(who, loc);
        }
        catch (Exception ex)
        {
            _monitor?.Log($"[OfflineConversationBridge] Error al abrir diálogo vanilla: {ex.Message}", LogLevel.Trace);
        }
        finally
        {
            AllowVanillaTalkOnce = false;
        }
    }

    public void NotifyCompanionEvent(string eventType, NPC npc, Farmer? owner, string? detail = null)
    {
        // En Bloque 1 queda listo de forma segura sin AliveNpcs para enriquecerlo en Bloque 3
        _monitor?.Log($"[OfflineConversationBridge] Evento de acompañante: {eventType} para {npc?.displayName} (detalle: {detail})", LogLevel.Trace);
    }

    public void ShowCompanionBubble(NPC npc, string localizedText, int durationMs = 3000)
    {
        if (npc == null || string.IsNullOrWhiteSpace(localizedText)) return;
        npc.showTextAboveHead(localizedText, duration: durationMs);
    }

    public void SendMemory(string description, int dailyLimit)
    {
        // Standalone offline: no feed needed
    }

    public void AddMessengerMessage(string npcName, string farmerName, string message)
    {
        // Standalone offline: no messenger app integration
    }

    public void ResetDaily()
    {
    }

    public void DrainQueue()
    {
    }
}
