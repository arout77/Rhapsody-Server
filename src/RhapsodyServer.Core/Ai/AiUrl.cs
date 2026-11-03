namespace RhapsodyServer.Core.Ai;

public static class AiUrl
{
    /// <summary>Lower-cased scheme and host, no trailing slash. Used to compare endpoints and to build request URLs.</summary>
    public static string Normalize(string url)
    {
        var t = (url ?? "").Trim();
        if (Uri.TryCreate(t, UriKind.Absolute, out var u))
            return (u.GetLeftPart(UriPartial.Authority).ToLowerInvariant() + u.AbsolutePath).TrimEnd('/');
        return t.TrimEnd('/');
    }

    public static bool Same(string a, string b) => Normalize(a).Equals(Normalize(b), StringComparison.Ordinal);

    public static bool IsLoopback(Uri u) =>
        u.IsLoopback || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Throws a message fit to show the person. A key travels in every request, so plain http is only allowed
    /// for this computer (Ollama, LM Studio); anything else has to be https.
    /// </summary>
    public static string Validate(string url)
    {
        var t = (url ?? "").Trim();
        if (t.Length == 0) throw new ArgumentException("Enter the provider's base URL, for example https://api.groq.com/openai/v1.");
        if (!Uri.TryCreate(t, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("The base URL has to start with https:// (or http:// for a server on this computer).");
        if (u.Scheme == Uri.UriSchemeHttp && !IsLoopback(u))
            throw new ArgumentException("Use https:// for a server on the internet. Plain http would send your API key unencrypted; it is only allowed for this computer.");
        if (!string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment))
            throw new ArgumentException("The base URL should be just the address, without a password, query or #fragment.");
        return Normalize(t);
    }

    public static string HostLabel(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
}
