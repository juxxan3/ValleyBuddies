namespace ValleyBuddies.Systems.LivingEvents.Events;

using System;
using Microsoft.Xna.Framework;
using StardewValley;

/// <summary>
/// Micro-evento interactivo: Contemplación espontánea del atardecer dorado al aire libre (17:00 - 19:30).
/// </summary>
public sealed class SpontaneousViewEvent : ILivingEvent
{
    public static SpontaneousViewEvent Instance { get; } = new();

    public string Id => "spontaneous_view";

    public void TriggerView(NPC npc, Farmer owner)
    {
        if (npc == null || owner == null) return;

        try { npc.currentLocation?.playSound("wand"); } catch { }
        npc.doEmote(32);

        LivingEventManager.Instance.TriggerPrompt(
            Id,
            npc,
            owner,
            "living.spontaneous_view.prompt",
            "watch", "living.spontaneous_view.opt_watch",
            "photo", "living.spontaneous_view.opt_photo",
            introKey: "living.spontaneous_view.intro",
            emotionTag: "$h"
        );
    }

    public void OnPlayerChoice(NPC npc, Farmer owner, string choiceId)
    {
        if (npc == null || owner == null) return;

        if (choiceId.Equals("watch", StringComparison.OrdinalIgnoreCase))
        {
            try { npc.currentLocation?.playSound("healSound"); } catch { }

            if (owner.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
            {
                Game1.player.Stamina = Math.Min(Game1.player.MaxStamina, Game1.player.Stamina + 15f);
            }
            else
            {
                ModEntry.MultiplayerSync?.SendSupportHeal(owner.UniqueMultiplayerID, 0, 15f, npc.Name);
            }

            npc.doEmote(20);
            owner.changeFriendship(25, npc);

            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.spontaneous_view.reply_watch", "$l");
        }
        else
        {
            try
            {
                var loc = npc.currentLocation;
                if (loc != null)
                {
                    loc.playSound("cameraNoise");
                    Vector2 center = (npc.Position + owner.Position) / 2f;
                    var flash = new TemporaryAnimatedSprite(28, center, Color.White) { interval = 40f };
                    if (Game1.IsMultiplayer && Game1.Multiplayer != null)
                        Game1.Multiplayer.broadcastSprites(loc, flash);
                    else
                        loc.TemporarySprites.Add(flash);
                }
            }
            catch { }

            npc.doEmote(52);
            owner.changeFriendship(20, npc);

            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.spontaneous_view.reply_photo", "$h");
        }
    }
}
