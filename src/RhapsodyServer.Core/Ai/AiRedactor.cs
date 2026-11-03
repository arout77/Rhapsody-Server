using System.Text.RegularExpressions;

namespace RhapsodyServer.Core.Ai;

/// <summary>
/// Scrubs secrets and personal details out of anything before it leaves this computer. It errs on the side of
/// removing too much: a diagnosis rarely needs a password, and a leaked one costs far more than a vaguer answer.
/// Pattern based, so it cannot catch a secret with no recognisable label; that is why the page also shows
/// exactly what was sent.
/// </summary>
public sealed class AiRedactor
{
    public const string Mark = "[REDACTED]";

    readonly List<string> _literals;
    readonly string? _userName;

    public AiRedactor(IEnumerable<string?>? literalSecrets = null, string? userName = null)
    {
        _literals = (literalSecrets ?? Array.Empty<string?>())
            .Where(s => !string.IsNullOrWhiteSpace(s) && s!.Trim().Length >= 6)
            .Select(s => s!.Trim()).Distinct().OrderByDescending(s => s.Length).ToList();
        _userName = string.IsNullOrWhiteSpace(userName) || userName!.Trim().Length < 3 ? null : userName.Trim();
    }

    // Names that label a secret: DB_PASS, MAIL_PASSWORD, APP_KEY, api_key, "secret": "...", Authorization: ...
    const string Labels = @"[A-Za-z0-9_.\-]*(?:PASS(?:WORD|WD)?|PWD|SECRET|TOKEN|API[_\-]?KEY|APP[_\-]?KEY|[_\-]KEY|PRIVATE|AUTHORIZATION|CREDENTIAL|SALT|COOKIE)[A-Za-z0-9_.\-]*";

    static readonly Regex LabelledValue = new(
        "(?<label>[\"']?" + Labels + @"[""']?)(?<sep>\s*[=:]\s*)(?<val>""[^""\r\n]*""|'[^'\r\n]*'|[^\s""',;}]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex HeaderLine = new(
        @"(?im)^(?<head>\s*(?:Proxy-)?Authorization\s*:\s*).+$|^(?<head2>\s*(?:Set-)?Cookie\s*:\s*).+$",
        RegexOptions.Compiled);

    static readonly Regex Bearer = new(@"(?i)\b(Bearer|Basic)\s+[A-Za-z0-9._~+/\-]+=*", RegexOptions.Compiled);
    static readonly Regex UrlUserInfo = new(@"(?i)\b(?<scheme>[a-z][a-z0-9+.\-]*://)(?<user>[^/\s:@]+):(?<pw>[^/\s@]+)@", RegexOptions.Compiled);
    static readonly Regex KnownKeys = new(
        @"\b(?:sk-[A-Za-z0-9_\-]{16,}|gsk_[A-Za-z0-9]{16,}|AIza[0-9A-Za-z_\-]{30,}|ghp_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|xox[abprs]-[A-Za-z0-9\-]{10,}|AKIA[0-9A-Z]{16})\b",
        RegexOptions.Compiled);
    static readonly Regex Base64Key = new(@"base64:[A-Za-z0-9+/=]{16,}", RegexOptions.Compiled);
    static readonly Regex LongHex = new(@"\b[0-9a-fA-F]{32,}\b", RegexOptions.Compiled);
    static readonly Regex CliPassword = new(@"(?i)(--password(?:=|\s+))\S+", RegexOptions.Compiled);
    static readonly Regex MysqlDashP = new(@"(?i)(\b(?:mysql|mariadb|mysqldump|mariadb-dump)(?:\.exe)?\b[^\r\n]*?\s-p)\S+", RegexOptions.Compiled);
    static readonly Regex Email = new(@"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}\b", RegexOptions.Compiled);
    static readonly Regex UserFolder = new(@"(?i)(?<pre>[A-Za-z]:[\\/]+Users[\\/]+)(?<name>[^\\/\s""':*?<>|]+)", RegexOptions.Compiled);

    public string Apply(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text;

        foreach (var lit in _literals) s = s.Replace(lit, Mark, StringComparison.Ordinal);

        s = HeaderLine.Replace(s, m => (m.Groups["head"].Success ? m.Groups["head"].Value : m.Groups["head2"].Value) + Mark);
        s = UrlUserInfo.Replace(s, m => $"{m.Groups["scheme"].Value}{m.Groups["user"].Value}:{Mark}@");
        s = Bearer.Replace(s, m => $"{m.Groups[1].Value} {Mark}");
        s = KnownKeys.Replace(s, Mark);
        s = Base64Key.Replace(s, "base64:" + Mark);
        s = LabelledValue.Replace(s, m => m.Groups["val"].Value == Mark ? m.Value : m.Groups["label"].Value + m.Groups["sep"].Value + Mark);
        s = CliPassword.Replace(s, "$1" + Mark);
        s = MysqlDashP.Replace(s, "$1" + Mark);
        s = LongHex.Replace(s, Mark);
        s = Email.Replace(s, "[email]");
        s = UserFolder.Replace(s, m => m.Groups["pre"].Value + "<user>");
        if (_userName != null)
            s = Regex.Replace(s, $@"(?<![A-Za-z0-9]){Regex.Escape(_userName)}(?![A-Za-z0-9])", "<user>", RegexOptions.IgnoreCase);
        return s;
    }
}
