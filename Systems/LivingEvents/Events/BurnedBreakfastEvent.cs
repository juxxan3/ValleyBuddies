namespace ValleyBuddies.Systems.LivingEvents.Events;

using Microsoft.Xna.Framework;
using StardewValley;
using ValleyBuddies.Systems;
using ValleyBuddies.Utilities;

/// <summary>
/// Micro-evento interactivo: Desayuno quemado en la estufa.
/// El cónyuge se quema con la sartén, corre hacia el jugador y pide ayuda para curarse.
/// </summary>
public sealed class BurnedBreakfastEvent : ILivingEvent
{
    public static BurnedBreakfastEvent Instance { get; } = new();

    public string Id => "burned_breakfast";

    public void TriggerBurn(NPC spouse, Farmer owner, Point stoveSpot)
    {
        if (spouse == null || owner == null) return;

        // 1. Puff de humo oscuro sobre la estufa + sonido fireball
        var loc = spouse.currentLocation;
        if (loc != null)
        {
            Vector2 stovePixel = new Vector2(stoveSpot.X * 64f + 24f, stoveSpot.Y * 64f - 16f);
            try
            {
                var smoke1 = new TemporaryAnimatedSprite(10, stovePixel, Color.DimGray * 0.85f, 8, false, 90f)
                {
                    scale = 2.4f,
                    motion = new Vector2(0f, -0.6f)
                };
                var smoke2 = new TemporaryAnimatedSprite(10, stovePixel + new Vector2(10f, -10f), Color.DarkSlateGray * 0.7f, 8, false, 110f)
                {
                    delayBeforeAnimationStart = 120,
                    scale = 2.0f,
                    motion = new Vector2(0.2f, -0.7f)
                };
                if (Game1.IsMultiplayer && Game1.Multiplayer != null)
                    Game1.Multiplayer.broadcastSprites(loc, smoke1, smoke2);
                else
                {
                    loc.temporarySprites.Add(smoke1);
                    loc.temporarySprites.Add(smoke2);
                }
                loc.playSound("fireball");
            }
            catch { }
        }

        // 2. Emote de sudor/tristeza + animación triste y burbuja de queja inicial
        spouse.doEmote(28);
        NpcAnimationService.TryPlayEmotionGesture(spouse, "sad");
        string ouch = I18n.Get("living.burned_breakfast.ouch", new { playerName = owner.displayName ?? owner.Name, npcName = spouse.displayName });
        ModEntry.ConversationBridge.ShowCompanionBubble(spouse, ouch, 3200);
    }

    public void TriggerBurnWithWalk(NPC npc, Farmer owner, Point stoveSpot)
    {
        if (npc == null || owner == null) return;
        TriggerBurn(npc, owner, stoveSpot);
        LivingEventManager.Instance.StartWalkApproach(npc, owner, delayTicks: 160, timeoutTicks: 360, onArrived: PresentPrompt);
    }

    public void PresentPrompt(NPC spouse, Farmer owner)
    {
        if (spouse == null || owner == null) return;

        PortraitDialogueHelper.ClearOverheadBubble(spouse);
        NpcAnimationService.TryPlayEmotionGesture(spouse, "sad");

        LivingEventManager.Instance.TriggerPrompt(
            Id,
            spouse,
            owner,
            "living.burned_breakfast.prompt",
            "heal", "living.burned_breakfast.opt_yes",
            "refuse", "living.burned_breakfast.opt_no",
            introKey: "living.burned_breakfast.cry",
            emotionTag: "$s"
        );
    }

    public void OnPlayerChoice(NPC npc, Farmer owner, string choiceId)
    {
        if (npc == null || owner == null) return;

        PortraitDialogueHelper.ClearOverheadBubble(npc);

        if (choiceId.Equals("heal", System.StringComparison.OrdinalIgnoreCase))
        {
            try { npc.currentLocation?.playSound("healSound"); } catch { }
            try
            {
                var loc = npc.currentLocation;
                if (loc != null)
                {
                    var pinkHeart = new TemporaryAnimatedSprite(10, npc.Position, Color.Pink, 8, false, 80f)
                    {
                        scale = 1.8f,
                        motion = new Vector2(0f, -0.5f)
                    };
                    if (Game1.IsMultiplayer && Game1.Multiplayer != null)
                        Game1.Multiplayer.broadcastSprites(loc, pinkHeart);
                    else
                        loc.temporarySprites.Add(pinkHeart);
                }
            }
            catch { }

            npc.doEmote(20);
            owner.changeFriendship(25, npc);

            // Regalo de consuelo: Café caliente (O)395
            if (owner.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
            {
                var coffee = ItemRegistry.Create("(O)395", 1);
                if (coffee != null)
                {
                    ValleyBuddies.Execution.NpcActionExecutor.GiveItemToFarmerDirectly(owner, coffee);
                    Game1.playSound("coin");
                }
            }
            else
            {
                ModEntry.MultiplayerSync?.SendSpouseGift(owner.UniqueMultiplayerID, "(O)395", 1, npc.Name);
            }

            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.burned_breakfast.healed", "$l");
        }
        else
        {
            npc.doEmote(28);
            NpcAnimationService.TryPlayEmotionGesture(npc, "sad");
            owner.changeFriendship(-10, npc);

            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.burned_breakfast.refused", "$s");
        }
    }
}
