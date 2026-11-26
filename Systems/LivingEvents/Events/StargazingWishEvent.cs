namespace ValleyBuddies.Systems.LivingEvents.Events;

using System;
using Microsoft.Xna.Framework;
using StardewValley;

/// <summary>
/// Micro-evento interactivo: Estrella fugaz nocturna (Exteriores despejados desde las 20:00).
/// </summary>
public sealed class StargazingWishEvent : ILivingEvent
{
    public static StargazingWishEvent Instance { get; } = new();

    public string Id => "stargazing_wish";

    public void TriggerStar(NPC npc, Farmer owner)
    {
        if (npc == null || owner == null) return;

        try
        {
            var loc = npc.currentLocation;
            if (loc != null)
            {
                loc.playSound("crystal");
                Vector2 starStart = owner.Position + new Vector2(-120f, -220f);
                var star = new TemporaryAnimatedSprite(
                    "LooseSprites\\Cursors",
                    new Rectangle(354, 401, 7, 7),
                    60f, 3, 2, starStart, false, false, 1f, 0.02f, Color.LightGoldenrodYellow, 4f, 0f, 0f, 0f)
                {
                    motion = new Vector2(4.5f, 2.5f)
                };
                if (Game1.IsMultiplayer && Game1.Multiplayer != null)
                    Game1.Multiplayer.broadcastSprites(loc, star);
                else
                    loc.TemporarySprites.Add(star);
            }
        }
        catch { }

        npc.doEmote(16);

        LivingEventManager.Instance.TriggerPrompt(
            Id,
            npc,
            owner,
            "living.stargazing.prompt",
            "together", "living.stargazing.opt_together",
            "harvest", "living.stargazing.opt_harvest",
            introKey: "living.stargazing.intro",
            emotionTag: "$u"
        );
    }

    public void OnPlayerChoice(NPC npc, Farmer owner, string choiceId)
    {
        if (npc == null || owner == null) return;

        if (choiceId.Equals("together", StringComparison.OrdinalIgnoreCase))
        {
            try { npc.currentLocation?.playSound("yoba"); } catch { }
            npc.doEmote(20);
            owner.changeFriendship(35, npc);
            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.stargazing.reply_together", "$l");
        }
        else
        {
            try { npc.currentLocation?.playSound("shiny4"); } catch { }
            LivingEventManager.GrantBuffCoopSafe(owner, "luck");
            npc.doEmote(32);
            owner.changeFriendship(20, npc);
            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.stargazing.reply_harvest", "$h");
        }
    }
}
