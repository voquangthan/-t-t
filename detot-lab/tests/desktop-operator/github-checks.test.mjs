import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { classifyGithubResponseError, requestGithubJson, summarizeCheckRuns } from "../../tools/computer-use/github-checks.mjs";

const apiResponse = (status, headers) => ({ status: () => status, headers: () => headers });

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
  const failure = classifyGithubResponseError(apiResponse(403, { "x-ratelimit-remaining": "0" }));
  assert.deepEqual(failure, { error: "github_http_403", rateLimited: true });
  const result = summarizeCheckRuns(failure);
  assert.equal(result.checksStatus, "unavailable");
  assert.equal(result.checksError, "rate_limited");
  assert.equal(result.checksHttpStatus, 403);
});

test("403 permission responses remain unavailable", () => {
  const failure = classifyGithubResponseError(apiResponse(403, { "x-ratelimit-remaining": "37" }));
  assert.deepEqual(failure, { error: "github_http_403", rateLimited: false });
  const result = summarizeCheckRuns(failure);
  assert.equal(result.checksStatus, "unavailable");
  assert.equal(result.checksError, "http_error");
  assert.equal(result.checksHttpStatus, 403);
});

test("5xx responses remain unavailable", () => {
  const failure = classifyGithubResponseError(apiResponse(503, {}));
  assert.deepEqual(failure, { error: "github_http_503", rateLimited: false });
  const result = summarizeCheckRuns(failure);
  assert.equal(result.checksStatus, "unavailable");
  assert.equal(result.checksError, "http_error");
  assert.equal(result.checksHttpStatus, 503);
});

test("invalid APIResponse headers fail closed without calling a Fetch Headers method", () => {
  const result = classifyGithubResponseError({ status: () => 403, headers: () => null });
  assert.deepEqual(result, { error: "github_invalid_response", rateLimited: false });
});

test("requestGithubJson handles the real Playwright APIResponse shape for rate limits", async () => {
  let requestedUrl;
  let requestedOptions;
  const context = { request: { get: async (url, options) => {
    requestedUrl = url;
    requestedOptions = options;
    return { ok: () => false, status: () => 403, headers: () => ({ "x-ratelimit-remaining": "0" }) };
  } } };
  assert.deepEqual(await requestGithubJson(context, "https://api.github.com/repos/example/project/check-runs"),
    { error: "github_http_403", rateLimited: true });
  assert.equal(requestedUrl, "https://api.github.com/repos/example/project/check-runs");
  assert.equal(requestedOptions.headers.Accept, "application/vnd.github+json");
});

test("requestGithubJson distinguishes a permission 403 and preserves successful JSON", async () => {
  const denied = { request: { get: async () => ({
    ok: () => false, status: () => 403, headers: () => ({ "x-ratelimit-remaining": "37" }),
  }) } };
  assert.deepEqual(await requestGithubJson(denied, "https://api.github.com/repos/example/project/check-runs"),
    { error: "github_http_403", rateLimited: false });
  const success = { request: { get: async () => ({ ok: () => true, json: async () => ({ total_count: 0, check_runs: [] }) }) } };
  assert.deepEqual(await requestGithubJson(success, "https://api.github.com/repos/example/project/check-runs"),
    { total_count: 0, check_runs: [] });
});

test("requestGithubJson fails closed on invalid responses and network errors", async () => {
  assert.deepEqual(await requestGithubJson({}, "https://api.github.com"),
    { error: "github_invalid_response", rateLimited: false });
  assert.deepEqual(await requestGithubJson({ request: { get: async () => { throw new Error("network detail"); } } },
    "https://api.github.com"), { error: "github_api_unavailable", rateLimited: false });
});

test("reading public PR and CI data does not launch or navigate Edge", async () => {
  const temporary = await fs.mkdtemp(path.join(os.tmpdir(), "detot-edge-read-"));
  const moduleRoot = path.join(temporary, "playwright-core");
  await fs.mkdir(moduleRoot);
  await fs.writeFile(path.join(moduleRoot, "package.json"), JSON.stringify({ main: "index.js" }));
  await fs.writeFile(path.join(moduleRoot, "index.js"), `
module.exports = {
  chromium: { launchPersistentContext: async () => { throw new Error("Edge must not launch for observation"); } },
  request: { newContext: async () => ({
    get: async (url) => ({ ok: () => true, json: async () => url.includes("/pulls?")
      ? [{ number: 7, title: "Synthetic PR", state: "open", merged_at: null,
          head: { sha: "abcdef0123456789", ref: "feature" }, base: { ref: "main" } }]
      : { total_count: 1, check_runs: [{ name: "CI fixture", status: "completed", conclusion: "success" }] } }),
    dispose: async () => {},
  }) },
};
`);
  try {
    const script = fileURLToPath(new URL("../../tools/computer-use/edge-reader.mjs", import.meta.url));
    const childEnvironment = Object.fromEntries(["SystemRoot", "WINDIR", "TEMP", "TMP"]
      .filter(key => process.env[key])
      .map(key => [key, process.env[key]]));
    childEnvironment.DETOT_PLAYWRIGHT_CORE = moduleRoot;
    childEnvironment.DETOT_DESKTOP_EDGE_PROFILE = path.join(temporary, "profile");
    const child = spawnSync(process.execPath, [script], {
      input: `${JSON.stringify({ id: 1, action: "read-github", repository: "example/project" })}\n`,
      encoding: "utf8",
      timeout: 5000,
      env: childEnvironment,
    });
    assert.equal(child.status, 0, child.stderr);
    const response = JSON.parse(child.stdout.trim());
    assert.equal(response.id, 1);
    assert.equal(response.result.repository, "example/project");
    assert.equal(response.result.pullRequests[0].checks[0].conclusion, "success");
  } finally {
    await fs.rm(temporary, { recursive: true, force: true });
  }
});

test("check-run pagination is disclosed when the per-request result is truncated", () => {
  const runs = Array.from({ length: 30 }, (_, index) => ({ name: `Check ${index}`, status: "completed", conclusion: "success" }));
  const result = summarizeCheckRuns({ total_count: 34, check_runs: runs });
  assert.equal(result.checksStatus, "available");
  assert.equal(result.checks.length, 30);
  assert.equal(result.checksTotalCount, 34);
  assert.equal(result.checksTruncated, true);
});
