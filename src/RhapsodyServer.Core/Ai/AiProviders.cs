using System.Text.Json;

namespace RhapsodyServer.Core.Ai;

public sealed record AiProvider(string Id, string Name, string BaseUrl, string Model, bool NeedsKey, string? KeyUrl, string? Note);

/// <summary>
/// The provider presets. Model names and free-tier terms change often, so they are data, not code:
/// the defaults ship inside the app, and a file named <c>ai-providers.json</c> in the data folder replaces them
/// without a rebuild (same shape as Ai/providers.json).
/// </summary>
public static class AiProviders
{
    sealed record Doc(List<AiProvider>? Providers);
    static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<AiProvider> Load(string? overridePath = null)
    {
        if (overridePath != null && File.Exists(overridePath))
        {
            try
            {
                var custom = JsonSerializer.Deserialize<Doc>(File.ReadAllText(overridePath), Opts)?.Providers;
                if (custom is { Count: > 0 } && custom.All(p => !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Name)))
                    return custom;
            }
            catch (JsonException) { /* a broken override must never take the app down: fall back to the built-in list */ }
        }
        using var s = typeof(AiProviders).Assembly.GetManifestResourceStream("ai/providers.json")
            ?? throw new InvalidOperationException("ai/providers.json is not embedded");
        return JsonSerializer.Deserialize<Doc>(s, Opts)!.Providers!;
    }

    public static AiProvider? Find(IEnumerable<AiProvider> all, string id) =>
        all.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
