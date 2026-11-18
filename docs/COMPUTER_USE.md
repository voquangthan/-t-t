# Computer Use (local Windows only)

The Computer Use page is available only when the Backend is running locally on Windows. The API rejects public-origin and non-loopback requests.

## Permission boundaries

- **Observe:** read the current Git branch, whether tracked files changed, the sanitized GitHub `origin`, approved application names, up to three recent public pull-request/check summaries, and a bounded UI Automation snapshot of visible top-level windows and their accessible controls. The UI snapshot is capped at six windows and twelve controls per window; password fields are redacted and input values are never read. VS Code/Explorer verification uses the expected workspace folder name; Edge verification returns only a canonical GitHub URL when it matches the requested public repository path.
- **Navigate/interact:** open the workspace in Explorer, open VS Code at the fixed workspace root, launch a dedicated Edge profile, or open the configured repository's PR list. UI targets use opaque IDs valid for 90 seconds, are tied to the observed window/process and current Windows session, and are invalidated after an action. The low-risk actions include window activation, selecting a visible tab, clicking a hyperlink, entering bounded text in a freshly revalidated non-password editable field without submitting, and vertical scrolling. Keyboard input is limited to left/right/home/end inside an editable field; radio, combo, list/tree and other selection contexts do not accept keyboard actions unless a future approval path explicitly authorizes them. A key-up is always sent after key-down. Actions return their verification status and a fresh observation; key input is reported as `reobserve_required` because sending it alone does not prove the UI outcome.
- **Approved application launch:** Chat can open Windows Settings, Task Manager, Word, Excel, PowerPoint and an installed Adobe PDF Reader/Acrobat using fixed action IDs and known executable locations. Windows Settings and Task Manager are observation-only: their UI elements advertise no click, typing, selection or key actions. Launch is reported successful only after UI Automation sees a window; missing apps and unverified windows are reported separately. File Explorer can open a directory or reveal a selected file only when it exists below the configured workspace. Paths are relative, traversal and reparse-point paths are denied, and files are not executed or opened in an associated application by this action.
- **Account selection:** disabled. The current adapter has no trusted pending-Git-operation context tying a Git Credential Manager chooser to the intended GitHub host, repository, and operation. The assistant must not select an account or click Continue in a GCM chooser; the user handles that authentication dialog directly.
- **Approval-required UI actions:** generic button/menu/toggle clicks, double-clicking file/list items, and Enter/submit remain blocked with `approval_required`; there is not yet an approval workflow that can authorize those actions. Settings changes, external sends, overwrites, deletion and arbitrary code execution remain unavailable. The adapter cannot run shell commands or project code, read file contents, sign in to an account, edit a PR, commit, push, or merge.

Windows desktop access runs in a user-started Desktop Companion in the interactive Windows session. Backend-to-Companion IPC is a local named pipe restricted with `PipeOptions.CurrentUserOnly`; requests contain a fixed action ID, correlation ID, and—only for element actions—a bounded opaque target ID plus validated typed arguments. Unknown/duplicate JSON fields, arbitrary URLs, commands, scripts, paths and file reads are rejected. Target IDs are held only in Companion memory, expire after 90 seconds, and are invalidated whenever a UI action runs. Before interacting, Companion rechecks the target window handle, owning process, session, visibility and enabled state. The Companion does not elevate, install a service, auto-start, or accept network connections. UI Automation reuses the existing FlaUI UIA3 dependency.

The Backend exposes `POST /api/desktop/observe` with `{ "target": "desktop-ui" }` for the bounded accessibility snapshot and `POST /api/desktop/actions` for fixed UI action IDs, including `open-workspace-item` with a workspace-relative `text` path. These routes remain local-only and require the same-origin guard. Chat currently supports a direct natural-language request to observe the UI and fixed application launches; generic multi-step action planning through Chat/Planner is the next integration stage, so this adapter/API is not yet a universal natural-language E2E loop. Double-click and generic buttons are deliberately not executed until an approval path can authorize them.

## GitHub profile

The Companion uses its own Edge profile at `%LOCALAPPDATA%\DetotLab\ComputerUse\EdgeProfile` (reported as `detot-dedicated`). It does not reuse or inspect the personal Edge profile and does not read cookies or OAuth tokens. Public PR/check summaries use unauthenticated requests to `api.github.com`. The read-only account observation opens the GitHub profile settings page in that dedicated profile and returns a username only when the accessibility tree contains one unambiguous `Signed in as <username>` label. It reports `signed_out` only when the exact GitHub `/login` URL and its `Sign in to GitHub` accessibility heading are both visible. A failed Companion call or Edge launch reports `failed`; all ambiguous or inaccessible states report `not_verified`. It never infers an account from the URL, page title, Git remote, or process state. This path is implemented but still requires the interactive smoke test to confirm live UIA behavior. Sign-in, if needed, is performed directly by the user in the dedicated Edge window.

The dedicated Edge profile is launched with the Chromium sandbox enabled. If Edge launch is blocked by the Windows session or browser policy, the adapter reports an error and does not fall back to the user's personal browser profile.

## Runtime configuration

Start the Companion from an interactive, non-elevated PowerShell window:

```powershell
cd D:\-t-t\detot-lab
.\Start-DesktopCompanion.ps1
```

The script builds/runs the fixed Companion project for the current user and then waits for local Backend actions. Press Ctrl+C to stop. To run its bounded live smoke check (reports session/user/integrity/desktop for the Companion and approved apps; opens the dedicated Edge GitHub PR page and VS Code at this workspace; verifies UIA window/URL state):

```powershell
& 'D:\-t-t\.local\worktrees\codex-computer-use-vscode-fix\detot-lab\Start-DesktopCompanion.ps1' -SmokeTest
```

The smoke check reads only public PR/check metadata, fixed Git status fields, and the explicit account label described above; it does not sign in, inspect account credentials, or write to Git. It reports Edge launch/process/window/URL states separately. VS Code opens the configured absolute workspace path with `--new-window`, leaving existing user windows open. The Companion snapshots existing top-level window handles before launch and verifies only a newly appeared Code window, so a reused user window cannot satisfy the check. Workspace verification requires an accessible full path or the exact workspace root label together with the workspace component in the title; title or process presence alone never passes. The expected field is the configured project workspace path. Observed output includes the expected folder label only when verified; any other workspace label/path is redacted. It also reports signal booleans and a reason such as `workspace_mismatch`, `workspace_root_not_visible`, or `timeout_workspace_accessibility_unavailable`. `verifiedSameSession` means both target windows are UIA-visible from the Companion desktop and their process session IDs match its interactive session; this is independent of whether VS Code's requested workspace was verified. The overall smoke passes only with a verified or explicitly `signed_out` account result; `not_verified` and `failed` remain failures. Backend endpoints still require local loopback and reject public-origin mode. If the Companion is stopped, actions fail with a clear unavailable response rather than falling back to UIA in the isolated Backend process.

Autonomous code execution remains hard-blocked. This adapter only handles the fixed observation, navigation and bounded low-risk UI interaction actions listed above; it never accepts a shell command or script.

## Checks

From `detot-lab`, run:

```powershell
dotnet build Detot.Lab.csproj
dotnet run --project tests/desktop-operator/DesktopOperator.Tests.csproj
node --test tests/desktop-operator/github-checks.test.mjs
dotnet run --project tests/cloud-storage/CloudStorageChecks.csproj
dotnet build desktop-companion/DesktopCompanion.csproj
```

The optional `--github-read-smoke` test calls the public GitHub API for `voquangthan/-t-t`; it reads PR title/state/SHA and check-run summaries only. The Cloud Storage regression locates Node from `PATH`; set `DETOT_TEST_NODE` only when the test runner needs an explicit executable path. The desktop smoke script requires an interactive Windows desktop and is not replaced by unit tests.
