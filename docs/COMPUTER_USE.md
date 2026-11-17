# Computer Use (local Windows only)

The Computer Use page is available only when the Backend is running locally on Windows. The API rejects public-origin and non-loopback requests.

## Permission boundaries

- **Observe:** read the current Git branch, whether tracked files changed, the sanitized GitHub `origin`, the GitHub login in the dedicated Edge profile, and up to three recent public pull-request/check summaries.
- **Navigate:** open the workspace in Explorer, open VS Code, launch the dedicated Edge profile, or open the configured repository's PR list.
- **Write:** no write actions are exposed. The adapter cannot run shell commands, read file contents, select or sign in to an account, edit a PR, commit, push, or merge.

Windows application launch is a fixed allowlist. FlaUI UIA3 checks only whether a top-level window exists; it does not return window titles or inspect controls. Edge navigation and GitHub reads use Playwright Core from the locked OpenClaw runtime dependency.

## GitHub profile

The Playwright adapter uses its own profile at `detot-lab/.local/computer-use/edge-profile`. It does not reuse the user's personal Edge profile or copy browser credentials into the Backend. If the dedicated profile has no GitHub session, account lookup reports that state; the user must sign in directly in that Edge window if they choose to use it. Public PR/check summaries do not require a GitHub login.

## Runtime configuration

`Start-Lab.ps1` supplies the Node executable and the sibling `playwright-core` path. The adapter starts the fixed `tools/computer-use/edge-reader.mjs` worker with a restricted environment and stores its profile under `.local`. Do not expose the Backend beyond loopback for these endpoints.

Autonomous code execution remains hard-blocked. This adapter only handles the fixed read and navigation actions listed above.

## Checks

From `detot-lab`, run:

```powershell
dotnet build Detot.Lab.csproj
dotnet run --project tests/desktop-operator/DesktopOperator.Tests.csproj
```

The optional `--github-read-smoke` test calls the public GitHub API for `voquangthan/-t-t`; it reads PR title/state/SHA and check-run summaries only.
