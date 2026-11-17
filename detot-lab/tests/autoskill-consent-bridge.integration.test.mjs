import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import crypto from "node:crypto";
import fs from "node:fs";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const TOKEN = "synthetic-autoskill-gateway-token-with-sufficient-entropy";

async function unusedLoopbackPort() {
  const server = net.createServer();
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  const port = server.address().port;
  await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
  return port;
}

async function waitForHealth(origin, child) {
  const deadline = Date.now() + 15000;
  while (Date.now() < deadline) {
    if (child.exitCode !== null) throw new Error(`backend_test_process_exited:${child.exitCode}`);
    try {
      const response = await fetch(`${origin}/health`, { signal: AbortSignal.timeout(500) });
      if (response.ok) return;
    } catch { /* startup window */ }
    await new Promise(resolve => setTimeout(resolve, 150));
  }
  throw new Error("backend_test_startup_timeout");
}

function postJson(origin, route, body, headers = {}) {
  return fetch(`${origin}${route}`, {
    method: "POST",
    headers: { "content-type": "application/json", ...headers },
    body: JSON.stringify(body),
    signal: AbortSignal.timeout(3000),
  });
}

test("Backend AutoSkill consent bridge authenticates Gateway and returns only exact owner-bound grants", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "detot-autoskill-consent-bridge-"));
  const webPort = await unusedLoopbackPort();
  const gatewayPort = await unusedLoopbackPort();
  const origin = `http://127.0.0.1:${webPort}`;
  const localDirectory = name => path.join(root, name);
  const environment = {
    ...process.env,
    DETOT_WEB_PORT: String(webPort),
    DETOT_GATEWAY_PORT: String(gatewayPort),
    DETOT_GATEWAY_TOKEN: TOKEN,
    DETOT_AUTOSKILL_CONSENT_BRIDGE_ENABLED: "true",
    DETOT_PRACTICE_DIRECTORY: localDirectory("practice"),
    DETOT_GOOGLE_DIRECTORY: localDirectory("google"),
    DETOT_GOOGLE_KEY_DIRECTORY: localDirectory("keys"),
    DETOT_LEARNING_DIRECTORY: localDirectory("learning"),
    DETOT_WEATHER_DIRECTORY: localDirectory("weather"),
    DETOT_COPY_AUTHORITY_DIR: localDirectory("authority"),
    DETOT_ENABLE_CALENDAR_TRIAL: "false",
  };
  const binary = path.join(ROOT, "bin", "Debug", "net10.0", "Detot.Lab.dll");
  assert.ok(fs.existsSync(binary), "build the backend before running this integration test");
  const child = spawn("dotnet", [binary], { cwd: ROOT, env: environment, stdio: "ignore", windowsHide: true });
  let exitPromise;
  try {
    exitPromise = new Promise(resolve => child.once("exit", resolve));
    await waitForHealth(origin, child);

    const bridge = (stage, token = TOKEN) => postJson(origin, "/api/internal/autoskill/consent",
      { stage, ownerId: "attacker-selected-owner" }, token ? { authorization: `Bearer ${token}` } : {});
    assert.equal((await bridge("capture", "wrong-token")).status, 403);
    assert.equal((await bridge("capture", "")).status, 403);
    assert.equal((await bridge("capture")).status, 403, "new owner consent defaults to deny");

    const grant = (source, destination, allowed) => postJson(origin, "/api/consent", {
      source, purpose: "skill_learning", destination, allowed, requestId: crypto.randomUUID(),
    }, { origin });
    assert.equal((await grant("chat_history", "local_only", true)).status, 200);
    const capture = await bridge("capture");
    assert.equal(capture.status, 200);
    const captureData = await capture.json();
    assert.equal(captureData.ownerId, "workspace-owner", "the owner must come from the authenticated Backend context, not request JSON");
    assert.deepEqual(captureData.requirements, [
      { source: "chat_history", purpose: "skill_learning", destination: "local_only" },
    ]);
    assert.equal(Number.isSafeInteger(captureData.revision), true);

    assert.equal((await bridge("analysis")).status, 403, "local capture grant must not imply DeepSeek grant");
    assert.equal((await grant("chat_history", "DeepSeek", true)).status, 200);
    const analysis = await bridge("analysis");
    assert.equal(analysis.status, 200);
    const analysisData = await analysis.json();
    assert.deepEqual(analysisData.requirements, [
      { source: "chat_history", purpose: "skill_learning", destination: "local_only" },
      { source: "chat_history", purpose: "skill_learning", destination: "DeepSeek" },
    ]);
    assert.ok(analysisData.revision >= captureData.revision);

    assert.equal((await grant("chat_history", "DeepSeek", false)).status, 200);
    assert.equal((await bridge("analysis")).status, 403, "revocation must block the next bridge authorization");
    assert.equal((await bridge("unsupported")).status, 400);
  } finally {
    if (child.exitCode === null) child.kill("SIGTERM");
    if (exitPromise) {
      await Promise.race([exitPromise, new Promise(resolve => setTimeout(resolve, 3000))]);
      if (child.exitCode === null) child.kill("SIGKILL");
    }
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("Backend AutoSkill consent bridge remains unavailable unless explicitly enabled", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "detot-autoskill-bridge-disabled-"));
  const webPort = await unusedLoopbackPort();
  const gatewayPort = await unusedLoopbackPort();
  const origin = `http://127.0.0.1:${webPort}`;
  const localDirectory = name => path.join(root, name);
  const child = spawn("dotnet", [path.join(ROOT, "bin", "Debug", "net10.0", "Detot.Lab.dll")], {
    cwd: ROOT,
    env: {
      ...process.env,
      DETOT_WEB_PORT: String(webPort),
      DETOT_GATEWAY_PORT: String(gatewayPort),
      DETOT_GATEWAY_TOKEN: TOKEN,
      DETOT_AUTOSKILL_CONSENT_BRIDGE_ENABLED: "false",
      DETOT_PRACTICE_DIRECTORY: localDirectory("practice"),
      DETOT_GOOGLE_DIRECTORY: localDirectory("google"),
      DETOT_GOOGLE_KEY_DIRECTORY: localDirectory("keys"),
      DETOT_LEARNING_DIRECTORY: localDirectory("learning"),
      DETOT_WEATHER_DIRECTORY: localDirectory("weather"),
      DETOT_COPY_AUTHORITY_DIR: localDirectory("authority"),
      DETOT_ENABLE_CALENDAR_TRIAL: "false",
    },
    stdio: "ignore",
    windowsHide: true,
  });
  let exitPromise;
  try {
    exitPromise = new Promise(resolve => child.once("exit", resolve));
    await waitForHealth(origin, child);
    const response = await postJson(origin, "/api/internal/autoskill/consent", { stage: "capture" }, {
      authorization: `Bearer ${TOKEN}`,
    });
    assert.equal(response.status, 404);
  } finally {
    if (child.exitCode === null) child.kill("SIGTERM");
    if (exitPromise) {
      await Promise.race([exitPromise, new Promise(resolve => setTimeout(resolve, 3000))]);
      if (child.exitCode === null) child.kill("SIGKILL");
    }
    fs.rmSync(root, { recursive: true, force: true });
  }
});
