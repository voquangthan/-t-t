# Codex ↔ GitHub Work review loop

Status: **implementation in progress; real Work-trigger review has not been observed.** This integration does not call an OpenAI API, enable AutoSkill, modify consent, or merge a PR.

## Authentication and trust

- The authenticated GitHub connector was checked as `voquangthan`; repository metadata reports `admin` and push access for that account.
- The listener reads a GitHub token from `DETOT_GITHUB_TOKEN`/`GITHUB_TOKEN`, or from the configured `git credential fill` helper. Tokens stay in process memory and are never written to state, logs, config, or Git.
- A reviewer is trusted only when its exact login, numeric GitHub user ID, and account type are configured in ignored local config. A login by itself is insufficient. Do not pin the PR author/shared account as an independent Work identity unless the integration owner verifies that identity out of band.
- Before accepting feedback, the listener verifies the configured login, numeric user ID and account type through GitHub's user API, checks repository permission, then matches the event's user ID/type and collaborator association. It rejects the PR author's identity, since that cannot independently attest its own review. A review also needs one strict `DETOT-REVIEW/1` verdict and full SHA matching PR and local HEAD; review submissions must match GitHub `commit_id` too.
- Codex comments, invalid identities, malformed verdicts, old SHAs and duplicate event IDs are rejected or ignored. Review prose is untrusted input for Codex to analyze, never a shell command or policy override.

## Runtime files

Runtime state belongs under `.local/review-loop/` (already gitignored):

- `state/<task_id>.json`: task/stage, branch, PR, SHA, configured reviewer, status, revision attempts, processed review IDs, deadline, quality gates, checkpoint and latest review feedback.
- `audit/<task_id>.jsonl`: payload-minimal events with IDs, verdict, SHA and reason codes; never tokens.
- A per-task exclusive lock prevents duplicate concurrent processing; dead-owner locks can be reclaimed, malformed locks stop for manual recovery.

The listener polls GitHub REST over HTTPS and checks `/user`, reviewer profile and permission, PR base/head, review submissions and issue comments. It uses bounded API calls and pagination. On GitHub auth/read failure or missing reviewer identity it records `REVIEW_CHANNEL_UNAVAILABLE`; timeout records `TIMEOUT`. Both are terminal checkpoints for this run.

## Local setup

Copy `tools/review-feedback/config.example.json` to `.local/review-loop/config.json`. Set reviewer login, numeric user ID, and account type only after independently verifying the Work identity and its GitHub comment/review mechanism. The real PR #1 comment was authored by `voquangthan` (user ID `198889798`), the PR owner; this confirms GitHub API delivery but does not establish an independent Work identity, so it must not be copied as a trusted reviewer. Set `DETOT_REVIEW_LOOP_CONFIG` to the local file. The listener obtains auth through Git's credential helper; alternatively provide a short-lived, read-only `DETOT_GITHUB_TOKEN` in the process environment. It needs repository contents metadata plus pull-request/issue read access; it does not write comments, approvals, refs or merges.

After opening a PR from `codex/*` into `main`, initialize task state with current local/remote HEAD and named required quality gates. A plain `true` in JSON is not evidence. Record every successful command's exit code and output digest against the exact SHA:

```powershell
$sha = git rev-parse HEAD
node tools/review-feedback/listener.mjs init --task review-task-01 --stage stage-01 --branch codex/review --pr 123 --sha $sha --gates tools/review-feedback/quality-gates.example.json
node tools/review-feedback/listener.mjs run-gate --task review-task-01 --gate review-feedback-tests
node tools/review-feedback/listener.mjs wait --task review-task-01
node tools/review-feedback/listener.mjs show --task review-task-01
```

The reviewer must submit a top-level comment or PR review body in this exact format; any explanation follows after the required fields:

```text
DETOT-REVIEW/1
VERDICT: PASS
COMMIT_SHA: <40-character commit SHA under review>
CHECKPOINT: short summary
```

`REVISE` exposes the saved feedback and stops the poller for Codex to fix/test. After pushing the new commit to the same PR branch, `update-head` checks that local HEAD and remote PR head agree, increments one of at most three repair rounds, clears all prior gate evidence and resets review status. Reinitializing an existing task with the same immutable identity is idempotent; a different task cannot overwrite it. `BLOCKED`, `TIMEOUT`, `REVIEW_CHANNEL_UNAVAILABLE`, and the three-round limit stop the task. Review batches stop at the first terminal verdict. A PASS is revalidated against local and remote HEAD on every poll; any head change revokes it. Stage advance requires output digest and exit status from the listener's built-in allowlisted quality commands for the exact current SHA; arbitrary booleans/commands cannot satisfy the gate. No merge or next task is executed automatically.

## Verification boundary

Unit and integration fixtures validate review parsing, identity/SHA gates, idempotency, branch/base checks, permission failure, timeout/restart and revision limits. They do not prove the Work trigger or a real GitHub reviewer comment. Full acceptance requires a real PR update, a real Work-trigger review from the configured login at the current SHA, and observation of the listener accepting it exactly once.
