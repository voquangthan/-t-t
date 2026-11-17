import assert from "node:assert/strict";
import test from "node:test";
import { summarizeCheckRuns } from "../../tools/computer-use/github-checks.mjs";

test("a successful empty response is the only empty-check state", () => {
  assert.deepEqual(summarizeCheckRuns({ total_count: 0, check_runs: [] }), {
    checksStatus: "empty",
    checksError: null,
    checksHttpStatus: null,
    checksTotalCount: 0,
    checksTruncated: false,
    checks: [],
  });
});

test("403 rate-limit responses remain unavailable instead of appearing empty", () => {
  const result = summarizeCheckRuns({ error: "github_http_403", rateLimited: true });
  assert.equal(result.checksStatus, "unavailable");
  assert.equal(result.checksError, "rate_limited");
  assert.equal(result.checksHttpStatus, 403);
});

test("403 permission responses remain unavailable", () => {
  const result = summarizeCheckRuns({ error: "github_http_403", rateLimited: false });
  assert.equal(result.checksStatus, "unavailable");
  assert.equal(result.checksError, "http_error");
  assert.equal(result.checksHttpStatus, 403);
});

test("5xx responses remain unavailable", () => {
  const result = summarizeCheckRuns({ error: "github_http_503" });
  assert.equal(result.checksStatus, "unavailable");
  assert.equal(result.checksError, "http_error");
  assert.equal(result.checksHttpStatus, 503);
});

test("check-run pagination is disclosed when the per-request result is truncated", () => {
  const runs = Array.from({ length: 30 }, (_, index) => ({ name: `Check ${index}`, status: "completed", conclusion: "success" }));
  const result = summarizeCheckRuns({ total_count: 34, check_runs: runs });
  assert.equal(result.checksStatus, "available");
  assert.equal(result.checks.length, 30);
  assert.equal(result.checksTotalCount, 34);
  assert.equal(result.checksTruncated, true);
});
