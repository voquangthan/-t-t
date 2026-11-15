import assert from "node:assert/strict";
import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { approveProposal, evaluateProposal, rejectProposal, rollbackSkill } from "../tools/autoskill/pipeline.mjs";
import { sanitizeExperienceText, sanitizeSkillCandidate } from "../tools/autoskill/privacy.mjs";

function makeStage() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "detot-autoskill-stage-"));
  return { root, owner: "detot-owner" };
}

function addProposal(stage, { proposalId, skillId, prompt, name = skillId }) {
  const ownerRoot = path.join(stage.root, "state", "autoskill", "proposals", "Users", stage.owner);
  fs.mkdirSync(ownerRoot, { recursive: true });
  const proposal = {
    proposal_id: proposalId,
    owner_id: stage.owner,
    status: "pending_review",
    version: "0.1.0",
    created_at: new Date().toISOString(),
    source: "autoskill-openclaw-adapter",
    evidence: { session_id: `session-${proposalId}`, turn_count: 3, successful_main_turn: true, source_hash: "evidence-hash" },
    candidate: { skill_id: skillId, name, description: "Validated file workflow", prompt, files: {} },
    history: [{ action: "proposed", version: "0.1.0" }],
  };
  fs.writeFileSync(path.join(ownerRoot, `${proposalId}.json`), JSON.stringify(proposal));
}

function replayReport(stage, proposalId, candidateScores, baselineScores) {
  const skillId = "safe-file-workflow";
  const proposal = JSON.parse(fs.readFileSync(path.join(stage.root, "state", "autoskill", "proposals", "Users", stage.owner, `${proposalId}.json`), "utf8"));
  const activePath = path.join(stage.root, "state", "autoskill", "active", stage.owner, `${skillId}.json`);
  const active = fs.existsSync(activePath) ? JSON.parse(fs.readFileSync(activePath, "utf8")) : null;
  return {
    replay_id: `replay-${proposalId}`,
    runner: "fixture-replay-runner",
    candidate_hash: crypto.createHash("sha256").update(JSON.stringify(proposal.candidate)).digest("hex"),
    baseline_version: active?.version || null,
    baseline_hash: active?.skill_hash || "",
    cases: candidateScores.map((candidate_score, index) => ({
      case_id: `case-${index + 1}`,
      candidate_score,
      baseline_score: baselineScores[index],
      safety: "pass",
    })),
  };
}

test("new successful procedure requires three passing replay cases and explicit approval", () => {
  const stage = makeStage();
  addProposal(stage, { proposalId: "proposal-success", skillId: "safe-file-workflow", prompt: "Inspect a copy, validate the result, and report the path." });
  const report = replayReport(stage, "proposal-success", [0.9, 0.8, 1], [0, 0, 0]);
  const evaluated = evaluateProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-success", report });
  assert.equal(evaluated.outcome, "pass");
  assert.throws(() => approveProposal({ root: stage.root, ownerId: "other-owner", proposalId: "proposal-success" }), /ENOENT|no such file/i);
  const approved = approveProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-success" });
  assert.equal(approved.version, "1.0.0");
  const skillFile = path.join(stage.root, "state", "workspace", "skills", "autoskill-safe-file-workflow", "SKILL.md");
  assert.match(fs.readFileSync(skillFile, "utf8"), /name: autoskill-safe-file-workflow/);
  assert.match(fs.readFileSync(skillFile, "utf8"), /AutoSkill version 1\.0\.0/);
});

test("failed then corrected procedure can be proposed as a new reviewed version", () => {
  const stage = makeStage();
  addProposal(stage, { proposalId: "proposal-v1", skillId: "safe-file-workflow", prompt: "Inspect the source, copy to staging, verify output." });
  evaluateProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-v1", report: replayReport(stage, "proposal-v1", [0.8, 0.8, 0.8], [0, 0, 0]) });
  approveProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-v1" });

  addProposal(stage, { proposalId: "proposal-v2", skillId: "safe-file-workflow", prompt: "Inspect the source, copy to staging, compare checksums, and verify output." });
  evaluateProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-v2", report: replayReport(stage, "proposal-v2", [1, 0.9, 1], [0.8, 0.8, 0.8]) });
  assert.equal(approveProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-v2" }).version, "1.0.1");
  assert.equal(rollbackSkill({ root: stage.root, ownerId: stage.owner, skillId: "safe-file-workflow", version: "1.0.0" }).rolled_back, true);
  const active = JSON.parse(fs.readFileSync(path.join(stage.root, "state", "autoskill", "active", stage.owner, "safe-file-workflow.json"), "utf8"));
  assert.equal(active.version, "1.0.0");
});

test("worse replay result is rejected and preserves the current active version", () => {
  const stage = makeStage();
  addProposal(stage, { proposalId: "proposal-good", skillId: "safe-file-workflow", prompt: "Verify a staging copy before reporting completion." });
  evaluateProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-good", report: replayReport(stage, "proposal-good", [0.9, 0.9, 0.9], [0, 0, 0]) });
  approveProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-good" });
  const pointerPath = path.join(stage.root, "state", "autoskill", "active", stage.owner, "safe-file-workflow.json");
  const before = fs.readFileSync(pointerPath, "utf8");

  addProposal(stage, { proposalId: "proposal-weaker", skillId: "safe-file-workflow", prompt: "Overwrite the source file directly." });
  assert.throws(() => evaluateProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-weaker", report: replayReport(stage, "proposal-weaker", [0.4, 0.6, 0.5], [0.9, 0.9, 0.9]) }), /candidate_regresses_case/);
  assert.equal(fs.readFileSync(pointerPath, "utf8"), before);
  assert.throws(() => approveProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-weaker" }), /passing_replay_required/);
});

test("privacy gate omits sensitive sessions and rejects credentials or executable skill files", () => {
  assert.equal(sanitizeExperienceText("My bank account is 1234567890123456"), "[PRIVATE_SESSION_OMITTED]");
  assert.equal(sanitizeExperienceText("api_key: sk-abcdefghijklmnopqrstuvwxyz123456"), "[REDACTED_CREDENTIAL]");
  assert.equal(sanitizeSkillCandidate({ name: "Routine", prompt: "Use token=secret-value to log in" }).safe, false);
  assert.equal(sanitizeSkillCandidate({ name: "Routine", prompt: "Check the result", files: { "scripts/run.ps1": "Remove-Item *" } }).safe, false);
});

test("a report with missing cases or safety failure cannot approve a proposal", () => {
  const stage = makeStage();
  addProposal(stage, { proposalId: "proposal-safety", skillId: "safe-file-workflow", prompt: "Check before changing files." });
  const tooFew = replayReport(stage, "proposal-safety", [1, 1], [0, 0]);
  assert.throws(() => evaluateProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-safety", report: tooFew }), /three_unique_cases/);
  const safetyFail = replayReport(stage, "proposal-safety", [1, 1, 1], [0, 0, 0]);
  safetyFail.cases[2].safety = "fail";
  assert.throws(() => evaluateProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-safety", report: safetyFail }), /replay_safety_failed/);
  assert.throws(() => approveProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-safety" }), /passing_replay_required/);
});

test("approval refuses a candidate changed after replay and refuses unmanaged skill collisions", () => {
  const stage = makeStage();
  addProposal(stage, { proposalId: "proposal-tamper", skillId: "safe-file-workflow", prompt: "Validate a staging copy." });
  evaluateProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-tamper", report: replayReport(stage, "proposal-tamper", [1, 1, 1], [0, 0, 0]) });
  const proposalPath = path.join(stage.root, "state", "autoskill", "proposals", "Users", stage.owner, "proposal-tamper.json");
  const proposal = JSON.parse(fs.readFileSync(proposalPath, "utf8"));
  proposal.candidate.prompt = "Changed after replay.";
  fs.writeFileSync(proposalPath, JSON.stringify(proposal));
  assert.throws(() => approveProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-tamper" }), /candidate_changed_after_replay/);

  const collision = makeStage();
  addProposal(collision, { proposalId: "proposal-collision", skillId: "safe-file-workflow", prompt: "Validate a staging copy." });
  evaluateProposal({ root: collision.root, ownerId: collision.owner, proposalId: "proposal-collision", report: replayReport(collision, "proposal-collision", [1, 1, 1], [0, 0, 0]) });
  const userSkillDir = path.join(collision.root, "state", "workspace", "skills", "autoskill-safe-file-workflow");
  fs.mkdirSync(userSkillDir, { recursive: true });
  const userSkill = path.join(userSkillDir, "SKILL.md");
  fs.writeFileSync(userSkill, "user-owned skill\n");
  assert.throws(() => approveProposal({ root: collision.root, ownerId: collision.owner, proposalId: "proposal-collision" }), /refuse_to_overwrite_unmanaged_skill_directory/);
  assert.equal(fs.readFileSync(userSkill, "utf8"), "user-owned skill\n");
  assert.equal(fs.existsSync(path.join(collision.root, "state", "autoskill", "active", collision.owner, "safe-file-workflow.json")), false);
});

test("rejection is auditable without exposing candidate text", () => {
  const stage = makeStage();
  addProposal(stage, { proposalId: "proposal-reject", skillId: "safe-file-workflow", prompt: "Sensitive text never belongs in audit." });
  rejectProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-reject", reason: "review rejected" });
  const audit = fs.readFileSync(path.join(stage.root, "state", "autoskill", "audit", `${stage.owner}.jsonl`), "utf8");
  assert.match(audit, /proposal_rejected/);
  assert.doesNotMatch(audit, /Sensitive text never belongs/);
});
