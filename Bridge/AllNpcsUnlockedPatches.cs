using System;
using System.Reflection;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;

namespace ValleyBuddies.Bridge;

/// <summary>
/// Harmony patches and delegates that unlock AI dialogue for ALL characters in the game,
/// including Stardew Valley Expanded (SVE), Ridgeside Village (RSV), and any custom mod NPCs.
/// </summary>
public static class AllNpcsUnlockedPatches
{
    private static IMonitor? _monitor;

    public static void Apply(Harmony harmony, IMonitor monitor)
    {
        _monitor = monitor;
        try
        {
            // 1. Patch NpcEligibilityService in AliveNpcs
            Type? eligibilityType = AccessTools.TypeByName("AliveNpcs.Compatibility.NpcEligibilityService");
            if (eligibilityType != null)
            {
                var isNpcEnabledMethod = AccessTools.Method(eligibilityType, "IsNpcEnabled", new[] { typeof(NPC) });
                if (isNpcEnabledMethod != null)
                {
                    harmony.Patch(isNpcEnabledMethod,
                        prefix: new HarmonyMethod(typeof(AllNpcsUnlockedPatches), nameof(IsNpcEnabled_Prefix)));
                }

                var isNpcNameEnabledMethod = AccessTools.Method(eligibilityType, "IsNpcNameEnabled", new[] { typeof(string) });
                if (isNpcNameEnabledMethod != null)
                {
                    harmony.Patch(isNpcNameEnabledMethod,
                        prefix: new HarmonyMethod(typeof(AllNpcsUnlockedPatches), nameof(IsNpcNameEnabled_Prefix)));
                }

                var isNpcAvailableForEditorMethod = AccessTools.Method(eligibilityType, "IsNpcAvailableForEditor", new[] { typeof(NPC) });
                if (isNpcAvailableForEditorMethod != null)
                {
                    harmony.Patch(isNpcAvailableForEditorMethod,
                        prefix: new HarmonyMethod(typeof(AllNpcsUnlockedPatches), nameof(IsNpcAvailableForEditor_Prefix)));
                }

                var isNameConfigMethod = AccessTools.Method(eligibilityType, "IsNpcNameEnabledForConfigMenu", new[] { typeof(string) });
                if (isNameConfigMethod != null)
                {
                    harmony.Patch(isNameConfigMethod,
                        prefix: new HarmonyMethod(typeof(AllNpcsUnlockedPatches), nameof(IsNpcNameEnabled_Prefix)));
                }

                var isListedMethod = AccessTools.Method(eligibilityType, "IsNpcListedForConfigMenu", new[] { typeof(string) });
                if (isListedMethod != null)
                {
                    harmony.Patch(isListedMethod,
                        prefix: new HarmonyMethod(typeof(AllNpcsUnlockedPatches), nameof(IsNpcNameEnabled_Prefix)));
                }

                var isModIdMethod = AccessTools.Method(eligibilityType, "IsModIdEnabled", new[] { typeof(string) });
                if (isModIdMethod != null)
                {
                    harmony.Patch(isModIdMethod,
                        prefix: new HarmonyMethod(typeof(AllNpcsUnlockedPatches), nameof(IsModIdEnabled_Prefix)));
                }
            }

            // 2. Patch NpcCompatibilityPolicy in AliveNpcs
            Type? policyType = AccessTools.TypeByName("AliveNpcs.Compatibility.NpcCompatibilityPolicy");
            if (policyType != null)
            {
                var getRejectionMethod = AccessTools.FirstMethod(policyType, m => m.Name == "GetRejectionReason");
                if (getRejectionMethod != null)
                {
                    harmony.Patch(getRejectionMethod,
                        prefix: new HarmonyMethod(typeof(AllNpcsUnlockedPatches), nameof(GetRejectionReason_Prefix)));
                }

                var isEligibleMethod = AccessTools.FirstMethod(policyType, m => m.Name == "IsEligible");
                if (isEligibleMethod != null)
                {
                    harmony.Patch(isEligibleMethod,
                        prefix: new HarmonyMethod(typeof(AllNpcsUnlockedPatches), nameof(IsEligible_Prefix)));
                }
            }

            // 3. Overwrite cached delegate in DialoguePatches._isNpcEnabled
            Type? dialoguePatchesType = AccessTools.TypeByName("AliveNpcs.Patches.DialoguePatches");
            if (dialoguePatchesType != null)
            {
                var isNpcEnabledField = AccessTools.Field(dialoguePatchesType, "_isNpcEnabled");
                if (isNpcEnabledField != null)
                {
                    isNpcEnabledField.SetValue(null, (Func<NPC, bool>)(npc =>
                        npc != null
                        && npc.GetType() == typeof(NPC)
                        && !string.IsNullOrWhiteSpace(npc.Name)
                        && npc.CanSocialize));
                }
            }

            _monitor.Log("AllNpcsUnlockedPatches applied: AI dialogue unlocked for all game & mod socializable NPCs (SVE, RSV, Custom).", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _monitor.Log($"AllNpcsUnlockedPatches encountered an issue: {ex.Message}", LogLevel.Warn);
        }
    }

    public static bool IsNpcEnabled_Prefix(NPC npc, ref bool __result)
    {
        if (npc != null && npc.GetType() == typeof(NPC) && !string.IsNullOrWhiteSpace(npc.Name) && npc.CanSocialize)
        {
            __result = true;
            return false;
        }
        return true;
    }

    public static bool IsNpcNameEnabled_Prefix(string npcName, ref bool __result)
    {
        if (!string.IsNullOrWhiteSpace(npcName))
        {
            __result = true;
            return false;
        }
        return true;
    }

    public static bool IsNpcAvailableForEditor_Prefix(NPC npc, ref bool __result)
    {
        if (npc != null && npc.GetType() == typeof(NPC) && !string.IsNullOrWhiteSpace(npc.Name) && npc.CanSocialize)
        {
            __result = true;
            return false;
        }
        return true;
    }

    public static bool IsModIdEnabled_Prefix(string modId, ref bool __result)
    {
        if (!string.IsNullOrWhiteSpace(modId))
        {
            __result = true;
            return false;
        }
        return true;
    }

    public static bool GetRejectionReason_Prefix(ref string? __result)
    {
        __result = null; // null means NO REJECTION (allowed)
        return false;
    }

    public static bool IsEligible_Prefix(ref bool __result)
    {
        __result = true;
        return false;
    }
}
