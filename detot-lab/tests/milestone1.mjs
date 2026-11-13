import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { once } from "node:events";
import { access, mkdir, mkdtemp, readFile, readdir, writeFile } from "node:fs/promises";
import { createServer } from "node:http";
import { resolve } from "node:path";
import { setTimeout as sleep } from "node:timers/promises";
import { DatabaseSync } from "node:sqlite";
import { gunzipSync, zstdDecompressSync } from "node:zlib";

const lab = resolve(import.meta.dirname, "..");
const runtime = process.env.DETOT_TEST_OPENCLAW_ENTRY
  ? resolve(process.env.DETOT_TEST_OPENCLAW_ENTRY)
  : resolve(lab, ".local/openclaw-registry/node_modules/openclaw/openclaw.mjs");
try { await access(runtime); }
catch { throw new Error("OpenClaw candidate runtime is missing: " + runtime + ". Install the locked candidate before running this suite."); }
const localData = resolve(lab, ".local");
await mkdir(localData, { recursive: true });
const scratch = await mkdtemp(resolve(localData, "m1-"));
const base = JSON.parse(await readFile(resolve(lab, "gateway.lab.json"), "utf8"));
const token = randomBytes(32).toString("hex");
const live = process.argv.includes("--live");
const key = live ? process.env.DEEPSEEK_API_KEY?.trim() : undefined;
if (live && !key) throw new Error("Supply DEEPSEEK_API_KEY to the process for --live");
const results = [];
let embeddingRequests = 0;
let toolRequests = 0;
let plainRequests = 0;
let handlerFailed = false;
const marker = "DETOT-M1-731";
const fixtureTool = {
  type: "function", name: "detot_lab_fixture",
  description: "Return the synthetic verification code.",
  parameters: { type: "object", properties: {}, additionalProperties: false },
};

const mock = createServer(async (req, res) => {
  try {
    if (req.method === "GET") return res.writeHead(200).end("{}");
    const chunks = [];
    for await (const chunk of req) chunks.push(chunk);
    const body = JSON.parse(Buffer.concat(chunks).toString("utf8"));
    if (req.url === "/v1/embeddings") {
      embeddingRequests++;
      const inputs = Array.isArray(body.input) ? body.input : [body.input];
      assert(inputs.every(input => typeof input === "string"));
      const data = inputs.map((input, index) => {
        const related = /trà sen|thức uống ưa thích|đồ uống khoái khẩu|trà thơm/i.test(input);
        return { object: "embedding", index, embedding: related ? [1, 0, 0, 0] : [0, 1, 0, 0] };
      });
      return res.writeHead(200, { "content-type": "application/json" }).end(JSON.stringify({ object: "list", model: body.model, data }));
    }
    assert.equal(req.url, "/v1/chat/completions");
    const names = (body.tools ?? []).map(tool => tool.function?.name);
    assert(names.every(name => name === fixtureTool.name));
    let delta = { role: "assistant", content: marker };
    let finish = "stop";
    if (names.length) {
      assert.deepEqual(names, [fixtureTool.name]);
      toolRequests++;
      if (!JSON.stringify(body.messages).includes(marker)) {
        delta = { role: "assistant", tool_calls: [{ index: 0, id: "call_m1", type: "function", function: { name: fixtureTool.name, arguments: "{}" } }] };
        finish = "tool_calls";
      }
    } else {
      plainRequests++;
    }
    const chunk = (delta, reason) => JSON.stringify({ id: "chatcmpl-m1", object: "chat.completion.chunk", created: 1760000000, model: "fixture", choices: [{ index: 0, delta, finish_reason: reason }] });
    res.writeHead(200, { "content-type": "text/event-stream" })
      .end("data: " + chunk(delta, null) + "\n\ndata: " + chunk({}, finish) + "\n\ndata: [DONE]\n\n");
  } catch {
    handlerFailed = true;
    res.writeHead(400).end("Fixture rejected provider request");
  }
});
mock.listen(0, "127.0.0.1");
await once(mock, "listening");
const mockUrl = "http://127.0.0.1:" + mock.address().port + "/v1";

async function test(name, action) {
  try {
    const details = await action();
    results.push({ name, status: "PASS", details });
  } catch (error) {
    results.push({ name, status: "FAIL", details: error.safeDetail ?? error.code ?? error.name });
  }
  console.log(JSON.stringify(results.at(-1)));
}
function fail(detail) {
  throw Object.assign(new Error("Baseline assertion failed"), { safeDetail: detail });
}
async function setup(name, configure) {
  const state = resolve(scratch, name);
  const workspace = resolve(state, "workspace");
  await mkdir(resolve(workspace, "memory"), { recursive: true });
  const config = structuredClone(base);
  config.agents.defaults.workspace = workspace;
  if (!live || name === "memory") {
    config.agents.defaults.model = { primary: "m1fixture/fixture" };
    delete config.agents.defaults.models;
    config.models = { mode: "replace", providers: { m1fixture: {
      baseUrl: mockUrl, apiKey: "fixture-only", api: "openai-completions",
      models: [{ id: "fixture", name: "Fixture", reasoning: false, input: ["text"], contextWindow: 65536, maxTokens: 4096 }],
    } } };
  }
  await configure(config, workspace);
  const path = resolve(state, "gateway.json");
  await writeFile(path, JSON.stringify(config));
  const env = Object.fromEntries(Object.entries(process.env).filter(([name]) =>
    /^(systemroot|windir|comspec|path|pathext|temp|tmp|userprofile|localappdata|appdata|programdata|programfiles(?:\(x86\))?|processor_architecture|number_of_processors|username|userdomain|homedrive|homepath)$/i.test(name)));
  Object.assign(env, {
    OPENCLAW_STATE_DIR: state, OPENCLAW_CONFIG_PATH: path, OPENCLAW_GATEWAY_TOKEN: token,
    OPENCLAW_SKIP_CHANNELS: "1", OPENCLAW_DISABLE_BONJOUR: "1",
  });
  if (key && name === "gateway") env.DEEPSEEK_API_KEY = key;
  return { env, state, workspace };
}
function launch(profile, args) {
  return spawn(process.execPath, [runtime, ...args], {
    cwd: profile.state, env: profile.env, windowsHide: true, stdio: ["ignore", "pipe", "pipe"],
  });
}
async function cli(profile, args) {
  const child = launch(profile, args);
  let output = "", errors = "";
  child.stdout.setEncoding("utf8").on("data", text => { output += text; });
  child.stderr.setEncoding("utf8").on("data", text => { errors += text; });
  const timer = setTimeout(() => child.kill(), 240000);
  try {
    const [exit] = await once(child, "close");
    if (exit !== 0) {
      // CLI diagnostics are local to the synthetic profile. Emit only a short classification.
      const kind = /not enabled|not found|unknown|error|failed|unavailable|busy/i.exec(errors + output)?.[0] ?? "no diagnostic";
      fail("CLI " + args.slice(0, 2).join(" ") + " exit " + exit + " (" + kind + ")");
    }
    return output;
  } finally { clearTimeout(timer); }
}
function json(output) {
  const trimmed = output.trim();
  const start = trimmed.search(/^[\[{]/m);
  try { return JSON.parse(trimmed.slice(start)); }
  catch { fail("CLI returned unreadable JSON"); }
}

async function availablePort() {
  const socket = createServer();
  socket.listen(0, "127.0.0.1");
  await once(socket, "listening");
  const port = socket.address().port;
  await new Promise(resolve => socket.close(resolve));
  return port;
}

async function waitReady(url, child) {
  const deadline = Date.now() + 240000;
  while (Date.now() < deadline && child.exitCode === null) {
    const res = await fetch(url + "/readyz", { signal: AbortSignal.timeout(1500) }).catch(() => null);
    if (res?.ok) return { status: res.status };
    await sleep(750);
  }
  fail("Gateway did not reach readyz, exit=" + child.exitCode);
}

async function stopChild(child) {
  if (child.exitCode === null) {
    const ended = once(child, "close");
    child.kill();
    await ended;
  }
}

async function memoryBaseline() {
  let profile;
  const original = "# Durable synthetic memory\n\n- Nhân vật thử A thích trà sen.\n";
  await test("Memory Core index and paraphrase search", async () => {
    profile = await setup("memory", async (config, workspace) => {
      config.plugins = { enabled: true, allow: ["memory-core"], slots: { memory: "memory-core" },
        entries: { "memory-core": { enabled: true, config: { dreaming: { enabled: true } } } } };
      config.memory.search = { enabled: true, provider: "openai-compatible", fallback: "none",
        model: "m1-fixture-embedding", remote: { baseUrl: mockUrl } };
      await writeFile(resolve(workspace, "MEMORY.md"), original);
      await writeFile(resolve(workspace, "memory", new Date().toISOString().slice(0, 10) + ".md"),
        "# Synthetic daily note\n\n- Nhân vật thử B chọn xe đạp màu tím.\n");
    });
    await cli(profile, ["memory", "index", "--agent", "main"]);
    const output = await cli(profile, ["memory", "search", "--query", "Nhân vật thử A có đồ uống khoái khẩu nào?", "--agent", "main", "--max-results", "2", "--json"]);
    const data = json(output);
    const matches = Array.isArray(data) ? data : data.results;
    if (!matches?.length) fail("No recall result; embedding requests=" + embeddingRequests);
    assert.match(JSON.stringify(matches[0]), /trà sen/);
    assert(embeddingRequests > 0);
    return { embeddingRequests, topPath: matches[0].path ?? null };
  });
  if (profile) await test("Dreaming deep-phase preview", async () => {
    const output = await cli(profile, ["memory", "promote", "--agent", "main", "--json"]);
    const data = json(output);
    assert.equal(await readFile(resolve(profile.workspace, "MEMORY.md"), "utf8"), original);
    return { previewOnly: true, fields: Object.keys(data), candidateCount: Array.isArray(data.candidates) ? data.candidates.length : null };
  });
}

async function gatewayBaseline() {
  const port = await availablePort();
  const url = "http://127.0.0.1:" + port;
  const profile = await setup("gateway", config => { config.gateway.port = port; });
  const child = launch(profile, ["gateway", "run"]);
  child.stdout.resume(); child.stderr.resume();
  try {
    await test("Gateway readiness", () => waitReady(url, child));
    if (results.find(row => row.name === "Gateway readiness")?.status !== "PASS") return;
    const payload = { model: "openclaw/default", input: "Hãy dùng tool giả lập để lấy mã thử nghiệm.", tools: [fixtureTool] };
    function post(body, auth = token) {
      return fetch(url + "/v1/responses", { method: "POST",
        headers: { authorization: "Bearer " + auth, "content-type": "application/json" },
        body: JSON.stringify(body), signal: AbortSignal.timeout(100000) });
    }
    await test("Unauthorized bearer and unknown continuation", async () => {
      const denied = await post(payload, "fixture-wrong-bearer");
      assert.equal(denied.status, 401);
      const missing = await post({ ...payload, previous_response_id: "resp_m1_nonexistent" });
      assert.equal(missing.status, 400);
      return { denied: denied.status, missing: missing.status };
    });
    await test(live ? "DeepSeek live tool calling and continuation" : "Mock provider tool calling and continuation", async () => {
      const before = toolRequests;
      const first = await post({ ...payload, tool_choice: { type: "function", name: fixtureTool.name } });
      if (!first.ok) fail("First request HTTP " + first.status);
      const firstBody = await first.json();
      const calls = firstBody.output.filter(x => x.type === "function_call");
      assert.equal(calls.length, 1);
      assert.equal(calls[0].name, fixtureTool.name);
      assert.deepEqual(JSON.parse(calls[0].arguments), {});
      const second = await post({ ...payload, instructions: "Đọc kết quả tool và trả đúng mã, không gọi tool lần nữa.",
        previous_response_id: firstBody.id, input: [{ type: "function_call_output", call_id: calls[0].call_id, output: marker }] });
      if (!second.ok) fail("Continuation HTTP " + second.status);
      const final = await second.json();
      assert(!final.output.some(x => x.type === "function_call"));
      assert(final.output.some(x => x.type === "message" && x.content?.some(part => part.text?.includes(marker))));
      if (!live) assert.equal(toolRequests - before, 2);
      return { firstHttp: first.status, secondHttp: second.status, upstreamToolRequests: live ? null : toolRequests - before };
    });
  } finally {
    await stopChild(child);
  }
}

async function dreamingSweep() {
  await test("Managed Dreaming sweep on unconfirmed fixture", async () => {
    const port = await availablePort();
    const durable = "# Synthetic durable memory\n\n- Nhân vật A thích trà sen.\n";
    const profile = await setup("memory", async (config, workspace) => {
      config.gateway.port = port;
      config.cron.enabled = true;
      config.plugins = { enabled: true, allow: ["memory-core"], slots: { memory: "memory-core" },
        entries: { "memory-core": { enabled: true, config: { dreaming: { enabled: true, frequency: "0 3 * * *" } } } } };
      config.memory.search = { enabled: true, provider: "openai-compatible", fallback: "none",
        model: "m1-fixture-embedding", remote: { baseUrl: mockUrl } };
      await writeFile(resolve(workspace, "MEMORY.md"), durable);
      await writeFile(resolve(workspace, "memory", new Date().toISOString().slice(0, 10) + ".md"),
        "# Synthetic daily note\n\n- Chưa xác nhận nhân vật B có thích xe đạp tím hay không.\n");
    });
    const child = launch(profile, ["gateway", "run"]);
    child.stdout.resume(); child.stderr.resume();
    try {
      await waitReady("http://127.0.0.1:" + port, child);
      let job;
      for (let attempt = 0; attempt < 3 && !job; attempt++) {
        const list = json(await cli(profile, ["cron", "list", "--all", "--json"]));
        job = (Array.isArray(list) ? list : list.jobs)?.find(item => /dreaming/i.test(JSON.stringify(item)));
        if (!job) await sleep(3000);
      }
      if (!job?.id) fail("Managed Dreaming job not found");
      const run = json(await cli(profile, ["cron", "run", job.id, "--wait", "--wait-timeout", "90s", "--json"]));
      assert.equal(await readFile(resolve(profile.workspace, "MEMORY.md"), "utf8"), durable);
      return { jobFound: true, runCompleted: true, completion: run.completionStatus ?? run.status ?? null, durableUnchanged: true };
    } finally { await stopChild(child); }
  });
}

async function retentionBaseline() {
  const port = await availablePort();
  const profile = await setup("retention", config => {
    config.gateway.port = port;
    // Keep only the one synthetic tool; send a tool result on test turns so the fixture replies.
  });
  const url = "http://127.0.0.1:" + port;
  const launchGateway = () => {
    const child = launch(profile, ["gateway", "run"]);
    child.stdout.resume();
    child.stderr.resume();
    return child;
  };
  let gateway = launchGateway();
  let expiredId;
  let controlId;
  let deletionId;
  let relatedIds = [];
  let sessionKey;
  const canary = "DETOT-M1-DELETE-7E39";
  const privateCanary = "DETOT-M1-INCOGNITO-9F12";
  const dbPath = resolve(profile.state, "state/openclaw.sqlite");
  function request(input, { previous, session } = {}) {
    return fetch(url + "/v1/responses", {
      method: "POST",
      headers: { authorization: "Bearer " + token, "content-type": "application/json",
        ...(session ? { "x-openclaw-session-key": session } : {}) },
      body: JSON.stringify({ model: "openclaw/default", input, tools: [fixtureTool], previous_response_id: previous }),
      signal: AbortSignal.timeout(120000),
    });
  }
  async function turn(label, session) {
    const res = await request(marker + " " + label, { session });
    if (!res.ok) fail("Fixture turn HTTP " + res.status);
    return (await res.json()).id;
  }
  function mapping(id) {
    const db = new DatabaseSync(dbPath, { readOnly: true });
    try {
      const row = db.prepare("SELECT value_json, expires_at FROM plugin_state_entries WHERE plugin_id = ? AND namespace = ? AND entry_key = ?")
        .get("core:openresponses", "response-sessions", id);
      return row ? { ...JSON.parse(row.value_json), expiresAt: row.expires_at } : undefined;
    } finally { db.close(); }
  }
  function mappingsForSession(key) {
    const db = new DatabaseSync(dbPath, { readOnly: true });
    try {
      return db.prepare("SELECT entry_key, value_json, expires_at FROM plugin_state_entries WHERE plugin_id = ? AND namespace = ?")
        .all("core:openresponses", "response-sessions")
        .filter(row => JSON.parse(row.value_json).sessionKey === key)
        .map(row => ({ id: row.entry_key, ...JSON.parse(row.value_json), expiresAt: row.expires_at }));
    } finally { db.close(); }
  }
  function databaseLeaks(dbFile, needles) {
    const db = new DatabaseSync(dbFile, { readOnly: true });
    const found = [];
    try {
      const tables = db.prepare("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'").all();
      for (const { name } of tables) {
        const quoted = '"' + name.replaceAll('"', '""') + '"';
        const columns = db.prepare("PRAGMA table_info(" + quoted + ")").all();
        if (!columns.length) continue;
        const projection = columns.map(column => '"' + column.name.replaceAll('"', '""') + '"').join(", ");
        for (const row of db.prepare("SELECT " + projection + " FROM " + quoted).iterate()) {
          for (const value of Object.values(row)) {
            if (typeof value === "string" && needles.some(needle => value.includes(needle))) {
              found.push({ table: name });
              break;
            }
          }
        }
      }
    } finally { db.close(); }
    return found;
  }
  async function storageLeaks(root, needles) {
    const entries = await readdir(root, { recursive: true, withFileTypes: true });
    const found = [];
    for (const entry of entries) {
      if (!entry.isFile()) continue;
      const path = resolve(entry.parentPath, entry.name);
      const bytes = await readFile(path);
      let text;
      try {
        text = entry.name.endsWith(".zst") ? zstdDecompressSync(bytes).toString("utf8")
          : entry.name.endsWith(".gz") ? gunzipSync(bytes).toString("utf8")
          : bytes.toString("utf8");
      }
      catch { continue; }
      if (needles.some(needle => text.includes(needle))) found.push(entry.name);
    }
    return found;
  }
  async function sqliteLeaks(root, needles) {
    const entries = await readdir(root, { recursive: true, withFileTypes: true });
    const found = [];
    for (const entry of entries) {
      if (!entry.isFile() || !/\.(?:sqlite3?|db3?)$/i.test(entry.name)) continue;
      const path = resolve(entry.parentPath, entry.name);
      try { found.push(...databaseLeaks(path, needles).map(item => ({ file: entry.name, table: item.table }))); }
      catch { found.push({ file: entry.name, table: "unreadable-database" }); }
    }
    return found;
  }
  try {
    await test("Retention Gateway readiness", () => waitReady(url, gateway));
    if (results.at(-1).status !== "PASS") return;
    await test("Incognito explicit session and no response mapping", async () => {
      const created = json(await cli(profile, ["gateway", "call", "sessions.create", "--json", "--params",
        JSON.stringify({ agentId: "main", incognito: true })]));
      const session = created.key ?? created.sessionKey ?? created.session?.key;
      if (!session) fail("sessions.create did not return a session key");
      const id = await turn(privateCanary, session);
      assert.equal(mapping(id), undefined);
      assert.equal((await request(marker, { session, previous: id })).status, 400);
      await turn(privateCanary + "-SECOND", session);
      return { explicitContinuation: true, responseMapping: false, responseIdStatus: 400 };
    });
    await test("Persisted continuation TTL fixture setup", async () => {
      expiredId = await turn(canary);
      controlId = await turn("DETOT-M1-TTL-CONTROL");
      const stored = mapping(expiredId);
      sessionKey = stored?.sessionKey;
      assert(sessionKey && stored.expiresAt > Date.now());
      assert(mapping(controlId)?.expiresAt > Date.now());
      const deletion = await request(marker, { previous: expiredId });
      assert.equal(deletion.status, 200);
      deletionId = (await deletion.json()).id;
      assert.equal(mapping(deletionId)?.sessionKey, sessionKey);
      relatedIds = [expiredId, deletionId];
      return { positiveTtl: true, storedMappings: 3 };
    });
    if (results.at(-1).status !== "PASS") return;
    await stopChild(gateway);
    const db = new DatabaseSync(dbPath);
    try {
      // Age only this mapping in the stopped synthetic profile. Keep the control mapping unchanged.
      const result = db.prepare("UPDATE plugin_state_entries SET expires_at = ? WHERE plugin_id = ? AND namespace = ? AND entry_key = ?")
        .run(Date.now() - 1000, "core:openresponses", "response-sessions", expiredId);
      assert.equal(result.changes, 1);
    } finally { db.close(); }
    gateway = launchGateway();
    await test("Expired mapping rejected after restart; control survives", async () => {
      await waitReady(url, gateway);
      const expired = await request(marker, { previous: expiredId });
      const control = await request(marker, { previous: controlId });
      assert.equal(expired.status, 400);
      assert.equal(control.status, 200);
      return { expiredHttp: expired.status, validHttp: control.status };
    });
    await test("Cross-store deletion and restart revocation of ordinary synthetic session", async () => {
      const controlMappings = mappingsForSession(mapping(controlId).sessionKey);
      assert(controlMappings.some(item => item.id === controlId), "control continuation must exist before deletion");
      const preDeleteMappings = mappingsForSession(sessionKey);
      relatedIds = [...new Set([...relatedIds, ...preDeleteMappings.map(item => item.id)])];
      assert(relatedIds.length >= 2, "test must exercise multiple continuation IDs for one session");
      assert((await storageLeaks(profile.state, [canary])).length > 0, "canary must be present in persisted files before deletion");
      assert((await sqliteLeaks(profile.state, [canary])).length > 0, "canary must be present in indexed/database storage before deletion");
      await cli(profile, ["sessions", "delete", sessionKey, "--yes", "--json"]);
      const retainedMappings = mappingsForSession(sessionKey).map(item => item.id);
      const remainingSessions = await cli(profile, ["sessions", "--json"]);
      if (remainingSessions.includes(sessionKey)) fail("Deleted session key remains listed");
      const responseStatuses = [];
      for (const id of relatedIds) responseStatuses.push((await request(marker, { previous: id })).status);
      const archiveRoot = profile.state;
      const diskLeaks = await storageLeaks(archiveRoot, [canary]);
      const dbLeaks = await sqliteLeaks(archiveRoot, [canary, ...relatedIds]);
      const restartedContinuationStatuses = [];
      await stopChild(gateway);
      gateway = launchGateway();
      await waitReady(url, gateway);
      for (const id of relatedIds) restartedContinuationStatuses.push((await request(marker, { previous: id })).status);
      const survivingControl = await request(marker, { previous: controlId });
      if (survivingControl.status !== 200) fail("Unrelated control continuation did not survive deletion/restart");
      if (retainedMappings.length || responseStatuses.some(status => status !== 400) || restartedContinuationStatuses.some(status => status !== 400)) {
        fail("Deleted session mappings=" + retainedMappings.length + "; before-restart continuation statuses=" + responseStatuses.join(",") + "; after-restart=" + restartedContinuationStatuses.join(","));
      }
      if (diskLeaks.length || dbLeaks.length) fail("Deleted content remains in files=" + diskLeaks.length + "; SQLite tables=" + dbLeaks.map(item => item.table).join(","));
      return { revokedContinuationIds: relatedIds.length, allRejectedBeforeAndAfterRestart: true, diskCanaryLeaks: 0, sqliteLeaks: 0, unrelatedControlSurvived: true };
    });
  } finally { await stopChild(gateway); }
}

try {
  if (live) await gatewayBaseline();
  else if (process.argv.includes("--dreaming")) await dreamingSweep();
  else if (process.argv.includes("--retention")) await retentionBaseline();
  else await Promise.all([memoryBaseline(), gatewayBaseline()]);
  if (!live) {
    results.push({ name: "DeepSeek live tool calling and continuation", status: "BLOCKED", details: "Run separately with a user-provisioned DeepSeek key" });
    console.log(JSON.stringify(results.at(-1)));
  }
  assert(!handlerFailed, "Mock rejected a request");
} finally {
  mock.close();
  await writeFile(resolve(scratch, "results.json"), JSON.stringify({ results, embeddingRequests, toolRequests, plainRequests }, null, 2));
  console.log("Local evidence: " + scratch);
}
if (results.some(result => result.status === "FAIL")) process.exitCode = 1;
