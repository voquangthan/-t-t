import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const LAB_ROOT = path.resolve(HERE, "../..");
const DEFAULT_CONFIG = path.join(HERE, "config.example.json");
const ID_RE = /^[a-zA-Z0-9][a-zA-Z0-9._-]{0,80}$/u;
const SHA_RE = /^[a-f0-9]{40}$/u;
const REVIEW_ID_RE = /^(?:issue|review):\d+$/u;
const FINAL_STATUSES = new Set(["PASS", "BLOCKED", "REVIEW_CHANNEL_UNAVAILABLE", "TIMEOUT", "REVISE_LIMIT"]);
const VERDICTS = new Set(["PASS", "REVISE", "BLOCKED"]);
const ALLOWED_REVIEW_PERMISSIONS = new Set(["triage", "write", "maintain", "admin"]);
const ALLOWED_AUTHOR_ASSOCIATIONS = new Set(["OWNER", "COLLABORATOR", "MEMBER", "CONTRIBUTOR"]);
const GATE_NAME_RE = /^[a-zA-Z][a-zA-Z0-9._-]{0,63}$/u;
const EVIDENCE_HASH_RE = /^[a-f0-9]{64}$/u;
const QUALITY_GATE_COMMANDS = Object.freeze({
  "review-feedback-tests": { command: process.execPath, args: ["--test", "tests/review-feedback/listener.test.mjs"] },
});

function safeError(code) { return new Error(code); }
function boundedText(value, limit = 8000) { return String(value ?? "").slice(0, limit); }
function hashText(value) { return crypto.createHash("sha256").update(String(value)).digest("hex"); }

function atomicWrite(filePath, value) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  const temp = `${filePath}.${process.pid}.${crypto.randomBytes(6).toString("hex")}.tmp`;
  fs.writeFileSync(temp, `${JSON.stringify(value, null, 2)}\n`, { flag: "wx", mode: 0o600 });
  fs.renameSync(temp, filePath);
}

async function withTaskLock(lockPath, action) {
  fs.mkdirSync(path.dirname(lockPath), { recursive: true });
  for (let attempt = 0; attempt < 2; attempt += 1) {
    try {
      const fd = fs.openSync(lockPath, "wx", 0o600);
      fs.writeFileSync(fd, JSON.stringify({ pid: process.pid, started_at: new Date().toISOString() }));
      try { return await action(); }
      finally { fs.closeSync(fd); fs.unlinkSync(lockPath); }
    } catch (error) {
      if (error?.code !== "EEXIST") throw error;
      let holder;
      try { holder = JSON.parse(fs.readFileSync(lockPath, "utf8")); }
      catch { throw safeError("task_lock_invalid; manual_recovery_required"); }
      if (!Number.isSafeInteger(holder.pid) || holder.pid < 1) throw safeError("task_lock_invalid; manual_recovery_required");
      let alive = true;
      try { process.kill(holder.pid, 0); } catch (probeError) { if (probeError?.code === "ESRCH") alive = false; else throw safeError("task_lock_owner_unverifiable; manual_recovery_required"); }
      if (alive) throw safeError("task_already_being_processed");
      fs.unlinkSync(lockPath);
    }
  }
  throw safeError("task_lock_unavailable");
}

function readCredentialFromGit() {
  const envToken = process.env.DETOT_GITHUB_TOKEN || process.env.GITHUB_TOKEN;
  if (envToken) return envToken.trim();
  const result = spawnSync("git", ["credential", "fill"], {
    input: "protocol=https\nhost=github.com\n\n",
    encoding: "utf8",
    timeout: 5000,
    maxBuffer: 4096,
    env: { ...process.env, GIT_TERMINAL_PROMPT: "0" },
    windowsHide: true,
  });
  if (result.error || result.status !== 0) return "";
  const password = result.stdout.split(/\r?\n/u).find((line) => line.startsWith("password="));
  return password?.slice("password=".length).trim() || "";
}

function parseVerdict(body, commitId = "") {
  const text = boundedText(body, 12000);
  const marker = /^DETOT-REVIEW\/1\s*$/gmu;
  const verdicts = [...text.matchAll(/^VERDICT:\s*(PASS|REVISE|BLOCKED)\s*$/gmu)];
  const hashes = [...text.matchAll(/^COMMIT_SHA:\s*([a-f0-9]{40})\s*$/gmu)];
  if ([...text.matchAll(marker)].length !== 1 || verdicts.length !== 1 || hashes.length !== 1) return null;
  const verdict = verdicts[0][1];
  const sha = hashes[0][1];
  if (!VERDICTS.has(verdict) || (commitId && commitId !== sha)) return null;
  const checkpoint = text.match(/^CHECKPOINT:\s*(.{1,300})\s*$/mu)?.[1]?.trim() || "";
  return { verdict, commitSha: sha, checkpoint };
}

function safeConfig(config) {
  const repo = String(config?.repository || "");
  const reviewer = String(config?.reviewer_login || "").toLowerCase();
  const reviewerId = config?.reviewer_id == null || config.reviewer_id === "" ? null : Number(config.reviewer_id);
  const reviewerType = String(config?.reviewer_type || "");
  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/u.test(repo)) throw safeError("invalid_repository_config");
  if (reviewer && !/^[A-Za-z0-9-]+(?:\[bot\])?$/u.test(reviewer)) throw safeError("invalid_reviewer_login");
  if (reviewer && (!Number.isSafeInteger(reviewerId) || reviewerId < 1 || !["User", "Bot"].includes(reviewerType))) throw safeError("reviewer_user_id_and_type_required");
  return { repository: repo, reviewerLogin: reviewer, reviewerId, reviewerType, baseBranch: config.base_branch || "main", pollSeconds: Math.min(120, Math.max(5, Number(config.poll_seconds || 20))), timeoutMinutes: Math.min(1440, Math.max(1, Number(config.timeout_minutes || 30))) };
}

function requiredGateNames(value) {
  const names = Array.isArray(value) ? value : value && Array.isArray(value.required) ? value.required : null;
  if (!names || names.length === 0 || names.some((name) => typeof name !== "string" || !GATE_NAME_RE.test(name) || !QUALITY_GATE_COMMANDS[name]) || new Set(names).size !== names.length) {
    throw safeError("quality_gates_must_be_unique_named_list");
  }
  return [...names];
}

export class ReviewFeedbackListener {
  constructor({ repoRoot = LAB_ROOT, dataRoot, config, fetchImpl = globalThis.fetch, gitHead, now = () => new Date(), tokenProvider = readCredentialFromGit, qualityGateRunner }) {
    this.repoRoot = path.resolve(repoRoot);
    this.dataRoot = path.resolve(dataRoot || path.join(this.repoRoot, ".local", "review-loop"));
    this.config = safeConfig(config);
    this.fetchImpl = fetchImpl;
    this.gitHead = gitHead || (() => {
      const result = spawnSync("git", ["-C", this.repoRoot, "rev-parse", "HEAD"], { encoding: "utf8", timeout: 5000, windowsHide: true });
      if (result.status !== 0 || !SHA_RE.test(result.stdout.trim())) throw safeError("local_git_head_unavailable");
      return result.stdout.trim();
    });
    this.now = now;
    this.tokenProvider = tokenProvider;
    this.qualityGateRunner = qualityGateRunner || ((gate) => {
      const spec = QUALITY_GATE_COMMANDS[gate];
      if (!spec) throw safeError("quality_gate_not_supported");
      const result = spawnSync(spec.command, spec.args, { cwd: this.repoRoot, encoding: "utf8", timeout: 180_000, maxBuffer: 2 * 1024 * 1024, windowsHide: true, env: { ...process.env, GIT_TERMINAL_PROMPT: "0" } });
      return { command: [spec.command, ...spec.args].join(" "), exitCode: result.status ?? 1, output: `${result.stdout || ""}\n${result.stderr || ""}`.trim() || result.error?.message || "no_output" };
    });
  }

  taskPath(taskId) {
    if (!ID_RE.test(String(taskId))) throw safeError("invalid_task_id");
    return path.join(this.dataRoot, "state", `${taskId}.json`);
  }
  auditPath(taskId) { return path.join(this.dataRoot, "audit", `${taskId}.jsonl`); }
  lockPath(taskId) { return path.join(this.dataRoot, "locks", `${taskId}.lock`); }
  readState(taskId) { return JSON.parse(fs.readFileSync(this.taskPath(taskId), "utf8")); }

  initialize({ taskId, stageId, branch, prNumber, commitSha, qualityGates = {}, timeoutMinutes = this.config.timeoutMinutes }) {
    return withTaskLock(this.lockPath(taskId), () => {
      if (!ID_RE.test(String(stageId || "")) || !String(branch).startsWith("codex/")) throw safeError("invalid_stage_or_branch");
      if (!Number.isSafeInteger(prNumber) || prNumber < 1 || !SHA_RE.test(String(commitSha))) throw safeError("invalid_pr_or_commit_sha");
      if (this.gitHead() !== commitSha) throw safeError("commit_sha_does_not_match_local_head");
      const requiredGates = requiredGateNames(qualityGates);
      const taskPath = this.taskPath(taskId);
      if (fs.existsSync(taskPath)) {
        const existing = this.readState(taskId);
        const sameTask = existing.task_id === taskId && existing.stage_id === stageId && existing.branch === branch && existing.pr_number === prNumber;
        if (!sameTask) throw safeError("task_id_already_registered_for_different_task");
        return existing;
      }
      const createdAt = this.now();
      const state = {
        schema_version: 1,
        task_id: taskId,
        stage_id: stageId,
        branch,
        pr_number: prNumber,
        commit_sha: commitSha,
        reviewer_login: this.config.reviewerLogin,
        reviewer_id: this.config.reviewerId,
        reviewer_type: this.config.reviewerType || null,
        review_status: "PENDING",
        review_attempts: 0,
        last_processed_review_id: null,
        processed_review_ids: [],
        checkpoint: { status: "waiting_for_review", created_at: createdAt.toISOString(), expires_at: new Date(createdAt.getTime() + timeoutMinutes * 60_000).toISOString() },
        required_quality_gates: requiredGates,
        quality_gate_evidence: {},
        stage_advance_allowed: false,
        last_feedback: null,
      };
      atomicWrite(this.taskPath(taskId), state);
      this.audit(taskId, { event: "task_registered", stage_id: stageId, pr_number: prNumber, commit_sha: commitSha });
      return state;
    });
  }

  async runQualityGate(taskId, gate) {
    const state = this.readState(taskId);
    if (!state.required_quality_gates.includes(gate) || !QUALITY_GATE_COMMANDS[gate]) throw safeError("quality_gate_not_required");
    const commitSha = this.gitHead();
    if (commitSha !== state.commit_sha) throw safeError("quality_gate_sha_or_state_mismatch");
    const result = await this.qualityGateRunner(gate);
    return this.#recordQualityGate(taskId, { gate, commitSha, command: result.command, exitCode: result.exitCode, evidenceText: result.output });
  }

  #recordQualityGate(taskId, { gate, commitSha, command, exitCode, evidenceText }) {
    return withTaskLock(this.lockPath(taskId), () => {
      const state = this.readState(taskId);
      if (!state.required_quality_gates.includes(gate)) throw safeError("quality_gate_not_required");
      if (!["PENDING", "PASS_PENDING_GATES"].includes(state.review_status) || commitSha !== state.commit_sha || commitSha !== this.gitHead()) throw safeError("quality_gate_sha_or_state_mismatch");
      if (!Number.isInteger(exitCode) || exitCode !== 0 || !String(command || "").trim() || !String(evidenceText || "").trim()) throw safeError("quality_gate_evidence_incomplete");
      const evidence = {
        commit_sha: commitSha,
        command: boundedText(command, 500),
        exit_code: exitCode,
        output_sha256: hashText(evidenceText),
        output_bytes: Buffer.byteLength(evidenceText),
        recorded_at: this.now().toISOString(),
      };
      state.quality_gate_evidence[gate] = evidence;
      if (state.last_feedback?.verdict === "PASS" && this.qualityGatesPass(state, state.commit_sha)) {
        state.review_status = "PASS";
        state.stage_advance_allowed = true;
        state.checkpoint = { ...state.checkpoint, status: "review_and_quality_gates_passed", updated_at: this.now().toISOString() };
      }
      atomicWrite(this.taskPath(taskId), state);
      this.audit(taskId, { event: "quality_gate_recorded", gate, commit_sha: commitSha, output_sha256: evidence.output_sha256 });
      return state;
    });
  }

  qualityGatesPass(state, sha = state.commit_sha) {
    return state.required_quality_gates.length > 0 && state.required_quality_gates.every((gate) => {
      const evidence = state.quality_gate_evidence?.[gate];
      return evidence?.commit_sha === sha && evidence.exit_code === 0 && EVIDENCE_HASH_RE.test(evidence.output_sha256 || "") && evidence.output_bytes > 0;
    });
  }

  updateHead(taskId, commitSha) {
    return withTaskLock(this.lockPath(taskId), async () => {
      const state = this.readState(taskId);
      if (state.review_attempts >= 3) throw safeError("revision_limit_reached");
      if (!["REVISE", "PENDING"].includes(state.review_status)) throw safeError("task_not_waiting_for_revision");
      if (!SHA_RE.test(String(commitSha)) || commitSha !== this.gitHead()) throw safeError("new_head_does_not_match_local_head");
      const token = this.tokenProvider();
      if (!token) throw safeError("github_authentication_unavailable");
      const pull = await this.api(`/repos/${this.config.repository}/pulls/${state.pr_number}`, token);
      if (pull?.base?.ref !== this.config.baseBranch || pull?.head?.ref !== state.branch || pull?.head?.sha !== commitSha) {
        throw safeError("new_head_not_verified_on_remote");
      }
      state.review_attempts += 1;
      state.commit_sha = commitSha;
      state.quality_gate_evidence = {};
      state.review_status = "PENDING";
      state.stage_advance_allowed = false;
      state.checkpoint = { ...state.checkpoint, status: "revision_pushed", updated_at: this.now().toISOString() };
      state.last_feedback = null;
      atomicWrite(this.taskPath(taskId), state);
      this.audit(taskId, { event: "revision_checkpoint", attempt: state.review_attempts, commit_sha: commitSha });
      return state;
    });
  }

  async api(pathname, token) {
    const response = await this.fetchImpl(`https://api.github.com${pathname}`, {
      headers: { Accept: "application/vnd.github+json", Authorization: `Bearer ${token}`, "X-GitHub-Api-Version": "2022-11-28" },
      signal: AbortSignal.timeout(15000),
    });
    if (!response.ok) throw safeError(response.status === 401 || response.status === 403 ? `github_permission_unavailable_${response.status}` : `github_api_error_${response.status}`);
    return response.json();
  }

  async reviewEvents(state, token) {
    const [comments, reviews] = await Promise.all([
      this.apiPages(`/repos/${this.config.repository}/issues/${state.pr_number}/comments?per_page=100&sort=created&direction=desc`, token),
      this.apiPages(`/repos/${this.config.repository}/pulls/${state.pr_number}/reviews?per_page=100`, token),
    ]);
    if (!Array.isArray(comments) || !Array.isArray(reviews)) throw safeError("github_review_response_invalid");
    return [
      ...comments.map((item) => ({ id: `issue:${item.id}`, login: item.user?.login, userId: item.user?.id, userType: item.user?.type, authorAssociation: item.author_association, body: item.body, createdAt: item.created_at, commitId: "", source: "issue_comment" })),
      ...reviews.map((item) => ({ id: `review:${item.id}`, login: item.user?.login, userId: item.user?.id, userType: item.user?.type, authorAssociation: item.author_association, body: item.body, createdAt: item.submitted_at, commitId: item.commit_id || "", source: "pull_request_review" })),
    ].filter((item) => item.id !== "issue:undefined" && item.id !== "review:undefined")
      .sort((a, b) => Date.parse(a.createdAt || 0) - Date.parse(b.createdAt || 0) || a.id.localeCompare(b.id));
  }

  async apiPages(initialPath, token, maxPages = 10) {
    const items = [];
    let next = initialPath;
    for (let page = 0; next && page < maxPages; page += 1) {
      const response = await this.fetchImpl(`https://api.github.com${next}`, {
        headers: { Accept: "application/vnd.github+json", Authorization: `Bearer ${token}`, "X-GitHub-Api-Version": "2022-11-28" },
        signal: AbortSignal.timeout(15000),
      });
      if (!response.ok) throw safeError(response.status === 401 || response.status === 403 ? `github_permission_unavailable_${response.status}` : `github_api_error_${response.status}`);
      const data = await response.json();
      if (!Array.isArray(data)) throw safeError("github_review_response_invalid");
      items.push(...data);
      const link = response.headers?.get?.("link") || "";
      const match = link.split(",").map((item) => item.match(/<([^>]+)>;\s*rel="next"/u)).find(Boolean);
      if (!match) break;
      const url = new URL(match[1]);
      if (url.origin !== "https://api.github.com") throw safeError("github_pagination_origin_invalid");
      next = `${url.pathname}${url.search}`;
    }
    return items;
  }

  audit(taskId, event) {
    fs.mkdirSync(path.dirname(this.auditPath(taskId)), { recursive: true });
    const safeEvent = { at: this.now().toISOString(), task_id: taskId, ...event };
    fs.appendFileSync(this.auditPath(taskId), `${JSON.stringify(safeEvent)}\n`, { mode: 0o600 });
  }

  async pollOnce(taskId) {
    return withTaskLock(this.lockPath(taskId), () => this.pollLocked(taskId));
  }

  async pollLocked(taskId) {
    const state = this.readState(taskId);
    if (FINAL_STATUSES.has(state.review_status) && state.review_status !== "PASS") return state;
    const now = this.now();
    if (now.getTime() >= Date.parse(state.checkpoint.expires_at)) {
      const noReviewerResponse = state.last_processed_review_id === null;
      state.review_status = noReviewerResponse ? "REVIEW_CHANNEL_UNAVAILABLE" : "TIMEOUT";
      state.checkpoint = {
        ...state.checkpoint,
        status: noReviewerResponse ? "review_channel_unavailable" : "timeout",
        reason: noReviewerResponse ? "no_reviewer_comment_before_deadline" : "review_deadline_expired",
        stopped_at: now.toISOString(),
      };
      atomicWrite(this.taskPath(taskId), state);
      this.audit(taskId, { event: noReviewerResponse ? "review_channel_unavailable" : "review_timeout", reason: state.checkpoint.reason, commit_sha: state.commit_sha });
      return state;
    }

    if (!this.config.reviewerLogin || !this.config.reviewerId || !this.config.reviewerType) return this.unavailable(taskId, state, "configured_reviewer_identity_unverified");

    const token = this.tokenProvider();
    if (!token) return this.unavailable(taskId, state, "github_authentication_unavailable");
    try {
      const user = await this.api("/user", token);
      if (!user?.login) return this.unavailable(taskId, state, "github_identity_unavailable");
      const reviewer = await this.api(`/users/${encodeURIComponent(this.config.reviewerLogin)}`, token);
      if (String(reviewer?.login || "").toLowerCase() !== this.config.reviewerLogin || Number(reviewer?.id) !== this.config.reviewerId || reviewer?.type !== this.config.reviewerType) {
        return this.unavailable(taskId, state, "configured_reviewer_profile_mismatch");
      }
      const permission = await this.api(`/repos/${this.config.repository}/collaborators/${encodeURIComponent(this.config.reviewerLogin)}/permission`, token);
      if (!ALLOWED_REVIEW_PERMISSIONS.has(permission?.permission)) return this.unavailable(taskId, state, "reviewer_repository_permission_insufficient");
      const pull = await this.api(`/repos/${this.config.repository}/pulls/${state.pr_number}`, token);
      if (pull?.base?.ref !== this.config.baseBranch || pull?.head?.ref !== state.branch || !String(state.branch).startsWith("codex/")) {
        return this.unavailable(taskId, state, "pull_request_target_or_branch_mismatch");
      }
      if (pull.head.sha !== state.commit_sha || this.gitHead() !== state.commit_sha) {
        if (state.review_status === "PASS") {
          state.review_status = "PENDING";
          state.stage_advance_allowed = false;
          state.quality_gate_evidence = {};
          state.last_feedback = null;
          state.checkpoint = { ...state.checkpoint, status: "head_changed_review_invalidated", updated_at: this.now().toISOString() };
          atomicWrite(this.taskPath(taskId), state);
          this.audit(taskId, { event: "head_changed_review_invalidated", commit_sha: state.commit_sha, remote_head_sha: pull.head.sha || null });
          return state;
        }
        return this.unavailable(taskId, state, "pull_request_head_sha_mismatch");
      }
      const events = await this.reviewEvents(state, token);
      const latestPull = await this.api(`/repos/${this.config.repository}/pulls/${state.pr_number}`, token);
      if (latestPull?.head?.sha !== state.commit_sha || this.gitHead() !== state.commit_sha) {
        state.review_status = "PENDING";
        state.stage_advance_allowed = false;
        state.quality_gate_evidence = {};
        state.last_feedback = null;
        state.checkpoint = { ...state.checkpoint, status: "head_changed_review_invalidated", updated_at: this.now().toISOString() };
        atomicWrite(this.taskPath(taskId), state);
        this.audit(taskId, { event: "head_changed_review_invalidated", commit_sha: state.commit_sha, remote_head_sha: latestPull?.head?.sha || null });
        return state;
      }
      if (state.review_status === "PASS") return state;
      const processed = new Set(state.processed_review_ids);
      let changed = false;
      for (const event of events) {
        if (!REVIEW_ID_RE.test(event.id) || processed.has(event.id)) continue;
        processed.add(event.id);
        changed = true;
        state.last_processed_review_id = event.id;
        const identityMatches = String(event.login || "").toLowerCase() === this.config.reviewerLogin
          && Number(event.userId) === this.config.reviewerId
          && event.userType === this.config.reviewerType
          && Number(event.userId) !== Number(pull.user?.id)
          && ALLOWED_AUTHOR_ASSOCIATIONS.has(String(event.authorAssociation || ""));
        if (!identityMatches) {
          this.audit(taskId, { event: "review_rejected_invalid_reviewer", review_id: event.id, source: event.source });
          continue;
        }
        const parsed = parseVerdict(event.body, event.commitId);
        if (!parsed) {
          this.audit(taskId, { event: "review_rejected_invalid_format", review_id: event.id, source: event.source });
          continue;
        }
        if (parsed.commitSha !== state.commit_sha) {
          this.audit(taskId, { event: "review_rejected_stale_sha", review_id: event.id, expected_sha: state.commit_sha, reviewed_sha: parsed.commitSha });
          continue;
        }
        const verifiedPull = await this.api(`/repos/${this.config.repository}/pulls/${state.pr_number}`, token);
        if (verifiedPull?.head?.sha !== parsed.commitSha || this.gitHead() !== parsed.commitSha) {
          state.review_status = "PENDING";
          state.stage_advance_allowed = false;
          state.quality_gate_evidence = {};
          state.last_feedback = null;
          state.checkpoint = { ...state.checkpoint, status: "head_changed_review_invalidated", updated_at: this.now().toISOString() };
          this.audit(taskId, { event: "head_changed_review_invalidated", review_id: event.id, commit_sha: parsed.commitSha, remote_head_sha: verifiedPull?.head?.sha || null });
          break;
        }
        state.last_feedback = { review_id: event.id, source: event.source, verdict: parsed.verdict, commit_sha: parsed.commitSha, checkpoint: parsed.checkpoint, body: boundedText(event.body) };
        if (parsed.verdict === "REVISE") {
          state.stage_advance_allowed = false;
          if (state.review_attempts >= 3) {
            state.review_status = "REVISE_LIMIT";
            state.checkpoint = { ...state.checkpoint, status: "revision_limit_reached", stopped_at: now.toISOString() };
          } else {
            state.review_status = "REVISE";
            state.checkpoint = { ...state.checkpoint, status: "review_requests_revision", updated_at: now.toISOString() };
          }
        } else if (parsed.verdict === "BLOCKED") {
          state.review_status = "BLOCKED";
          state.stage_advance_allowed = false;
          state.checkpoint = { ...state.checkpoint, status: "review_blocked", reason: parsed.checkpoint, stopped_at: now.toISOString() };
        } else {
          const gatesPass = this.qualityGatesPass(state, parsed.commitSha);
          state.review_status = gatesPass ? "PASS" : "PASS_PENDING_GATES";
          state.stage_advance_allowed = gatesPass;
          state.checkpoint = { ...state.checkpoint, status: gatesPass ? "review_and_quality_gates_passed" : "review_passed_quality_gates_pending", updated_at: now.toISOString() };
        }
        this.audit(taskId, { event: "review_accepted", review_id: event.id, verdict: parsed.verdict, commit_sha: parsed.commitSha, status: state.review_status });
        if (FINAL_STATUSES.has(state.review_status) || state.review_status === "REVISE" || state.review_status === "PASS_PENDING_GATES") break;
      }
      state.processed_review_ids = [...processed].slice(-1000);
      if (changed) atomicWrite(this.taskPath(taskId), state);
      return state;
    } catch (error) {
      return this.unavailable(taskId, state, error?.message?.replace(/[^a-zA-Z0-9_-]/gu, "_").slice(0, 80) || "github_request_failed");
    }
  }

  unavailable(taskId, state, reason) {
    state.review_status = "REVIEW_CHANNEL_UNAVAILABLE";
    state.stage_advance_allowed = false;
    state.checkpoint = { ...state.checkpoint, status: "review_channel_unavailable", reason, stopped_at: this.now().toISOString() };
    atomicWrite(this.taskPath(taskId), state);
    this.audit(taskId, { event: "review_channel_unavailable", reason, commit_sha: state.commit_sha });
    return state;
  }

  async wait(taskId, { pollSeconds = this.config.pollSeconds, timeoutMinutes = this.config.timeoutMinutes, onPoll = () => {} } = {}) {
    const deadline = this.now().getTime() + timeoutMinutes * 60_000;
    while (this.now().getTime() < deadline) {
      const state = await this.pollOnce(taskId);
      onPoll({ status: state.review_status, last_processed_review_id: state.last_processed_review_id });
      if (FINAL_STATUSES.has(state.review_status) || state.review_status === "REVISE" || state.review_status === "PASS_PENDING_GATES") return state;
      await new Promise((resolve) => setTimeout(resolve, pollSeconds * 1000));
    }
    const state = this.readState(taskId);
    if (!FINAL_STATUSES.has(state.review_status)) {
      const noReviewerResponse = state.last_processed_review_id === null;
      state.review_status = noReviewerResponse ? "REVIEW_CHANNEL_UNAVAILABLE" : "TIMEOUT";
      state.checkpoint = {
        ...state.checkpoint,
        status: noReviewerResponse ? "review_channel_unavailable" : "timeout",
        reason: noReviewerResponse ? "no_reviewer_comment_before_deadline" : "review_deadline_expired",
        stopped_at: this.now().toISOString(),
      };
      atomicWrite(this.taskPath(taskId), state);
      this.audit(taskId, { event: noReviewerResponse ? "review_channel_unavailable" : "review_timeout", reason: state.checkpoint.reason, commit_sha: state.commit_sha });
    }
    return state;
  }
}

export function loadJsonConfig(configPath = process.env.DETOT_REVIEW_LOOP_CONFIG || DEFAULT_CONFIG) {
  return JSON.parse(fs.readFileSync(configPath, "utf8"));
}

function usage() {
  return [
    "review-feedback listener <init|run-gate|poll|wait|update-head|show> ...",
    "init --task ID --stage ID --branch codex/name --pr N --sha SHA --gates path/to/required-gates.json",
    "run-gate --task ID --gate dotnet-build|review-feedback-tests",
    "poll|wait --task ID",
    "update-head --task ID --sha SHA",
  ].join("\n");
}

function argsMap(argv) {
  const result = {};
  for (let i = 0; i < argv.length; i += 1) {
    const item = argv[i];
    if (!item.startsWith("--") || !argv[i + 1]) throw safeError("invalid_cli_arguments");
    result[item.slice(2)] = argv[++i];
  }
  return result;
}

async function main(argv) {
  const [command, ...raw] = argv;
  if (!command || command === "--help" || command === "-h") return process.stdout.write(`${usage()}\n`);
  const options = argsMap(raw);
  const config = loadJsonConfig(options.config);
  const listener = new ReviewFeedbackListener({ config });
  if (command === "init") {
    const gates = JSON.parse(fs.readFileSync(path.resolve(options.gates), "utf8"));
    const state = await listener.initialize({ taskId: options.task, stageId: options.stage, branch: options.branch, prNumber: Number(options.pr), commitSha: options.sha, qualityGates: gates });
    return process.stdout.write(`${JSON.stringify(state, null, 2)}\n`);
  }
  if (command === "run-gate") {
    const state = await listener.runQualityGate(options.task, options.gate);
    return process.stdout.write(`${JSON.stringify(state, null, 2)}\n`);
  }
  if (command === "show") return process.stdout.write(`${JSON.stringify(listener.readState(options.task), null, 2)}\n`);
  if (command === "update-head") {
    const state = await listener.updateHead(options.task, options.sha);
    return process.stdout.write(`${JSON.stringify(state, null, 2)}\n`);
  }
  if (command === "poll" || command === "wait") {
    const state = command === "poll" ? await listener.pollOnce(options.task) : await listener.wait(options.task);
    return process.stdout.write(`${JSON.stringify(state, null, 2)}\n`);
  }
  throw safeError(`unknown_command; ${usage()}`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main(process.argv.slice(2)).catch((error) => {
    process.stderr.write(`${error?.message?.replace(/\b(?:ghp|github_pat)_[A-Za-z0-9_]+/gu, "[REDACTED]") || "review_listener_failed"}\n`);
    process.exitCode = 1;
  });
}
