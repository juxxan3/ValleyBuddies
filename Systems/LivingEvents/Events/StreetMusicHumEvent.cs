namespace ValleyBuddies.Systems.LivingEvents.Events;

using System;
using StardewValley;

/// <summary>
/// Micro-evento interactivo: Melodía compartida al pasear por Pueblo, Playa o Salón.
/// </summary>
public sealed class StreetMusicHumEvent : ILivingEvent
{
    public static StreetMusicHumEvent Instance { get; } = new();

    public string Id => "street_music_hum";

    public void TriggerMusic(NPC npc, Farmer owner)
    {
        if (npc == null || owner == null) return;

        try { npc.currentLocation?.playSound("flute"); } catch { }
        npc.doEmote(56); // Musical note

        LivingEventManager.Instance.TriggerPrompt(
            Id,
            npc,
            owner,
            "living.street_music.prompt",
            "join", "living.street_music.opt_join",
            "listen", "living.street_music.opt_listen",
            introKey: "living.street_music.intro",
            emotionTag: "$h"
        );
    }

    public void OnPlayerChoice(NPC npc, Farmer owner, string choiceId)
    {
        if (npc == null || owner == null) return;

        if (choiceId.Equals("join", StringComparison.OrdinalIgnoreCase))
        {
            try { npc.currentLocation?.playSound("dwop"); } catch { }
            LivingEventManager.GrantStaminaCoopSafe(owner, npc, 25f);
            npc.doEmote(56);
            owner.changeFriendship(25, npc);
            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.street_music.reply_join", "$h");
        }
        else
        {
            try { npc.currentLocation?.playSound("healSound"); } catch { }
            npc.doEmote(20);
            owner.changeFriendship(30, npc);
            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.street_music.reply_listen", "$l");
        }
    }
}
