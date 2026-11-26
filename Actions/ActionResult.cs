namespace ValleyBuddies.Actions;

/// <summary>Result returned by Validate() and Execute().</summary>
public sealed class ActionResult
{
    public bool   IsSuccess   { get; private init; }
    public string Message     { get; private init; } = string.Empty;
    public string MemoryLine  { get; private init; } = string.Empty;
    public string? HudKey     { get; private init; }
    public object? HudTokens  { get; private init; }

    public static ActionResult Ok(string message, string? memoryLine = null) =>
        new() { IsSuccess = true,  Message = message, MemoryLine = memoryLine ?? message };

    public static ActionResult Fail(string reason) =>
        new() { IsSuccess = false, Message = reason,  MemoryLine = reason };

    public ActionResult WithHud(string key, object? tokens = null) =>
        new() { IsSuccess = IsSuccess, Message = Message, MemoryLine = MemoryLine, HudKey = key, HudTokens = tokens };
}
