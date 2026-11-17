import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { ReviewFeedbackListener } from "../../tools/review-feedback/listener.mjs";

const sha = (letter) => letter.repeat(40);
const reviewer = "work-reviewer[bot]";

function reviewBody(verdict, head, checkpoint = "quality gates inspected") {
  return `DETOT-REVIEW/1\nVERDICT: ${verdict}\nCOMMIT_SHA: ${head}\nCHECKPOINT: ${checkpoint}`;
}

function setup({ head = sha("a"), comments = [], reviews = [], apiStatus = 200, now = new Date("2026-11-17T10:00:00Z"), qualityGates = { build: true, tests: true } } = {}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "detot-review-loop-"));
  let currentHead = head;
  let fixtureComments = comments;
  let fixtureReviews = reviews;
  const calls = [];
  const fetchImpl = async (url) => {
    calls.push(String(url));
    const route = new URL(url).pathname;
    if (apiStatus !== 200) return response(apiStatus, { message: "not authorized" });
    if (route === "/user") return response(200, { login: "voquangthan" });
    if (route === "/repos/voquangthan/-t-t/pulls/42") return response(200, { base: { ref: "main" }, head: { ref: "codex/review", sha: currentHead } });
    if (route.endsWith("/issues/42/comments")) return response(200, fixtureComments);
    if (route.endsWith("/pulls/42/reviews")) return response(200, fixtureReviews);
    return response(404, {});
  };
  const listener = new ReviewFeedbackListener({
    repoRoot: root,
    dataRoot: path.join(root, ".local", "review-loop"),
    config: { repository: "voquangthan/-t-t", reviewer_login: reviewer, base_branch: "main", timeout_minutes: 10 },
    fetchImpl,
    gitHead: () => currentHead,
    now: () => now,
    tokenProvider: () => "synthetic-test-token",
  });
  return {
    root, listener, calls,
    setHead(value) { currentHead = value; },
    setComments(value) { fixtureComments = value; },
    setReviews(value) { fixtureReviews = value; },
    async init({ gates = qualityGates, minutes = 10 } = {}) {
      return listener.initialize({ taskId: "task-1", stageId: "stage-a", branch: "codex/review", prNumber: 42, commitSha: currentHead, qualityGates: gates, timeoutMinutes: minutes });
    },
    close() { fs.rmSync(root, { recursive: true, force: true }); },
  };
}

function response(status, value) {
  return { ok: status >= 200 && status < 300, status, headers: { get: () => "" }, json: async () => value };
}

test("PR created then reviewer PASS at current SHA waits for all gates", async () => {
  const env = setup({ comments: [{ id: 1, user: { login: reviewer }, body: reviewBody("PASS", sha("a")), created_at: "2026-11-17T10:01:00Z" }] });
  try {
    await env.init();
    let state = await env.listener.pollOnce("task-1");
    assert.equal(state.review_status, "PASS");
    assert.equal(state.stage_advance_allowed, true);
    assert.equal(state.last_processed_review_id, "issue:1");
    state = await env.listener.pollOnce("task-1");
    assert.equal(state.processed_review_ids.length, 1);
  } finally { env.close(); }
});

test("a new pushed SHA invalidates old review and requires a new current-SHA review", async () => {
  const env = setup({ comments: [{ id: 2, user: { login: reviewer }, body: reviewBody("REVISE", sha("a")), created_at: "2026-11-17T10:01:00Z" }] });
  try {
    await env.init();
    let state = await env.listener.pollOnce("task-1");
    assert.equal(state.review_status, "REVISE");
    const next = sha("b"); env.setHead(next);
    state = await env.listener.updateHead("task-1", next);
    assert.equal(state.review_attempts, 1);
    env.setComments([
      { id: 2, user: { login: reviewer }, body: reviewBody("PASS", sha("a")), created_at: "2026-11-17T10:01:00Z" },
      { id: 8, user: { login: reviewer }, body: reviewBody("PASS", sha("a")), created_at: "2026-11-17T10:01:30Z" },
      { id: 3, user: { login: reviewer }, body: reviewBody("PASS", next), created_at: "2026-11-17T10:02:00Z" },
    ]);
    state = await env.listener.pollOnce("task-1");
    assert.equal(state.review_status, "PASS");
    assert.equal(state.last_processed_review_id, "issue:3");
    assert.ok(fs.readFileSync(env.listener.auditPath("task-1"), "utf8").includes("review_rejected_stale_sha"));
  } finally { env.close(); }
});

test("only exact configured reviewer login can submit accepted verdicts", async () => {
  const env = setup({ comments: [{ id: 4, user: { login: "codex-bot" }, body: reviewBody("PASS", sha("a")), created_at: "2026-11-17T10:01:00Z" }] });
  try {
    await env.init();
    const state = await env.listener.pollOnce("task-1");
    assert.equal(state.review_status, "PENDING");
    assert.equal(state.stage_advance_allowed, false);
    assert.ok(fs.readFileSync(env.listener.auditPath("task-1"), "utf8").includes("review_rejected_invalid_reviewer"));
  } finally { env.close(); }
});

test("duplicate review id is processed only once", async () => {
  const comment = { id: 5, user: { login: reviewer }, body: reviewBody("REVISE", sha("a")), created_at: "2026-11-17T10:01:00Z" };
  const env = setup({ comments: [comment] });
  try {
    await env.init(); await env.listener.pollOnce("task-1");
    const firstAudit = fs.readFileSync(env.listener.auditPath("task-1"), "utf8");
    const restarted = new ReviewFeedbackListener({
      repoRoot: env.root, dataRoot: path.join(env.root, ".local", "review-loop"),
      config: { repository: "voquangthan/-t-t", reviewer_login: reviewer },
      fetchImpl: async (url) => {
        const route = new URL(url).pathname;
        if (route === "/user") return response(200, { login: "voquangthan" });
        if (route === "/repos/voquangthan/-t-t/pulls/42") return response(200, { base: { ref: "main" }, head: { ref: "codex/review", sha: sha("a") } });
        if (route.endsWith("/issues/42/comments")) return response(200, [comment]);
        if (route.endsWith("/pulls/42/reviews")) return response(200, []);
        return response(404, {});
      },
      gitHead: () => sha("a"), now: () => new Date("2026-11-17T10:03:00Z"), tokenProvider: () => "synthetic-test-token",
    });
    await restarted.pollOnce("task-1");
    const secondAudit = fs.readFileSync(restarted.auditPath("task-1"), "utf8");
    assert.equal(secondAudit, firstAudit);
  } finally { env.close(); }
});

test("BLOCKED and unavailable GitHub permission stop at a checkpoint", async () => {
  const blocked = setup({ comments: [{ id: 6, user: { login: reviewer }, body: reviewBody("BLOCKED", sha("a"), "reviewer needs access"), created_at: "2026-11-17T10:01:00Z" }] });
  try { await blocked.init(); const state = await blocked.listener.pollOnce("task-1"); assert.equal(state.review_status, "BLOCKED"); assert.equal(state.checkpoint.reason, "reviewer needs access"); }
  finally { blocked.close(); }
  const denied = setup({ apiStatus: 403 });
  try { await denied.init(); const state = await denied.listener.pollOnce("task-1"); assert.equal(state.review_status, "REVIEW_UNAVAILABLE"); assert.equal(state.checkpoint.reason, "github_permission_unavailable_403"); }
  finally { denied.close(); }
});

test("PASS with pending quality gates cannot advance stage", async () => {
  const env = setup({ comments: [{ id: 7, user: { login: reviewer }, body: reviewBody("PASS", sha("a")), created_at: "2026-11-17T10:01:00Z" }], qualityGates: { build: true, tests: false } });
  try { await env.init(); const state = await env.listener.pollOnce("task-1"); assert.equal(state.review_status, "PASS_PENDING_GATES"); assert.equal(state.stage_advance_allowed, false); }
  finally { env.close(); }
});

test("invalid format, wrong base and mismatched review commit are never accepted", async () => {
  const env = setup({ reviews: [{ id: 8, user: { login: reviewer }, body: reviewBody("PASS", sha("a")), commit_id: sha("c"), submitted_at: "2026-11-17T10:01:00Z" }] });
  try {
    await env.init();
    const state = await env.listener.pollOnce("task-1");
    assert.equal(state.review_status, "PENDING");
    assert.ok(fs.readFileSync(env.listener.auditPath("task-1"), "utf8").includes("review_rejected_invalid_format"));
  } finally { env.close(); }
});

test("missing Work comment becomes REVIEW_UNAVAILABLE after restart and deadline", async () => {
  const env = setup({ now: new Date("2026-11-17T10:00:00Z") });
  try {
    await env.init({ minutes: 1 });
    const restarted = new ReviewFeedbackListener({
      repoRoot: env.root, dataRoot: path.join(env.root, ".local", "review-loop"),
      config: { repository: "voquangthan/-t-t", reviewer_login: reviewer },
      fetchImpl: async () => { throw new Error("must_not_call_after_expiry"); },
      gitHead: () => sha("a"), now: () => new Date("2026-11-17T10:02:00Z"), tokenProvider: () => "test",
    });
    const state = await restarted.pollOnce("task-1");
    assert.equal(state.review_status, "REVIEW_UNAVAILABLE");
    assert.equal(restarted.readState("task-1").checkpoint.reason, "no_reviewer_comment_before_deadline");
  } finally { env.close(); }
});

test("invalid reviewer activity expires as TIMEOUT instead of being accepted", async () => {
  const env = setup({ comments: [{ id: 11, user: { login: "someone-else" }, body: reviewBody("PASS", sha("a")), created_at: "2026-11-17T10:01:00Z" }], now: new Date("2026-11-17T10:00:00Z") });
  try {
    await env.init({ minutes: 1 });
    await env.listener.pollOnce("task-1");
    const restarted = new ReviewFeedbackListener({
      repoRoot: env.root, dataRoot: path.join(env.root, ".local", "review-loop"),
      config: { repository: "voquangthan/-t-t", reviewer_login: reviewer }, fetchImpl: async () => response(200, []),
      gitHead: () => sha("a"), now: () => new Date("2026-11-17T10:02:00Z"), tokenProvider: () => "test",
    });
    const state = await restarted.pollOnce("task-1");
    assert.equal(state.review_status, "TIMEOUT");
    assert.equal(state.checkpoint.reason, "review_deadline_expired");
  } finally { env.close(); }
});

test("missing reviewer login persists REVIEW_UNAVAILABLE without calling GitHub", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "detot-review-no-reviewer-"));
  let calls = 0;
  const listener = new ReviewFeedbackListener({
    repoRoot: root, dataRoot: path.join(root, ".local", "review-loop"),
    config: { repository: "voquangthan/-t-t", reviewer_login: "" },
    fetchImpl: async () => { calls += 1; return response(200, {}); },
    gitHead: () => sha("a"), tokenProvider: () => "test",
  });
  try {
    await listener.initialize({ taskId: "task-1", stageId: "stage-a", branch: "codex/review", prNumber: 42, commitSha: sha("a"), qualityGates: { tests: true } });
    const state = await listener.pollOnce("task-1");
    assert.equal(state.review_status, "REVIEW_UNAVAILABLE");
    assert.equal(state.checkpoint.reason, "configured_reviewer_identity_missing");
    assert.equal(calls, 0);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test("state writes stable task IDs and never persist a credential", async () => {
  const env = setup();
  try {
    await env.init();
    const state = fs.readFileSync(env.listener.taskPath("task-1"), "utf8");
    assert.ok(!state.includes("synthetic-test-token"));
    assert.equal(JSON.parse(state).task_id, "task-1");
  } finally { env.close(); }
});

test("third revision may be submitted but a fourth repair round is denied", async () => {
  const env = setup({ comments: [{ id: 10, user: { login: reviewer }, body: reviewBody("REVISE", sha("a")), created_at: "2026-11-17T10:01:00Z" }] });
  try {
    await env.init();
    await env.listener.pollOnce("task-1");
    for (const letter of ["b", "c", "d"]) {
      const next = sha(letter);
      env.setHead(next);
      await env.listener.updateHead("task-1", next);
      const state = env.listener.readState("task-1");
      state.review_status = "REVISE";
      fs.writeFileSync(env.listener.taskPath("task-1"), JSON.stringify(state));
    }
    await assert.rejects(() => env.listener.updateHead("task-1", sha("e")), /revision_limit_reached/u);
  } finally { env.close(); }
});
