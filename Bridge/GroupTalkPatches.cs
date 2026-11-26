using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace ValleyBuddies.Bridge;

/// <summary>
/// Harmony patches that unlock and fix AliveNpcs Group Conversations (Charla en grupo).
/// Fixes the issue where group conversations were disabled or said "{{name}} no puede unirse a una conversación ahora mismo."
/// Allows group talk to initiate smoothly whenever 2 NPCs are nearby, in both single player and multiplayer.
/// </summary>
public static class GroupTalkPatches
{
    private static IMonitor? _monitor;
    private static bool _loggedSignatureMismatch;
    private static bool _loggedBuildRequestMismatch;

    public static void Apply(Harmony harmony, IMonitor monitor)
    {
        _monitor = monitor;
        try
        {
            Type? directorType = AccessTools.TypeByName("AliveNpcs.Services.GroupConversations.GroupConversationDirector");
            if (directorType == null)
            {
                _monitor.Log("GroupTalkPatches: GroupConversationDirector type not found.", LogLevel.Warn);
                return;
            }

            // 1. Patch FindGroupCompanion
            MethodInfo? findCompanionMethod = AccessTools.Method(directorType, "FindGroupCompanion", new[] { typeof(NPC) })
                ?? AccessTools.FirstMethod(directorType, m => m.Name == "FindGroupCompanion"
                    && m.GetParameters().Length >= 1
                    && m.GetParameters()[0].ParameterType == typeof(NPC));
            if (findCompanionMethod != null)
            {
                harmony.Patch(findCompanionMethod,
                    prefix: new HarmonyMethod(typeof(GroupTalkPatches), nameof(FindGroupCompanion_Prefix)));
                _monitor.Log("GroupTalkPatches: Patched FindGroupCompanion.", LogLevel.Info);
            }

            // 2. Patch CanStartWith
            MethodInfo? canStartMethod = AccessTools.Method(directorType, "CanStartWith", new[] { typeof(NPC), typeof(NPC), typeof(string).MakeByRefType() })
                ?? AccessTools.FirstMethod(directorType, m => m.Name == "CanStartWith"
                    && m.GetParameters().Length >= 3
                    && m.GetParameters()[0].ParameterType == typeof(NPC)
                    && m.GetParameters()[1].ParameterType == typeof(NPC));
            if (canStartMethod != null)
            {
                harmony.Patch(canStartMethod,
                    prefix: new HarmonyMethod(typeof(GroupTalkPatches), nameof(CanStartWith_Prefix)));
                _monitor.Log("GroupTalkPatches: Patched CanStartWith.", LogLevel.Info);
            }

            // 3. Patch TryStartWith
            MethodInfo? tryStartMethod = AccessTools.Method(directorType, "TryStartWith", new[] { typeof(NPC), typeof(NPC), typeof(string).MakeByRefType() })
                ?? AccessTools.FirstMethod(directorType, m => m.Name == "TryStartWith"
                    && m.GetParameters().Length >= 3
                    && m.GetParameters()[0].ParameterType == typeof(NPC)
                    && m.GetParameters()[1].ParameterType == typeof(NPC));
            if (tryStartMethod != null)
            {
                harmony.Patch(tryStartMethod,
                    prefix: new HarmonyMethod(typeof(GroupTalkPatches), nameof(TryStartWith_Prefix)));
                _monitor.Log("GroupTalkPatches: Patched TryStartWith.", LogLevel.Info);
            }

            _monitor.Log("GroupTalkPatches successfully applied.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _monitor.Log($"GroupTalkPatches encountered an error: {ex}", LogLevel.Error);
        }
    }

    public static bool FindGroupCompanion_Prefix(object __instance, NPC lead, ref NPC? __result)
    {
        try
        {
            if (lead == null || lead.currentLocation == null || Game1.currentLocation == null || lead.currentLocation != Game1.currentLocation)
            {
                __result = null;
                return false;
            }

            NPC? best = null;
            float bestDist = float.MaxValue;
            const float maxDistance = 10f; // Allow finding companion up to 10 tiles away

            foreach (var charItem in lead.currentLocation.characters)
            {
                if (charItem is NPC item && item != lead && !string.Equals(item.Name, lead.Name, StringComparison.OrdinalIgnoreCase))
                {
                    if (item.IsVillager && !item.IsInvisible && !item.isSleeping.Value && !string.IsNullOrWhiteSpace(item.Name))
                    {
                        float d = Vector2.Distance(item.Tile, lead.Tile);
                        if (d <= maxDistance && d < bestDist)
                        {
                            bestDist = d;
                            best = item;
                        }
                    }
                }
            }

            __result = best;
            return false;
        }
        catch (Exception ex)
        {
            _monitor?.Log($"FindGroupCompanion_Prefix error: {ex.Message}", LogLevel.Trace);
            return true; // Fall back to original
        }
    }

    public static bool CanStartWith_Prefix(object __instance, NPC lead, NPC other, ref string reason, ref bool __result)
    {
        try
        {
            if (!Context.IsWorldReady || Game1.currentLocation == null || Game1.player == null)
            {
                reason = Utilities.I18n.Get("grouptalk.world_not_ready");
                __result = false;
                return false;
            }

            if (lead == null || other == null)
            {
                reason = Utilities.I18n.Get("grouptalk.invalid_characters");
                __result = false;
                return false;
            }

            if (lead.currentLocation != Game1.currentLocation || other.currentLocation != Game1.currentLocation)
            {
                reason = Utilities.I18n.Get("grouptalk.different_locations");
                __result = false;
                return false;
            }

            float dist = Vector2.Distance(lead.Tile, other.Tile);
            if (dist > 12f)
            {
                reason = Utilities.I18n.Get("grouptalk.too_far");
                __result = false;
                return false;
            }

            if (lead.IsInvisible || other.IsInvisible || lead.isSleeping.Value || other.isSleeping.Value)
            {
                reason = Utilities.I18n.Get("grouptalk.npc_unavailable");
                __result = false;
                return false;
            }

            // Check if another group conversation session, scripted talk, or pending request is already active
            var directorType = __instance.GetType();
            var sessionField = AccessTools.Field(directorType, "_session");
            var pendingField = AccessTools.Field(directorType, "_pending");
            var scriptedField = AccessTools.Field(directorType, "_scripted");
            if ((sessionField != null && sessionField.GetValue(__instance) != null)
                || (pendingField != null && pendingField.GetValue(__instance) != null)
                || (scriptedField != null && scriptedField.GetValue(__instance) != null))
            {
                reason = Utilities.I18n.Get("grouptalk.already_active");
                __result = false;
                return false;
            }

            reason = string.Empty;
            __result = true;
            return false; // Skip original to bypass !Context.IsMainPlayer, EnableSeenAndHeard, and _groupedNpcsToday blocks!
        }
        catch (Exception ex)
        {
            _monitor?.Log($"CanStartWith_Prefix error: {ex.Message}", LogLevel.Trace);
            return true; // Fall back to original
        }
    }

    public static bool TryStartWith_Prefix(object __instance, NPC lead, NPC other, ref string detail, ref bool __result)
    {
        try
        {
            string checkReason = string.Empty;
            if (!CanStartWith_Prefix(__instance, lead, other, ref checkReason, ref __result) && !__result)
            {
                detail = checkReason;
                return false;
            }

            Type directorType = __instance.GetType();
            Type? pendingType = AccessTools.Inner(directorType, "PendingGroup");
            if (pendingType == null)
            {
                _monitor?.Log("TryStartWith_Prefix: PendingGroup type not found.", LogLevel.Warn);
                return true;
            }

            // 1. Both NPCs halt, turn towards the interacting player, and show thinking emote
            lead.Halt();
            other.Halt();
            Farmer interactingFarmer = Execution.NpcActionExecutor.Instance.GetCompanionOwner(lead.Name)
                ?? Execution.NpcActionExecutor.Instance.GetCompanionOwner(other.Name)
                ?? Game1.player;
            lead.facePlayer(interactingFarmer);
            other.facePlayer(interactingFarmer);
            lead.doEmote(8);  // Speech bubble "..."
            other.doEmote(8);

            // 2. Instantiate PendingGroup dynamically (supports both <=1.8.0 legacy 4-string ctor and 1.8.1+/1.8.2+ list-based ctor)
            string reqId = $"group:{Guid.NewGuid():N}";
            string locationName = Game1.currentLocation.NameOrUniqueName;
            if (!TryCreatePendingGroup(pendingType, reqId, lead.Name, other.Name, locationName, out object? pending) || pending == null)
            {
                return true;
            }

            // 3. Build group shared world context
            object? dialogues = AccessTools.Field(directorType, "_dialogues")?.GetValue(__instance);
            string sharedWorldContext = string.Empty;
            if (dialogues != null)
            {
                MethodInfo? buildCtx = AccessTools.Method(dialogues.GetType(), "BuildGroupSharedWorldContext");
                if (buildCtx != null && TryInvokeWithOptionalDefaults(buildCtx, dialogues, Array.Empty<object?>(), out object? ctxResult))
                {
                    sharedWorldContext = ctxResult as string ?? string.Empty;
                }
            }
            AccessTools.Property(pendingType, "SharedWorldContext")?.SetValue(pending, sharedWorldContext);

            // 4. Build Request dynamically (handles both 1.8.1+/1.8.2+ collection+optional params and legacy 7-arg signature)
            MethodInfo? buildRequest = AccessTools.Method(directorType, "BuildRequest");
            if (buildRequest == null)
            {
                LogBuildRequestMismatchOnce("BuildRequest method not found on GroupConversationDirector.", null);
                return true;
            }

            Type? groupLineType = AccessTools.TypeByName("AliveNpcs.Services.GroupConversations.GroupLine");
            Array emptyTranscript = Array.CreateInstance(groupLineType ?? typeof(object), 0);
            object?[] base7Args = new object?[]
            {
                lead.Name,
                other.Name,
                Game1.currentLocation,
                emptyTranscript,
                null,
                false,
                sharedWorldContext
            };

            if (!TryInvokeBuildRequest(buildRequest, __instance, base7Args, out object? request) || request == null)
            {
                _monitor?.Log("TryStartWith_Prefix: Could not build GroupConversationRequest.", LogLevel.Warn);
                return true;
            }

            // 5. Store pending in director
            AccessTools.Field(directorType, "_pending")?.SetValue(__instance, pending);

            // 6. Launch generation task
            MethodInfo? prepareOpening = AccessTools.Method(directorType, "PrepareOpeningAsync");
            MethodInfo? presentMethod = AccessTools.Method(directorType, "Present");
            var mainThread = AccessTools.Field(directorType, "_mainThread")?.GetValue(__instance) as ConcurrentQueue<Action>;
            CancellationTokenSource? cancellation = AccessTools.Property(pendingType, "Cancellation")?.GetValue(pending) as CancellationTokenSource;
            CancellationToken token = cancellation?.Token ?? CancellationToken.None;

            _monitor?.Log($"[GroupChat] Starting group conversation between {lead.displayName} and {other.displayName}.", LogLevel.Info);

            Task.Run(async () =>
            {
                try
                {
                    if (prepareOpening == null || presentMethod == null) return;
                    if (!TryInvokeWithOptionalDefaults(prepareOpening, __instance, new object?[] { pending, request, token }, out object? rawTask)
                        || rawTask is not Task<string?> task)
                    {
                        _monitor?.Log("[GroupChat] PrepareOpeningAsync could not be invoked dynamically.", LogLevel.Warn);
                        ClearPendingSafely(__instance, directorType, pending, mainThread, cancellation);
                        return;
                    }

                    string? firstLine = await task;

                    if (!string.IsNullOrEmpty(firstLine))
                    {
                        Action presentAction = () =>
                        {
                            try
                            {
                                // Ensure any leftover interaction menu or dialogue is cleared before opening GroupConversationMenu
                                if (Game1.dialogueUp)
                                {
                                    Game1.dialogueUp = false;
                                    Game1.currentSpeaker = null;
                                }
                                if (Game1.activeClickableMenu != null && Game1.activeClickableMenu.GetType().Name.Contains("Interaction"))
                                {
                                    Game1.activeClickableMenu = null;
                                }

                                if (!TryInvokeWithOptionalDefaults(presentMethod, __instance, new object?[] { pending }, out _))
                                {
                                    _monitor?.Log("[GroupChat] Present could not be invoked dynamically.", LogLevel.Warn);
                                }
                            }
                            catch (Exception ex)
                            {
                                _monitor?.Log($"[GroupChat] Error presenting group chat: {ex}", LogLevel.Error);
                            }
                        };

                        if (mainThread != null)
                        {
                            mainThread.Enqueue(presentAction);
                        }
                    }
                    else
                    {
                        _monitor?.Log("[GroupChat] Opening turn could not be generated by Gemini.", LogLevel.Warn);
                        ClearPendingSafely(__instance, directorType, pending, mainThread, cancellation);
                    }
                }
                catch (Exception ex)
                {
                    _monitor?.Log($"[GroupChat] Exception during opening preparation: {ex.Message}", LogLevel.Error);
                    ClearPendingSafely(__instance, directorType, pending, mainThread, cancellation);
                }
            });

            detail = Utilities.I18n.Get("grouptalk.joined", new { leadName = lead.displayName, otherName = other.displayName });
            __result = true;
            return false;
        }
        catch (Exception ex)
        {
            _monitor?.Log($"TryStartWith_Prefix error: {ex}", LogLevel.Error);
            return true; // Fall back to original
        }
    }

    private static void ClearPendingSafely(
        object director,
        Type directorType,
        object pending,
        ConcurrentQueue<Action>? mainThread,
        CancellationTokenSource? cancellation)
    {
        var pendingField = AccessTools.Field(directorType, "_pending");
        Action cleanup = () =>
        {
            try
            {
                if (pendingField != null && ReferenceEquals(pendingField.GetValue(director), pending))
                {
                    pendingField.SetValue(director, null);
                }
            }
            catch { }

            try { cancellation?.Dispose(); } catch { }
        };

        if (mainThread != null)
        {
            mainThread.Enqueue(cleanup);
        }
        else
        {
            cleanup();
        }
    }

    private static bool TryCreatePendingGroup(
        Type pendingGroupType,
        string requestId,
        string leadName,
        string otherName,
        string locationName,
        out object? instance)
    {
        instance = null;
        try
        {
            ConstructorInfo[] ctors = pendingGroupType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            // 1. AliveNpcs 1.8.1+/1.8.2+: (string requestId, IReadOnlyList<string> / List<string> / string[] npcs, string locationName, [optional...])
            foreach (ConstructorInfo ctor in ctors)
            {
                ParameterInfo[] parms = ctor.GetParameters();
                if (parms.Length >= 3
                    && parms[0].ParameterType == typeof(string)
                    && IsStringListParameter(parms[1].ParameterType)
                    && parms[2].ParameterType == typeof(string)
                    && AreTrailingParametersDefaulted(parms, 3))
                {
                    object?[] args = new object?[parms.Length];
                    args[0] = requestId;
                    args[1] = CreateParticipantsArg(parms[1].ParameterType, leadName, otherName);
                    args[2] = locationName;
                    FillTrailingDefaultArgs(parms, 3, args);

                    instance = ctor.Invoke(args);
                    if (instance != null)
                        return true;
                }
            }

            // 2. Legacy AliveNpcs <= 1.8.0: (string requestId, string leadName, string otherName, string locationName, [optional...])
            foreach (ConstructorInfo ctor in ctors)
            {
                ParameterInfo[] parms = ctor.GetParameters();
                if (parms.Length >= 4
                    && parms[0].ParameterType == typeof(string)
                    && parms[1].ParameterType == typeof(string)
                    && parms[2].ParameterType == typeof(string)
                    && parms[3].ParameterType == typeof(string)
                    && AreTrailingParametersDefaulted(parms, 4))
                {
                    object?[] args = new object?[parms.Length];
                    args[0] = requestId;
                    args[1] = leadName;
                    args[2] = otherName;
                    args[3] = locationName;
                    FillTrailingDefaultArgs(parms, 4, args);

                    instance = ctor.Invoke(args);
                    if (instance != null)
                        return true;
                }
            }

            if (!_loggedSignatureMismatch)
            {
                _loggedSignatureMismatch = true;
                string sigs = string.Join(" | ", ctors.Select(c =>
                    $"({string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})"));
                _monitor?.Log($"GroupTalkPatches: Incompatible PendingGroup constructor signatures in {pendingGroupType.FullName}: [{sigs}]", LogLevel.Warn);
            }
        }
        catch (Exception ex)
        {
            if (!_loggedSignatureMismatch)
            {
                _loggedSignatureMismatch = true;
                _monitor?.Log($"GroupTalkPatches: Failed to instantiate PendingGroup dynamically: {ex.Message}", LogLevel.Warn);
            }
        }
        return false;
    }

    private static bool TryInvokeBuildRequest(
        MethodInfo buildRequestMethod,
        object directorInstance,
        object?[] base7Args,
        out object? request)
    {
        request = null;
        try
        {
            ParameterInfo[] parms = buildRequestMethod.GetParameters();

            // AliveNpcs 1.8.1+/1.8.2+: BuildRequest(IReadOnlyList<string> names, GameLocation location, IReadOnlyList<GroupLine> transcript, string? playerMessage, bool isFinalTurn, string sharedWorldContext = "", [optional...])
            if (parms.Length >= 6 && base7Args.Length >= 7 && IsStringListParameter(parms[0].ParameterType))
            {
                string leadName = base7Args[0] as string ?? string.Empty;
                string otherName = base7Args[1] as string ?? string.Empty;

                object?[] fullArgs = new object?[parms.Length];
                fullArgs[0] = CreateParticipantsArg(parms[0].ParameterType, leadName, otherName);
                fullArgs[1] = base7Args[2];
                fullArgs[2] = base7Args[3];
                fullArgs[3] = base7Args[4];
                fullArgs[4] = base7Args[5];
                fullArgs[5] = base7Args[6];
                FillTrailingNullableOrDefaultArgs(parms, 6, fullArgs);

                request = buildRequestMethod.Invoke(directorInstance, fullArgs);
                return request != null;
            }

            // Legacy AliveNpcs <= 1.8.0 (7 params) or pair-first variant with extra optional params
            if (parms.Length < base7Args.Length)
            {
                LogBuildRequestMismatchOnce($"BuildRequest expects {parms.Length} parameters (minimum {base7Args.Length} needed for legacy signature).", parms);
                return false;
            }

            if (parms.Length == base7Args.Length)
            {
                request = buildRequestMethod.Invoke(directorInstance, base7Args);
                return request != null;
            }

            object?[] legacyPlusArgs = new object?[parms.Length];
            Array.Copy(base7Args, legacyPlusArgs, base7Args.Length);
            FillTrailingNullableOrDefaultArgs(parms, base7Args.Length, legacyPlusArgs);

            request = buildRequestMethod.Invoke(directorInstance, legacyPlusArgs);
            return request != null;
        }
        catch (Exception ex)
        {
            LogBuildRequestMismatchOnce($"Exception invoking BuildRequest: {ex.Message}", buildRequestMethod.GetParameters());
            return false;
        }
    }

    private static bool TryInvokeWithOptionalDefaults(MethodInfo method, object target, object?[] requiredArgs, out object? result)
    {
        result = null;
        ParameterInfo[] parms = method.GetParameters();
        if (parms.Length < requiredArgs.Length)
            return false;

        object?[] args = new object?[parms.Length];
        if (requiredArgs.Length > 0)
            Array.Copy(requiredArgs, args, requiredArgs.Length);
        FillTrailingNullableOrDefaultArgs(parms, requiredArgs.Length, args);

        result = method.Invoke(target, args);
        return true;
    }

    private static bool IsStringListParameter(Type paramType)
        => paramType.IsAssignableFrom(typeof(List<string>)) || paramType == typeof(string[]);

    private static object CreateParticipantsArg(Type paramType, string leadName, string otherName)
        => paramType == typeof(string[]) ? new[] { leadName, otherName } : new List<string> { leadName, otherName };

    private static bool AreTrailingParametersDefaulted(ParameterInfo[] parms, int startIndex)
    {
        for (int i = startIndex; i < parms.Length; i++)
        {
            if (!parms[i].HasDefaultValue)
                return false;
        }
        return true;
    }

    private static void FillTrailingDefaultArgs(ParameterInfo[] parms, int startIndex, object?[] args)
    {
        for (int i = startIndex; i < parms.Length; i++)
        {
            args[i] = ResolveDefaultValue(parms[i]);
        }
    }

    private static void FillTrailingNullableOrDefaultArgs(ParameterInfo[] parms, int startIndex, object?[] args)
    {
        for (int i = startIndex; i < parms.Length; i++)
        {
            args[i] = parms[i].HasDefaultValue
                ? ResolveDefaultValue(parms[i])
                : ResolveNullOrDefaultForType(parms[i].ParameterType);
        }
    }

    private static object? ResolveDefaultValue(ParameterInfo parm)
    {
        object? rawDefault = parm.DefaultValue;
        if (rawDefault == null || rawDefault == DBNull.Value || rawDefault == Type.Missing)
            return ResolveNullOrDefaultForType(parm.ParameterType);
        return rawDefault;
    }

    private static object? ResolveNullOrDefaultForType(Type paramType)
    {
        if (!paramType.IsValueType || Nullable.GetUnderlyingType(paramType) != null)
            return null;
        return Activator.CreateInstance(paramType);
    }

    private static void LogBuildRequestMismatchOnce(string reason, ParameterInfo[]? parms)
    {
        if (_loggedBuildRequestMismatch) return;
        _loggedBuildRequestMismatch = true;
        string sig = parms != null
            ? $"({string.Join(", ", parms.Select(p => $"{p.ParameterType.Name} {p.Name}"))})"
            : "<unknown>";
        _monitor?.Log($"GroupTalkPatches: BuildRequest signature mismatch — {reason} Actual params: {sig}", LogLevel.Warn);
    }
}
