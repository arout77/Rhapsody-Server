namespace RhapsodyServer.Core.Ai;

/// <summary>One function the model may call. <c>Parameters</c> is a JSON-schema object (an anonymous object is fine).</summary>
public sealed record ToolSpec(string Name, string Description, object Parameters);

/// <summary>A request from the model to run one of the tools. <c>ArgumentsJson</c> is the raw JSON the model wrote.</summary>
public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// One message in a conversation, in the provider-neutral shape the agent keeps.
/// Roles: <c>system</c>, <c>user</c>, <c>assistant</c> (may carry <see cref="ToolCalls"/>), <c>tool</c> (answers one call).
/// </summary>
public sealed record ChatMessage(string Role, string? Content, IReadOnlyList<ToolCall>? ToolCalls = null, string? ToolCallId = null, string? ToolName = null)
{
    public static ChatMessage System(string text) => new("system", text);
    public static ChatMessage User(string text) => new("user", text);
    public static ChatMessage Assistant(string? text, IReadOnlyList<ToolCall>? calls = null) => new("assistant", text, calls);
    public static ChatMessage Tool(ToolCall call, string result) => new("tool", result, null, call.Id, call.Name);
}

/// <summary>What the model said back: an answer, or a list of tools it wants run first (sometimes both).</summary>
public sealed record ChatTurn(string Text, IReadOnlyList<ToolCall> ToolCalls, string Provider, string Model, long Ms, bool CutOff);
