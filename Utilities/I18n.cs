using System;
using StardewModdingAPI;

namespace ValleyBuddies.Utilities;

/// <summary>
/// Centralized translation helper providing type-safe, performant access to SMAPI i18n strings.
/// </summary>
public static class I18n
{
    private static ITranslationHelper? _translations;

    public static void Init(ITranslationHelper translations)
    {
        _translations = translations;
    }

    public static string Get(string key)
    {
        if (_translations == null) return key;
        var t = _translations.Get(key);
        return t.HasValue() ? t.ToString() : key;
    }

    public static string Get(string key, object? tokens)
    {
        if (_translations == null) return key;
        if (tokens == null) return Get(key);
        var t = _translations.Get(key, tokens);
        return t.HasValue() ? t.ToString() : key;
    }

    public static bool Has(string key)
    {
        return _translations != null && _translations.Get(key).HasValue();
    }
}
