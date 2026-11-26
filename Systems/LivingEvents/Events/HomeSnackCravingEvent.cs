namespace ValleyBuddies.Systems.LivingEvents.Events;

using System;
using StardewValley;
using ValleyBuddies.Execution;

/// <summary>
/// Micro-evento interactivo: Antojo hogareño de tarde dentro de FarmHouse / Cabin con el cónyuge.
/// Entrega Té Verde (O)614 o Galletas (O)223 reales de forma segura en Co-op.
/// </summary>
public sealed class HomeSnackCravingEvent : ILivingEvent
{
    public static HomeSnackCravingEvent Instance { get; } = new();

    public string Id => "home_snack_craving";

    public void TriggerSnackCraving(NPC spouse, Farmer owner)
    {
        if (spouse == null || owner == null) return;

        spouse.faceGeneralDirection(owner.Position);
        spouse.doEmote(32);

        LivingEventManager.Instance.TriggerPrompt(
            Id,
            spouse,
            owner,
            "living.home_snack.prompt",
            "tea", "living.home_snack.opt_tea",
            "cookies", "living.home_snack.opt_cookies",
            introKey: "living.home_snack.intro",
            emotionTag: "$h"
        );
    }

    public void OnPlayerChoice(NPC npc, Farmer owner, string choiceId)
    {
        if (npc == null || owner == null) return;

        bool choseTea = choiceId.Equals("tea", StringComparison.OrdinalIgnoreCase);
        string itemId = choseTea ? "(O)614" : "(O)223";

        try { npc.currentLocation?.playSound(choseTea ? "gulp" : "coin"); } catch { }

        if (owner.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
        {
            var item = ItemRegistry.Create(itemId, 1);
            if (item != null)
            {
                NpcActionExecutor.GiveItemToFarmerDirectly(owner, item);
            }
        }
        else
        {
            ModEntry.MultiplayerSync?.SendSpouseGift(owner.UniqueMultiplayerID, itemId, 1, npc.Name);
        }

        npc.doEmote(choseTea ? 20 : 32);
        owner.changeFriendship(20, npc);

        string replyKey = choseTea ? "living.home_snack.reply_tea" : "living.home_snack.reply_cookies";
        string tag = choseTea ? "$l" : "$h";
        LivingEventManager.Instance.ShowReactionDialogue(npc, owner, replyKey, tag);
    }
}
