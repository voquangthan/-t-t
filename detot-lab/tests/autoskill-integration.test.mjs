import assert from "node:assert/strict";
import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { approveProposal, evaluateProposal } from "../tools/autoskill/pipeline.mjs";
import { CONSENT_REQUIREMENTS } from "../tools/autoskill/provenance.mjs";

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
    async authorizeConsent(stage) {
      return { allowed: true, stage, revision: 1, requirements: CONSENT_REQUIREMENTS[stage] };
    },
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
  const archiveFiles = fs.readdirSync(path.join(paths.sessionArchiveDir, "detot-owner"));
  const archivePath = path.join(paths.sessionArchiveDir, "detot-owner", archiveFiles.find((name) => name.endsWith(".jsonl")));
  const manifest = JSON.parse(fs.readFileSync(`${archivePath}.provenance.json`, "utf8"));
  assert.equal(manifest.provenance.provenance_type, "conversation");
  assert.ok(manifest.content_segments.every((segment) => segment.content_hash && !("content" in segment)));
  assert.ok(!JSON.stringify(manifest).includes("Safely inspect a report"));
  const proposalId = result.jobs[0].proposal_id;
  const proposalFile = path.join(paths.reviewQueueDir, "Users", "detot-owner", `${proposalId}.json`);
  const proposal = JSON.parse(fs.readFileSync(proposalFile, "utf8"));
  assert.equal(proposal.provenance.provenance_type, "conversation");
  assert.ok(proposal.provenance.dependency_ids.includes(manifest.provenance.artifact_id));
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
  const versionSnapshot = JSON.parse(fs.readFileSync(activated.path, "utf8"));
  assert.equal(versionSnapshot.provenance.artifact_type, "skill_version");
  assert.ok(versionSnapshot.provenance.dependency_ids.includes(proposal.provenance.artifact_id));
  assert.match(fs.readFileSync(path.join(root, "state", "workspace", "skills", "autoskill-file-audit", "SKILL.md"), "utf8"), /Copy to staging/);
});

function pathToFileUrl(target) {
  return new URL(`file:///${target.replaceAll("\\", "/")}`);
}
