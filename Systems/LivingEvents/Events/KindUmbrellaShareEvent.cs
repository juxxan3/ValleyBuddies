namespace ValleyBuddies.Systems.LivingEvents.Events;

using System;
using StardewValley;

/// <summary>
/// Micro-evento interactivo: Compartir refugio bajo un alero o correr saltando charcos bajo la lluvia.
/// </summary>
public sealed class KindUmbrellaShareEvent : ILivingEvent
{
    public static KindUmbrellaShareEvent Instance { get; } = new();

    public string Id => "kind_umbrella_share";

    public void TriggerUmbrella(NPC npc, Farmer owner)
    {
        if (npc == null || owner == null) return;

        try { npc.currentLocation?.playSound("waterSlosh"); } catch { }
        npc.doEmote(20);

        LivingEventManager.Instance.TriggerPrompt(
            Id,
            npc,
            owner,
            "living.umbrella.prompt",
            "close", "living.umbrella.opt_close",
            "splash", "living.umbrella.opt_splash",
            introKey: "living.umbrella.intro",
            emotionTag: "$h"
        );
    }

    public void OnPlayerChoice(NPC npc, Farmer owner, string choiceId)
    {
        if (npc == null || owner == null) return;

        if (choiceId.Equals("close", StringComparison.OrdinalIgnoreCase))
        {
            try { npc.currentLocation?.playSound("healSound"); } catch { }
            LivingEventManager.GrantStaminaCoopSafe(owner, npc, 20f);
            npc.doEmote(20);
            owner.changeFriendship(30, npc);
            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.umbrella.reply_close", "$l");
        }
        else
        {
            try { npc.currentLocation?.playSound("waterSlosh"); } catch { }
            LivingEventManager.GrantBuffCoopSafe(owner, "speed");
            npc.jump();
            npc.doEmote(32);
            owner.changeFriendship(20, npc);
            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.umbrella.reply_splash", "$h");
        }
    }
}
