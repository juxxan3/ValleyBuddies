using System;
using System.Collections.Generic;
using ValleyBuddies.Config;
using StardewValley;

namespace ValleyBuddies.Utilities;

public static class SafeItemResolver
{
    private static readonly HashSet<string> BlockedGiftIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "(O)74", "(O)434", "(O)789", "(O)71", "(O)StardropTea", "(O)279" // Prismatic Shard, Stardrop, Mayor Shorts, Trimmed Lucky Purple Shorts, Stardrop Tea, Magic Rock Candy
    };

    private static readonly Dictionary<string, string> SpanishToQualifiedId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["madera"] = "(O)388", ["wood"] = "(O)388",
        ["madera noble"] = "(O)709", ["hardwood"] = "(O)709",
        ["piedra"] = "(O)390", ["stone"] = "(O)390",
        ["fibra"] = "(O)771", ["fiber"] = "(O)771",
        ["carbon"] = "(O)382", ["carbón"] = "(O)382", ["coal"] = "(O)382",
        ["heno"] = "(O)178", ["hay"] = "(O)178",
        ["cafe"] = "(O)395", ["café"] = "(O)395", ["coffee"] = "(O)395",
        ["te verde"] = "(O)614", ["té verde"] = "(O)614", ["green tea"] = "(O)614",
        ["ensalada"] = "(O)196", ["salad"] = "(O)196",
        ["pizza"] = "(O)206", ["cerveza"] = "(O)346", ["vino"] = "(O)348",
        ["queso"] = "(O)424", ["queso de cabra"] = "(O)426",
        ["narciso"] = "(O)18", ["daffodil"] = "(O)18",
        ["girasol"] = "(O)421", ["sunflower"] = "(O)421",
        ["amatista"] = "(O)66", ["cuarzo"] = "(O)80", ["esmeralda"] = "(O)60",
        ["pastel rosa"] = "(O)221", ["pastel de chocolate"] = "(O)220",
        ["cobre"] = "(O)378", ["hierro"] = "(O)380", ["oro"] = "(O)384", ["iridio"] = "(O)386"
    };

    public static string? ResolveObjectQualifiedId(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return null;
        string cleaned = rawName.Trim();

        if (ItemRegistry.Exists(cleaned))
        {
            string qualified = ItemRegistry.QualifyItemId(cleaned);
            if (qualified.StartsWith("(O)", StringComparison.OrdinalIgnoreCase))
                return qualified;
        }

        if (SpanishToQualifiedId.TryGetValue(cleaned, out string? mapped) && ItemRegistry.Exists(mapped))
            return mapped;

        foreach (var kvp in Game1.objectData)
        {
            if (kvp.Value == null) continue;
            string qid = "(O)" + kvp.Key;
            if (kvp.Value.Name != null && kvp.Value.Name.Equals(cleaned, StringComparison.OrdinalIgnoreCase))
                return qid;

            var data = ItemRegistry.GetData(qid);
            if (data != null && data.DisplayName.Equals(cleaned, StringComparison.OrdinalIgnoreCase))
                return qid;
        }

        if (cleaned.Length >= 4)
        {
            foreach (var kvp in Game1.objectData)
            {
                if (kvp.Value?.Name != null && kvp.Value.Name.Contains(cleaned, StringComparison.OrdinalIgnoreCase))
                    return "(O)" + kvp.Key;
            }
        }

        return null;
    }

    public static bool IsAllowedAsNpcGift(string qualifiedId, ActionsConfig config, int hearts, out string reason)
    {
        reason = string.Empty;
        if (BlockedGiftIds.Contains(qualifiedId) && !config.AllowRareNpcGifts)
        {
            reason = "item is too rare or story-restricted to gift freely";
            return false;
        }

        Item? sample = ItemRegistry.Create(qualifiedId, allowNull: true);
        if (sample == null)
        {
            reason = "item does not exist in ItemRegistry";
            return false;
        }

        int priceLimit = config.MaxGiftItemValue + (hearts >= 10 ? 250 : 0);
        if (sample.salePrice() > priceLimit && !config.AllowRareNpcGifts)
        {
            reason = $"item exceeds max gift value ({priceLimit}g)";
            return false;
        }

        return true;
    }

    public static Item? TryCreateValidItem(string qualifiedId, int stack = 1)
    {
        if (string.IsNullOrWhiteSpace(qualifiedId)) return null;
        var data = ItemRegistry.GetData(qualifiedId);
        if (data == null) return null;
        try
        {
            var item = ItemRegistry.Create(qualifiedId, stack, allowNull: true);
            if (item == null || item.Name.Contains("Error", StringComparison.OrdinalIgnoreCase))
                return null;
            return item;
        }
        catch
        {
            return null;
        }
    }
}
