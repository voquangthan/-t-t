import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { ReviewFeedbackListener, loadJsonConfig } from "../../tools/review-feedback/listener.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const listenerPath = path.resolve(here, "../../tools/review-feedback/listener.mjs");
const exampleConfigPath = path.resolve(here, "../../tools/review-feedback/config.example.json");
const sha = (letter) => letter.repeat(40);
const reviewer = "work-reviewer";
const requiredGates = ["review-feedback-tests"];

function reviewBody(verdict, head, checkpoint = "quality gates inspected") {
  return `DETOT-REVIEW/1\nVERDICT: ${verdict}\nCOMMIT_SHA: ${head}\nCHECKPOINT: ${checkpoint}`;
}

function verifiedReviewer() { return { login: reviewer, id: 12345, type: "User" }; }

function setup({ head = sha("a"), comments = [], reviews = [], apiStatus = 200, reviewerProfile = { login: reviewer, id: 12345, type: "User" }, permission = "write", pullAuthorId = 99999, now = new Date("2026-11-17T10:00:00Z"), gates = requiredGates } = {}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "detot-review-loop-"));
  let currentHead = head;
  let remoteHead = head;
  let currentTime = now;
  let pullState = "open";
  let merged = false;
  let fixtureComments = comments;
  let fixtureReviews = reviews;
  const calls = [];
  const fetchImpl = async (url) => {
    calls.push(String(url));
    const route = new URL(url).pathname;
    if (apiStatus !== 200) return response(apiStatus, { message: "not authorized" });
    if (route === "/user") return response(200, { login: "voquangthan" });
    if (route === "/users/work-reviewer") return response(200, reviewerProfile);
    if (route === "/repos/voquangthan/-t-t/collaborators/work-reviewer/permission") return response(200, { permission });
    if (route === "/repos/voquangthan/-t-t/pulls/42") return response(200, { user: { login: "pr-author", id: pullAuthorId }, state: pullState, merged, base: { ref: "main" }, head: { ref: "codex/review", sha: remoteHead } });
    if (route.endsWith("/issues/42/comments")) return response(200, fixtureComments);
    if (route.endsWith("/pulls/42/reviews")) return response(200, fixtureReviews);
    return response(404, {});
  };
  const listener = new ReviewFeedbackListener({
    repoRoot: root,
    dataRoot: path.join(root, ".local", "review-loop"),
    config: { repository: "voquangthan/-t-t", reviewer_login: reviewer, reviewer_id: 12345, reviewer_type: "User", base_branch: "main", timeout_minutes: 10 },
    fetchImpl,
    gitHead: () => currentHead,
    now: () => currentTime,
    tokenProvider: () => "synthetic-test-token",
    qualityGateRunner: async (gate) => ({ command: `fixture:${gate}`, exitCode: 0, output: `${gate} passed for ${currentHead}` }),
  });
  const env = {
    root, listener, calls,
    setHead(value) { currentHead = value; remoteHead = value; },
    setRemoteHead(value) { remoteHead = value; },
    setNow(value) { currentTime = value; },
    setPullState(value, isMerged = false) { pullState = value; merged = isMerged; },
    setComments(value) { fixtureComments = value; },
    setReviews(value) { fixtureReviews = value; },
    async init({ required = gates, minutes = 10 } = {}) {
      return listener.initialize({ taskId: "task-1", stageId: "stage-a", branch: "codex/review", prNumber: 42, commitSha: currentHead, qualityGates: required, timeoutMinutes: minutes });
    },
    async passGates() {
      for (const gate of requiredGates) await listener.runQualityGate("task-1", gate);
    },
    close() { fs.rmSync(root, { recursive: true, force: true }); },
  };
  return env;
}

function response(status, value) {
  return { ok: status >= 200 && status < 300, status, headers: { get: () => "" }, json: async () => value };
}

test("PASS is held until current-SHA quality gate evidence has been recorded", async () => {
  const env = setup({ comments: [{ id: 1, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("PASS", sha("a")), created_at: "2026-11-17T10:01:00Z" }] });
  try {
    await env.init();
    let state = await env.listener.pollOnce("task-1");
    assert.equal(state.review_status, "PASS_PENDING_GATES");
    assert.equal(state.stage_advance_allowed, false);
    await env.passGates();
    state = env.listener.readState("task-1");
    assert.equal(state.review_status, "PASS");
    assert.equal(state.stage_advance_allowed, true);
    assert.equal(state.last_processed_review_id, "issue:1");
    state = await env.listener.pollOnce("task-1");
    assert.equal(state.review_status, "PASS");
    assert.equal(state.processed_review_ids.length, 1);
  } finally { env.close(); }
});

test("new SHA invalidates gate evidence and stale reviews; rerun gates are required", async () => {
  const env = setup({ comments: [{ id: 2, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("REVISE", sha("a")), created_at: "2026-11-17T10:01:00Z" }] });
  try {
    await env.init();
    assert.equal((await env.listener.pollOnce("task-1")).review_status, "REVISE");
    const next = sha("b"); env.setHead(next);
    let state = await env.listener.updateHead("task-1", next);
    assert.equal(state.review_attempts, 1);
    assert.deepEqual(state.quality_gate_evidence, {});
    env.setComments([
      { id: 2, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("PASS", sha("a")), created_at: "2026-11-17T10:01:00Z" },
      { id: 8, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("PASS", sha("a")), created_at: "2026-11-17T10:01:30Z" },
      { id: 3, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("PASS", next), created_at: "2026-11-17T10:02:00Z" },
    ]);
    state = await env.listener.pollOnce("task-1");
    assert.equal(state.review_status, "PASS_PENDING_GATES");
    assert.equal(state.stage_advance_allowed, false);
    assert.equal(state.last_processed_review_id, "issue:3");
    const currentGitHead = env.listener.gitHead;
    env.listener.gitHead = () => sha("a");
    await assert.rejects(() => env.listener.runQualityGate("task-1", requiredGates[0]), /quality_gate_sha_or_state_mismatch/u);
    env.listener.gitHead = currentGitHead;
    assert.ok(fs.readFileSync(env.listener.auditPath("task-1"), "utf8").includes("review_rejected_stale_sha"));
  } finally { env.close(); }
});

test("quality gate registry rejects caller booleans, failed commands, unsupported gates and stale SHAs", async () => {
  const env = setup();
  try {
    await assert.rejects(() => env.init({ required: { build: true, tests: true } }), /quality_gates_must_be_unique_named_list/u);
    await env.init();
    await assert.rejects(() => env.listener.runQualityGate("task-1", "lint"), /quality_gate_not_required/u);
    env.listener.qualityGateRunner = async (gate) => ({ command: gate, exitCode: 1, output: "failed" });
    await assert.rejects(() => env.listener.runQualityGate("task-1", requiredGates[0]), /quality_gate_evidence_incomplete/u);
    env.setHead(sha("b"));
    await assert.rejects(() => env.listener.runQualityGate("task-1", requiredGates[0]), /quality_gate_sha_or_state_mismatch/u);
  } finally { env.close(); }
});

test("BLOCKED stops batch processing and permanently clears advance flag", async () => {
  const env = setup({ comments: [
    { id: 6, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("BLOCKED", sha("a"), "reviewer needs access"), created_at: "2026-11-17T10:01:00Z" },
    { id: 7, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("PASS", sha("a")), created_at: "2026-11-17T10:02:00Z" },
  ] });
  try {
    await env.init(); await env.passGates();
    const state = await env.listener.pollOnce("task-1");
    assert.equal(state.review_status, "BLOCKED");
    assert.equal(state.checkpoint.reason, "reviewer needs access");
    assert.equal(state.stage_advance_allowed, false);
    assert.deepEqual(state.processed_review_ids, ["issue:6"]);
  } finally { env.close(); }
});

test("PASS followed by BLOCKED in one fetched batch ends blocked with no advance", async () => {
  const env = setup({ comments: [
    { id: 20, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("PASS", sha("a")), created_at: "2026-11-17T10:01:00Z" },
    { id: 21, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("BLOCKED", sha("a")), created_at: "2026-11-17T10:02:00Z" },
  ] });
  try {
    await env.init(); await env.passGates();
    const state = await env.listener.pollOnce("task-1");
    assert.equal(state.review_status, "BLOCKED");
    assert.equal(state.stage_advance_allowed, false);
    assert.deepEqual(state.processed_review_ids, ["issue:20", "issue:21"]);
  } finally { env.close(); }
});

test("BLOCKED arriving on a later poll revokes an already passing state", async () => {
  const env = setup({ comments: [{ id: 22, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("PASS", sha("a")), created_at: "2026-11-17T10:01:00Z" }] });
  try {
    await env.init(); await env.passGates();
    assert.equal((await env.listener.pollOnce("task-1")).review_status, "PASS");
    env.setComments([
      { id: 22, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("PASS", sha("a")), created_at: "2026-11-17T10:01:00Z" },
      { id: 23, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("BLOCKED", sha("a"), "stop"), created_at: "2026-11-17T10:02:00Z" },
    ]);
    const state = await env.listener.pollOnce("task-1");
    assert.equal(state.review_status, "BLOCKED");
    assert.equal(state.stage_advance_allowed, false);
  } finally { env.close(); }
});

test("BLOCKED arriving while PASS awaits gates overrides the stale verdict", async () => {
  const env = setup({ comments: [{ id: 24, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("PASS", sha("a")), created_at: "2026-11-17T10:01:00Z" }] });
  try {
    await env.init();
    assert.equal((await env.listener.pollOnce("task-1")).review_status, "PASS_PENDING_GATES");
    env.setComments([
      { id: 24, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("PASS", sha("a")), created_at: "2026-11-17T10:01:00Z" },
      { id: 25, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("BLOCKED", sha("a"), "stop"), created_at: "2026-11-17T10:02:00Z" },
    ]);
    const state = await env.listener.pollOnce("task-1");
    assert.equal(state.review_status, "BLOCKED");
    assert.equal(state.stage_advance_allowed, false);
  } finally { env.close(); }
});

test("a terminal PASS is revoked when local or remote HEAD moves", async () => {
  const env = setup({ comments: [{ id: 30, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("PASS", sha("a")), created_at: "2026-11-17T10:01:00Z" }] });
  try {
    await env.init(); await env.passGates();
    let state = await env.listener.pollOnce("task-1");
    assert.equal(state.review_status, "PASS");
    env.setHead(sha("b"));
    state = await env.listener.pollOnce("task-1");
    assert.equal(state.review_status, "PENDING");
    assert.equal(state.stage_advance_allowed, false);
    assert.deepEqual(state.quality_gate_evidence, {});
  } finally { env.close(); }
});

test("HEAD moving while review events are fetched invalidates the review before acceptance", async () => {
  const env = setup({ comments: [{ id: 31, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("PASS", sha("a")), created_at: "2026-11-17T10:01:00Z" }] });
  try {
    await env.init(); await env.passGates();
    const original = env.listener.fetchImpl;
    env.listener.fetchImpl = async (url, options) => {
      const result = await original(url, options);
      if (new URL(url).pathname.endsWith("/pulls/42/reviews")) env.setHead(sha("b"));
      return result;
    };
    const state = await env.listener.pollOnce("task-1");
    assert.equal(state.review_status, "PENDING");
    assert.equal(state.stage_advance_allowed, false);
    assert.ok(fs.readFileSync(env.listener.auditPath("task-1"), "utf8").includes("head_changed_review_invalidated"));
  } finally { env.close(); }
});

test("run-gate cannot promote stale PASS when only remote head changed", async () => {
  const env = setup({ comments: [{ id: 32, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("PASS", sha("a")), created_at: "2026-11-17T10:01:00Z" }] });
  try {
    await env.init();
    assert.equal((await env.listener.pollOnce("task-1")).review_status, "PASS_PENDING_GATES");
    env.setRemoteHead(sha("b"));
    const state = await env.listener.runQualityGate("task-1", requiredGates[0]);
    assert.equal(state.review_status, "REVIEW_CHANNEL_UNAVAILABLE");
    assert.equal(state.stage_advance_allowed, false);
    assert.deepEqual(state.quality_gate_evidence, {});
  } finally { env.close(); }
});

test("PR closing while a gate process runs cannot grant stage advance", async () => {
  const env = setup({ comments: [{ id: 36, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("PASS", sha("a")), created_at: "2026-11-17T10:01:00Z" }] });
  try {
    await env.init();
    assert.equal((await env.listener.pollOnce("task-1")).review_status, "PASS_PENDING_GATES");
    env.listener.qualityGateRunner = async (gate) => {
      env.setPullState("closed");
      return { command: gate, exitCode: 0, output: "passed" };
    };
    const state = await env.listener.runQualityGate("task-1", requiredGates[0]);
    assert.equal(state.review_status, "REVIEW_CHANNEL_UNAVAILABLE");
    assert.equal(state.stage_advance_allowed, false);
  } finally { env.close(); }
});

test("deadline expiring during a gate run revokes rather than grants stage advance", async () => {
  const env = setup({ comments: [{ id: 37, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("PASS", sha("a")), created_at: "2026-11-17T10:01:00Z" }] });
  try {
    await env.init({ minutes: 1 });
    assert.equal((await env.listener.pollOnce("task-1")).review_status, "PASS_PENDING_GATES");
    env.listener.qualityGateRunner = async (gate) => {
      env.setNow(new Date("2026-11-17T10:02:00Z"));
      return { command: gate, exitCode: 0, output: "passed" };
    };
    const state = await env.listener.runQualityGate("task-1", requiredGates[0]);
    assert.equal(state.review_status, "TIMEOUT");
    assert.equal(state.stage_advance_allowed, false);
  } finally { env.close(); }
});

test("closed or merged PR cannot accept feedback, including a close during polling", async () => {
  const closed = setup({ comments: [{ id: 33, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("PASS", sha("a")), created_at: "2026-11-17T10:01:00Z" }] });
  try {
    await closed.init(); await closed.passGates(); closed.setPullState("closed");
    const state = await closed.listener.pollOnce("task-1");
    assert.equal(state.review_status, "REVIEW_CHANNEL_UNAVAILABLE");
    assert.equal(state.checkpoint.reason, "pull_request_not_open");
    assert.equal(state.stage_advance_allowed, false);
  } finally { closed.close(); }

  const closing = setup({ comments: [{ id: 34, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("PASS", sha("a")), created_at: "2026-11-17T10:01:00Z" }] });
  try {
    await closing.init(); await closing.passGates();
    const original = closing.listener.fetchImpl;
    closing.listener.fetchImpl = async (url, options) => {
      const result = await original(url, options);
      if (new URL(url).pathname.endsWith("/issues/42/comments")) closing.setPullState("closed");
      return result;
    };
    const state = await closing.listener.pollOnce("task-1");
    assert.equal(state.review_status, "REVIEW_CHANNEL_UNAVAILABLE");
    assert.equal(state.stage_advance_allowed, false);
  } finally { closing.close(); }
});

test("timeout after PASS always revokes stage advance even if remote head also moved", async () => {
  const env = setup({ comments: [{ id: 35, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("PASS", sha("a")), created_at: "2026-11-17T10:01:00Z" }] });
  try {
    await env.init({ minutes: 1 }); await env.passGates();
    assert.equal((await env.listener.pollOnce("task-1")).review_status, "PASS");
    env.setRemoteHead(sha("b"));
    env.setNow(new Date("2026-11-17T10:02:00Z"));
    const state = await env.listener.pollOnce("task-1");
    assert.equal(state.review_status, "TIMEOUT");
    assert.equal(state.stage_advance_allowed, false);
  } finally { env.close(); }
});

test("reinitializing the same task is idempotent and preserves attempts and dedup history", async () => {
  const env = setup({ comments: [{ id: 40, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("REVISE", sha("a")), created_at: "2026-11-17T10:01:00Z" }] });
  try {
    await env.init(); await env.listener.pollOnce("task-1");
    const next = sha("b"); env.setHead(next); await env.listener.updateHead("task-1", next);
    const before = env.listener.readState("task-1");
    const again = await env.listener.initialize({ taskId: "task-1", stageId: "stage-a", branch: "codex/review", prNumber: 42, commitSha: next, qualityGates: requiredGates });
    assert.equal(again.review_attempts, 1);
    assert.deepEqual(again.processed_review_ids, before.processed_review_ids);
    assert.equal(again.commit_sha, next);
    await assert.rejects(() => env.listener.initialize({ taskId: "task-1", stageId: "other-stage", branch: "codex/review", prNumber: 42, commitSha: next, qualityGates: requiredGates }), /task_id_already_registered_for_different_task/u);
  } finally { env.close(); }
});

test("duplicate review event ID is processed once across listener restart", async () => {
  const comment = { id: 41, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("REVISE", sha("a")), created_at: "2026-11-17T10:01:00Z" };
  const env = setup({ comments: [comment] });
  try {
    await env.init(); await env.listener.pollOnce("task-1");
    const before = fs.readFileSync(env.listener.auditPath("task-1"), "utf8");
    const restarted = new ReviewFeedbackListener({
      repoRoot: env.root, dataRoot: path.join(env.root, ".local", "review-loop"),
      config: { repository: "voquangthan/-t-t", reviewer_login: reviewer, reviewer_id: 12345, reviewer_type: "User" },
      fetchImpl: env.listener.fetchImpl, gitHead: () => sha("a"), now: () => new Date("2026-11-17T10:02:00Z"), tokenProvider: () => "synthetic-test-token",
    });
    const state = await restarted.pollOnce("task-1");
    assert.equal(state.review_attempts, 0);
    assert.equal(fs.readFileSync(restarted.auditPath("task-1"), "utf8"), before);
  } finally { env.close(); }
});

test("reviewer must match configured login, while strict source and SHA structure are checked", async () => {
  const env = setup({ comments: [{ id: 4, user: { login: "codex-bot" }, author_association: "NONE", body: reviewBody("PASS", sha("a")), created_at: "2026-11-17T10:01:00Z" }] });
  try {
    await env.init(); const state = await env.listener.pollOnce("task-1");
    assert.equal(state.review_status, "PENDING");
    assert.equal(state.stage_advance_allowed, false);
    assert.ok(fs.readFileSync(env.listener.auditPath("task-1"), "utf8").includes("review_rejected_invalid_reviewer"));
  } finally { env.close(); }
});

test("reviewer profile, repository permission and author separation are independently verified", async () => {
  const mismatch = setup({ reviewerProfile: { login: reviewer, id: 77777, type: "User" } });
  try {
    await mismatch.init();
    const state = await mismatch.listener.pollOnce("task-1");
    assert.equal(state.review_status, "REVIEW_CHANNEL_UNAVAILABLE");
    assert.equal(state.checkpoint.reason, "configured_reviewer_profile_mismatch");
  } finally { mismatch.close(); }

  const noPermission = setup({ permission: "read" });
  try {
    await noPermission.init();
    const state = await noPermission.listener.pollOnce("task-1");
    assert.equal(state.review_status, "REVIEW_CHANNEL_UNAVAILABLE");
    assert.equal(state.checkpoint.reason, "reviewer_repository_permission_insufficient");
  } finally { noPermission.close(); }

  const selfReview = setup({ pullAuthorId: 12345, comments: [{ id: 42, user: verifiedReviewer(), author_association: "OWNER", body: reviewBody("PASS", sha("a")), created_at: "2026-11-17T10:01:00Z" }] });
  try {
    await selfReview.init();
    const state = await selfReview.listener.pollOnce("task-1");
    assert.equal(state.review_status, "PENDING");
    assert.equal(state.stage_advance_allowed, false);
    assert.ok(fs.readFileSync(selfReview.listener.auditPath("task-1"), "utf8").includes("review_rejected_invalid_reviewer"));
  } finally { selfReview.close(); }
});

test("invalid format, wrong base and mismatched review commit are never accepted", async () => {
  const env = setup({ reviews: [{ id: 8, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("PASS", sha("a")), commit_id: sha("c"), submitted_at: "2026-11-17T10:01:00Z" }] });
  try {
    await env.init(); const state = await env.listener.pollOnce("task-1");
    assert.equal(state.review_status, "PENDING");
    assert.ok(fs.readFileSync(env.listener.auditPath("task-1"), "utf8").includes("review_rejected_invalid_format"));
  } finally { env.close(); }
});

test("GitHub permission failure and absent reviewer become explicit checkpoints", async () => {
  const denied = setup({ apiStatus: 403 });
  try { await denied.init(); const state = await denied.listener.pollOnce("task-1"); assert.equal(state.review_status, "REVIEW_CHANNEL_UNAVAILABLE"); assert.equal(state.checkpoint.reason, "github_permission_unavailable_403"); }
  finally { denied.close(); }
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "detot-review-no-reviewer-"));
  try {
    const listener = new ReviewFeedbackListener({ repoRoot: root, dataRoot: path.join(root, ".local", "review-loop"), config: { repository: "voquangthan/-t-t", reviewer_login: "" }, fetchImpl: async () => response(200, {}), gitHead: () => sha("a"), tokenProvider: () => "test" });
    await listener.initialize({ taskId: "task-1", stageId: "stage-a", branch: "codex/review", prNumber: 42, commitSha: sha("a"), qualityGates: requiredGates });
    const state = await listener.pollOnce("task-1");
    assert.equal(state.review_status, "REVIEW_CHANNEL_UNAVAILABLE");
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test("missing reviewer comment becomes REVIEW_CHANNEL_UNAVAILABLE at the deadline", async () => {
  const env = setup({ now: new Date("2026-11-17T10:00:00Z") });
  try {
    await env.init({ minutes: 1 });
    const restarted = new ReviewFeedbackListener({ repoRoot: env.root, dataRoot: path.join(env.root, ".local", "review-loop"), config: { repository: "voquangthan/-t-t", reviewer_login: reviewer, reviewer_id: 12345, reviewer_type: "User" }, fetchImpl: async () => { throw new Error("must_not_call_after_expiry"); }, gitHead: () => sha("a"), now: () => new Date("2026-11-17T10:02:00Z"), tokenProvider: () => "test" });
    const state = await restarted.pollOnce("task-1");
    assert.equal(state.review_status, "REVIEW_CHANNEL_UNAVAILABLE");
    assert.equal(state.checkpoint.reason, "no_reviewer_comment_before_deadline");
  } finally { env.close(); }
});

test("CLI example config loads and fresh-clone CLI reports missing config clearly", () => {
  const config = loadJsonConfig(exampleConfigPath);
  assert.equal(config.repository, "voquangthan/-t-t");
  assert.equal(config.reviewer_login, "");
  const missingPath = path.join(os.tmpdir(), `detot-missing-config-${process.pid}.json`);
  const result = spawnSync(process.execPath, [listenerPath, "show", "--task", "missing", "--config", missingPath], { encoding: "utf8", windowsHide: true });
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /ENOENT/u);
});

test("state persists task IDs and gate evidence without credentials or raw output", async () => {
  const env = setup();
  try {
    await env.init();
    await env.listener.runQualityGate("task-1", requiredGates[0]);
    const raw = fs.readFileSync(env.listener.taskPath("task-1"), "utf8");
    assert.ok(!raw.includes("synthetic-test-token"));
    assert.ok(!raw.includes("passed for"));
    assert.equal(JSON.parse(raw).task_id, "task-1");
    assert.equal(JSON.parse(raw).quality_gate_evidence[requiredGates[0]].commit_sha, sha("a"));
  } finally { env.close(); }
});

test("missing trusted reviewer keeps gate evidence but never grants stage advance", async () => {
  const env = setup();
  try {
    await env.init();
    env.listener.config = { ...env.listener.config, reviewerLogin: "", reviewerId: null, reviewerType: "" };
    const state = await env.listener.runQualityGate("task-1", requiredGates[0]);
    assert.equal(state.review_status, "REVIEW_CHANNEL_UNAVAILABLE");
    assert.equal(state.stage_advance_allowed, false);
    assert.equal(state.quality_gate_evidence[requiredGates[0]].commit_sha, sha("a"));
  } finally { env.close(); }
});

test("three revision rounds are retained across reinitialization and fourth is denied", async () => {
  const env = setup({ comments: [{ id: 50, user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("REVISE", sha("a")), created_at: "2026-11-17T10:01:00Z" }] });
  try {
    await env.init(); await env.listener.pollOnce("task-1");
    for (const letter of ["b", "c", "d"]) {
      const next = sha(letter); env.setHead(next); await env.listener.updateHead("task-1", next);
      env.setComments([{ id: 50 + letter.charCodeAt(0), user: verifiedReviewer(), author_association: "COLLABORATOR", body: reviewBody("REVISE", next), created_at: "2026-11-17T10:02:00Z" }]);
      await env.listener.pollOnce("task-1");
      await env.listener.initialize({ taskId: "task-1", stageId: "stage-a", branch: "codex/review", prNumber: 42, commitSha: next, qualityGates: requiredGates });
    }
    assert.equal(env.listener.readState("task-1").review_attempts, 3);
    await assert.rejects(() => env.listener.updateHead("task-1", sha("e")), /revision_limit_reached/u);
  } finally { env.close(); }
});
