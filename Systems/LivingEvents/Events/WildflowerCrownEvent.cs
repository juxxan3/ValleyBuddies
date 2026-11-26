namespace ValleyBuddies.Systems.LivingEvents.Events;

using System;
using StardewValley;

/// <summary>
/// Micro-evento interactivo: Corona o ramillete de flores silvestres (Exteriores soleados Primavera/Verano).
/// </summary>
public sealed class WildflowerCrownEvent : ILivingEvent
{
    public static WildflowerCrownEvent Instance { get; } = new();

    public string Id => "wildflower_crown";

    public void TriggerCrown(NPC npc, Farmer owner)
    {
        if (npc == null || owner == null) return;

        try { npc.currentLocation?.playSound("dwop"); } catch { }
        npc.doEmote(20);

        LivingEventManager.Instance.TriggerPrompt(
            Id,
            npc,
            owner,
            "living.wildflower.prompt",
            "wear", "living.wildflower.opt_wear",
            "keep", "living.wildflower.opt_keep",
            introKey: "living.wildflower.intro",
            emotionTag: "$h"
        );
    }

    public void OnPlayerChoice(NPC npc, Farmer owner, string choiceId)
    {
        if (npc == null || owner == null) return;

        if (choiceId.Equals("wear", StringComparison.OrdinalIgnoreCase))
        {
            try { npc.currentLocation?.playSound("healSound"); } catch { }
            LivingEventManager.GrantStaminaCoopSafe(owner, npc, 25f);
            npc.doEmote(20);
            owner.changeFriendship(30, npc);
            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.wildflower.reply_wear", "$l");
        }
        else
        {
            string flowerId = Game1.IsSpring ? "(O)18" : "(O)402";
            LivingEventManager.GiveItemCoopSafe(owner, npc, flowerId);
            npc.doEmote(32);
            owner.changeFriendship(20, npc);
            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.wildflower.reply_keep", "$h");
        }
    }
}
