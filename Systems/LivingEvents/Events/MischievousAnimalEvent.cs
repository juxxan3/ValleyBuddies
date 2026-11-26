namespace ValleyBuddies.Systems.LivingEvents.Events;

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using ValleyBuddies.Systems;

/// <summary>
/// Micro-evento interactivo: Animal travieso en la granja o establo.
/// Se dispara cerca de animales (o tras acariciarlos) cuando dueño y acompañante están en el mismo mapa.
/// </summary>
public sealed class MischievousAnimalEvent : ILivingEvent
{
    public static MischievousAnimalEvent Instance { get; } = new();

    public string Id => "mischievous_animal";

    private readonly Dictionary<string, long> _pendingAnimalIds = new(StringComparer.OrdinalIgnoreCase);

    public void ResetDaily() => _pendingAnimalIds.Clear();

    public void TriggerAnimalMischief(NPC npc, Farmer owner, FarmAnimal? animal = null)
    {
        if (npc == null || owner == null) return;

        var resolved = animal ?? FindNearestAnimal(npc.currentLocation, npc.Tile);
        if (resolved != null)
        {
            _pendingAnimalIds[npc.Name] = resolved.myID.Value;
        }

        try
        {
            string sfx = resolved?.type.Value.Contains("Chicken", StringComparison.OrdinalIgnoreCase) == true ||
                         resolved?.type.Value.Contains("Duck", StringComparison.OrdinalIgnoreCase) == true
                ? "cluck"
                : "dwop";
            npc.currentLocation?.playSound(sfx);
        }
        catch { }

        NpcAnimationService.TryPlayEmotionGesture(npc, "surprised");

        LivingEventManager.Instance.TriggerPrompt(
            Id,
            npc,
            owner,
            "living.mischievous_animal.prompt",
            "calm", "living.mischievous_animal.opt_calm",
            "laugh", "living.mischievous_animal.opt_laugh",
            itemDisplayName: resolved?.displayName,
            introKey: "living.mischievous_animal.intro",
            emotionTag: "$u"
        );
    }

    public void OnPlayerChoice(NPC npc, Farmer owner, string choiceId)
    {
        if (npc == null || owner == null) return;

        long targetAnimalId = 0;
        if (_pendingAnimalIds.TryGetValue(npc.Name, out long savedId))
        {
            targetAnimalId = savedId;
            _pendingAnimalIds.Remove(npc.Name);
        }

        if (choiceId.Equals("calm", StringComparison.OrdinalIgnoreCase))
        {
            try { npc.currentLocation?.playSound("dwop"); } catch { }

            FarmAnimal? animal = FindAnimalByIdOrNearest(npc.currentLocation, targetAnimalId, npc.Tile);
            if (animal != null)
            {
                animal.friendshipTowardFarmer.Value = Math.Min(1000, animal.friendshipTowardFarmer.Value + 15);
                if (!animal.IsEmoting) animal.doEmote(20);
            }

            npc.doEmote(20);
            owner.changeFriendship(20, npc);

            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.mischievous_animal.reply_calm", "$h", animal?.displayName);
        }
        else
        {
            npc.doEmote(56);
            owner.changeFriendship(15, npc);

            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "living.mischievous_animal.reply_laugh", "$l");
        }
    }

    private static FarmAnimal? FindNearestAnimal(GameLocation? loc, Vector2 originTile)
    {
        if (loc is not Farm && loc is not AnimalHouse) return null;
        var animals = loc is Farm f ? f.animals.Values : ((AnimalHouse)loc).animals.Values;
        FarmAnimal? best = null;
        float bestDist = float.MaxValue;
        foreach (var a in animals)
        {
            if (a == null) continue;
            float d = Vector2.Distance(a.Tile, originTile);
            if (d < bestDist)
            {
                bestDist = d;
                best = a;
            }
        }
        return best;
    }

    private static FarmAnimal? FindAnimalByIdOrNearest(GameLocation? loc, long animalId, Vector2 originTile)
    {
        if (animalId != 0)
        {
            if (loc is Farm f && f.animals.TryGetValue(animalId, out var fa)) return fa;
            if (loc is AnimalHouse ah && ah.animals.TryGetValue(animalId, out var aha)) return aha;
        }
        return FindNearestAnimal(loc, originTile);
    }
}
