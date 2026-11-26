using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using ValleyBuddies.Actions;
using ValleyBuddies.Execution;
using ValleyBuddies.Memory;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;

namespace ValleyBuddies.Bridge;

/// <summary>
/// Harmony PREFIX  → injects [NPC PHYSICAL ACTIONS] block into the AI prompt.
/// Harmony POSTFIX → reads "npc_action" from the AI JSON and enqueues NpcJob instances.
/// </summary>
public static class ReactionInterceptor
{
    private static readonly ConcurrentQueue<(string NpcName, List<ActionRequest> Actions)> _queue = new();

    private static readonly System.Threading.AsyncLocal<string> _currentNpc = new();
    private static string? _lastNpcFallback;

    private static IMonitor?       _monitor;
    private static ValleyBuddiesReflection? _bridge;
    private static ActionRegistry? _registry;
    private static ActionContext?  _context;
    private static ActionMemory?   _memory;
    private static Func<string, string>?  _promptProvider;
    private static Func<bool>?     _officialActive;
    private static Func<string, object?, string>? _hudText;
    public static Multiplayer.MultiplayerSyncService? MultiplayerSync { get; set; }

    public static string? CurrentNpcName => _currentNpc.Value ?? _lastNpcFallback;

    // ──────────── Initialization ───────────────────────────────────

    public static void Initialize(IMonitor monitor, ValleyBuddiesReflection bridge,
        ActionRegistry registry, ActionContext context, ActionMemory memory,
        Func<string, string> promptProvider, Func<bool> officialActive)
    {
        _monitor        = monitor;
        _bridge         = bridge;
        _registry       = registry;
        _context        = context;
        _memory         = memory;
        _promptProvider = promptProvider;
        _officialActive = officialActive;

        // Also configure the executor
        NpcActionExecutor.Instance.Configure(monitor, memory, context.Config);
    }

    public static void SetHudText(Func<string, object?, string> fn) => _hudText = fn;

    // ──────────── Harmony patches ──────────────────────────────────

    public static bool ApplyPatches(Harmony harmony, ValleyBuddiesReflection bridge, IMonitor monitor)
    {
        try
        {
            harmony.Patch(bridge.EvaluateResponseMethod!,
                prefix: new HarmonyMethod(typeof(ReactionInterceptor), nameof(EvaluateResponsePrefix)));
            harmony.Patch(bridge.ParseReactionMethod!,
                postfix: new HarmonyMethod(typeof(ReactionInterceptor), nameof(ParseReactionPostfix)));
            monitor.Log("Harmony patches applied (EvaluateResponseAsync + ParseReactionResponse).", LogLevel.Debug);
            return true;
        }
        catch (Exception ex)
        {
            monitor.Log($"ValleyBuddies DISABLED — Harmony patch failed: {ex.Message}", LogLevel.Warn);
            return false;
        }
    }

    // PREFIX: runs before GeminiService calls the LLM
    public static void EvaluateResponsePrefix(string npcName, ref string modContext)
    {
        try
        {
            _currentNpc.Value = npcName;
            _lastNpcFallback  = npcName;

            if (_promptProvider == null) return;

            // If official injection already put the block in, skip
            if (modContext != null && modContext.Contains("[NPC PHYSICAL ACTIONS]", StringComparison.Ordinal))
            {
                _officialActive?.Invoke();
                return;
            }

            string block = _promptProvider(npcName);
            if (!string.IsNullOrWhiteSpace(block))
                modContext = string.IsNullOrWhiteSpace(modContext) ? block : modContext + "\n\n" + block;
        }
        catch (Exception ex)
        {
            _monitor?.Log("EvaluateResponsePrefix error (ignored): " + ex.Message, LogLevel.Trace);
        }
    }

    // POSTFIX: runs after ParseReactionResponse returns
    public static void ParseReactionPostfix(string rawResponse, object __result)
    {
        if (__result == null || string.IsNullOrWhiteSpace(rawResponse)) return;
        if (_registry == null || _context == null || !_context.Config.EnableMod) return;

        try
        {
            string npcName = _currentNpc.Value ?? _lastNpcFallback ?? "Unknown";
            var requests   = ExtractActions(rawResponse, npcName);
            if (requests.Count > 0)
                _queue.Enqueue((npcName, requests));
        }
        catch (Exception ex)
        {
            _monitor?.Log("ParseReactionPostfix error (ignored): " + ex.Message, LogLevel.Trace);
        }
    }

    // ──────────── Queue drain (called from main thread in UpdateTicked) ──

    public static void DrainQueue()
    {
        while (_queue.TryDequeue(out var pending))
            Dispatch(pending.NpcName, pending.Actions);
    }

    public static void ClearQueue()
    {
        while (_queue.TryDequeue(out _)) { }
    }

    private static void Dispatch(string npcName, List<ActionRequest> requests)
    {
        if (_registry == null || _context == null || _memory == null) return;
        if (_context.Config.BlockDuringEvents && Game1.eventUp) return;

        Farmer actingPlayer = !Context.IsMainPlayer ? Game1.player : (Execution.NpcActionExecutor.Instance.GetCompanionOwner(npcName) ?? Game1.player);

        int hearts = 0;
        try { hearts = actingPlayer?.getFriendshipHeartLevelForNPC(npcName) ?? 0; } catch { }

        foreach (var req in requests)
        {
            if (!_registry.TryGet(req.Action, out var action) || action == null) continue;
            if (!action.IsEnabled(_context)) continue;
            int minHearts = action.GetMinHearts(_context);
            if (hearts < minHearts)
            {
                _monitor?.Log($"[Actions] {npcName} refused {req.Action} — needs {minHearts} hearts, has {hearts}.", LogLevel.Debug);
                continue;
            }

            var valid = action.Validate(req, _context, hearts);
            if (!valid.IsSuccess)
            {
                _monitor?.Log($"[Actions] {npcName}.{req.Action} rejected: {valid.Message}", LogLevel.Debug);
                continue;
            }

            long myFarmerId = actingPlayer?.UniqueMultiplayerID ?? (Game1.player?.UniqueMultiplayerID ?? 0);
            string myFarmerName = actingPlayer?.Name ?? (Game1.player?.Name ?? string.Empty);

            if (!Context.IsMainPlayer && MultiplayerSync != null)
            {
                var dto = new Multiplayer.ExecuteNpcActionDto
                {
                    OwnerPlayerId = myFarmerId,
                    NpcName = npcName,
                    ActionName = req.Action,
                    PayloadJson = req.Payload.ValueKind != System.Text.Json.JsonValueKind.Undefined ? req.Payload.GetRawText() : "{}",
                    LocationName = actingPlayer?.currentLocation?.NameOrUniqueName ?? (Game1.player?.currentLocation?.NameOrUniqueName ?? ""),
                    PlayerTileX = (int)(actingPlayer?.Tile.X ?? (Game1.player?.Tile.X ?? 0)),
                    PlayerTileY = (int)(actingPlayer?.Tile.Y ?? (Game1.player?.Tile.Y ?? 0))
                };
                _monitor?.Log($"[Actions] Farmhand relaying '{req.Action}' for {npcName} to Host (loc={dto.LocationName})", LogLevel.Info);
                MultiplayerSync.SendActionRequestToHost(dto);
                ShowHud(valid);
                FeedMemory(npcName, req.Action, valid);
                continue;
            }

            var job = action.BuildJob(req, _context);
            if (job != null)
            {
                job.FarmerId = myFarmerId;
                job.FarmerName = myFarmerName;

                // Track companion state immediately for local player
                if (job.Kind == JobKind.Follow)
                    NpcActionExecutor.Instance.SetCompanion(npcName, myFarmerId, isVisitingFarm: false, isFollowing: true, farmerName: myFarmerName);
                else if (job.Kind == JobKind.VisitFarm || job.Kind == JobKind.HangOut)
                    NpcActionExecutor.Instance.SetCompanion(npcName, myFarmerId, isVisitingFarm: true, isFollowing: false, farmerName: myFarmerName);
                else if (job.Kind != JobKind.LeaveFarm && !NpcActionExecutor.Instance.IsNpcFollowingOrVisiting(npcName))
                    NpcActionExecutor.Instance.SetCompanion(npcName, myFarmerId, isVisitingFarm: Game1.getFarm() != null, isFollowing: false, farmerName: myFarmerName);

                NpcActionExecutor.Instance.Enqueue(job);
                _monitor?.Log($"[Actions] Enqueued {job.Kind} for {npcName} (Invited by: {myFarmerName} [{myFarmerId}]).", LogLevel.Info);

                // Broadcast job to Host and all multiplayer peers
                string extra = job.Kind switch
                {
                    JobKind.Follow or JobKind.ComeHere or JobKind.VisitFarm or JobKind.HangOut or JobKind.GoToLocation or JobKind.MeetAtTime or JobKind.Sleepover =>
                        job.Data.TryGetValue("locationName", out var loc)
                            ? (loc?.ToString() ?? "")
                            : (Game1.player?.currentLocation?.NameOrUniqueName ?? ""),
                    JobKind.ChopTrees =>
                        job.Data.TryGetValue("treeFilter", out var tf) ? (tf?.ToString() ?? "") : "",
                    JobKind.FetchFromChest =>
                        job.Data.TryGetValue("fetchCategory", out var fc) ? (fc?.ToString() ?? "") : "",
                    JobKind.TakeFromChest =>
                        job.Data.TryGetValue("item", out var it) ? (it?.ToString() ?? "") : "",
                    JobKind.GiveItem =>
                        job.Data.TryGetValue("itemName", out var itemName) ? (itemName?.ToString() ?? "") : "",
                    JobKind.DoAnimation =>
                        job.Data.TryGetValue("anim", out var anim) ? (anim?.ToString() ?? "") : "",
                    JobKind.HarvestCrops =>
                        job.Data.TryGetValue("harvestFlowers", out var hf) ? (hf?.ToString() ?? "") : "",
                    JobKind.ExternalTask =>
                        job.Data.TryGetValue("externalTaskId", out var extId)
                            ? (extId?.ToString() ?? "")
                            : (job.Data.TryGetValue("taskId", out var tId) ? (tId?.ToString() ?? "") : ""),
                    _ => ""
                };

                ValleyBuddies.ModEntry.BroadcastJob(npcName, job.Kind, myFarmerId, myFarmerName, extra);

                // HUD message
                ShowHud(valid);

                // Memory
                FeedMemory(npcName, req.Action, valid);
            }
        }
    }

    // ──────────── JSON extraction ──────────────────────────────────

    private static List<ActionRequest> ExtractActions(string raw, string npcName)
    {
        var list = new List<ActionRequest>();
        string? json = ExtractJsonObject(raw);
        if (json == null) return list;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("npc_action", out var field)) return list;

            void Collect(JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    string actionStr = el.GetString()!;
                    if (!string.IsNullOrWhiteSpace(actionStr))
                    {
                        list.Add(new ActionRequest
                        {
                            Action  = actionStr,
                            NpcName = npcName,
                            Payload = el.Clone(),
                        });
                    }
                    return;
                }

                if (el.ValueKind != JsonValueKind.Object) return;
                if (!el.TryGetProperty("action", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) return;
                list.Add(new ActionRequest
                {
                    Action  = nameEl.GetString()!,
                    NpcName = npcName,
                    Payload = el.Clone(),
                });
            }

            if (field.ValueKind == JsonValueKind.Object || field.ValueKind == JsonValueKind.String)
                Collect(field);
            else if (field.ValueKind == JsonValueKind.Array)
                foreach (var item in field.EnumerateArray())
                    Collect(item);
        }
        catch (JsonException) { }

        return list;
    }

    private static string? ExtractJsonObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string text = raw.Trim();
        int brace = text.IndexOf("```", StringComparison.Ordinal);
        if (brace >= 0)
        {
            int inner = text.IndexOf('{', brace);
            if (inner >= 0) text = text[inner..];
        }
        int start = text.IndexOf('{');
        int end   = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return text[start..(end + 1)];
    }

    // ──────────── HUD + Memory helpers ────────────────────────────

    private static void ShowHud(ActionResult result)
    {
        if (!(_context?.Config.ShowHudMessages ?? false) || result?.HudKey == null) return;
        try
        {
            string msg = _hudText != null
                ? _hudText(result.HudKey, result.HudTokens)
                : result.Message;
            Game1.addHUDMessage(new HUDMessage(msg, HUDMessage.newQuest_type));
        }
        catch { }
    }

    private static void FeedMemory(string npcName, string actionName, ActionResult result)
    {
        if (!(_context?.Config.EnableMemoryFeed ?? false)) return;
        if (!ValleyBuddiesMemoryFeed.IsActive) return;
        try
        {
            Farmer actingPlayer = !Context.IsMainPlayer ? Game1.player : (Execution.NpcActionExecutor.Instance.GetCompanionOwner(npcName) ?? Game1.player);
            string? farmerName = actingPlayer?.Name ?? (Game1.player?.Name ?? "the farmer");
            ValleyBuddiesMemoryFeed.Send(
                $"{farmerName} and {npcName}: {result.MemoryLine}",
                _context!.Config.MemoryDailyLimit);
        }
        catch { }
    }
}
