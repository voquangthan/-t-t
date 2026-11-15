import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { sanitizeSkillCandidate } from "./privacy.mjs";

const ID_RE = /^[a-zA-Z0-9][a-zA-Z0-9._-]{0,95}$/u;
const HERE = path.dirname(fileURLToPath(import.meta.url));
const LAB_ROOT = path.resolve(HERE, "../..");
export const DEFAULT_STAGING_ROOT = path.join(LAB_ROOT, ".local", "autoskill-staging");

function requireId(value, label) {
  const id = String(value ?? "");
  if (!ID_RE.test(id)) throw new Error(`invalid_${label}`);
  return id;
}

function ensureInside(root, target) {
  const resolvedRoot = path.resolve(root);
  const resolvedTarget = path.resolve(target);
  const rel = path.relative(resolvedRoot, resolvedTarget);
  if (!rel || rel === ".") return resolvedTarget;
  if (rel.startsWith(`..${path.sep}`) || rel === ".." || path.isAbsolute(rel)) {
    throw new Error("path_outside_staging_root");
  }
  let current = resolvedTarget;
  while (current !== resolvedRoot) {
    try {
      const stat = fs.lstatSync(current);
      if (stat.isSymbolicLink()) throw new Error("symlink_not_allowed");
    } catch (error) {
      if (error?.code !== "ENOENT") throw error;
    }
    current = path.dirname(current);
  }
  return resolvedTarget;
}

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, "utf8"));
}

function writeJsonAtomic(filePath, value) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  const tempPath = `${filePath}.${process.pid}.${crypto.randomBytes(5).toString("hex")}.tmp`;
  fs.writeFileSync(tempPath, `${JSON.stringify(value, null, 2)}\n`, { flag: "wx" });
  fs.renameSync(tempPath, filePath);
}

function appendAudit(root, ownerId, event) {
  const auditPath = ensureInside(root, path.join(root, "state", "autoskill", "audit", `${ownerId}.jsonl`));
  fs.mkdirSync(path.dirname(auditPath), { recursive: true });
  fs.appendFileSync(auditPath, `${JSON.stringify({ at: new Date().toISOString(), owner_id: ownerId, ...event })}\n`, "utf8");
}

function proposalPath(root, ownerId, proposalId) {
  return ensureInside(root, path.join(root, "state", "autoskill", "proposals", "Users", ownerId, `${proposalId}.json`));
}

function activePath(root, ownerId, skillId) {
  return ensureInside(root, path.join(root, "state", "autoskill", "active", ownerId, `${skillId}.json`));
}

function versionPath(root, ownerId, skillId, version) {
  return ensureInside(root, path.join(root, "state", "autoskill", "SkillBank", "Users", ownerId, skillId, "versions", `${version}.json`));
}

function mirrorPaths(root, skillId) {
  const dir = ensureInside(root, path.join(root, "state", "workspace", "skills", `autoskill-${skillId}`));
  return {
    dir,
    marker: ensureInside(root, path.join(dir, ".detot-autoskill-managed.json")),
    skill: ensureInside(root, path.join(dir, "SKILL.md")),
  };
}

function writeManagedMirror(root, ownerId, skillId, snapshot) {
  const paths = mirrorPaths(root, skillId);
  if (fs.existsSync(paths.dir)) {
    if (!fs.existsSync(paths.marker)) throw new Error("refuse_to_overwrite_unmanaged_skill_directory");
    const marker = readJson(paths.marker);
    if (marker.owner_id !== ownerId || marker.skill_id !== skillId) throw new Error("managed_skill_owner_mismatch");
  } else {
    fs.mkdirSync(paths.dir, { recursive: true });
    writeJsonAtomic(paths.marker, { managed_by: "detot-autoskill-pipeline", owner_id: ownerId, skill_id: skillId });
  }
  const temp = `${paths.skill}.${process.pid}.tmp`;
  fs.writeFileSync(temp, snapshot.skill_markdown, { flag: "wx" });
  fs.renameSync(temp, paths.skill);
}

function assertMirrorWritable(root, ownerId, skillId) {
  const paths = mirrorPaths(root, skillId);
  if (!fs.existsSync(paths.dir)) return;
  if (!fs.existsSync(paths.marker)) throw new Error("refuse_to_overwrite_unmanaged_skill_directory");
  const marker = readJson(paths.marker);
  if (marker.owner_id !== ownerId || marker.skill_id !== skillId) throw new Error("managed_skill_owner_mismatch");
  const active = readActive(root, ownerId, skillId);
  if (!active) {
    if (fs.existsSync(paths.skill)) throw new Error("orphaned_managed_skill_requires_recovery");
    return;
  }
  if (!fs.existsSync(paths.skill) || hash(fs.readFileSync(paths.skill, "utf8")) !== active.snapshot.skill_hash) {
    throw new Error("active_skill_mirror_changed_outside_pipeline");
  }
}

function skillIdFor(candidate) {
  const raw = String(candidate?.skill_id || candidate?.name || "").trim().toLowerCase();
  const id = raw.replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 72);
  if (!ID_RE.test(id)) throw new Error("candidate_missing_valid_skill_name");
  return id;
}

function hash(value) {
  return crypto.createHash("sha256").update(typeof value === "string" ? value : JSON.stringify(value)).digest("hex");
}

function readActive(root, ownerId, skillId) {
  const pointerPath = activePath(root, ownerId, skillId);
  if (!fs.existsSync(pointerPath)) return null;
  const pointer = readJson(pointerPath);
  const snapshotPath = versionPath(root, ownerId, skillId, pointer.version);
  const snapshot = readJson(snapshotPath);
  return { pointer, snapshot, snapshotPath };
}

function nextVersion(current) {
  if (!current) return "1.0.0";
  const match = String(current).match(/^(\d+)\.(\d+)\.(\d+)$/u);
  if (!match) throw new Error("invalid_active_skill_version");
  return `${match[1]}.${match[2]}.${Number(match[3]) + 1}`;
}

function buildSkillMarkdown(candidate, skillId, version) {
  const frontmatterName = `autoskill-${skillId}`;
  const displayName = String(candidate.name || skillId).replace(/[\r\n"']/gu, " ").trim().slice(0, 120);
  const safeDescription = String(candidate.description || "Reusable approved skill.").replace(/[\r\n"']/gu, " ").trim().slice(0, 150);
  const prompt = String(candidate.prompt || "").trim();
  if (!prompt || prompt.length > 12000) throw new Error("invalid_skill_prompt");
  return [
    "---",
    `name: ${frontmatterName}`,
    `description: "${safeDescription}"`,
    "---",
    "",
    `<!-- AutoSkill version ${version} -->`,
    "",
    `# ${displayName}`,
    "",
    prompt,
    "",
  ].join("\n");
}

export function listProposals({ root = DEFAULT_STAGING_ROOT, ownerId = "detot-owner" } = {}) {
  const stagingRoot = path.resolve(root);
  const owner = requireId(ownerId, "owner_id");
  const ownerRoot = ensureInside(stagingRoot, path.join(stagingRoot, "state", "autoskill", "proposals", "Users", owner));
  if (!fs.existsSync(ownerRoot)) return [];
  return fs.readdirSync(ownerRoot).filter((name) => name.endsWith(".json")).sort().map((name) => {
    const proposal = readJson(ensureInside(stagingRoot, path.join(ownerRoot, name)));
    return {
      proposal_id: proposal.proposal_id,
      status: proposal.status,
      version: proposal.version,
      created_at: proposal.created_at,
      skill_name: proposal.candidate?.name || "",
    };
  });
}

export function evaluateProposal({ root = DEFAULT_STAGING_ROOT, ownerId = "detot-owner", proposalId, report }) {
  const stagingRoot = path.resolve(root);
  const owner = requireId(ownerId, "owner_id");
  const id = requireId(proposalId, "proposal_id");
  const filePath = proposalPath(stagingRoot, owner, id);
  const proposal = readJson(filePath);
  if (proposal.owner_id !== owner || proposal.proposal_id !== id) throw new Error("proposal_owner_or_id_mismatch");
  if (!["pending_review", "evaluated"].includes(proposal.status)) throw new Error("proposal_not_evaluable");
  const sanitized = sanitizeSkillCandidate(proposal.candidate);
  if (!sanitized.safe) throw new Error(`unsafe_candidate:${sanitized.reasons.join(",")}`);
  if (!report || typeof report !== "object" || !Array.isArray(report.cases)) throw new Error("invalid_replay_report");
  const cases = report.cases;
  const caseIds = new Set(cases.map((item) => String(item?.case_id || "")));
  if (cases.length < 3 || caseIds.size !== cases.length || caseIds.has("")) throw new Error("replay_requires_three_unique_cases");
  const expectedCandidateHash = hash(sanitized.candidate);
  if (report.candidate_hash !== expectedCandidateHash) throw new Error("replay_candidate_hash_mismatch");
  const isScore = (value) => typeof value === "number" && Number.isFinite(value) && value >= 0 && value <= 1;
  for (const item of cases) {
    if (!isScore(item.candidate_score) || !isScore(item.baseline_score)) throw new Error("invalid_replay_score");
    if (item.safety !== "pass") throw new Error("replay_safety_failed");
    if (item.candidate_score < item.baseline_score) throw new Error("candidate_regresses_case");
  }
  const candidateMean = cases.reduce((sum, item) => sum + item.candidate_score, 0) / cases.length;
  const baselineMean = cases.reduce((sum, item) => sum + item.baseline_score, 0) / cases.length;
  if (!(candidateMean > baselineMean)) throw new Error("candidate_not_better_than_baseline");
  const skillId = skillIdFor(proposal.candidate);
  const active = readActive(stagingRoot, owner, skillId);
  const currentVersion = active?.pointer.version || null;
  const currentHash = active?.snapshot.skill_hash || "";
  if (report.baseline_version !== currentVersion || report.baseline_hash !== currentHash) {
    throw new Error("replay_baseline_is_stale");
  }
  const evaluation = {
    replay_id: String(report.replay_id || crypto.randomUUID()),
    runner: String(report.runner || "external-replay-runner"),
    case_count: cases.length,
    candidate_mean: Number(candidateMean.toFixed(4)),
    baseline_mean: Number(baselineMean.toFixed(4)),
    candidate_hash: expectedCandidateHash,
    baseline_version: currentVersion,
    baseline_hash: currentHash,
    safety: "pass",
    outcome: "pass",
    evaluated_at: new Date().toISOString(),
  };
  proposal.status = "evaluated";
  proposal.evaluation = evaluation;
  proposal.history = [...(Array.isArray(proposal.history) ? proposal.history : []), { action: "evaluated", ...evaluation }];
  writeJsonAtomic(filePath, proposal);
  appendAudit(stagingRoot, owner, { action: "proposal_evaluated", proposal_id: id, skill_id: skillId, replay_id: evaluation.replay_id, outcome: "pass", case_count: cases.length });
  return evaluation;
}

export function approveProposal({ root = DEFAULT_STAGING_ROOT, ownerId = "detot-owner", proposalId }) {
  const stagingRoot = path.resolve(root);
  const owner = requireId(ownerId, "owner_id");
  const id = requireId(proposalId, "proposal_id");
  const filePath = proposalPath(stagingRoot, owner, id);
  const proposal = readJson(filePath);
  if (proposal.owner_id !== owner || proposal.proposal_id !== id) throw new Error("proposal_owner_or_id_mismatch");
  if (proposal.status !== "evaluated" || proposal.evaluation?.outcome !== "pass") throw new Error("passing_replay_required");
  const safe = sanitizeSkillCandidate(proposal.candidate);
  if (!safe.safe || hash(safe.candidate) !== proposal.evaluation.candidate_hash) throw new Error("candidate_changed_after_replay");
  const skillId = skillIdFor(safe.candidate);
  const active = readActive(stagingRoot, owner, skillId);
  const currentVersion = active?.pointer.version || null;
  const currentHash = active?.snapshot.skill_hash || "";
  if (currentVersion !== proposal.evaluation.baseline_version || currentHash !== proposal.evaluation.baseline_hash) {
    throw new Error("active_skill_changed_after_replay");
  }
  const version = nextVersion(currentVersion);
  assertMirrorWritable(stagingRoot, owner, skillId);
  const markdown = buildSkillMarkdown(safe.candidate, skillId, version);
  const snapshot = {
    owner_id: owner,
    skill_id: skillId,
    version,
    skill_hash: hash(markdown),
    skill_markdown: markdown,
    source: proposal.source || "autoskill-openclaw-adapter",
    source_proposal_id: id,
    evidence: proposal.evidence || {},
    evaluation: proposal.evaluation,
    activated_at: new Date().toISOString(),
  };
  const snapshotFile = versionPath(stagingRoot, owner, skillId, version);
  if (fs.existsSync(snapshotFile)) throw new Error("skill_version_already_exists");
  writeJsonAtomic(snapshotFile, snapshot);
  writeJsonAtomic(activePath(stagingRoot, owner, skillId), { owner_id: owner, skill_id: skillId, version, skill_hash: snapshot.skill_hash, updated_at: snapshot.activated_at });
  writeManagedMirror(stagingRoot, owner, skillId, snapshot);
  proposal.status = "approved";
  proposal.history = [...proposal.history, { action: "approved_and_activated", version, at: snapshot.activated_at }];
  writeJsonAtomic(filePath, proposal);
  appendAudit(stagingRoot, owner, { action: "skill_activated", proposal_id: id, skill_id: skillId, version, skill_hash: snapshot.skill_hash });
  return { skill_id: skillId, version, skill_hash: snapshot.skill_hash, path: snapshotFile };
}

export function rejectProposal({ root = DEFAULT_STAGING_ROOT, ownerId = "detot-owner", proposalId, reason = "review_rejected" }) {
  const stagingRoot = path.resolve(root);
  const owner = requireId(ownerId, "owner_id");
  const id = requireId(proposalId, "proposal_id");
  const filePath = proposalPath(stagingRoot, owner, id);
  const proposal = readJson(filePath);
  if (proposal.owner_id !== owner || proposal.proposal_id !== id) throw new Error("proposal_owner_or_id_mismatch");
  if (["approved", "rejected"].includes(proposal.status)) throw new Error("proposal_already_finalized");
  const at = new Date().toISOString();
  const safeReason = ["review_rejected", "duplicate", "low_quality", "privacy_risk", "safety_failure", "obsolete"].includes(String(reason))
    ? String(reason)
    : "review_rejected";
  proposal.status = "rejected";
  proposal.history = [...(Array.isArray(proposal.history) ? proposal.history : []), { action: "rejected", reason: safeReason, at }];
  writeJsonAtomic(filePath, proposal);
  appendAudit(stagingRoot, owner, { action: "proposal_rejected", proposal_id: id, reason: safeReason });
  return { proposal_id: id, status: proposal.status };
}

export function rollbackSkill({ root = DEFAULT_STAGING_ROOT, ownerId = "detot-owner", skillId, version }) {
  const stagingRoot = path.resolve(root);
  const owner = requireId(ownerId, "owner_id");
  const skill = requireId(skillId, "skill_id");
  const targetVersion = requireId(version, "version");
  const target = readJson(versionPath(stagingRoot, owner, skill, targetVersion));
  if (target.owner_id !== owner || target.skill_id !== skill || target.version !== targetVersion) throw new Error("rollback_target_mismatch");
  assertMirrorWritable(stagingRoot, owner, skill);
  const at = new Date().toISOString();
  writeJsonAtomic(activePath(stagingRoot, owner, skill), { owner_id: owner, skill_id: skill, version: targetVersion, skill_hash: target.skill_hash, updated_at: at, rollback: true });
  writeManagedMirror(stagingRoot, owner, skill, target);
  appendAudit(stagingRoot, owner, { action: "skill_rollback", skill_id: skill, version: targetVersion, skill_hash: target.skill_hash });
  return { skill_id: skill, version: targetVersion, skill_hash: target.skill_hash, rolled_back: true };
}

function parseArgs(args) {
  const out = { _: [] };
  for (let i = 0; i < args.length; i += 1) {
    const item = args[i];
    if (!item.startsWith("--")) { out._.push(item); continue; }
    const key = item.slice(2);
    out[key] = args[++i];
  }
  return out;
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const root = path.resolve(args.root || DEFAULT_STAGING_ROOT);
  if (root !== path.resolve(DEFAULT_STAGING_ROOT)) throw new Error("cli_is_restricted_to_default_staging_root");
  const ownerId = args.owner || "detot-owner";
  const command = args._[0];
  let result;
  if (command === "list") result = listProposals({ root, ownerId });
  else if (command === "evaluate") result = evaluateProposal({ root, ownerId, proposalId: args.proposal, report: readJson(path.resolve(args.report)) });
  else if (command === "approve") result = approveProposal({ root, ownerId, proposalId: args.proposal });
  else if (command === "reject") result = rejectProposal({ root, ownerId, proposalId: args.proposal, reason: args.reason || "review_rejected" });
  else if (command === "rollback") result = rollbackSkill({ root, ownerId, skillId: args.skill, version: args.version });
  else throw new Error("usage: node pipeline.mjs <list|evaluate|approve|reject|rollback> [options]");
  process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  });
}
