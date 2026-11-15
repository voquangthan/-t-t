import assert from "node:assert/strict";
import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { approveProposal, evaluateProposal } from "../tools/autoskill/pipeline.mjs";

const LAB_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const EMBEDDED_RUNTIME = path.join(LAB_ROOT, ".local", "research", "AutoSkill", "AutoSkill4OpenClaw", "adapter", "embedded_runtime.js");

test("AutoSkill proposal flows through replay gate into staging activation", { skip: !fs.existsSync(EMBEDDED_RUNTIME) }, async () => {
  const { createEmbeddedProcessor } = await import(pathToFileUrl(EMBEDDED_RUNTIME));
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "detot-autoskill-e2e-"));
  const paths = {
    sessionArchiveDir: path.join(root, "state", "autoskill", "sessions"),
    reviewQueueDir: path.join(root, "state", "autoskill", "proposals"),
    skillBankDir: path.join(root, "state", "autoskill", "SkillBank"),
    openclawSkillsDir: path.join(root, "state", "workspace", "skills"),
  };
  const processor = createEmbeddedProcessor({
    userId: "detot-owner",
    runtimeMode: "embedded",
    reviewOnly: true,
    skillInstallMode: "openclaw_mirror",
    successOnly: false,
    includeUserFeedback: false,
    extractOnAgentEnd: true,
    embedded: { ...paths, liveExtractEveryTurns: 0, sessionMaxTurns: 20, archiveRetentionDays: 7, bm25TopK: 8 },
  }, {}, {}, {
    async invokeModel() {
      return JSON.stringify({ skills: [{ skill_id: "file-audit", name: "File audit", description: "Verify files safely", prompt: "Copy to staging, validate, then report.", files: {}, confidence: 0.93 }] });
    },
  });
  const result = await processor.handle({
    user: "detot-owner", session_id: "integration-e2e", turn_type: "main", session_done: true, success: true,
    messages: [
      { role: "user", content: "Safely inspect a report and prepare a verified copy." },
      { role: "assistant", content: "I will stage and verify the result." },
      { role: "tool", content: "Staging copy checksum verified." },
    ],
  }, {}, {});
  assert.equal(result.jobs[0].status, "proposed");
  const proposalId = result.jobs[0].proposal_id;
  const proposalFile = path.join(paths.reviewQueueDir, "Users", "detot-owner", `${proposalId}.json`);
  const proposal = JSON.parse(fs.readFileSync(proposalFile, "utf8"));
  const candidateHash = crypto.createHash("sha256").update(JSON.stringify(proposal.candidate)).digest("hex");
  evaluateProposal({
    root,
    ownerId: "detot-owner",
    proposalId,
    report: {
      replay_id: "fixture-replay-e2e",
      runner: "fixture-replay-runner",
      candidate_hash: candidateHash,
      baseline_version: null,
      baseline_hash: "",
      cases: [
        { case_id: "normal-copy", candidate_score: 1, baseline_score: 0, safety: "pass" },
        { case_id: "retry-copy", candidate_score: 0.9, baseline_score: 0, safety: "pass" },
        { case_id: "invalid-file", candidate_score: 0.8, baseline_score: 0, safety: "pass" },
      ],
    },
  });
  const activated = approveProposal({ root, ownerId: "detot-owner", proposalId });
  assert.equal(activated.version, "1.0.0");
  assert.match(fs.readFileSync(path.join(root, "state", "workspace", "skills", "autoskill-file-audit", "SKILL.md"), "utf8"), /Copy to staging/);
});

function pathToFileUrl(target) {
  return new URL(`file:///${target.replaceAll("\\", "/")}`);
}
