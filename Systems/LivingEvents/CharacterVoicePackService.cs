namespace ValleyBuddies.Systems.LivingEvents;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;

public sealed class CharacterCinematicVoice
{
    public Dictionary<string, string> Opening { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Story { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Story2 { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Romance { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Closing { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> PersonalQuestion { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> PersonalOpt1 { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> PersonalOpt2 { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> PersonalReply1 { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> PersonalReply2 { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string[] PreferredDestinations { get; set; } = Array.Empty<string>();
    public string[] PreferredAnimations { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Servicio modular que carga paquetes de voces cinemáticas (assets/cinematics/*.json)
/// bajo demanda según los mods instalados (Vanilla, SVE, Ridgeside Village y packs custom_*).
/// </summary>
public sealed class CharacterVoicePackService
{
    public static CharacterVoicePackService Instance { get; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly Dictionary<string, CharacterCinematicVoice> _voices = new(StringComparer.OrdinalIgnoreCase);

    public int VanillaLoadedCount { get; private set; }
    public int SveLoadedCount { get; private set; }
    public int RsvLoadedCount { get; private set; }
    public int CustomLoadedCount { get; private set; }
    public bool IsSveDetected { get; private set; }
    public bool IsRsvDetected { get; private set; }
    public int TotalLoadedCount => _voices.Count;

    public void Initialize(IModHelper helper, IMonitor monitor)
    {
        _voices.Clear();
        VanillaLoadedCount = 0;
        SveLoadedCount = 0;
        RsvLoadedCount = 0;
        CustomLoadedCount = 0;

        if (helper == null) return;

        string cinematicsDir = Path.Combine(helper.DirectoryPath, "assets", "cinematics");
        if (!Directory.Exists(cinematicsDir))
        {
            monitor?.Log($"[VoicePacks] Directory not found: {cinematicsDir}", LogLevel.Warn);
            return;
        }

        // 1. Siempre cargar Vanilla (36 NPCs)
        VanillaLoadedCount = LoadPackFile(Path.Combine(cinematicsDir, "vanilla_voices.json"), monitor);

        // 2. SVE bajo demanda
        IsSveDetected = helper.ModRegistry.IsLoaded("FlashShifter.StardewValleyExpandedCP") ||
                        helper.ModRegistry.IsLoaded("FlashShifter.SVECode");
        if (IsSveDetected)
        {
            SveLoadedCount = LoadPackFile(Path.Combine(cinematicsDir, "sve_voices.json"), monitor);
        }

        // 3. Ridgeside Village bajo demanda
        IsRsvDetected = helper.ModRegistry.IsLoaded("Rafseazz.RidgesideVillage") ||
                        helper.ModRegistry.IsLoaded("Rafseazz.RSVCP");
        if (IsRsvDetected)
        {
            RsvLoadedCount = LoadPackFile(Path.Combine(cinematicsDir, "rsv_voices.json"), monitor);
        }

        // 4. Packs custom futuros (custom_*.json)
        try
        {
            foreach (string customFile in Directory.GetFiles(cinematicsDir, "custom_*.json"))
            {
                CustomLoadedCount += LoadPackFile(customFile, monitor);
            }
        }
        catch { }

        monitor?.Log(
            $"[VoicePacks] Loaded {_voices.Count} cinematic character voices (Vanilla={VanillaLoadedCount}, SVE={SveLoadedCount} [installed={IsSveDetected}], RSV={RsvLoadedCount} [installed={IsRsvDetected}], Custom={CustomLoadedCount}).",
            LogLevel.Info);
    }

    private int LoadPackFile(string fullPath, IMonitor? monitor)
    {
        if (!File.Exists(fullPath)) return 0;

        try
        {
            string raw = File.ReadAllText(fullPath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, CharacterCinematicVoice>>(raw, JsonOpts);
            if (dict == null) return 0;

            int count = 0;
            foreach (var (npcName, voice) in dict)
            {
                if (string.IsNullOrWhiteSpace(npcName) || voice == null) continue;
                _voices[npcName.Trim()] = voice;
                count++;
            }
            return count;
        }
        catch (Exception ex)
        {
            monitor?.Log($"[VoicePacks] Failed to load '{Path.GetFileName(fullPath)}': {ex.Message}", LogLevel.Warn);
            return 0;
        }
    }

    public bool TryGetVoice(string? npcName, out CharacterCinematicVoice? voice)
    {
        if (!string.IsNullOrWhiteSpace(npcName) && _voices.TryGetValue(npcName, out var found))
        {
            voice = found;
            return true;
        }
        voice = null;
        return false;
    }

    public string? ResolveLocalized(Dictionary<string, string>? map, Farmer? owner, NPC? npc)
    {
        if (map == null || map.Count == 0) return null;

        bool isSpanish = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.es;
        string? raw = null;

        if (isSpanish)
        {
            if (!map.TryGetValue("es", out raw) || string.IsNullOrWhiteSpace(raw))
                map.TryGetValue("en", out raw);
        }
        else
        {
            if (!map.TryGetValue("en", out raw) || string.IsNullOrWhiteSpace(raw))
                map.TryGetValue("es", out raw);
        }

        if (string.IsNullOrWhiteSpace(raw)) return null;

        string playerName = owner?.displayName ?? owner?.Name ?? "Farmer";
        string npcDisplay = npc?.displayName ?? npc?.Name ?? string.Empty;

        return raw.Replace("{{playerName}}", playerName, StringComparison.OrdinalIgnoreCase)
                  .Replace("{{npcName}}", npcDisplay, StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> GetAllPreferredDestinations(NPC? npc, bool preferIndoor = false)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (npc != null && TryGetVoice(npc.Name, out var voice) && voice?.PreferredDestinations != null)
        {
            if (preferIndoor)
            {
                foreach (string candidate in voice.PreferredDestinations)
                {
                    if (string.IsNullOrWhiteSpace(candidate)) continue;
                    string mapName = candidate.Equals("SecretWoods", StringComparison.OrdinalIgnoreCase) ? "Woods" : candidate;
                    var loc = Game1.getLocationFromName(mapName);
                    if (loc != null && !loc.IsOutdoors && seen.Add(mapName))
                    {
                        results.Add(mapName);
                    }
                }
            }

            foreach (string candidate in voice.PreferredDestinations)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                string mapName = candidate.Equals("SecretWoods", StringComparison.OrdinalIgnoreCase) ? "Woods" : candidate;
                if (Game1.getLocationFromName(mapName) != null && seen.Add(mapName))
                {
                    results.Add(mapName);
                }
            }
        }

        if (preferIndoor)
        {
            if (Game1.getLocationFromName("ArchaeologyHouse") != null && seen.Add("ArchaeologyHouse"))
                results.Add("ArchaeologyHouse");
            if (Game1.getLocationFromName("Saloon") != null && seen.Add("Saloon"))
                results.Add("Saloon");
        }

        return results;
    }

    public string? GetPreferredDestination(NPC? npc, bool preferIndoor = false)
    {
        var all = GetAllPreferredDestinations(npc, preferIndoor);
        return all.Count > 0 ? all[0] : null;
    }

    public bool IsPreferredDestination(NPC? npc, GameLocation? loc)
    {
        if (npc == null || loc == null) return false;
        if (!TryGetVoice(npc.Name, out var voice) || voice?.PreferredDestinations == null || voice.PreferredDestinations.Length == 0)
            return false;

        string locName = loc.NameOrUniqueName ?? string.Empty;
        string baseName = loc.Name ?? string.Empty;

        foreach (string candidate in voice.PreferredDestinations)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (locName.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                baseName.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (candidate.Equals("SecretWoods", StringComparison.OrdinalIgnoreCase) &&
                (loc is StardewValley.Locations.Woods || locName.Contains("Woods", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    public string[] GetPreferredAnimations(string? npcName)
    {
        if (TryGetVoice(npcName, out var voice) && voice?.PreferredAnimations != null && voice.PreferredAnimations.Length > 0)
        {
            return voice.PreferredAnimations;
        }
        return Array.Empty<string>();
    }
}
