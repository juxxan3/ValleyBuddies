namespace ValleyBuddies.Systems.LivingEvents.Events;

using System;
using StardewValley;

/// <summary>
/// Micro-evento interactivo: Trébol de cuatro hojas encontrado en el camino.
/// Otorga un Buff real temporal de Suerte (+1 Luck, Co-op safe) o amistad profunda según el deseo elegido.
/// </summary>
public sealed class DroppedKeepsakeEvent : ILivingEvent
{
    public static DroppedKeepsakeEvent Instance { get; } = new();

    public string Id => "dropped_keepsake";

    public void TriggerKeepsake(NPC npc, Farmer owner)
    {
        if (npc == null || owner == null) return;

        try { npc.currentLocation?.playSound("shiny4"); } catch { }
        npc.doEmote(16);

        LivingEventManager.Instance.TriggerPrompt(
            Id,
            npc,
            owner,
            "living.dropped_keepsake.prompt",
            "luck", "living.dropped_keepsake.opt_luck",
            "bond", "living.dropped_keepsake.opt_bond",
            introKey: "living.dropped_keepsake.intro",
            emotionTag: "$h"
        );
    }

    public void OnPlayerChoice(NPC npc, Farmer owner, string choiceId)
    {
        if (npc == null || owner == null) return;

        if (choiceId.Equals("luck", StringComparison.OrdinalIgnoreCase))
        {
            try { npc.currentLocation?.playSound("yoba"); } catch { }

            if (ModEntry.MultiplayerSync != null)
            {
                ModEntry.MultiplayerSync.SendLivingEventBuff(owner.UniqueMultiplayerID, "luck");
            }
            else if (owner.UniqueMultiplayerID == Game1.player?.UniqueMultiplayerID)
            {
                LivingEventManager.ApplyLocalLivingEventBuff("luck");
            }

            npc.doEmote(20);
            owner.changeFriendship(15, npc);

            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.dropped_keepsake.reply_luck", "$h");
        }
        else
        {
            try { npc.currentLocation?.playSound("dwop"); } catch { }

            npc.doEmote(20);
            owner.changeFriendship(30, npc);

            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.dropped_keepsake.reply_bond", "$l");
        }
    }
}
