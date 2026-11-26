using System;
using System.Globalization;
using System.Text.Json;

namespace ValleyBuddies.Actions;

/// <summary>Wraps one entry from the "npc_action" JSON array with LLM-resilient parsing.</summary>
public sealed class ActionRequest
{
    public long        OwnerPlayerId { get; set; } = StardewValley.Game1.player?.UniqueMultiplayerID ?? 0;
    public string      Action  { get; init; } = string.Empty;
    public string      NpcName { get; init; } = string.Empty;
    public JsonElement Payload { get; init; }

    private bool TryGetPropertyIgnoreCase(string key, out JsonElement value)
    {
        if (Payload.ValueKind == JsonValueKind.Object)
        {
            if (Payload.TryGetProperty(key, out value))
                return true;
            foreach (var prop in Payload.EnumerateObject())
            {
                if (prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    public string? GetString(string key)
    {
        if (!TryGetPropertyIgnoreCase(key, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString()?.Trim(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True   => "true",
            JsonValueKind.False  => "false",
            _ => null
        };
    }

    public string? GetStringAny(params string[] keys)
    {
        foreach (var k in keys)
        {
            string? val = GetString(k);
            if (!string.IsNullOrWhiteSpace(val))
                return val;
        }
        return null;
    }

    public int GetInt(string key, int fallback = 0)
    {
        if (!TryGetPropertyIgnoreCase(key, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)) return (int)Math.Round(d);
        if (v.ValueKind == JsonValueKind.String)
        {
            string s = (v.GetString() ?? "").Trim().Replace(":", "");
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                return parsed;
        }
        return fallback;
    }

    public double GetDouble(string key, double fallback = 0)
    {
        if (!TryGetPropertyIgnoreCase(key, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)) return d;
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString()?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            return parsed;
        return fallback;
    }

    public bool GetBool(string key, bool fallback = false)
    {
        if (!TryGetPropertyIgnoreCase(key, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        if (v.ValueKind == JsonValueKind.String)
        {
            string s = (v.GetString() ?? "").Trim().ToLowerInvariant();
            if (s is "true" or "1" or "yes" or "si" or "sí") return true;
            if (s is "false" or "0" or "no") return false;
        }
        return fallback;
    }
}
