# AutoSkill real E2E checkpoint

Status: **BLOCKED — SELF-IMPROVEMENT NOT VERIFIED**

## Starting point and safety boundary

- Starting commit: `94864e3` (`Add staged AutoSkill learning pipeline`).
- No changes were made to `gateway.lab.json` or the primary Gateway state.
- Primary ports `5183` and `18793` were not started or modified.
- Staging uses port `19794`, config `detot-lab/.local/autoskill-staging/gateway.real-e2e.json`, state `detot-lab/.local/autoskill-staging/real-e2e-state`, synthetic owner `detot-synthetic-owner`, and synthetic ticket data only.
- Staging config pins DeepSeek to `deepseek-flash` through the existing `DEEPSEEK_API_KEY` environment reference. The credential itself is not copied into the staging config or logs.
- The only allowed agent tool is `detot_synthetic_case_check`; filesystem, shell, browser, network, Gateway administration, and other tool groups are denied.
- AutoSkill remains review-only; retrieval is disabled until an evaluated proposal is approved for staging.

## Verified

- DeepSeek API was called directly with a synthetic prompt: HTTP 200, model `deepseek-flash`, 48 prompt tokens, 18 completion tokens, 1,066 ms. No secret or personal text was logged.
- Staging OpenClaw config passed `openclaw config validate` before Gateway created its state DB. A later validation against the initialized state failed in the read-only SQLite worker with `EPERM` creating a private snapshot.
- Synthetic checker contract returned `PASS` for its fixed training fixture.
- Helper syntax checks passed.
- A direct Node loopback bind on port `19795` returned `PASS`, so loopback sockets are available to a normal process.
- The non-elevated Gateway attempt logged SQLite snapshot `EIO`, plugin capture `EPERM` while linking the OpenClaw package, and a later config-backup `EIO`.
- A staging-only elevated attempt got past the snapshot/plugin-capture errors and reached `starting HTTP server`, but did not become ready within its 2-minute readiness window; no listening socket appeared. That exact staging process was stopped.
- A second elevated startup attempt stalled before producing a Gateway log or opening a listener and was interrupted. Its precise failure point is not known.
- No listener remains on port `19794`; no staging or primary Gateway process remains running from this work.

## Not verified

- No real DeepSeek request went through the Gateway staging session.
- No plugin-loaded Gateway health/readiness check passed.
- AutoSkill did not produce a proposal from an actual Gateway interaction.
- No baseline vs post-skill replay, activation, rollback, latency/token comparison, restart test, or E2E PASS exists.
- The existing 72 adapter tests and 8 pipeline tests from commit `94864e3` remain unit/integration-fixture evidence only.

## Files for continuation

- `tools/autoskill/real-e2e/prepare.mjs` builds the isolated config and the filesystem-free synthetic tool.
- `.local/autoskill-staging/logs/` contains staging-only startup diagnostics; inspect only with secret redaction.
- The latest startup log ends at Gateway HTTP server startup. Earlier non-elevated logs show `EIO`/`EPERM`; the elevated log has no final bind/readiness error before timeout.

## Next exact step

Continue diagnosing why the staging OpenClaw Gateway stalls after `starting HTTP server` without altering the primary config/state. Capture the exact Gateway process exit/health result before another long start attempt. Once port `19794` reaches `/readyz`, run a baseline, create a corrected tool-backed synthetic learning session, wait for AutoSkill proposal generation, review it, replay independent cases through the same Gateway using DeepSeek, and only approve if the measured score improves. Keep the Gateway process scoped to staging and stop only its recorded PID after tests.

Do not report SELF-IMPROVEMENT PASS until all evidence above is replaced with actual Gateway-backed DeepSeek, proposal, activation, comparative replay, and rollback results.
