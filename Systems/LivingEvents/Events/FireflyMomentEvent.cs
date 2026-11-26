namespace ValleyBuddies.Systems.LivingEvents.Events;

using System;
using Microsoft.Xna.Framework;
using StardewValley;

/// <summary>
/// Micro-evento interactivo: Noche de Luciérnagas (Exteriores despejados Primavera/Verano/Otoño desde 19:30).
/// </summary>
public sealed class FireflyMomentEvent : ILivingEvent
{
    public static FireflyMomentEvent Instance { get; } = new();

    public string Id => "firefly_moment";

    public void TriggerFireflies(NPC npc, Farmer owner)
    {
        if (npc == null || owner == null) return;

        try
        {
            var loc = npc.currentLocation;
            if (loc != null)
            {
                loc.playSound("wand");
                for (int i = 0; i < 6; i++)
                {
                    Vector2 offset = new Vector2(Game1.random.Next(-96, 97), Game1.random.Next(-96, 65));
                    Vector2 pos = ((npc.Position + owner.Position) / 2f) + offset;
                    var sprite = new TemporaryAnimatedSprite(
                        "LooseSprites\\Cursors",
                        new Rectangle(294, 1432, 16, 16),
                        90f, 4, 2, pos, false, false, 1f, 0.01f, Color.Gold, 2.5f, 0.01f, 0f, 0f);
                    if (Game1.IsMultiplayer && Game1.Multiplayer != null)
                        Game1.Multiplayer.broadcastSprites(loc, sprite);
                    else
                        loc.TemporarySprites.Add(sprite);
                }
            }
        }
        catch { }

        npc.doEmote(32);

        LivingEventManager.Instance.TriggerPrompt(
            Id,
            npc,
            owner,
            "living.firefly.prompt",
            "wish", "living.firefly.opt_wish",
            "admire", "living.firefly.opt_admire",
            introKey: "living.firefly.intro",
            emotionTag: "$h"
        );
    }

    public void OnPlayerChoice(NPC npc, Farmer owner, string choiceId)
    {
        if (npc == null || owner == null) return;

        if (choiceId.Equals("wish", StringComparison.OrdinalIgnoreCase))
        {
            try { npc.currentLocation?.playSound("yoba"); } catch { }
            LivingEventManager.GrantBuffCoopSafe(owner, "firefly_luck");
            npc.doEmote(20);
            owner.changeFriendship(20, npc);
            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.firefly.reply_wish", "$l");
        }
        else
        {
            try { npc.currentLocation?.playSound("healSound"); } catch { }
            LivingEventManager.GrantStaminaCoopSafe(owner, npc, 20f);
            npc.doEmote(20);
            owner.changeFriendship(30, npc);
            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.firefly.reply_admire", "$l");
        }
    }
}
