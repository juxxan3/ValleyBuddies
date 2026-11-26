using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyBuddies.Execution;

namespace ValleyBuddies.Bridge;

/// <summary>
/// Harmony diagnostic tracer (off by default, 0 overhead when disabled) for Game1.warpCharacter
/// and Character.setTileLocation / setTilePosition. Controlled via console command 'vb_warp_trace'.
/// </summary>
public static class WarpTracePatches
{
    private const int MaxEntries = 50;
    private static readonly object _bufferLock = new();
    private static readonly Queue<string> _entries = new();
    private static IMonitor? _monitor;

    [ThreadStatic]
    private static int _depth;

    public static bool IsEnabled { get; private set; }
    public static string? FilterNpcName { get; private set; }

    public struct WarpSnapshot
    {
        public string OldLocName;
        public Point OldTile;
        public bool Captured;
    }

    public static void EnableAll()
    {
        IsEnabled = true;
        FilterNpcName = null;
    }

    public static void EnableForNpc(string npcName)
    {
        IsEnabled = true;
        FilterNpcName = string.IsNullOrWhiteSpace(npcName) ? null : npcName.Trim();
    }

    public static void Disable()
    {
        IsEnabled = false;
        FilterNpcName = null;
    }

    public static void Clear()
    {
        lock (_bufferLock)
        {
            _entries.Clear();
        }
    }

    public static IReadOnlyList<string> GetDump()
    {
        lock (_bufferLock)
        {
            return _entries.ToList();
        }
    }

    public static void Apply(Harmony harmony, IMonitor monitor)
    {
        _monitor = monitor;
        try
        {
            var warpByLoc = AccessTools.Method(typeof(Game1), nameof(Game1.warpCharacter), new[] { typeof(NPC), typeof(GameLocation), typeof(Vector2) });
            if (warpByLoc != null)
            {
                harmony.Patch(warpByLoc,
                    prefix: new HarmonyMethod(typeof(WarpTracePatches), nameof(WarpByLoc_Prefix)),
                    postfix: new HarmonyMethod(typeof(WarpTracePatches), nameof(WarpByLoc_Postfix)));
            }

            var warpByName = AccessTools.Method(typeof(Game1), nameof(Game1.warpCharacter), new[] { typeof(NPC), typeof(string), typeof(Vector2) });
            if (warpByName != null)
            {
                harmony.Patch(warpByName,
                    prefix: new HarmonyMethod(typeof(WarpTracePatches), nameof(WarpByName_Prefix)),
                    postfix: new HarmonyMethod(typeof(WarpTracePatches), nameof(WarpByName_Postfix)));
            }

            var setTileLoc = AccessTools.Method(typeof(Character), nameof(Character.setTileLocation), new[] { typeof(Vector2) });
            if (setTileLoc != null)
            {
                harmony.Patch(setTileLoc,
                    prefix: new HarmonyMethod(typeof(WarpTracePatches), nameof(SetTile_Prefix)),
                    postfix: new HarmonyMethod(typeof(WarpTracePatches), nameof(SetTile_Postfix)));
            }

            var setTilePos = AccessTools.Method(typeof(Character), "setTilePosition", new[] { typeof(Point) });
            if (setTilePos != null)
            {
                harmony.Patch(setTilePos,
                    prefix: new HarmonyMethod(typeof(WarpTracePatches), nameof(SetTile_Prefix)),
                    postfix: new HarmonyMethod(typeof(WarpTracePatches), nameof(SetTile_Postfix)));
            }

            _monitor.Log("WarpTracePatches registered cleanly (disabled until vb_warp_trace on).", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            _monitor?.Log($"WarpTracePatches failed to apply: {ex.Message}", LogLevel.Warn);
        }
    }

    private static WarpSnapshot CapturePrefix(NPC? npc)
    {
        if (!IsEnabled || npc == null) return default;
        if (!string.IsNullOrEmpty(FilterNpcName) && !string.Equals(npc.Name, FilterNpcName, StringComparison.OrdinalIgnoreCase))
            return default;

        _depth++;
        if (_depth > 1) return default;

        return new WarpSnapshot
        {
            Captured = true,
            OldLocName = npc.currentLocation?.NameOrUniqueName ?? "null",
            OldTile = npc.TilePoint
        };
    }

    private static void FinishPostfix(NPC? npc, WarpSnapshot state, string apiName)
    {
        if (!IsEnabled || npc == null) return;
        if (!string.IsNullOrEmpty(FilterNpcName) && !string.Equals(npc.Name, FilterNpcName, StringComparison.OrdinalIgnoreCase))
            return;

        if (_depth > 0) _depth--;
        if (!state.Captured || _depth != 0) return;

        try
        {
            string newLocName = npc.currentLocation?.NameOrUniqueName ?? "null";
            Point newTile = npc.TilePoint;
            bool changedMap = !string.Equals(state.OldLocName, newLocName, StringComparison.OrdinalIgnoreCase);
            float tileJump = Vector2.Distance(new Vector2(state.OldTile.X, state.OldTile.Y), new Vector2(newTile.X, newTile.Y));

            if (!changedMap && tileJump < 1.5f)
                return;

            string distStr = "diff-map";
            if (Game1.player?.currentLocation != null
                && string.Equals(Game1.player.currentLocation.NameOrUniqueName, newLocName, StringComparison.OrdinalIgnoreCase))
            {
                distStr = $"{Vector2.Distance(Game1.player.Tile, new Vector2(newTile.X, newTile.Y)):F1}t";
            }

            var (isComp, activeKind, isSpouseHome) = NpcActionExecutor.Instance.GetWarpTraceState(npc.Name);
            string callers = BuildCompactCallStack();

            string line =
                $"tick={Game1.ticks} time={Game1.timeOfDay} api={apiName} npc={npc.Name} " +
                $"{state.OldLocName}@({state.OldTile.X},{state.OldTile.Y})->{newLocName}@({newTile.X},{newTile.Y}) " +
                $"jump={tileJump:F1}t distToPlayer={distStr} companion={isComp} job={activeKind} spouseHome={isSpouseHome} | stack=[{callers}]";

            RecordLine(line);
        }
        catch { }
    }

    internal static void RecordExplicitAuditNote(string note)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(note)) return;
        RecordLine(note);
    }

    private static void RecordLine(string line)
    {
        lock (_bufferLock)
        {
            _entries.Enqueue(line);
            while (_entries.Count > MaxEntries)
                _entries.Dequeue();
        }
        _monitor?.Log($"[WarpTrace] {line}", LogLevel.Info);
    }

    private static string BuildCompactCallStack()
    {
        var trace = new StackTrace(skipFrames: 2, fNeedFileInfo: true);
        var frames = trace.GetFrames();
        if (frames == null || frames.Length == 0) return "unknown";

        var picked = new List<string>(5);
        foreach (var frame in frames)
        {
            var method = frame.GetMethod();
            if (method == null) continue;
            string typeName = method.DeclaringType?.FullName ?? method.DeclaringType?.Name ?? string.Empty;
            string methodName = method.Name;

            if (string.IsNullOrEmpty(typeName)) continue;
            if (typeName.Contains("WarpTracePatches", StringComparison.Ordinal)) continue;
            if (typeName.StartsWith("HarmonyLib.", StringComparison.Ordinal)
                || typeName.StartsWith("System.", StringComparison.Ordinal)
                || typeName.Contains("_Patch", StringComparison.Ordinal)
                || methodName.StartsWith("DMD<", StringComparison.Ordinal))
                continue;

            string shortType = method.DeclaringType?.Name ?? typeName;
            int line = frame.GetFileLineNumber();
            string? file = frame.GetFileName();
            string locInfo = (!string.IsNullOrEmpty(file) && line > 0)
                ? $"({Path.GetFileName(file)}:{line})"
                : string.Empty;

            picked.Add($"{shortType}.{methodName}{locInfo}");
            if (picked.Count >= 5) break;
        }

        return picked.Count > 0 ? string.Join(" <- ", picked) : "native";
    }

    public static void WarpByLoc_Prefix(NPC character, out WarpSnapshot __state) => __state = CapturePrefix(character);
    public static void WarpByLoc_Postfix(NPC character, WarpSnapshot __state) => FinishPostfix(character, __state, "warpCharacter(loc)");

    public static void WarpByName_Prefix(NPC character, out WarpSnapshot __state) => __state = CapturePrefix(character);
    public static void WarpByName_Postfix(NPC character, WarpSnapshot __state) => FinishPostfix(character, __state, "warpCharacter(name)");

    public static void SetTile_Prefix(Character __instance, out WarpSnapshot __state) => __state = CapturePrefix(__instance as NPC);
    public static void SetTile_Postfix(Character __instance, WarpSnapshot __state) => FinishPostfix(__instance as NPC, __state, "setTile");
}
