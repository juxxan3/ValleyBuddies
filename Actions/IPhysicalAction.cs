using ValleyBuddies.Execution;

namespace ValleyBuddies.Actions;

public enum ActionCategory { Movement, FarmWork, Social, Adventure, Home }

/// <summary>Contract every physical action must implement.</summary>
public interface IPhysicalAction
{
    /// <summary>Unique snake_case name used in the JSON "action" field.</summary>
    string Name { get; }

    /// <summary>Short description injected into the AI prompt.</summary>
    string PromptDescription { get; }

    /// <summary>JSON schema fragment shown in the prompt.</summary>
    string PromptSchema { get; }

    /// <summary>Minimum hearts required for this action to appear in the prompt.</summary>
    int MinHearts { get; }

    /// <summary>Categorization of the action.</summary>
    ActionCategory Category => ActionCategory.Social;

    /// <summary>Dynamic minimum hearts resolved against current ActionsConfig.</summary>
    int GetMinHearts(ActionContext context) => MinHearts;

    /// <summary>Whether this action is currently enabled by context/config.</summary>
    bool IsEnabled(ActionContext context);

    /// <summary>Lightweight pre-flight check before touching the game state.</summary>
    ActionResult Validate(ActionRequest request, ActionContext context, int currentHearts);

    /// <summary>
    /// Create the NpcJob that will be run by NpcActionExecutor on the main thread.
    /// Returns null if nothing should happen (already handled inline).
    /// </summary>
    NpcJob? BuildJob(ActionRequest request, ActionContext context);
}
