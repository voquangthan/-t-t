using System.Collections.Concurrent;
using System.Security.Cryptography;

public sealed record ComputerUseApprovalChallenge(string Code, DateTimeOffset ExpiresAt);

/// <summary>One-time, short-lived explicit approval for one Companion action. Pending entries are memory-only.</summary>
public sealed class ComputerUseApprovalQueue(Func<string>? codeFactory = null)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, PendingApproval> pending = new(StringComparer.Ordinal);
    private readonly Func<string> makeCode = codeFactory ?? CreateCode;
    private sealed record PendingApproval(ComputerUseUiAction Action, string Code, DateTimeOffset ExpiresAt);

    public ComputerUseApprovalChallenge Create(string owner, ComputerUseUiAction action, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Owner is required.", nameof(owner));
        var code = makeCode();
        if (code.Length != 8 || code.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("Approval code factory returned an invalid code.");
        var expiry = now.Add(Lifetime);
        pending[owner] = new(action, code, expiry);
        return new(code, expiry);
    }

    public bool TryConsume(string owner, string confirmation, DateTimeOffset now, out ComputerUseUiAction? action)
    {
        action = null;
        if (!pending.TryGetValue(owner, out var item)) return false;
        if (now >= item.ExpiresAt)
        {
            ((ICollection<KeyValuePair<string, PendingApproval>>)pending).Remove(new(owner, item));
            return false;
        }
        if (!MatchesConfirmation(confirmation, item.Code)) return false;
        if (!((ICollection<KeyValuePair<string, PendingApproval>>)pending).Remove(new(owner, item))) return false;
        action = item.Action;
        return true;
    }

    public bool Cancel(string owner) => pending.TryRemove(owner, out _);
    public bool HasPending(string owner) => pending.ContainsKey(owner);

    public static string ConfirmationPrompt(string code) => $"xác nhận mã {code}";

    private static bool MatchesConfirmation(string text, string code)
    {
        var value = text.Trim().TrimEnd('.', '!', '?').Trim();
        return string.Equals(value, $"xác nhận mã {code}", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, $"confirm {code}", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, $"confirm code {code}", StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateCode() => Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
}
