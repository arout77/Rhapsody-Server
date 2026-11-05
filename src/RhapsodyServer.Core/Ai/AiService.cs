namespace RhapsodyServer.Core.Ai;

public sealed record AiEndpointInput(string Provider, string? BaseUrl, string? Model, string? ApiKey, bool ClearKey);
public sealed record AiSaveInput(bool Enabled, AiEndpointInput Primary, AiEndpointInput? Fallback);
public sealed record AiEndpointView(string Provider, string BaseUrl, string Model, bool HasKey);
public sealed record AiView(bool Enabled, bool Ready, AiEndpointView Primary, AiEndpointView? Fallback);
public sealed record AiAnswer(string Text, string Provider, string Model, bool UsedFallback, string? FallbackNote);
public sealed record AiTestResult(string Provider, string Model, long Ms, string Reply);

/// <summary>
/// Settings, keys and the "ask with a backup" logic. Knows nothing about the dashboard or about what is being asked.
/// Off until the person turns it on; one question at a time so a free tier's rate limit isn't hammered.
/// </summary>
public sealed class AiService
{
    public const string Primary = "primary", Fallback = "fallback";
    const int TestTokens = 300;

    readonly AiSettings _s;
    readonly Action _save;
    readonly AiKeyStore _keys;
    readonly AiClient _client;
    readonly IReadOnlyList<AiProvider> _providers;
    readonly SemaphoreSlim _gate = new(1, 1);
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    public AiService(AiSettings settings, Action save, AiKeyStore keys, string? providersOverridePath = null, AiClient? client = null)
    {
        _s = settings; _save = save; _keys = keys; _client = client ?? new AiClient();
        _providers = AiProviders.Load(providersOverridePath);
    }

    public IReadOnlyList<AiProvider> Providers => _providers;

    // ------------------------------------------------------------------ reading
    AiProvider? Preset(string id) => AiProviders.Find(_providers, id);

    /// <summary>The endpoint as it will be used: blanks filled from the provider's preset.</summary>
    (string BaseUrl, string Model) Effective(AiEndpoint ep)
    {
        var p = Preset(ep.Provider);
        return (string.IsNullOrWhiteSpace(ep.BaseUrl) ? p?.BaseUrl ?? "" : ep.BaseUrl.Trim(),
                string.IsNullOrWhiteSpace(ep.Model) ? p?.Model ?? "" : ep.Model.Trim());
    }

    bool NeedsKey(AiEndpoint ep) => Preset(ep.Provider)?.NeedsKey ?? false;

    string NameOf(AiEndpoint ep) => Preset(ep.Provider) is { Id: not "custom" } p ? p.Name : AiUrl.HostLabel(Effective(ep).BaseUrl);

    AiCall? CallFor(AiEndpoint? ep, string slot)
    {
        if (ep == null) return null;
        var (url, model) = Effective(ep);
        if (url.Length == 0 || model.Length == 0) return null;
        try { url = AiUrl.Validate(url); } catch (ArgumentException) { return null; }
        var key = _keys.Get(slot, url);
        if (NeedsKey(ep) && key == null) return null;
        return new AiCall(NameOf(ep), url, model, key);
    }

    AiEndpointView View(AiEndpoint ep, string slot)
    {
        var (url, model) = Effective(ep);
        return new AiEndpointView(ep.Provider, url, model, url.Length > 0 && _keys.Has(slot, url));
    }

    public AiView View() => new(_s.Enabled, _s.Enabled && CallFor(_s.Primary, Primary) != null,
        View(_s.Primary, Primary), _s.Fallback == null ? null : View(_s.Fallback, Fallback));

    /// <summary>Saved keys, so they can be scrubbed from anything that is sent.</summary>
    public IReadOnlyList<string> SecretLiterals() => _keys.AllKeys();

    // ------------------------------------------------------------------ saving
    (AiEndpoint Endpoint, string Url) Check(AiEndpointInput i, string label)
    {
        var preset = Preset(i.Provider ?? "") ?? throw new ArgumentException($"{label}: pick a provider from the list.");
        var url = string.IsNullOrWhiteSpace(i.BaseUrl) ? preset.BaseUrl : i.BaseUrl.Trim();
        var model = string.IsNullOrWhiteSpace(i.Model) ? preset.Model : i.Model.Trim();
        try { url = AiUrl.Validate(url); }
        catch (ArgumentException ex) { throw new ArgumentException($"{label}: {ex.Message}"); }
        if (model.Length == 0) throw new ArgumentException($"{label}: enter a model name.");
        if (model.Length > 120 || model.Any(char.IsControl)) throw new ArgumentException($"{label}: that model name doesn't look right.");
        return (new AiEndpoint { Provider = preset.Id, BaseUrl = url, Model = model }, url);
    }

    static string? CleanKey(string? key, string label)
    {
        var k = key?.Trim();
        if (string.IsNullOrEmpty(k)) return null;
        if (k.Length > 512 || k.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException($"{label}: that doesn't look like an API key (it has spaces or line breaks). Paste just the key.");
        return k;
    }

    void ApplyKey(string slot, string url, AiEndpointInput i, string label)
    {
        var key = CleanKey(i.ApiKey, label);
        if (key != null) _keys.Set(slot, url, key);
        else if (i.ClearKey) _keys.Remove(slot);
        else _keys.DropIfEndpointDiffers(slot, url);   // moved to another endpoint: the old key doesn't come along
    }

    public void Save(AiSaveInput input)
    {
        var (primary, pUrl) = Check(input.Primary, "Main provider");
        (AiEndpoint Endpoint, string Url)? backup = null;
        if (input.Fallback != null && !string.IsNullOrWhiteSpace(input.Fallback.Provider))
        {
            backup = Check(input.Fallback, "Backup provider");
            if (AiUrl.Same(pUrl, backup.Value.Url) && primary.Model == backup.Value.Endpoint.Model)
                throw new ArgumentException("Backup provider: that's the same as the main one. Pick a different provider or model.");
        }

        // Validate keys before touching anything, so a refused save leaves the old state intact.
        var pKey = CleanKey(input.Primary.ApiKey, "Main provider");
        var bKey = backup == null ? null : CleanKey(input.Fallback!.ApiKey, "Backup provider");
        if (input.Enabled && NeedsKey(primary) && pKey == null && (input.Primary.ClearKey || !_keys.Has(Primary, pUrl)))
            throw new ArgumentException($"Main provider: {Preset(primary.Provider)!.Name} needs an API key. Paste one, then turn AI help on.");

        ApplyKey(Primary, pUrl, input.Primary, "Main provider");
        if (backup != null) ApplyKey(Fallback, backup.Value.Url, input.Fallback!, "Backup provider");
        else _keys.Remove(Fallback);

        _s.Enabled = input.Enabled;
        _s.Primary = primary;
        _s.Fallback = backup?.Endpoint;
        _save();
    }

    // ------------------------------------------------------------------ asking
    CancellationTokenSource? _current;
    readonly object _currentLock = new();

    /// <summary>
    /// One call at a time (a free tier's rate limit is easy to hammer), and the NEWEST question wins: asking again, or
    /// closing the answer window and asking another, cancels the one still waiting instead of making the person wait it out.
    /// </summary>
    async Task<T> Exclusive<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        var mine = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationTokenSource? previous;
        lock (_currentLock) { previous = _current; _current = mine; }
        previous?.Cancel();
        var superseded = false;
        try
        {
            await _gate.WaitAsync(mine.Token);
            try { return await work(mine.Token); } finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            superseded = true;
            throw new AiException("Replaced by a newer question.", busy: true);
        }
        finally
        {
            lock (_currentLock) { if (ReferenceEquals(_current, mine)) _current = null; }
            if (!superseded) mine.Dispose();
        }
    }

    public Task<AiTestResult> TestAsync(string slot, CancellationToken ct) => Exclusive(async t =>
    {
        var ep = slot == Fallback ? _s.Fallback : _s.Primary;
        var (url, model) = ep == null ? ("", "") : Effective(ep);
        if (ep == null || url.Length == 0 || model.Length == 0) throw new AiException("Nothing to test yet. Fill in the provider and save first.");
        var key = _keys.Get(slot, AiUrl.Normalize(url));
        if (NeedsKey(ep) && key == null) throw new AiException($"{NameOf(ep)} needs an API key. Paste one and save first.");
        var r = await _client.ChatAsync(new AiCall(NameOf(ep), AiUrl.Validate(url), model, key),
            "You are a connection test. Reply with the single word: ready", "ping", TestTokens, Timeout, t);
        return new AiTestResult(r.Provider, r.Model, r.Ms, r.Text.Length > 80 ? r.Text[..80] + "…" : r.Text);
    }, ct);

    /// <summary>Stops whatever question is running (the person pressed Stop). Does nothing if none is.</summary>
    public void CancelCurrent()
    {
        lock (_currentLock) _current?.Cancel();
    }

    /// <summary>The longest a whole investigation (many model calls and lookups) may take.</summary>
    public TimeSpan RunTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Runs the lookup-and-think loop. Same rules as a single question (off until enabled, one at a time, newest wins), and the
    /// same backup behaviour, except that the backup takes over for the REST of the run: the conversation so far is provider-neutral.
    /// </summary>
    public Task<AgentResult> RunAgentAsync(AgentRequest request, IProgress<string>? progress, CancellationToken ct) => Exclusive(async t =>
    {
        if (!_s.Enabled) throw new AiException("AI help is turned off. Turn it on from the AI page.");
        var main = CallFor(_s.Primary, Primary)
            ?? throw new AiException("AI help isn't set up yet. Choose a provider and add a key on the AI page.");
        var backup = CallFor(_s.Fallback, Fallback);

        var active = main;
        var onBackup = false;
        string? note = null;

        async Task<(ChatTurn, bool, string?)> Step(IReadOnlyList<ChatMessage> msgs, IReadOnlyList<ToolSpec> tools, CancellationToken c)
        {
            try
            {
                var turn = await _client.ChatWithToolsAsync(active, msgs, tools, AiAgent.MaxAnswerTokens, Timeout, c);
                return (turn, onBackup, note);
            }
            catch (AiException first) when ((first.Retryable || first.ToolsUnsupported) && !onBackup && backup != null)
            {
                active = backup; onBackup = true;
                note = $"{main.ProviderName} couldn't answer, so {backup.ProviderName} did. {first.Message}";
                try
                {
                    var turn = await _client.ChatWithToolsAsync(active, msgs, tools, AiAgent.MaxAnswerTokens, Timeout, c);
                    return (turn, true, note);
                }
                catch (AiException second)
                {
                    throw new AiException($"{first.Message}\n\nThe backup provider failed too: {second.Message}", retryable: false);
                }
            }
        }

        using var overall = CancellationTokenSource.CreateLinkedTokenSource(t);
        overall.CancelAfter(RunTimeout);
        try { return await AiAgent.RunAsync(request, Step, progress, overall.Token); }
        catch (OperationCanceledException) when (!t.IsCancellationRequested)
        {
            throw new AiException($"That took longer than {(int)RunTimeout.TotalMinutes} minutes, so I stopped. Try a narrower question.", retryable: false);
        }
    }, ct);

    public Task<AiAnswer> AskAsync(string system, string user, CancellationToken ct) => Exclusive(async t =>
    {
        if (!_s.Enabled) throw new AiException("AI help is turned off. Turn it on from the AI page.");
        var main = CallFor(_s.Primary, Primary)
            ?? throw new AiException("AI help isn't set up yet. Choose a provider and add a key on the AI page.");
        var backup = CallFor(_s.Fallback, Fallback);

        try
        {
            var r = await _client.ChatAsync(main, system, user, AiExplainer.MaxAnswerTokens, Timeout, t);
            return new AiAnswer(r.Text, r.Provider, r.Model, false, null);
        }
        catch (AiException first) when (first.Retryable && backup != null)
        {
            try
            {
                var r = await _client.ChatAsync(backup, system, user, AiExplainer.MaxAnswerTokens, Timeout, t);
                return new AiAnswer(r.Text, r.Provider, r.Model, true, $"{main.ProviderName} couldn't answer, so {backup.ProviderName} did. {first.Message}");
            }
            catch (AiException second)
            {
                throw new AiException($"{first.Message}\n\nThe backup provider failed too: {second.Message}", retryable: false);
            }
        }
    }, ct);
}
