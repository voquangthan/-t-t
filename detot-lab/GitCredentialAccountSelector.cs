#if WINDOWS
using FlaUI.Core;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
#endif

public static class GitCredentialAccountSelector
{
    public sealed record Decision(bool Allowed, int Index, string? Login, string? Error);

    private static readonly System.Text.RegularExpressions.Regex GitHubLogin = new(
        @"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant |
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static Decision ResolveTarget(IReadOnlyList<string> visibleLogins, string? target)
    {
        var logins = visibleLogins.Where(login => !string.IsNullOrWhiteSpace(login) && GitHubLogin.IsMatch(login))
            .ToArray();
        if (logins.Length == 0) return new(false, -1, null, "no_accounts_listed");
        if (string.Equals(target, "bottom", StringComparison.OrdinalIgnoreCase))
            return new(true, logins.Length - 1, logins[^1], null);
        if (target is null || !GitHubLogin.IsMatch(target)) return new(false, -1, null, "invalid_account_target");
        var matches = logins.Select((login, index) => (login, index))
            .Where(item => string.Equals(item.login, target, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            1 => new(true, matches[0].index, matches[0].login, null),
            0 => new(false, -1, null, "account_not_listed"),
            _ => new(false, -1, null, "account_selection_ambiguous")
        };
    }

    public static bool CanUseChooserSnapshot(bool enumerationComplete, int totalCredentialManagerWindows,
        int matchingAccountPickers) => enumerationComplete && totalCredentialManagerWindows == 1 &&
        matchingAccountPickers == 1;

    public static DesktopOperator.ActionResult SelectAccount(string? target)
    {
#if !WINDOWS
        return new(false, "Chọn tài khoản GitHub chỉ khả dụng trên Windows.");
#else
        if (!OperatingSystem.IsWindows()) return new(false, "Chọn tài khoản GitHub chỉ khả dụng trên Windows.");
        if (target is null || (target != "bottom" && !GitHubLogin.IsMatch(target)))
            return new(false, "Tài khoản cần chọn không hợp lệ.");

        try
        {
            using var automation = new UIA3Automation();
            var desktop = automation.GetDesktop();
            var dialogs = new List<(FlaUI.Core.AutomationElements.AutomationElement Window,
                FlaUI.Core.AutomationElements.AutomationElement[] Items, string[] Logins)>();
            var totalCredentialManagerWindows = 0;
            var enumerationComplete = true;

            foreach (var process in System.Diagnostics.Process.GetProcessesByName("git-credential-manager"))
            {
                using (process)
                {
                    try
                    {
                        var windows = desktop.FindAllChildren(cf =>
                            cf.ByControlType(ControlType.Window).And(cf.ByProcessId(process.Id)));
                        totalCredentialManagerWindows += windows.Length;
                        foreach (var window in windows)
                        {
                            if (!string.Equals(window.Properties.Name.Value, "Select an account", StringComparison.Ordinal))
                                continue;
                            var rows = window.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem));
                            var items = new List<FlaUI.Core.AutomationElements.AutomationElement>();
                            var logins = new List<string>();
                            foreach (var row in rows)
                            {
                                var labels = row.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                                    .Select(element => element.Properties.Name.Value)
                                    .Where(label => !string.IsNullOrWhiteSpace(label))
                                    .ToArray();
                                var login = labels.FirstOrDefault(label => GitHubLogin.IsMatch(label));
                                if (login is null) continue;
                                items.Add(row);
                                logins.Add(login);
                            }
                            dialogs.Add((window, items.ToArray(), logins.ToArray()));
                        }
                    }
                    catch (Exception)
                    {
                        enumerationComplete = false;
                    }
                }
            }

            if (!CanUseChooserSnapshot(enumerationComplete, totalCredentialManagerWindows, dialogs.Count))
                return new(false, "Không thể xác minh một hộp thoại chọn tài khoản GCM duy nhất; mình không thao tác.");

            var dialog = dialogs[0];
            var decision = ResolveTarget(dialog.Logins, target);
            if (!decision.Allowed) return new(false, decision.Error switch
            {
                "account_not_listed" => "Tài khoản được yêu cầu không có trong hộp thoại hiện tại.",
                "account_selection_ambiguous" => "Tài khoản xuất hiện nhiều lần; mình không chọn.",
                _ => "Không tìm thấy tài khoản trong hộp thoại hiện tại."
            });

            var continueButton = dialog.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(button => string.Equals(button.Properties.Name.Value, "Continue", StringComparison.Ordinal));
            if (continueButton is null) return new(false, "Hộp thoại không có nút Continue; chưa chọn tài khoản.");

            var selectionPattern = dialog.Items[decision.Index].Patterns.SelectionItem.PatternOrDefault;
            if (selectionPattern is null)
                return new(false, "Dòng tài khoản không cung cấp trạng thái chọn có thể xác minh; mình không tiếp tục.");
            selectionPattern.Select();
            if (!selectionPattern.IsSelected.Value)
                return new(false, "Không xác minh được đúng tài khoản đã chọn; mình không tiếp tục.");
            if (!continueButton.Properties.IsEnabled.Value)
                return new(false, $"Đã chọn {decision.Login}, nhưng GCM chưa bật Continue; không thực hiện bước tiếp theo.");
            continueButton.Click();

            return new(true,
                $"Đã chọn tài khoản GitHub {decision.Login}. Mình không nhập credential hoặc chấp nhận yêu cầu cấp quyền tiếp theo.");
        }
        catch (Exception)
        {
            return new(false, "Không thể thao tác an toàn với hộp thoại Git Credential Manager hiện tại.");
        }
#endif
    }
}
