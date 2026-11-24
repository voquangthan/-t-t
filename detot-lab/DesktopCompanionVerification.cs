using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;

public static partial class DesktopCompanionVerification
{
    public sealed record WorkspaceEvidence(string ExpectedWorkspace, string? ObservedWorkspace,
        bool Verified, string Reason, bool WindowTitleMatched, bool RootLabelMatched, bool AccessiblePathMatched,
        bool ProcessCommandLineMatched);

    private static readonly Regex UserDataDir = new(
        "(?:^|\\s)(?:\\\"--user-data-dir=(?<wholeQuoted>[^\\\"]+)\\\"|--user-data-dir(?:=|\\s+)(?:\\\"(?<quoted>[^\\\"]+)\\\"|(?<plain>[^\\s]+)))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex GithubLoginLabel = new(
        "^Signed in as (?<login>[A-Za-z0-9-]{1,39})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex GithubSignedOutLabel = new(
        "^Sign in to GitHub$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex GithubUsername = new(
        "^[A-Za-z0-9-]{1,39}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsDedicatedEdgeCommandLine(string? commandLine, string expectedProfile)
    {
        return HasExactUserDataDirectory(commandLine, expectedProfile);
    }

    private static bool HasExactUserDataDirectory(string? commandLine, string expectedProfile)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(expectedProfile)) return false;
        var match = UserDataDir.Match(commandLine);
        if (!match.Success) return false;
        var profile = match.Groups["wholeQuoted"].Success ? match.Groups["wholeQuoted"].Value :
            match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["plain"].Value;
        try
        {
            return Path.GetFullPath(profile).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(Path.GetFullPath(expectedProfile).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    public static bool GithubAddressMatches(string? actual, string? expected)
    {
        if (!Uri.TryCreate(actual, UriKind.Absolute, out var actualUri) ||
            !Uri.TryCreate(expected, UriKind.Absolute, out var expectedUri)) return false;
        if (!actualUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !expectedUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !actualUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !expectedUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        return NormalizePath(actualUri.AbsolutePath).Equals(NormalizePath(expectedUri.AbsolutePath), StringComparison.OrdinalIgnoreCase);
    }

    public static string? CanonicalGithubAddress(string? actual, string? expected)
    {
        if (!GithubAddressMatches(actual, expected) || !Uri.TryCreate(expected, UriKind.Absolute, out var expectedUri)) return null;
        return $"https://github.com{NormalizePath(expectedUri.AbsolutePath)}";
    }

    public static bool WorkspaceLabelMatches(string expectedWorkspace, string? windowTitle,
        IEnumerable<string?> rootTreeLabels, IEnumerable<string?> accessiblePaths)
        => EvaluateWorkspace(expectedWorkspace, true, windowTitle, rootTreeLabels, accessiblePaths).Verified;

    public static WorkspaceEvidence EvaluateWorkspace(string expectedWorkspace, bool windowFound, string? windowTitle,
        IEnumerable<string?> rootTreeLabels, IEnumerable<string?> accessiblePaths, bool processCommandLineMatched = false)
    {
        if (string.IsNullOrWhiteSpace(expectedWorkspace))
            return new("unknown", null, false, "expected_workspace_unavailable", false, false, false, false);
        string fullPath;
        string folder;
        try
        {
            fullPath = Path.GetFullPath(expectedWorkspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            folder = Path.GetFileName(fullPath);
        }
        catch (ArgumentException) { return new("unknown", null, false, "expected_workspace_invalid", false, false, false, false); }

        var labels = rootTreeLabels.Where(label => !string.IsNullOrWhiteSpace(label)).Select(label => label!.Trim()).ToArray();
        var paths = accessiblePaths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!.Trim()).ToArray();
        var titleMatches = WindowTitleMatches(folder, windowTitle);
        var rootMatches = labels.Any(label => string.Equals(label, folder, StringComparison.OrdinalIgnoreCase));
        var pathMatches = paths.Any(path =>
        {
            if (string.Equals(path.TrimEnd('\\', '/'), fullPath, StringComparison.OrdinalIgnoreCase)) return true;
            return Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile &&
                string.Equals(Uri.UnescapeDataString(uri.LocalPath).TrimEnd('\\', '/'), fullPath, StringComparison.OrdinalIgnoreCase);
        });
        var verified = processCommandLineMatched || pathMatches || rootMatches && titleMatches;
        var observed = processCommandLineMatched || pathMatches || rootMatches ? folder : labels.Length > 0 || paths.Length > 0
            ? "different workspace (name redacted)" : null;
        var reason = !windowFound ? "window_not_found" : processCommandLineMatched ? "verified_by_scoped_process_command_line" :
            pathMatches ? "verified_by_accessible_path" :
            rootMatches && titleMatches ? "verified_by_workspace_root_and_title" :
            rootMatches ? "workspace_root_not_confirmed_by_title" : labels.Length > 0 || paths.Length > 0
                ? "workspace_mismatch" : titleMatches ? "workspace_root_not_visible" : "workspace_accessibility_unavailable";
        return new(fullPath, observed, verified, reason, titleMatches, rootMatches, pathMatches, processCommandLineMatched);
    }

    private static bool WindowTitleMatches(string folder, string? windowTitle)
    {
        if (!string.IsNullOrWhiteSpace(windowTitle))
        {
            var title = windowTitle.Trim();
            return title.Split([" — ", " - "], StringSplitOptions.TrimEntries)
                .Any(part => part.Equals(folder, StringComparison.OrdinalIgnoreCase));
        }
        return false;
    }

    public static string? ExtractSignedInUsername(IEnumerable<string?> accessibleLabels)
    {
        var matches = accessibleLabels.Select(label => GithubLoginLabel.Match(label?.Trim() ?? string.Empty))
            .Where(match => match.Success)
            .Select(match => match.Groups["login"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static bool IsGithubLoginPage(string? address) =>
        GithubAddressMatches(address, "https://github.com/login");

    public static bool HasGithubSignedOutMarker(IEnumerable<string?> accessibleLabels) =>
        accessibleLabels.Any(label => GithubSignedOutLabel.IsMatch(label?.Trim() ?? string.Empty));

    public static (string Status, string? Username) NormalizeGithubAccountResult(bool actionSucceeded,
        string? observedStatus, string? username)
    {
        if (!actionSucceeded || observedStatus == "failed") return ("failed", null);
        if (observedStatus == "signed_out") return ("signed_out", null);
        if (observedStatus == "verified" && username is not null && GithubUsername.IsMatch(username))
            return ("verified", username);
        return ("not_verified", null);
    }

    public static string[] BuildVsCodeWorkspaceArguments(string expectedWorkspace, string userDataDirectory)
    {
        var workspace = Path.GetFullPath(expectedWorkspace);
        var profile = Path.GetFullPath(userDataDirectory);
        var relativeProfile = Path.GetRelativePath(workspace, profile);
        var firstRelativeSegment = relativeProfile.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var isWorkspaceOrChild = relativeProfile == "." ||
            (!Path.IsPathFullyQualified(relativeProfile) && firstRelativeSegment != "..");
        if (isWorkspaceOrChild)
            throw new ArgumentException("The dedicated VS Code profile must be outside the workspace.", nameof(userDataDirectory));

        return ["--user-data-dir", profile, "--disable-extensions", "--new-window", workspace];
    }

    public static string BuildVsCodeAccessibilitySettings(string? existingSettingsJson)
    {
        var settings = string.IsNullOrWhiteSpace(existingSettingsJson)
            ? new JsonObject()
            : JsonNode.Parse(existingSettingsJson, documentOptions: new JsonDocumentOptions
                { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                ?? throw new JsonException("VS Code settings must be a JSON object.");
        settings["editor.accessibilitySupport"] = "on";
        return settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static bool IsRelevantExistingVsCodeWindow(bool windowExistedBeforeLaunch, WorkspaceEvidence evidence) =>
        windowExistedBeforeLaunch && evidence.WindowTitleMatched;

    public static bool IsWindowBoundProcessWorkspaceEvidence(bool processCommandLineMatched,
        int windowsOwnedByProcess, bool windowIsNew) => processCommandLineMatched && windowsOwnedByProcess == 1 && windowIsNew;

    public static string BuildVsCodeWorkspaceProfileDirectory(string profileRoot, string expectedWorkspace)
    {
        var root = Path.GetFullPath(profileRoot);
        var workspace = Path.GetFullPath(expectedWorkspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relativeRoot = Path.GetRelativePath(workspace, root);
        var firstSegment = relativeRoot.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (relativeRoot == "." || (!Path.IsPathFullyQualified(relativeRoot) && firstSegment != ".."))
            throw new ArgumentException("The VS Code profile root must be outside the workspace.", nameof(profileRoot));
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspace.ToUpperInvariant())))[..20].ToLowerInvariant();
        return Path.Combine(root, $"workspace-{key}");
    }

    public static bool IsDedicatedVsCodeCommandLine(string? commandLine, string expectedWorkspace, string expectedProfile)
    {
        if (!HasExactUserDataDirectory(commandLine, expectedProfile)) return false;
        try
        {
            var expected = Path.GetFullPath(expectedWorkspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return ParseWindowsCommandLine(commandLine!).Any(argument =>
                Path.IsPathFullyQualified(argument) && Path.GetFullPath(argument)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Equals(expected, StringComparison.OrdinalIgnoreCase));
        }
        catch (ArgumentException) { return false; }
    }

    public static bool IsNewWindow(nint nativeHandle, IReadOnlySet<nint> existingWindowHandles) =>
        nativeHandle > 0 && !existingWindowHandles.Contains(nativeHandle);

    private static IReadOnlyList<string> ParseWindowsCommandLine(string commandLine)
    {
        var arguments = new List<string>();
        var index = 0;
        while (index < commandLine.Length)
        {
            while (index < commandLine.Length && char.IsWhiteSpace(commandLine[index])) index++;
            if (index == commandLine.Length) break;
            var argument = new StringBuilder();
            var quoted = false;
            while (index < commandLine.Length)
            {
                var slashes = 0;
                while (index < commandLine.Length && commandLine[index] == '\\') { slashes++; index++; }
                if (index < commandLine.Length && commandLine[index] == '"')
                {
                    argument.Append('\\', slashes / 2);
                    if (slashes % 2 != 0) { argument.Append('"'); index++; }
                    else if (quoted && index + 1 < commandLine.Length && commandLine[index + 1] == '"')
                    { argument.Append('"'); index += 2; }
                    else { quoted = !quoted; index++; }
                    continue;
                }
                argument.Append('\\', slashes);
                if (index == commandLine.Length || (!quoted && char.IsWhiteSpace(commandLine[index]))) break;
                argument.Append(commandLine[index++]);
            }
            arguments.Add(argument.ToString());
            while (index < commandLine.Length && char.IsWhiteSpace(commandLine[index])) index++;
        }
        return arguments;
    }

    public static bool IsSmokeTestPassed(bool authenticatedIpc, bool sameSession, bool edgeVerified,
        bool workspaceVerified, bool pullRequestsAvailable, string? accountStatus) =>
        authenticatedIpc && sameSession && edgeVerified && workspaceVerified && pullRequestsAvailable &&
        accountStatus is "verified" or "signed_out";

    public static int? ReadNullableInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    public static string GetEdgeVerificationStatus(bool processStarted, bool dedicatedProcessFound,
        bool windowFound, bool urlVerified)
    {
        if (!processStarted) return "launch_failed";
        if (!dedicatedProcessFound) return "process_started";
        if (!windowFound) return "window_not_found";
        if (!urlVerified) return "url_not_verified";
        return "verified";
    }

    public static bool SameInteractiveSession(int currentSessionId, string? currentDesktop, bool userInteractive,
        int? edgeWindowSessionId, int? vscodeWindowSessionId, bool edgeWindowFound, bool vscodeWindowFound) =>
        currentSessionId > 0 && userInteractive && !string.IsNullOrWhiteSpace(currentDesktop) &&
        !currentDesktop.Equals("unavailable", StringComparison.OrdinalIgnoreCase) &&
        edgeWindowFound && vscodeWindowFound && edgeWindowSessionId == currentSessionId &&
        vscodeWindowSessionId == currentSessionId;

    private static string NormalizePath(string path)
    {
        var normalized = Uri.UnescapeDataString(path).TrimEnd('/');
        return normalized.Length == 0 ? "/" : normalized;
    }
}
