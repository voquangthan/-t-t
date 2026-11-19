using System.Security.Cryptography;

public sealed record PendingComputerClickApproval(
    string ApprovalId,
    string Owner,
    Guid? RequestId,
    string TargetId,
    string Label,
    string? WindowTitle,
    string? Process,
    DateTimeOffset ExpiresAt);

/// <summary>Short-lived, owner-bound approval capabilities; intentionally memory-only.</summary>
public sealed class PendingComputerClickApprovalStore(TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    public const int MaximumPending = 32;

    private readonly object gate = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, PendingComputerClickApproval> pending = new(StringComparer.Ordinal);

    public PendingComputerClickApproval? Create(string owner, Guid? requestId, string? targetId,
        string? label, string? windowTitle = null, string? process = null)
    {
        if (string.IsNullOrWhiteSpace(owner) || !Guid.TryParseExact(targetId, "N", out _) ||
            label is not { Length: > 0 and <= 96 } || label.Any(char.IsControl)) return null;
        lock (gate)
        {
            RemoveExpired();
            if (pending.Count >= MaximumPending) return null;
            string id;
            do { id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(); }
            while (pending.ContainsKey(id));
            var value = new PendingComputerClickApproval(id, owner, requestId, targetId!, label,
                Limit(windowTitle, 96), Limit(process, 48), clock.GetUtcNow() + Lifetime);
            pending.Add(id, value);
            return value;
        }
    }

    public bool TryConsume(string? approvalId, string owner, out PendingComputerClickApproval? approval,
        out string status)
    {
        approval = null;
        status = "not_found";
        if (!IsValidId(approvalId) || string.IsNullOrWhiteSpace(owner)) return false;
        lock (gate)
        {
            if (!pending.TryGetValue(approvalId!, out var found)) return false;
            if (!string.Equals(found.Owner, owner, StringComparison.Ordinal))
            {
                status = "owner_mismatch";
                return false;
            }
            pending.Remove(approvalId!);
            if (found.ExpiresAt <= clock.GetUtcNow())
            {
                status = "expired";
                return false;
            }
            approval = found;
            status = "consumed";
            return true;
        }
    }

    public bool Revoke(string? approvalId, string owner)
    {
        if (!IsValidId(approvalId) || string.IsNullOrWhiteSpace(owner)) return false;
        lock (gate)
        {
            if (!pending.TryGetValue(approvalId!, out var found) ||
                !string.Equals(found.Owner, owner, StringComparison.Ordinal)) return false;
            pending.Remove(approvalId!);
            return true;
        }
    }

    public static bool IsValidId(string? id) => id is { Length: 64 } &&
        id.All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f');

    private void RemoveExpired()
    {
        var now = clock.GetUtcNow();
        foreach (var id in pending.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
            pending.Remove(id);
    }

    private static string? Limit(string? value, int maximum) => string.IsNullOrWhiteSpace(value)
        ? null : value[..Math.Min(value.Length, maximum)];
}
