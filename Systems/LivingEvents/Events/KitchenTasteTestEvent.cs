namespace ValleyBuddies.Systems.LivingEvents.Events;

using System;
using StardewValley;
using ValleyBuddies.Systems;

/// <summary>
/// Micro-evento interactivo: Prueba de sabor en la cocina.
/// El cónyuge invita al jugador a probar una cucharada del desayuno recién cocinado.
/// </summary>
public sealed class KitchenTasteTestEvent : ILivingEvent
{
    public static KitchenTasteTestEvent Instance { get; } = new();

    public string Id => "kitchen_taste_test";

    public void TriggerTasteTest(NPC spouse, Farmer owner, Item? dish)
    {
        if (spouse == null || owner == null) return;

        spouse.faceGeneralDirection(owner.Position);
        spouse.doEmote(32);

        string itemName = dish?.DisplayName ?? "Breakfast";

        LivingEventManager.Instance.TriggerPrompt(
            Id,
            spouse,
            owner,
            "living.taste_test.prompt",
            "delicious", "living.taste_test.opt_delicious",
            "needs_salt", "living.taste_test.opt_salt",
            itemDisplayName: itemName,
            introKey: "living.taste_test.intro",
            emotionTag: "$h"
        );
    }

    public void OnPlayerChoice(NPC npc, Farmer owner, string choiceId)
    {
        if (npc == null || owner == null) return;

        if (choiceId.Equals("delicious", StringComparison.OrdinalIgnoreCase))
        {
            NpcAnimationService.TryPlayEmotionGesture(npc, "happy");
            try { npc.currentLocation?.playSound("dwop"); } catch { }

            owner.changeFriendship(20, npc);

            // +20 Energía al dueño
            if (owner.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
            {
                Game1.player.Stamina = Math.Min(Game1.player.MaxStamina, Game1.player.Stamina + 20f);
            }
            else
            {
                ModEntry.MultiplayerSync?.SendSupportHeal(owner.UniqueMultiplayerID, 0, 20f, npc.Name);
            }

            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.taste_test.reply_delicious", "$h");
        }
        else
        {
            npc.doEmote(60); // Sonrojo
            owner.changeFriendship(5, npc);

            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.taste_test.reply_salt", "$l");
        }
    }
}
