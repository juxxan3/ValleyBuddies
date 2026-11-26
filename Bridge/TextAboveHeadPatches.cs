using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;

namespace ValleyBuddies.Bridge;

/// <summary>
/// Patches NPC.showTextAboveHead and NPC.drawAboveAlwaysFrontLayer to automatically wrap
/// and render compact overhead speech bubbles scaled and clamped inside the viewport.
/// </summary>
public static class TextAboveHeadPatches
{
    private static IMonitor? _monitor;

    public static bool IsApplyingRemoteSpeech { get; set; } = false;
    private static readonly Dictionary<string, (string Text, int Tick)> _lastRelayedByNpc = new(StringComparer.OrdinalIgnoreCase);

    public static void Reset()
    {
        _lastRelayedByNpc.Clear();
        IsApplyingRemoteSpeech = false;
    }

    public static void Apply(Harmony harmony, IMonitor monitor)
    {
        _monitor = monitor;
        try
        {
            var showMethod = AccessTools.FirstMethod(typeof(NPC), m => m.Name == nameof(NPC.showTextAboveHead) && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(string));

            if (showMethod != null)
            {
                harmony.Patch(showMethod,
                    prefix: new HarmonyMethod(typeof(TextAboveHeadPatches), nameof(ShowTextAboveHead_Prefix)),
                    postfix: new HarmonyMethod(typeof(TextAboveHeadPatches), nameof(ShowTextAboveHead_Postfix)));
                _monitor.Log($"TextAboveHeadPatches applied to {showMethod}: Auto-wrapping overhead dialogue bubbles and multiplayer speech sync.", LogLevel.Debug);
            }
            else
            {
                _monitor.Log("TextAboveHeadPatches failed to apply: NPC.showTextAboveHead method was not found.", LogLevel.Warn);
            }

            var drawMethod = AccessTools.Method(typeof(NPC), nameof(NPC.drawAboveAlwaysFrontLayer), new[] { typeof(SpriteBatch) });
            if (drawMethod != null)
            {
                harmony.Patch(drawMethod,
                    prefix: new HarmonyMethod(typeof(TextAboveHeadPatches), nameof(DrawAboveAlwaysFrontLayer_Prefix)));
                _monitor.Log($"TextAboveHeadPatches applied to {drawMethod}: Viewport-clamped compact speech bubbles active.", LogLevel.Debug);
            }
            else
            {
                _monitor.Log("TextAboveHeadPatches notice: NPC.drawAboveAlwaysFrontLayer(SpriteBatch) method was not found.", LogLevel.Warn);
            }
        }
        catch (Exception ex)
        {
            _monitor.Log($"TextAboveHeadPatches failed to apply: {ex.Message}", LogLevel.Warn);
        }
    }

    public static bool ShowTextAboveHead_Prefix(ref string text, ref int duration, out string __state)
    {
        __state = string.Empty;
        if (ModEntry.Config?.EnableMod == false) return true;
        if (string.IsNullOrWhiteSpace(text)) return false; // Bloquea burbujas vacías por completo

        string cleaned = StripUnsupportedEmojiCharacters(text);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return false; // Cancela por completo la burbuja si solo contenía emojis no renderizables
        }

        __state = cleaned;

        // Ancho adaptativo compacto (anti-recorte en pantallas pequeñas/Android/Steam Deck)
        int configuredWidth = Math.Clamp(ModEntry.Config?.BubbleMaxWidthPx ?? 200, 140, 360);
        int viewportCap = Game1.viewport.Width > 0 ? Math.Max(140, (int)(Game1.viewport.Width * 0.34f)) : configuredWidth;
        int effectiveMaxWidth = Math.Min(configuredWidth, viewportCap);
        float scale = Math.Clamp(ModEntry.Config?.BubbleTextScale ?? 0.85f, 0.70f, 1.00f);

        try
        {
            text = WrapToCompactBubble(cleaned, effectiveMaxWidth, scale);
        }
        catch
        {
            text = cleaned;
        }

        // Duración inteligente por velocidad de lectura
        if (duration > 0 && duration <= 1600)
        {
            duration = Math.Max(duration, 1800);
        }
        else
        {
            int baseSmart = Math.Clamp(2300 + cleaned.Length * 55, 3600, 7500);
            float mult = Math.Clamp(ModEntry.Config?.BubbleDurationMultiplier ?? 1.2f, 0.8f, 2.0f);
            duration = Math.Max(duration, (int)Math.Round(baseSmart * mult));
            if (duration > 11000) duration = 11000;
        }

        return true;
    }

    public static bool DrawAboveAlwaysFrontLayer_Prefix(
        NPC __instance,
        SpriteBatch b,
        string? ___textAboveHead,
        int ___textAboveHeadPreTimer,
        int ___textAboveHeadTimer,
        int ___textAboveHeadStyle,
        Color? ___textAboveHeadColor,
        float ___textAboveHeadAlpha)
    {
        if (ModEntry.Config?.EnableMod == false) return true;
        if (__instance == null || __instance.Sprite == null || b == null || Game1.smallFont == null || Game1.menuTexture == null)
            return true;

        // No bloquear otros elementos de drawAboveAlwaysFrontLayer si no hay globo de texto activo
        if (string.IsNullOrEmpty(___textAboveHead) || ___textAboveHeadTimer <= 0)
            return true;

        if (___textAboveHeadPreTimer > 0)
        {
            if (__instance.IsEmoting) __instance.DrawEmote(b);
            return false;
        }

        float alpha = Math.Clamp(___textAboveHeadAlpha, 0f, 1f);
        if (alpha <= 0.01f)
        {
            if (__instance.IsEmoting) __instance.DrawEmote(b);
            return false;
        }

        try
        {
            float scale = Math.Clamp(ModEntry.Config?.BubbleTextScale ?? 0.85f, 0.70f, 1.00f);
            string safeText = SanitizeForSmallFont(Game1.smallFont, ___textAboveHead);
            if (string.IsNullOrWhiteSpace(safeText))
            {
                if (__instance.IsEmoting) __instance.DrawEmote(b);
                return false;
            }

            Vector2 textSize = Game1.smallFont.MeasureString(safeText) * scale;
            int padX = Math.Max(12, (int)Math.Round(18f * scale));
            int padY = Math.Max(8, (int)Math.Round(13f * scale));
            int boxW = Math.Max(48, (int)Math.Ceiling(textSize.X) + padX * 2);
            int boxH = Math.Max(42, (int)Math.Ceiling(textSize.Y) + padY * 2);

            Point standingPixel = __instance.StandingPixel;
            Vector2 vector = Game1.GlobalToLocal(new Vector2(standingPixel.X, standingPixel.Y - __instance.Sprite.SpriteHeight * 4 - 64 + __instance.yJumpOffset));
            if (___textAboveHeadStyle == 0)
            {
                vector += new Vector2(Game1.random.Next(-1, 2), Game1.random.Next(-1, 2));
            }
            if (__instance.NeedsBirdieEmoteHack())
            {
                vector.X += -__instance.GetBoundingBox().Width / 4 + 64;
            }
            if (__instance.shouldShadowBeOffset)
            {
                vector += __instance.drawOffset;
            }

            int boxX = (int)Math.Round(vector.X - boxW / 2f);
            int boxY = (int)Math.Round(vector.Y) - Math.Max(0, boxH - 56);

            int vpW = Game1.viewport.Width;
            int vpH = Game1.viewport.Height;
            if (vpW > 0)
                boxX = Math.Clamp(boxX, 12, Math.Max(12, vpW - boxW - 12));
            if (vpH > 0)
                boxY = Math.Clamp(boxY, 12, Math.Max(12, vpH - boxH - 12));

            Point tilePoint = __instance.TilePoint;
            float layerDepth = Math.Clamp((float)(tilePoint.Y * 64) / 10000f + 0.001f + (float)tilePoint.X / 10000f, 0.01f, 0.99f);

            IClickableMenu.drawTextureBox(
                b,
                Game1.menuTexture,
                new Rectangle(0, 256, 60, 60),
                boxX,
                boxY,
                boxW,
                boxH,
                Color.White * alpha,
                scale: 0.75f,
                drawShadow: true,
                draw_layer: layerDepth);

            Color baseTextColor = ___textAboveHeadColor ?? Game1.textColor;
            Vector2 textPos = new Vector2(boxX + padX, boxY + padY);
            float textLayer = Math.Min(1f, layerDepth + 0.0002f);

            b.DrawString(
                Game1.smallFont,
                safeText,
                textPos + new Vector2(1f, 1f),
                Game1.textShadowColor * (alpha * 0.35f),
                0f,
                Vector2.Zero,
                scale,
                SpriteEffects.None,
                textLayer);

            b.DrawString(
                Game1.smallFont,
                safeText,
                textPos,
                baseTextColor * alpha,
                0f,
                Vector2.Zero,
                scale,
                SpriteEffects.None,
                Math.Min(1f, textLayer + 0.0001f));

            if (__instance.IsEmoting && Game1.emoteSpriteSheet != null)
            {
                int emoteSheetWidth = Game1.emoteSpriteSheet.Width;
                if (emoteSheetWidth >= 16)
                {
                    int idx = __instance.CurrentEmoteIndex;
                    Rectangle emoteSrc = new Rectangle(idx * 16 % emoteSheetWidth, idx * 16 / emoteSheetWidth * 16, 16, 16);
                    Vector2 emotePos = new Vector2(boxX + boxW / 2f - 32f, Math.Max(8f, boxY - 68f));
                    float emoteLayer = Math.Min(1f, textLayer + 0.0003f);
                    b.Draw(
                        Game1.emoteSpriteSheet,
                        emotePos,
                        emoteSrc,
                        Color.White * alpha,
                        0f,
                        Vector2.Zero,
                        4f,
                        SpriteEffects.None,
                        emoteLayer);
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Error in DrawAboveAlwaysFrontLayer_Prefix: {ex.Message}", LogLevel.Trace);
            return true;
        }
    }

    /// <summary>
    /// Elimina caracteres emoji, pares sustitutos y símbolos no soportados por el rasterizador SpriteText de Stardew Valley,
    /// preservando 100% caracteres CJK, cirílicos, acentos latinos y signos de puntuación legítimos.
    /// </summary>
    public static string StripUnsupportedEmojiCharacters(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var sb = new System.Text.StringBuilder(input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];

            // Pares sustitutos (todos los emojis modernos en UTF-16 como 🌾, 🐮, 🥱, 👋, etc.)
            if (char.IsSurrogate(c))
            {
                if (char.IsHighSurrogate(c) && i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
                {
                    i++;
                }
                continue;
            }

            // Selectores de variación, unión de ancho cero y espacios invisibles
            if (c is >= '\uFE00' and <= '\uFE0F' or '\u200D' or '\u200B' or '\uFEFF')
            {
                continue;
            }

            // Símbolos misceláneos y dingbats (⛏, ✂, ☕, etc.)
            if (c is >= '\u2600' and <= '\u27BF' or >= '\u2B00' and <= '\u2BFF')
            {
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString().Trim();
    }

    public static void ShowTextAboveHead_Postfix(NPC __instance, string text, int duration, string? __state)
    {
        try
        {
            string rawText = !string.IsNullOrWhiteSpace(__state) ? __state : StripUnsupportedEmojiCharacters(text);
            if (Game1.IsMultiplayer && !IsApplyingRemoteSpeech && __instance != null && !string.IsNullOrWhiteSpace(rawText) && __instance.currentLocation != null)
            {
                int currentTick = Game1.ticks;
                if (_lastRelayedByNpc.TryGetValue(__instance.Name, out var last) &&
                    string.Equals(last.Text, rawText, StringComparison.Ordinal) &&
                    currentTick >= last.Tick &&
                    currentTick - last.Tick < 30)
                {
                    return;
                }

                _lastRelayedByNpc[__instance.Name] = (rawText, currentTick);
                ModEntry.MultiplayerSync?.RelayNpcSpeechToPeers(__instance, rawText, duration > 0 ? duration : 3500);
            }
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Error in ShowTextAboveHead_Postfix: {ex}", LogLevel.Trace);
        }
    }

    private static float MeasureTextWidth(string text, float scale)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        if (Game1.smallFont != null)
        {
            try
            {
                return Game1.smallFont.MeasureString(text).X * scale;
            }
            catch
            {
                return Game1.smallFont.MeasureString(SanitizeForSmallFont(Game1.smallFont, text)).X * scale;
            }
        }
        return SpriteText.getWidthOfString(text, 999999) * scale;
    }

    private static string SanitizeForSmallFont(SpriteFont font, string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        try
        {
            _ = font.MeasureString(text);
            return text;
        }
        catch
        {
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c is '\n' or '\r' or ' ')
                {
                    sb.Append(c);
                    continue;
                }
                try
                {
                    _ = font.MeasureString(c.ToString());
                    sb.Append(c);
                }
                catch { }
            }
            return sb.ToString();
        }
    }

    public static string WrapToCompactBubble(string text, int maxPixelWidth)
    {
        float scale = Math.Clamp(ModEntry.Config?.BubbleTextScale ?? 0.85f, 0.70f, 1.00f);
        return WrapToCompactBubble(text, maxPixelWidth, scale);
    }

    /// <summary>
    /// Wraps text based on Game1.smallFont * scale character metrics so that each line does not exceed maxPixelWidth.
    /// Preserves existing explicit line breaks and supports CJK character-by-character fallback.
    /// </summary>
    public static string WrapToCompactBubble(string text, int maxPixelWidth, float scale)
    {
        if (string.IsNullOrEmpty(text)) return text;

        if (!text.Contains(' ') && !text.Contains('\n') && !text.Contains('\r') && MeasureTextWidth(text, scale) <= maxPixelWidth)
            return text;

        var resultLines = new List<string>();
        string[] rawLines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        foreach (string rawLine in rawLines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                resultLines.Add(string.Empty);
                continue;
            }

            // Quick check: does the line already fit within maxPixelWidth?
            if (MeasureTextWidth(rawLine, scale) <= maxPixelWidth)
            {
                resultLines.Add(rawLine);
                continue;
            }

            // Line exceeds compact width -> wrap word by word with CJK character fallback
            string[] words = rawLine.Split(' ');
            string currentLine = string.Empty;

            foreach (string word in words)
            {
                if (string.IsNullOrEmpty(word)) continue;

                if (MeasureTextWidth(word, scale) <= maxPixelWidth)
                {
                    string candidate = string.IsNullOrEmpty(currentLine) ? word : currentLine + " " + word;
                    if (MeasureTextWidth(candidate, scale) > maxPixelWidth && !string.IsNullOrEmpty(currentLine))
                    {
                        resultLines.Add(currentLine);
                        currentLine = word;
                    }
                    else
                    {
                        currentLine = candidate;
                    }
                }
                else
                {
                    // Word itself exceeds maxPixelWidth (e.g. CJK sentence or unbreakable word)
                    if (!string.IsNullOrEmpty(currentLine))
                    {
                        resultLines.Add(currentLine);
                        currentLine = string.Empty;
                    }

                    var charBuffer = new StringBuilder();
                    for (int i = 0; i < word.Length; i++)
                    {
                        charBuffer.Append(word[i]);
                        if (MeasureTextWidth(charBuffer.ToString(), scale) > maxPixelWidth)
                        {
                            if (charBuffer.Length > 1)
                            {
                                charBuffer.Length--;
                                resultLines.Add(charBuffer.ToString());
                                charBuffer.Clear();
                                charBuffer.Append(word[i]);
                            }
                            else
                            {
                                resultLines.Add(charBuffer.ToString());
                                charBuffer.Clear();
                            }
                        }
                    }
                    currentLine = charBuffer.ToString();
                }
            }

            if (!string.IsNullOrEmpty(currentLine))
            {
                resultLines.Add(currentLine);
            }
        }

        return string.Join("\n", resultLines);
    }
}
