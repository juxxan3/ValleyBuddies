namespace ValleyBuddies.Systems;

using System;
using System.Collections.Generic;
using StardewValley;

/// <summary>
/// Centralized service for validating, loading, and playing native animations from Data\animationDescriptions.
/// Supports Vanilla, SVE, RSV, and custom modded NPCs with robust out-of-bounds sprite sheet validation.
/// </summary>
public static class NpcAnimationService
{
    private static Dictionary<string, string>? _cachedAnimations;

    private static void EnsureAnimationCache()
    {
        if (_cachedAnimations != null) return;
        try
        {
            _cachedAnimations = Game1.content.Load<Dictionary<string, string>>("Data\\animationDescriptions");
        }
        catch
        {
            _cachedAnimations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void ResetDaily()
    {
        _cachedAnimations = null;
    }

    /// <summary>
    /// Attempts to parse and play an animation key from Data\animationDescriptions.
    /// Validates sprite sheet dimensions to guarantee NPCs never show invisible/corrupted frames.
    /// </summary>
    public static bool TryPlayNamedAnimation(NPC npc, string animationKey, bool loop = true, int frameDurationMs = 140)
    {
        if (npc?.Sprite == null || string.IsNullOrWhiteSpace(animationKey))
            return false;

        EnsureAnimationCache();
        if (_cachedAnimations == null)
            return false;

        string? matchedKey = null;
        foreach (var key in _cachedAnimations.Keys)
        {
            if (key.Equals(animationKey, StringComparison.OrdinalIgnoreCase))
            {
                matchedKey = key;
                break;
            }
        }

        if (matchedKey == null || !_cachedAnimations.TryGetValue(matchedKey, out var rawDesc) || string.IsNullOrWhiteSpace(rawDesc))
            return false;

        try
        {
            string[] parts = rawDesc.Split('/');
            string loopDesc = (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[1])) ? parts[1] : parts[0];
            string[] tokens = loopDesc.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            var tex = npc.Sprite.Texture;
            int spriteWidth = Math.Max(1, npc.Sprite.SpriteWidth);
            int spriteHeight = Math.Max(1, npc.Sprite.SpriteHeight);
            int totalFrames = (tex != null) ? (tex.Width / spriteWidth) * (tex.Height / spriteHeight) : 9999;

            var frames = new List<FarmerSprite.AnimationFrame>();
            foreach (var tok in tokens)
            {
                if (int.TryParse(tok, out int fNum))
                {
                    if (fNum < 0 || fNum >= totalFrames)
                    {
                        return false; // Out-of-bounds frame protection against corrupted sprite sheets
                    }
                    frames.Add(new FarmerSprite.AnimationFrame(fNum, frameDurationMs, false, false));
                }
            }

            if (frames.Count == 0)
                return false;

            npc.Sprite.setCurrentAnimation(frames);
            npc.Sprite.loop = loop;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static readonly string[] SittingOrFurnitureSuffixes =
    {
        "_read", "_study", "_computer", "_videogames", "_sculpt", "_dishes",
        "_sleep", "_sit", "_couch", "_desk", "_piano", "_sewing", "_bed", "_tinker"
    };

    /// <summary>
    /// Devuelve true si la clave de animación corresponde a mobiliario/asiento (no apta para cinemáticas de pie).
    /// </summary>
    public static bool IsSittingOrFurnitureAnimation(string? animationKey)
    {
        if (string.IsNullOrWhiteSpace(animationKey)) return true;
        foreach (string suffix in SittingOrFurnitureSuffixes)
        {
            if (animationKey.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Validates candidate animation keys from Data\animationDescriptions against the NPC's actual sprite sheet
    /// and outputs safe frame indices for StardewValley.Event animate scripts.
    /// </summary>
    public static bool TryGetValidatedAnimationFrames(NPC? npc, string[] candidateKeys, out int[] frames, bool standingOnly = false)
    {
        frames = Array.Empty<int>();
        if (npc?.Sprite == null || candidateKeys == null || candidateKeys.Length == 0)
            return false;

        EnsureAnimationCache();
        if (_cachedAnimations == null)
            return false;

        var tex = npc.Sprite.Texture;
        if (tex == null)
            return false;

        int spriteWidth = Math.Max(1, npc.Sprite.SpriteWidth);
        int spriteHeight = Math.Max(1, npc.Sprite.SpriteHeight);
        int totalFrames = (tex.Width / spriteWidth) * (tex.Height / spriteHeight);
        if (totalFrames <= 0)
            return false;

        foreach (string candidate in candidateKeys)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (standingOnly && IsSittingOrFurnitureAnimation(candidate)) continue;

            string? matchedKey = null;
            foreach (var key in _cachedAnimations.Keys)
            {
                if (key.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    matchedKey = key;
                    break;
                }
            }

            if (matchedKey == null || !_cachedAnimations.TryGetValue(matchedKey, out var rawDesc) || string.IsNullOrWhiteSpace(rawDesc))
                continue;

            try
            {
                string[] parts = rawDesc.Split('/');
                string loopDesc = (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[1])) ? parts[1] : parts[0];
                string[] tokens = loopDesc.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                var parsed = new List<int>();
                bool valid = true;
                foreach (var tok in tokens)
                {
                    if (int.TryParse(tok, out int fNum))
                    {
                        if (fNum < 0 || fNum >= totalFrames)
                        {
                            valid = false;
                            break;
                        }
                        parsed.Add(fNum);
                    }
                }

                if (valid && parsed.Count > 0)
                {
                    frames = parsed.ToArray();
                    return true;
                }
            }
            catch { }
        }

        return false;
    }

    /// <summary>
    /// Attempts to play the iconic hobby/reading/working animation for an NPC.
    /// Falls back to {name}_read, {name}_work, and {name}_idle.
    /// </summary>
    public static bool TryPlayCharacterHobbyAnimation(NPC npc)
    {
        if (npc == null) return false;
        string name = npc.Name.ToLowerInvariant();

        string? specificKey = name switch
        {
            "penny" => "penny_read",
            "abigail" => "abigail_videogames",
            "sebastian" => "sebastian_computer",
            "sam" => "sam_guitar",
            "leah" => "leah_sculpt",
            "elliott" => "elliott_read",
            "harvey" => "harvey_read",
            "maru" => "maru_tinker",
            "alex" => "alex_lift_weights",
            "emily" => "emily_exercise",
            "shane" => "shane_drink",
            "haley" => "haley_photo",
            "krobus" => "krobus_dance",
            _ => null
        };

        if (specificKey != null && TryPlayNamedAnimation(npc, specificKey, loop: true, frameDurationMs: 140))
            return true;

        if (TryPlayNamedAnimation(npc, $"{name}_read", loop: true, frameDurationMs: 140)) return true;
        if (TryPlayNamedAnimation(npc, $"{name}_work", loop: true, frameDurationMs: 140)) return true;
        if (TryPlayNamedAnimation(npc, $"{name}_idle", loop: true, frameDurationMs: 140)) return true;

        return false;
    }

    /// <summary>
    /// Attempts to play dishwashing animation (e.g. penny_dishes).
    /// </summary>
    public static bool TryPlayDishwashingAnimation(NPC npc)
    {
        if (npc == null) return false;
        string name = npc.Name.ToLowerInvariant();
        return TryPlayNamedAnimation(npc, $"{name}_dishes", loop: true, frameDurationMs: 120);
    }

    /// <summary>
    /// Triggers an expressive character reaction animation and emote.
    /// Supported emotions: "sad", "happy", "surprised".
    /// </summary>
    public static bool TryPlayEmotionGesture(NPC npc, string emotionKind)
    {
        if (npc == null) return false;
        string name = npc.Name.ToLowerInvariant();

        switch (emotionKind.ToLowerInvariant())
        {
            case "sad":
                if (!TryPlayNamedAnimation(npc, $"{name}_sad", loop: false, frameDurationMs: 160))
                {
                    TryPlayNamedAnimation(npc, $"{name}_cry", loop: false, frameDurationMs: 160);
                }
                npc.doEmote(28);
                npc.shake(220);
                return true;

            case "happy":
                TryPlayNamedAnimation(npc, $"{name}_laugh", loop: false, frameDurationMs: 150);
                npc.jumpWithoutSound(3.0f);
                npc.doEmote(20);
                return true;

            case "surprised":
                TryPlayNamedAnimation(npc, $"{name}_surprised", loop: false, frameDurationMs: 150);
                npc.jumpWithoutSound(3.2f);
                npc.doEmote(16);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Safely halts any active custom animation and updates source rect to avoid frozen frames.
    /// </summary>
    public static void StopAnimationCleanly(NPC npc)
    {
        if (npc?.Sprite == null) return;
        try
        {
            npc.Sprite.CurrentAnimation = null;
            npc.Sprite.StopAnimation();
            npc.Sprite.UpdateSourceRect();
        }
        catch { }
    }
}
