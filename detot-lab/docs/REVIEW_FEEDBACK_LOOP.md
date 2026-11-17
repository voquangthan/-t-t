# Codex ↔ GitHub Work review loop

Status: **implementation in progress; real Work-trigger review has not been observed.** This integration does not call an OpenAI API, enable AutoSkill, modify consent, or merge a PR.

## Authentication and trust

- The authenticated GitHub connector was checked as `voquangthan`; repository metadata reports `admin` and push access for that account.
- The listener reads a GitHub token from `DETOT_GITHUB_TOKEN`/`GITHUB_TOKEN`, or from the configured `git credential fill` helper. Tokens stay in process memory and are never written to state, logs, config, or Git.
- The exact Work reviewer GitHub login is required in an ignored local config. Blank or unknown identities fail closed as `REVIEW_UNAVAILABLE`; no login is inferred from the first comment.
- A review is accepted only from that exact login, with one strict `DETOT-REVIEW/1` verdict and a full commit SHA matching both the PR head and local HEAD. For submitted PR reviews, the body SHA must also agree with GitHub's `commit_id`.
- Codex comments, invalid identities, malformed verdicts, old SHAs and duplicate event IDs are rejected or ignored. Review prose is untrusted input for Codex to analyze, never a shell command or policy override.

## Runtime files

Runtime state belongs under `.local/review-loop/` (already gitignored):

- `state/<task_id>.json`: task/stage, branch, PR, SHA, configured reviewer, status, revision attempts, processed review IDs, deadline, quality gates, checkpoint and latest review feedback.
- `audit/<task_id>.jsonl`: payload-minimal events with IDs, verdict, SHA and reason codes; never tokens.
- A per-task exclusive lock prevents duplicate concurrent processing; dead-owner locks can be reclaimed, malformed locks stop for manual recovery.

The listener polls GitHub REST over HTTPS and checks `/user`, PR base/head, review submissions and issue comments. It uses bounded API calls and pagination. On GitHub auth/read failure it records `REVIEW_UNAVAILABLE`; timeout records `TIMEOUT`. Both are terminal checkpoints for this run.

## Local setup

Copy `tools/review-feedback/config.example.json` to `.local/review-loop/config.json`, then set `reviewer_login` to the exact GitHub login supplied by the Work Reviewer administrator. Set `DETOT_REVIEW_LOOP_CONFIG` to that file. The listener obtains auth through Git's credential helper; alternatively provide a short-lived, read-only `DETOT_GITHUB_TOKEN` in the process environment. It needs repository contents metadata plus pull-request/issue read access; it does not write comments, approvals, refs or merges.

After opening a PR from `codex/*` into `main`, initialize task state with current local/remote HEAD and named quality gates:

```powershell
$sha = git rev-parse HEAD
node tools/review-feedback/listener.mjs init --task review-task-01 --stage stage-01 --branch codex/review --pr 123 --sha $sha --gates .local/review-loop/quality-gates.json
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

`REVISE` exposes the saved feedback and stops the poller for Codex to fix/test. After pushing the new commit to the same PR branch, `update-head` checks that local HEAD and the remote PR head agree, increments one of at most three repair rounds, and resets the review status to pending. `BLOCKED`, `TIMEOUT`, `REVIEW_UNAVAILABLE`, and the three-round limit stop the task. `PASS` advances no stage unless every named quality gate is explicitly `true`; even then it only sets `stage_advance_allowed`, it does not merge or execute another task.

## Verification boundary

Unit and integration fixtures validate review parsing, identity/SHA gates, idempotency, branch/base checks, permission failure, timeout/restart and revision limits. They do not prove the Work trigger or a real GitHub reviewer comment. Full acceptance requires a real PR update, a real Work-trigger review from the configured login at the current SHA, and observation of the listener accepting it exactly once.
