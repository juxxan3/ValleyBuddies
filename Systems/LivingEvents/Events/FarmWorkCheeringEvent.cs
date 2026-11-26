namespace ValleyBuddies.Systems.LivingEvents.Events;

using System;
using StardewValley;

/// <summary>
/// Micro-evento interactivo: Ánimo de faena en Granja o Invernadero.
/// El acompañante nota tu dedicación y ofrece un respiro (+35 Stamina) o impulso de velocidad (+1 Speed temporal, Co-op safe).
/// </summary>
public sealed class FarmWorkCheeringEvent : ILivingEvent
{
    public static FarmWorkCheeringEvent Instance { get; } = new();

    public string Id => "farm_work_cheering";

    public void TriggerCheering(NPC npc, Farmer owner)
    {
        if (npc == null || owner == null) return;

        npc.faceGeneralDirection(owner.Position);
        npc.doEmote(20);

        LivingEventManager.Instance.TriggerPrompt(
            Id,
            npc,
            owner,
            "living.farm_cheering.prompt",
            "breather", "living.farm_cheering.opt_breather",
            "boost", "living.farm_cheering.opt_boost",
            introKey: "living.farm_cheering.intro",
            emotionTag: "$h"
        );
    }

    public void OnPlayerChoice(NPC npc, Farmer owner, string choiceId)
    {
        if (npc == null || owner == null) return;

        if (choiceId.Equals("breather", StringComparison.OrdinalIgnoreCase))
        {
            try { npc.currentLocation?.playSound("healSound"); } catch { }

            if (owner.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
            {
                Game1.player.Stamina = Math.Min(Game1.player.MaxStamina, Game1.player.Stamina + 35f);
            }
            else
            {
                ModEntry.MultiplayerSync?.SendSupportHeal(owner.UniqueMultiplayerID, 0, 35f, npc.Name);
            }

            npc.doEmote(20);
            owner.changeFriendship(25, npc);

            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.farm_cheering.reply_breather", "$l");
        }
        else
        {
            try { npc.currentLocation?.playSound("crystal"); } catch { }

            if (ModEntry.MultiplayerSync != null)
            {
                ModEntry.MultiplayerSync.SendLivingEventBuff(owner.UniqueMultiplayerID, "speed");
            }
            else if (owner.UniqueMultiplayerID == Game1.player?.UniqueMultiplayerID)
            {
                LivingEventManager.ApplyLocalLivingEventBuff("speed");
            }

            npc.doEmote(32);
            owner.changeFriendship(15, npc);

            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.farm_cheering.reply_boost", "$h");
        }
    }
}
