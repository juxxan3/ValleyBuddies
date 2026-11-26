namespace ValleyBuddies.Systems.LivingEvents;

using StardewValley;

/// <summary>
/// Contrato modular para micro-eventos interactivos de vida e inmersión.
/// </summary>
public interface ILivingEvent
{
    string Id { get; }
    void OnPlayerChoice(NPC npc, Farmer owner, string choiceId);
}
