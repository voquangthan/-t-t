# AutoSkill controlled checkpoint — 17/11/2026

Status: **STOPPED at the requested checkpoint. Changes remain uncommitted. AutoSkill and its consent bridge remain disabled by default.** No Gateway was launched and no personal data or live provider was used.

## Files modified

Main repository (`D:\-t-t\detot-lab`):

- `Program.cs` — opt-in internal consent bridge; loopback and existing Gateway bearer-token checks; fixed Backend owner and stage-specific consent requirements.
- `Start-Lab.ps1` — passes the local bridge URL to the Gateway child and keeps bridge enablement off unless explicitly configured.
- `tools/autoskill/pipeline.mjs`, `tools/autoskill/prepare-staging.mjs` — align staging defaults with Backend owner `workspace-owner`; no staging data was migrated or rewritten.
- `tools/autoskill/adapter-review-only.patch` — synchronized adapter consent callback and owner-binding changes from the pinned research checkout. Reverse/apply round-trip validation passed.
- `tests/consent-api.test.mjs` — bridge API assertions.
- `tests/autoskill-consent-bridge.integration.test.mjs` — isolated Backend process tests using temporary storage and a synthetic token.
- `docs/AUTOSKILL_CONTROLLED_CHECKPOINT.md` — this checkpoint.

Ignored pinned research checkout `.local/research/AutoSkill` at upstream SHA `94c47ca488d4ba4117d20272e66d49b9877e68cf` also remains modified:

- `AutoSkill4OpenClaw/adapter/embedded_runtime.js`
- `AutoSkill4OpenClaw/adapter/embedded_runtime.test.mjs`
- `AutoSkill4OpenClaw/adapter/index.js`
- `AutoSkill4OpenClaw/adapter/index.test.mjs`
- `AutoSkill4OpenClaw/adapter/openclaw.plugin.json`
- new `AutoSkill4OpenClaw/adapter/detot-privacy.mjs`
- new `AutoSkill4OpenClaw/adapter/detot-provenance.mjs`

## Tests and checks

- `dotnet build --no-restore`: **PASS**, 0 warnings and 0 errors (previous run).
- AutoSkill project suite: **39/39 PASS** (previous run; synthetic fixtures).
- Pinned adapter suite: **83/83 PASS**, rerun at this checkpoint (temporary fixtures).
- `node --check tests/autoskill-consent-bridge.integration.test.mjs`: **PASS** (previous run).
- Patch reverse/apply round-trip on the pinned checkout: **PASS**.
- Isolated Backend bridge integration: **1/1 PASS** in an earlier elevated loopback run using synthetic storage/token and no Gateway. This is not a live Gateway E2E result.
- Latest normal-sandbox integration run: **0/2 PASS**; Backend child startup timed out. Earlier normal-sandbox loopback attempts returned `EACCES`/`ETIMEDOUT`. Record as **ENVIRONMENT_BLOCKER**, not as evidence of a code defect. Do not bypass sandbox restrictions.
- Latest main-repository `git diff --check`: **WARNING** at `tools/autoskill/adapter-review-only.patch:1170` for whitespace in patch content. Pinned checkout `git diff --check` had passed previously. This warning is recorded; no source changes were made to work around it.

## Features completed in this work

- Backend consent bridge is opt-in and defaults off. When enabled, only its loopback route with the existing Gateway token can request a grant; callers cannot choose an owner.
- `capture` requires `chat_history / skill_learning / local_only`; `analysis` additionally requires DeepSeek consent. Backend returns authenticated owner and consent revision.
- Adapter calls only the configured loopback endpoint, validates the response owner against its configured owner, and fails closed on missing configuration, invalid scope, mismatch, timeout, or provider error.
- `Start-Lab.ps1` passes the endpoint without enabling AutoSkill or the bridge by default.

## Remaining work

- Run both isolated Backend bridge integration cases in an environment where loopback child-process binding is permitted; keep this marked blocked until then.
- Review and resolve the patch-file whitespace warning as normal patch hygiene.
- Complete runtime/revocation review across archives, proposals, versions, active pointers, mirrors, runtime caches, embeddings, and backups. Current staging revocation only covers managed proposal/version files.
- No live Gateway E2E, cross-process restart/revoke race, or backup-restore integration has been run.

## Known blockers and next recommended action

- **ENVIRONMENT_BLOCKER:** the normal sandbox cannot reliably start the isolated Backend loopback integration (`EACCES`/`ETIMEDOUT`/startup timeout). No sandbox escalation or workaround was attempted for this checkpoint.
- **Not acceptance-ready:** the isolated consent bridge does not establish full AutoSkill revocation/retention compliance or end-to-end Gateway integration.
- Recommended next action: review the tracked adapter patch and its whitespace warning, then run the isolated Backend integration in an already-approved loopback-capable test environment. Keep AutoSkill and Gateway disabled; do not use personal data.

Overall Consent & Privacy / AutoSkill acceptance: **NOT PASS**.
