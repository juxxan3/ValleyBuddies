namespace ValleyBuddies.Systems.LivingEvents.Events;

using System;
using System.Collections.Generic;
using StardewValley;
using ValleyBuddies.Systems;

/// <summary>
/// Micro-evento interactivo: Hallazgo brillante en la granja o naturaleza.
/// El acompañante encuentra un ítem brillante y pregunta si el jugador lo quiere o si se lo queda.
/// </summary>
public sealed class ShinyFarmFindEvent : ILivingEvent
{
    public static ShinyFarmFindEvent Instance { get; } = new();

    public string Id => "shiny_farm_find";

    private readonly Dictionary<string, string> _pendingItems = new(StringComparer.OrdinalIgnoreCase);

    public void ResetDaily() => _pendingItems.Clear();

    public void TriggerFind(NPC npc, Farmer owner)
    {
        if (npc == null || owner == null) return;

        try { npc.currentLocation?.playSound("shiny4"); } catch { }
        NpcAnimationService.TryPlayEmotionGesture(npc, "surprised");

        string locName = npc.currentLocation?.Name ?? string.Empty;
        string itemId = "(O)80"; // Cuarzo

        if (locName.Contains("Beach", StringComparison.OrdinalIgnoreCase))
        {
            itemId = "(O)393"; // Coral
        }
        else if (locName.Contains("Mountain", StringComparison.OrdinalIgnoreCase))
        {
            itemId = "(O)86"; // Cristal terrestre
        }
        else if (locName.Contains("Forest", StringComparison.OrdinalIgnoreCase) || locName.Contains("Woods", StringComparison.OrdinalIgnoreCase))
        {
            itemId = "(O)18"; // Narciso
        }

        _pendingItems[npc.Name] = itemId;

        var sampleItem = ItemRegistry.Create(itemId, 1);
        string itemName = sampleItem?.DisplayName ?? "Item";

        LivingEventManager.Instance.TriggerPrompt(
            Id,
            npc,
            owner,
            "living.shiny_find.prompt",
            "take", "living.shiny_find.opt_take",
            "keep", "living.shiny_find.opt_keep",
            itemDisplayName: itemName,
            introKey: "living.shiny_find.intro",
            emotionTag: "$h"
        );
    }

    public void OnPlayerChoice(NPC npc, Farmer owner, string choiceId)
    {
        if (npc == null || owner == null) return;

        string itemId = "(O)80";
        if (_pendingItems.TryGetValue(npc.Name, out var savedId))
        {
            itemId = savedId;
            _pendingItems.Remove(npc.Name);
        }

        if (choiceId.Equals("take", StringComparison.OrdinalIgnoreCase))
        {
            try { npc.currentLocation?.playSound("coin"); } catch { }

            if (owner.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
            {
                var it = ItemRegistry.Create(itemId, 1);
                if (it != null)
                {
                    ValleyBuddies.Execution.NpcActionExecutor.GiveItemToFarmerDirectly(owner, it);
                }
            }
            else
            {
                ModEntry.MultiplayerSync?.SendSpouseGift(owner.UniqueMultiplayerID, itemId, 1, npc.Name);
            }

            npc.doEmote(20);
            owner.changeFriendship(10, npc);

            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.shiny_find.reply_taken", "$h");
        }
        else
        {
            NpcAnimationService.TryPlayEmotionGesture(npc, "happy");
            try { npc.currentLocation?.playSound("dwop"); } catch { }

            owner.changeFriendship(25, npc);

            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.shiny_find.reply_kept", "$l");
        }
    }
}
