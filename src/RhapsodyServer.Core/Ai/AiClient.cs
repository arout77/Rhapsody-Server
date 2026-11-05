using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RhapsodyServer.Core.Ai;

/// <summary>An AI call that didn't work, with a message fit to show the person.</summary>
public sealed class AiException : Exception
{
    /// <summary>True when trying a different provider could help (rate limit, outage, no connection). False for a wrong key or model.</summary>
    public bool Retryable { get; }
    public bool Busy { get; }
    public int? Status { get; }
    /// <summary>The provider or model doesn't take tool definitions, so the agent can't run on it.</summary>
    public bool ToolsUnsupported { get; }
    public AiException(string message, bool retryable = false, int? status = null, bool busy = false, bool toolsUnsupported = false) : base(message)
    { Retryable = retryable; Status = status; Busy = busy; ToolsUnsupported = toolsUnsupported; }
}

/// <summary>Where one request goes. The key is optional (Ollama, LM Studio).</summary>
public sealed record AiCall(string ProviderName, string BaseUrl, string Model, string? ApiKey);

public sealed record AiReply(string Text, string Provider, string Model, long Ms);

/// <summary>
/// Talks to anything that speaks the OpenAI "chat completions" format: Groq, Gemini, OpenRouter, Mistral, Ollama, LM Studio...
/// The call is made here, in the app, never from the browser, so the key never reaches the dashboard page.
/// </summary>
public sealed class AiClient
{
    readonly HttpClient _http;

    public AiClient(HttpClient? http = null)
    {
        // No automatic redirects: a redirect could carry the Authorization header to a host the person never chose.
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) })
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    static readonly Regex Think = new(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.Compiled);

    public async Task<AiReply> ChatAsync(AiCall call, string system, string user, int maxTokens, TimeSpan timeout, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = call.Model,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            },
            temperature = 0.2,
            max_tokens = maxTokens,
            stream = false
        });
        var (text, ms) = await PostAsync(call, body, timeout, ct);
        return Parse(call, text, ms);
    }

    /// <summary>
    /// One step of an agent conversation: the whole history plus the tools on offer. The model answers, or asks for tools.
    /// Uses the standard "tools" / "tool_calls" fields, which every provider in the preset list accepts (Ollama needs a model that supports them).
    /// </summary>
    public async Task<ChatTurn> ChatWithToolsAsync(AiCall call, IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolSpec> tools, int maxTokens, TimeSpan timeout, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = call.Model,
            ["messages"] = messages.Select(WireMessage).ToList(),
            ["temperature"] = 0.2,
            ["max_tokens"] = maxTokens,
            ["stream"] = false
        };
        if (tools.Count > 0)   // the last "answer now" round sends none; some providers reject an empty list
        {
            payload["tools"] = tools.Select(t => new { type = "function", function = new { name = t.Name, description = t.Description, parameters = t.Parameters } }).ToList();
            payload["tool_choice"] = "auto";
        }
        var body = JsonSerializer.Serialize(payload);
        try
        {
            var (text, ms) = await PostAsync(call, body, timeout, ct);
            return ParseTurn(call, text, ms);
        }
        // A 400 that talks about tools or functions means this model can't be an agent: say so plainly instead of "refused the request".
        catch (AiException ex) when (ex.Status is 400 or 422 && Regex.IsMatch(ex.Message, @"(?i)\b(tools?|functions?|tool_choice)\b"))
        {
            throw new AiException($"{call.ProviderName}'s model \"{call.Model}\" doesn't support tool calling, which Ask needs. " +
                $"Pick a model that does on the AI page (for Ollama, for example llama3.1 or qwen2.5). {ex.Message}", status: ex.Status, toolsUnsupported: true);
        }
    }

    static object WireMessage(ChatMessage m)
    {
        var d = new Dictionary<string, object?> { ["role"] = m.Role, ["content"] = m.Content };
        if (m.ToolCalls is { Count: > 0 })
            d["tool_calls"] = m.ToolCalls.Select(c => new { id = c.Id, type = "function", function = new { name = c.Name, arguments = c.ArgumentsJson } }).ToList();
        if (m.ToolCallId != null) d["tool_call_id"] = m.ToolCallId;
        if (m.Role == "tool" && m.ToolName != null) d["name"] = m.ToolName;
        return d;
    }

    /// <summary>Sends one request and returns the body. All the error wording lives here, shared by every kind of call.</summary>
    async Task<(string Text, long Ms)> PostAsync(AiCall call, string body, TimeSpan timeout, CancellationToken ct)
    {
        var url = AiUrl.Validate(call.BaseUrl) + "/chat/completions";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.UserAgent.ParseAdd("RhapsodyServer/1.0");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(call.ApiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", call.ApiKey);

        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            var text = await res.Content.ReadAsStringAsync(cts.Token);
            if (!res.IsSuccessStatusCode) throw MapError(call, res, text);
            return (text, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiException($"{call.ProviderName} didn't answer within {(int)timeout.TotalSeconds} seconds.", retryable: true);
        }
        catch (HttpRequestException ex)
        {
            var local = Uri.TryCreate(url, UriKind.Absolute, out var u) && AiUrl.IsLoopback(u);
            throw new AiException(local
                ? $"Couldn't connect to {AiUrl.HostLabel(call.BaseUrl)}:{new Uri(url).Port}. Is the server (Ollama, LM Studio...) running?"
                : $"Couldn't reach {AiUrl.HostLabel(call.BaseUrl)}: {ex.Message}", retryable: true);
        }
    }

    static AiException MapError(AiCall call, HttpResponseMessage res, string body)
    {
        var code = (int)res.StatusCode;
        var detail = ProviderMessage(body);
        var tail = detail.Length > 0 ? $" The provider said: {detail}" : "";
        switch (res.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
            case HttpStatusCode.Forbidden:
                return new AiException($"{call.ProviderName} rejected the API key (HTTP {code}). Check the key on the AI page.{tail}", status: code);
            case HttpStatusCode.NotFound:
                return new AiException($"{call.ProviderName} doesn't know the model \"{call.Model}\" or this address (HTTP 404). Check the model name and base URL on the AI page.{tail}", status: code);
            case HttpStatusCode.TooManyRequests:
            {
                var wait = res.Headers.RetryAfter?.Delta is { } d ? $" Try again in about {Math.Max(1, (int)Math.Ceiling(d.TotalSeconds))} seconds." : "";
                return new AiException($"{call.ProviderName} says you've hit its rate limit (HTTP 429).{wait}{tail}", retryable: true, status: code);
            }
        }
        if (code >= 500) return new AiException($"{call.ProviderName} is having trouble (HTTP {code}).{tail}", retryable: true, status: code);
        if (code is >= 300 and < 400) return new AiException($"{call.ProviderName} redirected the request (HTTP {code}). The base URL is probably wrong.", status: code);
        return new AiException($"{call.ProviderName} refused the request (HTTP {code}).{tail}", status: code);
    }

    /// <summary>The provider's own explanation, if the body has one. Short, single line, never the whole page.</summary>
    static string ProviderMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? m = null;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var e))
                m = e.ValueKind == JsonValueKind.String ? e.GetString()
                    : e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var mm) ? mm.ToString() : null;
            else if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 && root[0].TryGetProperty("error", out var e0)
                     && e0.TryGetProperty("message", out var m0))
                m = m0.ToString();   // Gemini sometimes wraps its error in an array
            if (string.IsNullOrWhiteSpace(m)) return "";
            m = Regex.Replace(m!, @"\s+", " ").Trim();
            return m.Length > 300 ? m[..300] + "…" : m;
        }
        catch (JsonException) { return ""; }
    }

    static int _idCounter;

    static ChatTurn ParseTurn(AiCall call, string json, long ms)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var choice = doc.RootElement.GetProperty("choices")[0];
            var message = choice.GetProperty("message");
            var text = "";
            if (message.TryGetProperty("content", out var c))
            {
                if (c.ValueKind == JsonValueKind.String) text = c.GetString() ?? "";
                else if (c.ValueKind == JsonValueKind.Array)
                    text = string.Concat(c.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : null));
            }
            text = Think.Replace(text, "").Trim();

            var calls = new List<ToolCall>();
            if (message.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                foreach (var item in tc.EnumerateArray())
                {
                    if (!item.TryGetProperty("function", out var fn) || !fn.TryGetProperty("name", out var nm) || nm.GetString() is not { Length: > 0 } name) continue;
                    // Arguments normally arrive as a JSON string; some local servers send the object itself.
                    var args = "{}";
                    if (fn.TryGetProperty("arguments", out var a))
                        args = a.ValueKind == JsonValueKind.String ? (a.GetString() is { Length: > 0 } s ? s : "{}") : a.ValueKind == JsonValueKind.Null ? "{}" : a.GetRawText();
                    // Some providers leave the id out; others (Mistral) insist on 9 letters and digits, so make ours that shape.
                    var id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(idEl.GetString())
                        ? idEl.GetString()! : "c" + Interlocked.Increment(ref _idCounter).ToString("D8");
                    calls.Add(new ToolCall(id, name, args));
                }

            var finish = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            if (text.Length == 0 && calls.Count == 0)
                throw new AiException(finish == "length"
                    ? $"{call.ProviderName}'s model ran out of room while thinking and gave no answer. Try again, or pick a different model on the AI page."
                    : $"{call.ProviderName} returned an empty answer. Try again, or pick a different model on the AI page.", retryable: true);
            return new ChatTurn(text, calls, call.ProviderName, call.Model, ms, finish == "length" && calls.Count == 0);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new AiException($"{call.ProviderName} answered in a format this app doesn't understand. Is the base URL an OpenAI-compatible endpoint (it should end like /v1)?");
        }
    }

    static AiReply Parse(AiCall call, string json, long ms)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var choice = doc.RootElement.GetProperty("choices")[0];
            var message = choice.GetProperty("message");
            var text = "";
            if (message.TryGetProperty("content", out var c))
            {
                if (c.ValueKind == JsonValueKind.String) text = c.GetString() ?? "";
                else if (c.ValueKind == JsonValueKind.Array)
                    text = string.Concat(c.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : null));
            }
            text = Think.Replace(text, "").Trim();
            var finish = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;

            if (text.Length == 0)
                throw new AiException(finish == "length"
                    ? $"{call.ProviderName}'s model ran out of room while thinking and gave no answer. Try again, or pick a different model on the AI page."
                    : $"{call.ProviderName} returned an empty answer. Try again, or pick a different model on the AI page.", retryable: true);
            if (finish == "length") text += "\n\n(The answer was cut off because the model reached its length limit.)";
            return new AiReply(text, call.ProviderName, call.Model, ms);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new AiException($"{call.ProviderName} answered in a format this app doesn't understand. Is the base URL an OpenAI-compatible endpoint (it should end like /v1)?");
        }
    }
}
