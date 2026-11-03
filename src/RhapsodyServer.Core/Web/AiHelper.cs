using System.Text.RegularExpressions;
using RhapsodyServer.Core.Ai;

namespace RhapsodyServer.Core.Web;

/// <summary>
/// What the dashboard's AI endpoints do, written once. The real backend and the test/demo backend each hand it their own
/// way of describing the setup and reading a log, so the tests exercise the real redaction, prompt and fallback code.
/// </summary>
public sealed class AiHelper
{
    readonly AiService _ai;
    readonly Func<string, string> _describeEnvironment;   // takes the text being explained, returns the setup summary
    readonly Func<string, int, string> _readLog;
    readonly Func<IEnumerable<string?>> _knownSecrets;

    public AiHelper(AiService ai, Func<string, string> describeEnvironment, Func<string, int, string> readLog, Func<IEnumerable<string?>> knownSecrets)
    { _ai = ai; _describeEnvironment = describeEnvironment; _readLog = readLog; _knownSecrets = knownSecrets; }

    public AiConfigDto Config()
    {
        var v = _ai.View();
        return new AiConfigDto(v.Enabled, v.Ready, v.Primary, v.Fallback,
            _ai.Providers.Select(p => new AiProviderDto(p.Id, p.Name, p.BaseUrl, p.Model, p.NeedsKey, p.KeyUrl, p.Note)).ToArray());
    }

    public void Save(AiSaveInput input) => _ai.Save(input);

    public Task<AiTestResult> TestAsync(string which, CancellationToken ct) =>
        _ai.TestAsync(which == AiService.Fallback ? AiService.Fallback : AiService.Primary, ct);

    const int MaxErrorChars = 20_000;
    static readonly Regex LogIdRx = new(@"^[a-z0-9.\-]+$", RegexOptions.Compiled);

    public async Task<AiAnswerDto> ExplainAsync(ExplainRequest r, CancellationToken ct)
    {
        var kind = (r.Kind ?? "").Trim().ToLowerInvariant();
        string? logText = null, logName = null, errorText = null;

        if (kind == "log")
        {
            logName = r.LogId ?? "";
            if (!LogIdRx.IsMatch(logName)) throw new ArgumentException("Bad log name");
            logText = _readLog(logName, 400);
            if (string.IsNullOrWhiteSpace(logText) || logText.StartsWith("(nothing logged yet)"))
                throw new ArgumentException("That log is empty, so there is nothing to explain yet.");
        }
        else if (kind == "error")
        {
            errorText = (r.Text ?? "").Trim();
            if (errorText.Length == 0) throw new ArgumentException("There is no error text to explain.");
            if (errorText.Length > MaxErrorChars) errorText = errorText[^MaxErrorChars..];
        }
        else throw new ArgumentException("Unknown kind of request.");

        var redactor = new AiRedactor(_knownSecrets().Concat(_ai.SecretLiterals()), Environment.UserName);
        var prompt = AiExplainer.Build(new ExplainInput(kind, r.Title, errorText, logName, logText,
            _describeEnvironment((errorText ?? "") + "\n" + (logText ?? ""))), redactor);

        var a = await _ai.AskAsync(prompt.System, prompt.User, ct);
        return new AiAnswerDto(a.Text, a.Provider, a.Model, a.UsedFallback, a.FallbackNote, prompt.User);
    }
}
