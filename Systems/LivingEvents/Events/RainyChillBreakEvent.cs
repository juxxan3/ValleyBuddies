namespace ValleyBuddies.Systems.LivingEvents.Events;

using System;
using StardewValley;

/// <summary>
/// Micro-evento interactivo: Escalofrío bajo la lluvia.
/// El acompañante se sacude el agua y propone hacer una pausa reconfortante.
/// </summary>
public sealed class RainyChillBreakEvent : ILivingEvent
{
    public static RainyChillBreakEvent Instance { get; } = new();

    public string Id => "rainy_chill_break";

    public void TriggerRainBreak(NPC npc, Farmer owner)
    {
        if (npc == null || owner == null) return;

        npc.shake(260);
        try { npc.currentLocation?.playSound("waterSlosh"); } catch { }
        npc.doEmote(28);

        LivingEventManager.Instance.TriggerPrompt(
            Id,
            npc,
            owner,
            "living.rain_chill.prompt",
            "rest", "living.rain_chill.opt_rest",
            "continue", "living.rain_chill.opt_continue",
            introKey: "living.rain_chill.intro",
            emotionTag: "$u"
        );
    }

    public void OnPlayerChoice(NPC npc, Farmer owner, string choiceId)
    {
        if (npc == null || owner == null) return;

        if (choiceId.Equals("rest", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                npc.currentLocation?.playSound("gulp");
                npc.currentLocation?.playSound("healSound");
            }
            catch { }

            // +20 Energía al dueño
            if (owner.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
            {
                Game1.player.Stamina = Math.Min(Game1.player.MaxStamina, Game1.player.Stamina + 20f);
            }
            else
            {
                ModEntry.MultiplayerSync?.SendSupportHeal(owner.UniqueMultiplayerID, 0, 20f, npc.Name);
            }

            npc.doEmote(20);
            owner.changeFriendship(20, npc);

            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.rain_chill.reply_cozy", "$l");
        }
        else
        {
            npc.doEmote(32);
            owner.changeFriendship(5, npc);

            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.rain_chill.reply_continue", "$h");
        }
    }
}
