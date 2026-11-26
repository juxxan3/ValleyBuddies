namespace ValleyBuddies.Systems.LivingEvents.Events;

using System;
using StardewValley;

/// <summary>
/// Micro-evento interactivo: Pequeño tropiezo en senderos del bosque, montaña o minas.
/// El acompañante trastabilla con una piedra suelta y acepta la mano o un breve descanso.
/// </summary>
public sealed class TrailStumbleEvent : ILivingEvent
{
    public static TrailStumbleEvent Instance { get; } = new();

    public string Id => "trail_stumble";

    public void TriggerStumble(NPC npc, Farmer owner)
    {
        if (npc == null || owner == null) return;

        npc.jumpWithoutSound(2.5f);
        npc.shake(260);
        npc.doEmote(28);

        LivingEventManager.Instance.TriggerPrompt(
            Id,
            npc,
            owner,
            "living.trail_stumble.prompt",
            "hand", "living.trail_stumble.opt_hand",
            "rest", "living.trail_stumble.opt_rest",
            introKey: "living.trail_stumble.intro",
            emotionTag: "$u"
        );
    }

    public void OnPlayerChoice(NPC npc, Farmer owner, string choiceId)
    {
        if (npc == null || owner == null) return;

        if (choiceId.Equals("hand", StringComparison.OrdinalIgnoreCase))
        {
            try { npc.currentLocation?.playSound("healSound"); } catch { }

            npc.doEmote(20);
            owner.changeFriendship(25, npc);

            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.trail_stumble.reply_hand", "$l");
        }
        else
        {
            try { npc.currentLocation?.playSound("dwop"); } catch { }

            if (owner.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
            {
                Game1.player.Stamina = Math.Min(Game1.player.MaxStamina, Game1.player.Stamina + 15f);
            }
            else
            {
                ModEntry.MultiplayerSync?.SendSupportHeal(owner.UniqueMultiplayerID, 0, 15f, npc.Name);
            }

            npc.doEmote(32);
            owner.changeFriendship(20, npc);

            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.trail_stumble.reply_rest", "$h");
        }
    }
}
