using System.Text.RegularExpressions;

/// <summary>Authority derived from a direct user request; never from a model tool call.</summary>
public sealed class ComputerUseCommandScope
{
    private static readonly (string Id, string Pattern)[] DirectCommandPhrases =
    [
        ("powershell-date", @"(?:powershell|pwsh)\s+.{0,24}\bget-date\b|\bget-date\b"),
        ("powershell-location", @"(?:powershell|pwsh)\s+.{0,24}\bget-location\b|\bget-location\b"),
        ("cmd-version", @"\b(?:cmd|command\s+prompt)\b.{0,24}\bver\b|\bver\b.{0,24}\b(?:cmd|command\s+prompt)\b"),
        ("git-status", @"\bgit\s+status\b"),
        ("git-branch", @"\bgit\s+branch\s+--show-current\b"),
        ("dotnet-version", @"\bdotnet\s+--version\b")
    ];

    private readonly HashSet<string> commandIds;
    private ComputerUseCommandScope(HashSet<string> commandIds) => this.commandIds = commandIds;

    public IReadOnlyCollection<string> CommandIdsRequested => commandIds;

    public static bool TryCreate(string? request, out ComputerUseCommandScope? scope)
    {
        scope = null;
        if (string.IsNullOrWhiteSpace(request) || request.Length > 1200 || request.Contains('\n') || request.Contains('\r') ||
            !Regex.IsMatch(request, @"^\s*(?:please\s+|hãy\s+|vui\s+lòng\s+)?(?:chạy|run|execute|mở|open)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            Regex.IsMatch(request, @"[;&|<>`$]", RegexOptions.CultureInvariant) ||
            Regex.IsMatch(request, @"(?:sudo|runas|administrator|elevat|executionpolicy|bypass|invoke-expression|\biex\b|downloadstring|remove-item|\bdel\b|\berase\b|\bformat\b|shutdown|restart-computer|set-content|out-file|\|\s*(?:iex|invoke-expression))",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;

        var matched = DirectCommandPhrases.Where(item => Regex.IsMatch(request, item.Pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).Select(item => item.Id).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (matched.Count != 1) return false;
        scope = new ComputerUseCommandScope(matched);
        return true;
    }

    public bool Allows(ComputerUseCommandAction action) => commandIds.Contains(action.CommandId);
}
