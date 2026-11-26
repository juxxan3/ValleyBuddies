namespace ValleyBuddies.Systems.LivingEvents.Events;

using System;
using StardewValley;

/// <summary>
/// Micro-evento interactivo: Juego amistoso con bolas de nieve o bebida caliente (Exteriores Invierno).
/// </summary>
public sealed class SnowballPlayEvent : ILivingEvent
{
    public static SnowballPlayEvent Instance { get; } = new();

    public string Id => "snowball_play";

    public void TriggerSnowball(NPC npc, Farmer owner)
    {
        if (npc == null || owner == null) return;

        npc.doEmote(32);

        LivingEventManager.Instance.TriggerPrompt(
            Id,
            npc,
            owner,
            "living.snowball.prompt",
            "play", "living.snowball.opt_play",
            "warm", "living.snowball.opt_warm",
            introKey: "living.snowball.intro",
            emotionTag: "$h"
        );
    }

    public void OnPlayerChoice(NPC npc, Farmer owner, string choiceId)
    {
        if (npc == null || owner == null) return;

        if (choiceId.Equals("play", StringComparison.OrdinalIgnoreCase))
        {
            try { npc.currentLocation?.playSound("throw"); } catch { }
            LivingEventManager.GrantBuffCoopSafe(owner, "snow_speed");
            npc.jump();
            npc.doEmote(32);
            owner.changeFriendship(25, npc);
            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.snowball.reply_play", "$h");
        }
        else
        {
            try { npc.currentLocation?.playSound("gulp"); } catch { }
            LivingEventManager.GiveItemCoopSafe(owner, npc, "(O)614"); // Té Verde
            npc.doEmote(20);
            owner.changeFriendship(25, npc);
            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.snowball.reply_warm", "$l");
        }
    }
}
