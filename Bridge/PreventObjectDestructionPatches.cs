using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;

namespace ValleyBuddies.Bridge;

/// <summary>
/// Harmony patches that protect player chests, machines, sprinklers, torches, campfires, fences, and crops from being destroyed
/// by walking NPCs, while allowing harmless passage through weeds/grass so NPCs never get stuck.
/// </summary>
public static class PreventObjectDestructionPatches
{
    private static IMonitor? _monitor;

    public static void Apply(Harmony harmony, IMonitor monitor)
    {
        _monitor = monitor;
        try
        {
            var destroyMethod = AccessTools.Method(
                typeof(GameLocation),
                nameof(GameLocation.characterDestroyObjectWithinRectangle),
                new[] { typeof(Rectangle), typeof(bool) }
            );

            if (destroyMethod != null)
            {
                harmony.Patch(
                    destroyMethod,
                    prefix: new HarmonyMethod(typeof(PreventObjectDestructionPatches), nameof(CharacterDestroyObjectWithinRectangle_Prefix))
                );
                _monitor.Log("PreventObjectDestructionPatches applied: player chests, machines, torches, campfires, fences, and crops protected from destruction.", LogLevel.Debug);
            }

            var trampleMethod = AccessTools.Method(
                typeof(GameLocation),
                nameof(GameLocation.characterTrampleTile),
                new[] { typeof(Vector2) }
            );

            if (trampleMethod != null)
            {
                harmony.Patch(
                    trampleMethod,
                    prefix: new HarmonyMethod(typeof(PreventObjectDestructionPatches), nameof(CharacterTrampleTile_Prefix))
                );
                _monitor.Log("PreventObjectDestructionPatches applied: characterTrampleTile protected.", LogLevel.Debug);
            }
        }
        catch (Exception ex)
        {
            _monitor.Log($"PreventObjectDestructionPatches failed to apply: {ex.Message}", LogLevel.Error);
        }
    }

    /// <summary>
    /// Protects all player-placed items (chests, craftables, machines, sprinklers, torches, campfires, fences, crops, tents)
    /// while allowing ordinary path clearance for harmless wild weeds/debris so NPC movement stays fluid.
    /// </summary>
    public static bool CharacterDestroyObjectWithinRectangle_Prefix(GameLocation __instance, Rectangle rect, bool showDestroyedObject, ref bool __result)
    {
        if (__instance == null) return true;

        int rightPixel = Math.Max(rect.X, rect.Right - 1);
        int bottomPixel = Math.Max(rect.Y, rect.Bottom - 1);
        Point[] pointsToCheck = new[]
        {
            new Point(rect.X / 64, rect.Y / 64),
            new Point(rightPixel / 64, rect.Y / 64),
            new Point(rect.X / 64, bottomPixel / 64),
            new Point(rightPixel / 64, bottomPixel / 64)
        };

        foreach (var p in pointsToCheck)
        {
            Vector2 tile = new Vector2(p.X, p.Y);

            // Protect all player-placed objects: chests, machines, sprinklers, torches, campfires, fences, scarecrows, etc.
            // Harmless wild weeds and wild debris (twigs, small stones) are allowed to be cleared so NPCs never freeze.
            if (__instance.objects.TryGetValue(tile, out var obj) && obj != null)
            {
                bool isWildDebris = obj.IsWeeds()
                    || obj.ParentSheetIndex == 294 || obj.ParentSheetIndex == 295   // wild twigs
                    || obj.ParentSheetIndex == 343 || obj.ParentSheetIndex == 450;  // wild stones

                if (!isWildDebris)
                {
                    __result = false;
                    return false; // Skip destruction! NEVER destroy player objects!
                }
            }

            // Protect player-grown crops, fruit trees, flooring/paths, and camping tents
            if (__instance.terrainFeatures.TryGetValue(tile, out var tf) && tf != null)
            {
                if ((tf is HoeDirt hd && hd.crop != null)
                    || tf is FruitTree
                    || tf is Flooring
                    || tf.GetType().Name.Contains("Tent", StringComparison.OrdinalIgnoreCase))
                {
                    __result = false;
                    return false; // Skip destruction!
                }
            }
        }

        // Allow default behavior for harmless wild weeds/debris so NPC movement stays fluid
        return true;
    }

    /// <summary>
    /// Prevents NPCs from instantly destroying young trees, crops, and tents when stepping on tiles.
    /// </summary>
    public static bool CharacterTrampleTile_Prefix(GameLocation __instance, Vector2 tile)
    {
        if (__instance != null && __instance.terrainFeatures.TryGetValue(tile, out var tf) && tf != null)
        {
            if ((tf is HoeDirt hd && hd.crop != null)
                || tf is FruitTree
                || tf.GetType().Name.Contains("Tent", StringComparison.OrdinalIgnoreCase))
            {
                return false; // Never trample crops, fruit trees, or tents!
            }
        }
        return true;
    }
}
