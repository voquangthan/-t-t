import { createRequire } from "node:module";
import readline from "node:readline";
import path from "node:path";
import fs from "node:fs/promises";
import { requestGithubJson, summarizeCheckRuns } from "./github-checks.mjs";

const require = createRequire(import.meta.url);
const moduleRoot = process.env.DETOT_PLAYWRIGHT_CORE;
if (!moduleRoot || path.basename(moduleRoot).toLowerCase() !== "playwright-core") {
  process.stderr.write("playwright_core_unavailable\n");
  process.exit(2);
}
const { chromium, request: playwrightRequest } = require(moduleRoot);
const profileRoot = process.env.DETOT_DESKTOP_EDGE_PROFILE;
if (!profileRoot || !path.isAbsolute(profileRoot)) {
  process.stderr.write("edge_profile_unavailable\n");
  process.exit(2);
}

const allowedActions = new Set(["open-edge", "open-github", "read-github", "read-account"]);
const githubRepo = /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/u;
let context;
let page;

async function ensureEdge() {
  if (context) return;
  await fs.mkdir(profileRoot, { recursive: true });
  context = await chromium.launchPersistentContext(profileRoot, {
    channel: "msedge",
    headless: false,
    acceptDownloads: false,
    args: ["--no-first-run", "--disable-extensions"],
  });
  page = context.pages()[0] ?? await context.newPage();
}

async function requestJson(apiContext, url) {
  return requestGithubJson(apiContext, url);
}

async function processRequest(request, phase) {
  if (request?.action === "shutdown") {
    await context?.close();
    context = undefined;
    return { stopped: true };
  }
  if (!allowedActions.has(request?.action)) return { error: "action_not_allowed" };

  if (request.action === "read-github") {
    if (typeof request.repository !== "string" || !githubRepo.test(request.repository))
      return { error: "repository_not_allowed" };
    phase.current = "github_api";
    if (typeof playwrightRequest?.newContext !== "function") return { error: "playwright_unavailable" };
    const apiContext = await playwrightRequest.newContext({ timeout: 10000 });
    try {
      return await readPullRequests(apiContext, request.repository);
    } finally {
      await apiContext.dispose();
    }
  }

  phase.current = "edge_launch";
  await ensureEdge();

  if (request.action === "open-edge") {
    if (page.url() === "about:blank") return { opened: true, message: "Edge đã mở ở profile riêng của Đệ Tốt." };
    phase.current = "browser_navigation";
    await page.goto("about:blank", { waitUntil: "domcontentloaded", timeout: 10000 });
    return { opened: true, message: "Edge đã mở ở profile riêng của Đệ Tốt." };
  }

  if (request.action === "read-account") {
    phase.current = "browser_navigation";
    await page.goto("https://github.com/settings/profile", { waitUntil: "domcontentloaded", timeout: 15000 });
    const login = await page.locator('meta[name="user-login"]').getAttribute("content").catch(() => null);
    return login ? { authenticated: true, login } : { authenticated: false, login: null, needsUserLogin: true };
  }

  if (typeof request.repository !== "string" || !githubRepo.test(request.repository))
    return { error: "repository_not_allowed" };
  if (request.action === "open-github") {
    phase.current = "browser_navigation";
    await page.goto(`https://github.com/${request.repository}/pulls`, { waitUntil: "domcontentloaded", timeout: 15000 });
    return { opened: true, message: "Đã mở danh sách Pull Request trong Edge." };
  }

  return { error: "action_not_allowed" };
}

async function readPullRequests(apiContext, repository) {
  const apiRoot = `https://api.github.com/repos/${repository}`;
  const pulls = await requestJson(apiContext, `${apiRoot}/pulls?state=all&per_page=3`);
  if (!Array.isArray(pulls)) return { error: pulls.error ?? "pull_request_unavailable" };
  const results = [];
  for (const pull of pulls) {
    if (!Number.isInteger(pull.number) || typeof pull.title !== "string" || typeof pull.head?.sha !== "string") continue;
    const checks = await requestJson(apiContext, `${apiRoot}/commits/${encodeURIComponent(pull.head.sha)}/check-runs?per_page=30`);
    results.push({
      number: pull.number,
      title: pull.title.slice(0, 180),
      state: pull.state,
      merged: Boolean(pull.merged_at),
      headSha: pull.head.sha,
      headBranch: typeof pull.head.ref === "string" ? pull.head.ref.slice(0, 120) : null,
      baseBranch: typeof pull.base?.ref === "string" ? pull.base.ref.slice(0, 120) : null,
      ...summarizeCheckRuns(checks),
    });
  }
  return { repository, pullRequests: results };
}

const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
for await (const line of input) {
  let id = null;
  const phase = { current: "request_parse" };
  try {
    const request = JSON.parse(line);
    id = request.id ?? null;
    const result = await processRequest(request, phase);
    process.stdout.write(JSON.stringify({ id, result }) + "\n");
    if (request.action === "shutdown") break;
  } catch (error) {
    const code = error?.name === "TimeoutError" ? "browser_timeout"
      : phase.current === "edge_launch" ? "edge_launch_failed"
      : phase.current === "browser_navigation" ? "browser_navigation_failed"
      : phase.current === "github_api" ? "github_api_unavailable"
      : "browser_operation_failed";
    process.stdout.write(JSON.stringify({ id, result: { error: code } }) + "\n");
  }
}
