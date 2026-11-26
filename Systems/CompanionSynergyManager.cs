using System;
using System.Collections.Concurrent;
using StardewValley;
using StardewValley.Buffs;

namespace ValleyBuddies.Systems;

public sealed class CompanionSynergyManager
{
    public static readonly CompanionSynergyManager Instance = new();
    private CompanionSynergyManager() { }

    private const string Prefix = "ValleyBuddies_Aura_";
    private readonly ConcurrentDictionary<string, int> _dailyWorkUnits = new(StringComparer.OrdinalIgnoreCase);

    public void RefreshCompanionAura(NPC npc, Farmer player, bool enabled)
    {
        if (!enabled || npc == null || player == null) return;
        string buffId = Prefix + npc.Name;

        BuffEffects effects = npc.Name switch
        {
            "Willy" => new BuffEffects { FishingLevel = { 2 } },
            "Robin" or "Clint" or "Dwarf" => new BuffEffects { MiningLevel = { 2 } },
            "Leah" or "Linus" => new BuffEffects { ForagingLevel = { 2 } },
            "Abigail" or "Krobus" => new BuffEffects { CombatLevel = { 1 }, Attack = { 1 } },
            "Harvey" => new BuffEffects { Defense = { 1 }, MaxStamina = { 20 } },
            "Emily" or "Haley" or "Gus" or "Evelyn" => new BuffEffects { LuckLevel = { 1 } },
            "Sam" or "Alex" or "Sebastian" => new BuffEffects { Speed = { 1 } },
            "Shane" or "Marnie" => new BuffEffects { FarmingLevel = { 1 } },
            "Maru" or "Demetrius" => new BuffEffects { MiningLevel = { 1 }, MagneticRadius = { 32 } },
            _ => new BuffEffects { LuckLevel = { 1 } }
        };

        var buff = new Buff(
            id: buffId,
            displayName: Utilities.I18n.Get("buff.companion_title", new { npcName = npc.displayName }),
            description: Utilities.I18n.Get("buff.companion_desc", new { npcName = npc.displayName }),
            duration: 15_000,
            effects: effects
        );
        player.applyBuff(buff);
    }

    public void RemoveCompanionAura(string npcName, Farmer player)
    {
        if (player?.buffs == null) return;
        player.buffs.Remove(Prefix + npcName);
    }

    public void ClearAllAuras(Farmer player)
    {
        if (player?.buffs == null) return;
        var toRemove = new System.Collections.Generic.List<string>();
        foreach (var b in player.buffs.AppliedBuffs.Keys)
        {
            if (b.StartsWith(Prefix))
                toRemove.Add(b);
        }
        foreach (var id in toRemove)
            player.buffs.Remove(id);
    }

    public void AddWorkUnits(string npcName, int units)
    {
        _dailyWorkUnits.AddOrUpdate(npcName, units, (_, current) => current + units);
    }

    public int GetWorkUnits(string npcName)
    {
        return _dailyWorkUnits.TryGetValue(npcName, out int units) ? units : 0;
    }

    public void ResetDaily()
    {
        _dailyWorkUnits.Clear();
    }

    public bool TryRelieveFatigueWithGift(NPC npc, Item item, out string reply)
    {
        reply = string.Empty;
        if (npc == null || item == null) return false;
        int taste = npc.getGiftTasteForThisItem(item);
        if (taste is NPC.gift_taste_love or NPC.gift_taste_like)
        {
            _dailyWorkUnits.AddOrUpdate(npc.Name, 0, (_, _) => 0);
            npc.doEmote(20);
            reply = Utilities.I18n.Get("synergy.fatigue_relieved");
            return true;
        }
        return false;
    }
}
