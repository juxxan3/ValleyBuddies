using System;
using System.Collections.Generic;
using StardewModdingAPI;

namespace ValleyBuddies.Memory;

/// <summary>Persistent per-save tracking: daily action counts, agreed meetings, NPC job history.</summary>
public sealed class ActionMemory
{
    private readonly IModHelper _helper;
    private readonly IMonitor   _monitor;
    private SaveData            _data = new();

    public ActionMemory(IModHelper helper, IMonitor monitor)
    {
        _helper  = helper;
        _monitor = monitor;
    }

    // ── Persistence ───────────────────────────────────────────────

    public void Load()
    {
        try { _data = _helper.Data.ReadSaveData<SaveData>("ValleyBuddies.Save") ?? new SaveData(); }
        catch { _data = new SaveData(); }

        _data.DailyActionCounts = new Dictionary<string, int>(
            _data.DailyActionCounts ?? new Dictionary<string, int>(),
            StringComparer.OrdinalIgnoreCase);
        _data.History = new Dictionary<string, List<string>>(
            _data.History ?? new Dictionary<string, List<string>>(),
            StringComparer.OrdinalIgnoreCase);
    }

    public void Save()
    {
        try { _helper.Data.WriteSaveData("ValleyBuddies.Save", _data); }
        catch (System.Exception ex) { _monitor.Log("Save failed: " + ex.Message, LogLevel.Warn); }
    }

    public void ResetDaily()
    {
        _data.DailyActionCounts.Clear();
    }

    // ── Action count tracking ─────────────────────────────────────

    public int GetDailyCount(string npcName)
    {
        _data.DailyActionCounts.TryGetValue(npcName, out int v);
        return v;
    }

    public void IncrementDailyCount(string npcName)
    {
        _data.DailyActionCounts.TryGetValue(npcName, out int v);
        _data.DailyActionCounts[npcName] = v + 1;
    }

    // ── History ───────────────────────────────────────────────────

    public void RecordEvent(string npcName, string description)
    {
        if (!_data.History.TryGetValue(npcName, out var list))
            _data.History[npcName] = list = new List<string>();
        if (list.Count > 20) list.RemoveAt(0);
        list.Add($"[Day {StardewValley.Game1.dayOfMonth}] {description}");
    }

    public List<string> GetRecentEvents(string npcName, int max = 5)
    {
        if (_data.History.TryGetValue(npcName, out var list) && list.Count > 0)
        {
            int start = System.Math.Max(0, list.Count - max);
            return list.GetRange(start, list.Count - start);
        }
        return new List<string>();
    }

    private sealed class SaveData
    {
        public Dictionary<string, int>          DailyActionCounts { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> History           { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
    }
}
