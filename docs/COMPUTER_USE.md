# Computer Use (local Windows only)

The Computer Use page is available only when the Backend is running locally on Windows. The API rejects public-origin and non-loopback requests.

## Permission boundaries

- **Observe:** read the current Git branch, whether tracked files changed, the sanitized GitHub `origin`, approved application names, the GitHub login in the dedicated Edge profile, and up to three recent public pull-request/check summaries. Application observation returns process identities only; it never reads window titles or control text.
- **Navigate:** open the workspace in Explorer, open VS Code, launch the dedicated Edge profile, or open the configured repository's PR list.
- **Account selection:** disabled. The current adapter has no trusted pending-Git-operation context tying a Git Credential Manager chooser to the intended GitHub host, repository, and operation. The assistant must not select an account or click Continue in a GCM chooser; the user handles that authentication dialog directly.
- **Write:** no write actions are exposed. A backend policy separately classifies observation and navigation targets and denies all write actions. The adapter cannot run shell commands or project code, read file contents, select or sign in to an account, edit a PR, commit, push, or merge.

Windows application launch and observation use fixed allowlists. FlaUI UIA3 checks approved process identities and whether a top-level window exists; it does not return window titles or inspect controls. Edge navigation and GitHub reads use Playwright Core from the locked OpenClaw runtime dependency.

## GitHub profile

The Playwright adapter uses its own profile under the selected runtime directory at `.local/runtime-<web-port>-<gateway-port>/computer-use/edge-profile`. It does not reuse the user's personal Edge profile or copy browser credentials into the Backend. Separate lab port pairs get separate Edge profiles; restarts of the same pair reuse that profile. If the dedicated profile has no GitHub session, account lookup reports that state; the user must sign in directly in that Edge window if they choose to use it. Public PR/check summaries do not require a GitHub login.

## Runtime configuration

`Start-Lab.ps1` supplies the Node executable, sibling `playwright-core` path, and runtime-scoped Edge profile directory. The adapter starts the fixed `tools/computer-use/edge-reader.mjs` worker with a restricted environment. Do not expose the Backend beyond loopback for these endpoints.

Autonomous code execution remains hard-blocked. This adapter only handles the fixed read and navigation actions listed above.

## Checks

From `detot-lab`, run:

```powershell
dotnet build Detot.Lab.csproj
dotnet run --project tests/desktop-operator/DesktopOperator.Tests.csproj
node --test tests/desktop-operator/github-checks.test.mjs
dotnet run --project tests/cloud-storage/CloudStorageChecks.csproj
```

The optional `--github-read-smoke` test calls the public GitHub API for `voquangthan/-t-t`; it reads PR title/state/SHA and check-run summaries only. The Cloud Storage regression locates Node from `PATH`; set `DETOT_TEST_NODE` only when the test runner needs an explicit executable path.
