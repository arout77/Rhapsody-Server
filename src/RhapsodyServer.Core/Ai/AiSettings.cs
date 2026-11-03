namespace RhapsodyServer.Core.Ai;

/// <summary>One place to send questions: a provider preset id plus the two things that can drift (URL and model).</summary>
public sealed class AiEndpoint
{
    public string Provider { get; set; } = "groq";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
}

/// <summary>
/// AI helper settings, stored in settings.json. The API keys are NOT here: they live in ai-keys.json,
/// encrypted for this Windows user (see <see cref="AiKeyStore"/>).
/// Off by default: until the person turns it on, nothing is ever sent anywhere.
/// </summary>
public sealed class AiSettings
{
    public bool Enabled { get; set; }
    public AiEndpoint Primary { get; set; } = new();
    /// <summary>Tried when the primary is rate-limited, down or unreachable (never when the key is wrong).</summary>
    public AiEndpoint? Fallback { get; set; }
}
