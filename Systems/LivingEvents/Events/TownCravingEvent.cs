namespace ValleyBuddies.Systems.LivingEvents.Events;

using System;
using StardewValley;
using ValleyBuddies.Systems;

/// <summary>
/// Micro-evento interactivo: Antojo espontáneo en el pueblo, Saloon o playa.
/// El acompañante siente antojo de algo rico y el jugador decide invitarle o cocinar luego en casa.
/// </summary>
public sealed class TownCravingEvent : ILivingEvent
{
    public static TownCravingEvent Instance { get; } = new();

    public string Id => "town_craving";

    public void TriggerCraving(NPC npc, Farmer owner)
    {
        if (npc == null || owner == null) return;

        npc.faceGeneralDirection(owner.Position);
        npc.doEmote(32);

        LivingEventManager.Instance.TriggerPrompt(
            Id,
            npc,
            owner,
            "living.town_craving.prompt",
            "treat", "living.town_craving.opt_treat",
            "later", "living.town_craving.opt_later",
            introKey: "living.town_craving.intro",
            emotionTag: "$h"
        );
    }

    public void OnPlayerChoice(NPC npc, Farmer owner, string choiceId)
    {
        if (npc == null || owner == null) return;

        if (choiceId.Equals("treat", StringComparison.OrdinalIgnoreCase))
        {
            if (owner.Money < 50)
            {
                npc.doEmote(32);
                owner.changeFriendship(10, npc);
                LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.town_craving.reply_later", "$h");
                return;
            }

            owner.Money -= 50;

            try
            {
                npc.currentLocation?.playSound("coin");
                npc.currentLocation?.playSound("gulp");
            }
            catch { }

            if (owner.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
            {
                Game1.player.Stamina = Math.Min(Game1.player.MaxStamina, Game1.player.Stamina + 20f);
            }
            else
            {
                ModEntry.MultiplayerSync?.SendSupportHeal(owner.UniqueMultiplayerID, 0, 20f, npc.Name);
            }

            NpcAnimationService.TryPlayEmotionGesture(npc, "happy");
            npc.doEmote(20);
            owner.changeFriendship(30, npc);

            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.town_craving.reply_treat", "$l");
        }
        else
        {
            npc.doEmote(32);
            owner.changeFriendship(10, npc);

            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.town_craving.reply_later", "$h");
        }
    }
}
