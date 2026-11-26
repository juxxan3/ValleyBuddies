namespace ValleyBuddies.Systems.LivingEvents.Events;

using System;
using StardewValley;
using ValleyBuddies.Systems;

/// <summary>
/// Micro-evento interactivo: Susto con un bicho en casa.
/// El cónyuge se asusta con un insecto y pide al jugador sacarlo o dejarlo.
/// </summary>
public sealed class HouseBugScareEvent : ILivingEvent
{
    public static HouseBugScareEvent Instance { get; } = new();

    public string Id => "house_bug_scare";

    public void TriggerBugScare(NPC spouse, Farmer owner)
    {
        if (spouse == null || owner == null) return;

        NpcAnimationService.TryPlayEmotionGesture(spouse, "surprised");

        LivingEventManager.Instance.TriggerPrompt(
            Id,
            spouse,
            owner,
            "living.bug_scare.prompt",
            "shoo", "living.bug_scare.opt_shoo",
            "leave", "living.bug_scare.opt_leave",
            introKey: "living.bug_scare.scream",
            emotionTag: "$u"
        );
    }

    public void OnPlayerChoice(NPC npc, Farmer owner, string choiceId)
    {
        if (npc == null || owner == null) return;

        if (choiceId.Equals("shoo", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                npc.currentLocation?.playSound("cut");
                npc.currentLocation?.playSound("dwop");
            }
            catch { }

            npc.doEmote(20);
            owner.changeFriendship(20, npc);

            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.bug_scare.reply_saved", "$h");
        }
        else
        {
            string archetype = NpcPersonalityDialogueService.GetPersonalityArchetype(npc);
            bool isBrave = archetype.Equals("adventurous", StringComparison.OrdinalIgnoreCase) ||
                           archetype.Equals("technical", StringComparison.OrdinalIgnoreCase) ||
                           npc.Name.Equals("Krobus", StringComparison.OrdinalIgnoreCase);

            if (isBrave)
            {
                owner.changeFriendship(10, npc);
                LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.bug_scare.reply_brave", "$h");
            }
            else
            {
                npc.shake(350);
                LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.bug_scare.reply_scared", "$s");
            }
        }
    }
}
