# AutoSkill staging pipeline

Status: **staging only**. The real Gateway configuration is unchanged and the plugin is not active on the primary lab.

## Scope and pinned source

- Upstream source is cloned to `.local/research/AutoSkill`, pinned at `94c47ca488d4ba4117d20272e66d49b9877e68cf`.
- The upstream repository has no root license file in the inspected tree, so the project does not vendor or commit its repository. The tracked patch contains only the lab integration delta and is applied to the pinned local clone.
- `tools/autoskill/apply-upstream-patch.mjs` verifies the pin and refuses to overwrite a locally modified adapter.
- `tools/autoskill/prepare-staging.mjs` writes a separate Gateway config under `.local/autoskill-staging`; it does not edit `gateway.lab.json`, start a process, or change the primary Gateway.

## Learning lifecycle

1. The staged plugin is currently disabled and cannot access conversations. No `agent_end` capture, archive, or extraction runs until a future backend consent gate is implemented and explicitly enabled.
2. AutoSkill extracts a candidate into an owner-scoped proposal file. It does not merge into the approved SkillBank, retrieve proposals into prompts, or mirror them as active skills.
3. The pipeline accepts a replay report only when it identifies the exact candidate hash and current baseline version/hash, contains at least three unique cases, has no safety failure, has no per-case regression, and improves the aggregate score.
4. An operator explicitly approves a passing proposal. Activation writes only into the staging workspace skills directory, records an immutable version snapshot, source proposal, evidence hash, replay summary and audit event.
5. Rollback changes the active pointer and staging `SKILL.md` back to an existing immutable version. It never deletes versions.

Rejection reasons are restricted to a small code list so audit entries do not capture arbitrary private text. Audit records contain IDs, hashes and status only.

## Privacy controls

- `prepare-staging.mjs` writes the plugin as `enabled: false`, with `allowConversationAccess: false` and `extractOnAgentEnd: false`. OAuth or a Gateway hook grant does not confer AI learning consent.
- The lab privacy module omits messages that match sensitive topics such as health, finance, identity documents, home address or credentials. It redacts common email, phone and long-number patterns.
- Candidates containing credential or private-topic patterns are rejected. Any resource files are rejected pending a separate review path; generated scripts are never activated.
- Feedback text is excluded. Only the fixed owner ID is used; no frontend-provided identity is trusted.
- Finalized, AutoSkill-owned archive files are pruned after seven days. Active session files are not pruned by this policy. Proposal and version history are retained.

This is a conservative heuristic privacy gate, not a guarantee that arbitrary free text contains no personal information. AutoSkill remains disabled while the consent foundation is enforced; do not enable automatic archive capture on the primary Gateway based solely on these filters.

## Replay report contract

`pipeline.mjs evaluate` consumes a JSON report produced by a replay runner. Example shape:

```json
{
  "replay_id": "replay-unique-id",
  "runner": "detot-staging-runner",
  "candidate_hash": "sha256-of-the-sanitized-candidate-json",
  "baseline_version": null,
  "baseline_hash": "",
  "cases": [
    { "case_id": "copy", "candidate_score": 1, "baseline_score": 0, "safety": "pass" },
    { "case_id": "retry", "candidate_score": 0.9, "baseline_score": 0, "safety": "pass" },
    { "case_id": "bad-input", "candidate_score": 0.9, "baseline_score": 0, "safety": "pass" }
  ]
}
```

The pipeline validates provenance and results; it does not itself call DeepSeek or claim that these fixture reports are real model replays. Real runner-backed replay and live DeepSeek E2E are still required before primary activation.

## Local staging commands

```powershell
cd D:\-t-t\detot-lab
node tools\autoskill\prepare-staging.mjs
node tools\autoskill\pipeline.mjs list
node tools\autoskill\pipeline.mjs evaluate --proposal <id> --report <report.json>
node tools\autoskill\pipeline.mjs approve --proposal <id>
node tools\autoskill\pipeline.mjs rollback --skill <skill-id> --version <version>
```

The default data root is `.local/autoskill-staging`. The CLI refuses a different root. `approve` only writes the staging workspace. Primary activation is intentionally not part of these commands.

## Verification boundary

- Adapter unit tests and isolated lifecycle tests cover proposal-only handling, privacy filters, tool evidence, archive expiry, and owner-partitioned output.
- Pipeline tests cover a successful proposal, fail-then-correct versioning, worse replay rejection, explicit approval, and rollback.
- The replay tests use fixture reports. They verify the gate, not the quality of an actual LLM response.
- A real OpenClaw Gateway was previously able to load the adapter in isolated staging when the host granted hook conversation access. The primary Gateway is not running at the latest status check; no production-config restart or DeepSeek E2E has been done.
