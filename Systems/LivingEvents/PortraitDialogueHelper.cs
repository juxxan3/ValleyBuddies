namespace ValleyBuddies.Systems.LivingEvents;

using System;
using System.Collections.Generic;
using StardewValley;
using StardewValley.Menus;

/// <summary>
/// Helper universal para desplegar cuadros de diálogo oficiales con retrato expresivo ($s/$h/$l/$u/$a)
/// y encadenarlos de forma segura con preguntas Sí/No para cualquier NPC (Vanilla, SVE, RSV, Custom).
/// </summary>
public static class PortraitDialogueHelper
{
    /// <summary>
    /// Limpia por completo cualquier burbuja flotante activa encima del NPC para evitar que reaparezca al cerrarse un diálogo.
    /// </summary>
    public static void ClearOverheadBubble(NPC? npc)
    {
        if (npc == null) return;
        try
        {
            npc.clearTextAboveHead();
        }
        catch { }
    }

    /// <summary>
    /// Muestra un cuadro de diálogo nativo con el retrato expresivo del NPC especificado.
    /// </summary>
    public static void ShowPortraitDialogue(NPC? npc, string text, string emotionTag = "", Action? onClosed = null)
    {
        if (npc == null || string.IsNullOrWhiteSpace(text))
        {
            try { onClosed?.Invoke(); } catch { }
            return;
        }

        ClearOverheadBubble(npc);

        string formatted = text.Trim();
        if (!string.IsNullOrEmpty(emotionTag))
        {
            bool hasTagAtEnd = formatted.EndsWith("$", StringComparison.Ordinal) ||
                               (formatted.Length >= 2 && formatted[^2] == '$');
            if (!hasTagAtEnd)
            {
                formatted += emotionTag;
            }
        }

        void Dispatch()
        {
            if (Game1.eventUp)
            {
                try { onClosed?.Invoke(); } catch { }
                return;
            }

            try
            {
                Game1.afterFadeFunction? prevAfter = Game1.afterDialogues;
                if (onClosed != null)
                {
                    Game1.afterDialogues = () =>
                    {
                        try { prevAfter?.Invoke(); } catch { }
                        try { onClosed(); } catch { }
                    };
                }

                var dlg = new Dialogue(npc, null, formatted);
                npc.CurrentDialogue ??= new Stack<Dialogue>();
                npc.CurrentDialogue.Push(dlg);
                PostDialogueInviteMonitor.SuppressCurrentInteraction(npc.Name);
                Game1.drawDialogue(npc);

                if (!Game1.dialogueUp)
                {
                    if (npc.CurrentDialogue.Count > 0 && ReferenceEquals(npc.CurrentDialogue.Peek(), dlg))
                    {
                        npc.CurrentDialogue.Pop();
                    }
                    if (onClosed != null)
                    {
                        Game1.afterDialogues = prevAfter;
                        onClosed();
                    }
                }
            }
            catch
            {
                try { onClosed?.Invoke(); } catch { }
            }
        }

        // Si ya hay un DialogueBox en transición de cierre (tryOutro tras afterQuestion), esperar un breve instante
        if (Game1.activeClickableMenu is DialogueBox || Game1.dialogueUp)
        {
            DelayedAction.functionAfterDelay(Dispatch, 130);
        }
        else
        {
            Dispatch();
        }
    }

    /// <summary>
    /// Muestra primero un diálogo con el retrato expresivo del NPC y, al cerrarse, abre el cuadro de pregunta interactiva.
    /// </summary>
    public static void ShowPortraitThenQuestion(
        NPC? npc,
        string introText,
        string emotionTag,
        string questionPrompt,
        Response[] options,
        Action<Farmer, string> onAnswered)
    {
        void OpenQuestion()
        {
            ClearOverheadBubble(npc);
            var loc = Game1.currentLocation;
            if (loc == null) return;

            loc.createQuestionDialogue(questionPrompt, options, (who, answer) =>
            {
                Game1.objectDialoguePortraitPerson = null;
                onAnswered?.Invoke(who, answer);
            }, npc);
        }

        if (npc == null || string.IsNullOrWhiteSpace(introText))
        {
            if (Game1.activeClickableMenu is DialogueBox || Game1.dialogueUp)
            {
                DelayedAction.functionAfterDelay(OpenQuestion, 130);
            }
            else
            {
                OpenQuestion();
            }
            return;
        }

        ShowPortraitDialogue(npc, introText, emotionTag, onClosed: OpenQuestion);
    }
}
