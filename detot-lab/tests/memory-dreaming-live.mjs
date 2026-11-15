import assert from "node:assert/strict";
import http from "node:http";
import { randomUUID } from "node:crypto";
import fs from "node:fs";

const origin = process.env.DETOT_MEMORY_DREAMING_ORIGIN;
const publicOrigin = process.env.DETOT_MEMORY_DREAMING_PUBLIC_ORIGIN;
const statePath = process.env.DETOT_MEMORY_DREAMING_STATE_PATH;
const ownerPassword = process.env.DETOT_REMOTE_PASSWORD;
const memberPassword = process.env.DETOT_MEMORY_MEMBER_PASSWORD;
assert.ok(origin && publicOrigin && statePath && ownerPassword && memberPassword, "Use an isolated authenticated M3 Backend.");
const host = new URL(publicOrigin).host;
let lastLoginAt = 0;

async function raw(method, path, cookie, value, form = false) {
  const target = new URL(origin + path);
  const body = value === undefined ? "" : form ? new URLSearchParams(value).toString() : JSON.stringify(value);
  return await new Promise((resolve, reject) => {
    const request = http.request({ hostname: target.hostname, port: target.port, path: target.pathname + target.search,
      method, headers: { Host: host, Origin: publicOrigin, ...(cookie ? { Cookie: cookie } : {}),
        ...(value === undefined ? {} : { "Content-Type": form ? "application/x-www-form-urlencoded" : "application/json",
          "Content-Length": Buffer.byteLength(body) }) } }, response => {
      let responseBody = "";
      response.setEncoding("utf8");
      response.on("data", chunk => { responseBody += chunk; });
      response.on("end", () => resolve({ status: response.statusCode, body: responseBody,
        cookie: response.headers["set-cookie"]?.[0]?.split(";")[0] }));
    });
    request.setTimeout(65000, () => request.destroy(new Error("HTTP test request timed out.")));
    request.on("error", reject);
    request.end(body);
  });
}
async function login(password) {
  const wait = 2200 - (Date.now() - lastLoginAt);
  if (wait > 0) await new Promise(resolve => setTimeout(resolve, wait));
  const result = await raw("POST", "/login", undefined, { password }, true);
  assert.equal(result.status, 302, `Login failed: ${result.body}`);
  lastLoginAt = Date.now();
  assert.ok(result.cookie);
  return result.cookie;
}
async function call(method, path, cookie, value) {
  const result = await raw(method, path, cookie, value);
  assert.ok(result.status >= 200 && result.status < 300,
    `Expected success for ${method} ${path}, received HTTP ${result.status}`);
  return result.status === 204 ? null : JSON.parse(result.body);
}
async function create(cookie, text, category = "task") {
  const result = await call("POST", "/api/memory", cookie, { text, category, requestId: randomUUID() });
  const id = result.reply.match(/mã ([a-f0-9-]{36})/i)?.[1];
  assert.ok(id, "A new encrypted memory ID must be returned.");
  return id;
}
async function waitForSuggestion(cookie, expectedIds, timeoutMs = 180000) {
  const deadline = Date.now() + timeoutMs;
  do {
    const suggestions = await call("GET", "/api/memory/review", cookie);
    const found = suggestions.find(item => expectedIds.every(id => item.evidence.some(evidence => evidence.id === id)));
    if (found) return found;
    await new Promise(resolve => setTimeout(resolve, 2000));
  } while (Date.now() < deadline);
  throw new Error("Dreaming did not produce the expected evidence-backed proposal before timeout.");
}
async function waitForCompletedSweep(cookie, timeoutMs = 60000) {
  const deadline = Date.now() + timeoutMs;
  do {
    const status = await call("GET", "/api/memory/dreaming", cookie);
    if (!status.pending) return status;
    await new Promise(resolve => setTimeout(resolve, 1000));
  } while (Date.now() < deadline);
  throw new Error("Dreaming sweep did not persist its checkpoint before timeout.");
}

const phase = process.argv[2];
if (phase === "checkpoint-seed") {
  const owner = await login(ownerPassword);
  const marker = randomUUID().slice(0, 8);
  const categories = ["device", "person", "preference", "event"];
  const texts = Array.from({ length: 48 }, (_, index) =>
    `M3 isolated batch ${marker}: entry ${index} records ${randomUUID()} for independent archive verification.`);
  const ids = [];
  for (let index = 0; index < texts.length; index++) ids.push(await create(owner, texts[index], categories[index % categories.length]));
  const deadline = Date.now() + 180000;
  let status;
  do {
    status = await call("GET", "/api/memory/dreaming", owner);
    if (status.scanned >= 40 && status.scanned < status.total) break;
    await new Promise(resolve => setTimeout(resolve, 250));
  } while (Date.now() < deadline);
  assert.ok(status.scanned >= 40 && status.scanned < status.total,
    `Expected an in-progress saved batch checkpoint, received ${JSON.stringify(status)}`);
  fs.writeFileSync(statePath, JSON.stringify({ ids, total: texts.length, checkpoint: status }));
  console.log("PASS: Dreaming saved an encrypted partial cursor between bounded 40-item batches");
}
else if (phase === "checkpoint-resume") {
  const state = JSON.parse(fs.readFileSync(statePath, "utf8"));
  const owner = await login(ownerPassword);
  const before = await call("GET", "/api/memory/dreaming", owner);
  assert.deepEqual(before, state.checkpoint, "Partial worker cursor must survive Backend restart.");
  assert.ok(before.pending && before.scanned < before.total);
  const after = await waitForCompletedSweep(owner, 90000);
  assert.equal(after.total, state.total);
  assert.equal(after.scanned, state.total);
  const records = await call("GET", "/api/memory", owner);
  assert.ok(state.ids.every(id => records.some(item => item.id === id)), "Restart must preserve every source memory.");
  console.log("PASS: restarted worker resumed remaining batch from its saved checkpoint without losing source memories");
}
else if (phase === "seed") {
  const owner = await login(ownerPassword);
  const member = await login(memberPassword);
  const marker = randomUUID().slice(0, 8);
  const duplicateText = `Kiểm tra máy in phòng lab M3 ${marker}.`;
  const duplicateIds = [await create(owner, duplicateText), await create(owner, duplicateText)];
  const progressedText = `Đã kiểm tra trạng thái máy in tiến triển hợp lệ M3 ${marker}.`;
  const progressed = await create(owner, progressedText);
  await create(owner, progressedText);
  await call("POST", `/api/memory/${progressed}/complete`, owner, { version: 1 });
  const conflictTexts = [
    "Inventory record for the reception printer lists assigned IP address as 10.20.0.41.",
    "Inventory record for the reception printer lists assigned IP address as 10.20.0.94.",
  ];
  const conflictIds = [await create(owner, conflictTexts[0], "device"), await create(owner, conflictTexts[1], "device")];
  const outdatedTexts = [
    "The inventory record dated 2022-04-10 lists the reception printer location as Room 204.",
    "The inventory record dated 2025-08-20 lists the reception printer location as Room 307.",
  ];
  const outdatedIds = [await create(owner, outdatedTexts[0], "device"), await create(owner, outdatedTexts[1], "device")];
  const memberText = `Thông tin riêng của tài khoản B M3 ${marker}.`;
  const memberIds = [await create(member, memberText), await create(member, memberText)];
  const progressIds = (await call("GET", "/api/memory", owner)).filter(item => item.text === progressedText).map(item => item.id);
  assert.equal(progressIds.length, 2);
  const initialSuggestion = await waitForSuggestion(owner, duplicateIds);
  const conflictSuggestion = await waitForSuggestion(owner, conflictIds);
  const outdatedSuggestion = await waitForSuggestion(owner, outdatedIds);
  await waitForSuggestion(member, memberIds);
  const checkpoint = await waitForCompletedSweep(owner);
  assert.ok(checkpoint.total >= 4 && checkpoint.scanned === checkpoint.total, JSON.stringify(checkpoint));
  const ownerRecords = await call("GET", "/api/memory", owner);
  assert.equal(ownerRecords.filter(item => duplicateIds.includes(item.id)).length, 2,
    "Dreaming must leave official memories untouched before approval.");
  const store = fs.readdirSync(process.env.DETOT_PRACTICE_DIRECTORY)
    .filter(name => name.startsWith("memory-") && name.endsWith(".bin"));
  assert.ok(store.length >= 3, "Expected separate encrypted owner and proposal stores.");
  for (const name of store) {
    const bytes = fs.readFileSync(`${process.env.DETOT_PRACTICE_DIRECTORY}/${name}`);
    assert.equal(bytes.includes(Buffer.from(duplicateText)), false, "Memory plaintext must not appear in encrypted storage.");
    assert.equal(bytes.includes(Buffer.from(memberText)), false, "Other owner's plaintext must not appear in encrypted storage.");
  }
  const state = { marker, duplicateText, duplicateIds, progressedText, progressIds, conflictTexts, conflictIds,
    outdatedTexts, outdatedIds, memberText, memberIds,
    initialSuggestionId: initialSuggestion.id, checkpoint };
  assert.equal(conflictSuggestion.kind, "update");
  assert.ok(["supersede", "update"].includes(outdatedSuggestion.kind));
  fs.writeFileSync(statePath, JSON.stringify(state));
  console.log("PASS: BGE-M3 candidate retrieval and DeepSeek proposal generation ran against isolated encrypted owner data");
  console.log("PASS: DeepSeek classified exact duplicates, conflicting claims and dated outdated information; it left valid task progress alone");
  console.log("PASS: owner-specific queue includes exact evidence for duplicate fixture");
  console.log("PASS: status lifecycle fixture seeded for valid-transition check");
}
else if (phase === "verify") {
  const state = JSON.parse(fs.readFileSync(statePath, "utf8"));
  const owner = await login(ownerPassword);
  const member = await login(memberPassword);
  const resumed = await waitForCompletedSweep(owner);
  assert.deepEqual(resumed, state.checkpoint, "Owner checkpoint must survive restart without rescanning completed work.");
  const ownerSuggestions = await call("GET", "/api/memory/review", owner);
  const duplicate = ownerSuggestions.find(item => state.duplicateIds.every(id => item.evidence.some(e => e.id === id)));
  const conflict = ownerSuggestions.find(item => item.kind === "update" && state.conflictIds.every(id => item.evidence.some(e => e.id === id)));
  const outdated = ownerSuggestions.find(item => ["supersede", "update"].includes(item.kind) &&
    state.outdatedIds.every(id => item.evidence.some(e => e.id === id)));
  assert.ok(duplicate, "Encrypted proposal and checkpoint must survive Backend restart.");
  assert.ok(conflict && outdated, "Conflict and outdated-information proposals must survive restart.");
  assert.equal(new Set(ownerSuggestions.map(item => item.fingerprint)).size, ownerSuggestions.length,
    "Retry/restart must not duplicate the same proposal.");
  assert.ok(!ownerSuggestions.some(item => state.progressIds.some(id => item.evidence.some(e => e.id === id))),
    "A valid open→done lifecycle must not be proposed as a contradiction.");
  assert.ok(conflict,
    "Conflicting claims should create an unverified, evidence-linked update proposal.");
  assert.ok(outdated,
    "Explicitly dated information should be detected as potentially outdated.");
  const unchanged = await call("GET", "/api/memory", owner);
  assert.equal(unchanged.filter(item => state.duplicateIds.includes(item.id)).length, 2,
    "A pending Dreaming suggestion must not edit primary memory before approval.");
  assert.ok(state.conflictIds.every(id => unchanged.some(item => item.id === id && state.conflictTexts.includes(item.text))),
    "Conflicting source claims must remain unchanged until review.");
  assert.ok(state.outdatedIds.every(id => unchanged.some(item => item.id === id && state.outdatedTexts.includes(item.text))),
    "Potentially outdated source claims must remain unchanged until review.");
  const conflictEditText = "Verified by owner review: reception printer IP is 10.20.0.94.";
  assert.equal((await raw("POST", `/api/memory/review/${conflict.id}`, owner,
    { action: "edit", text: conflictEditText })).status, 204);
  const editedClaim = await call("GET", `/api/memory/${state.conflictIds[0]}`, owner);
  assert.equal(editedClaim.text, conflictEditText);
  assert.ok((await call("GET", `/api/memory/${state.conflictIds[0]}/history`, owner))
    .some(item => item.action === "dreaming-update-approved"));
  assert.equal((await call("GET", `/api/memory/${state.conflictIds[1]}`, owner)).status, "archived");
  assert.equal((await raw("POST", `/api/memory/review/${outdated.id}`, owner, { action: "reject" })).status, 204);
  assert.ok(!(await call("GET", "/api/memory/review", owner)).some(item => item.id === outdated.id));
  console.log("PASS: owner edited a conflict proposal, rejected an outdated proposal and kept source history");
  const memberSuggestions = await call("GET", "/api/memory/review", member);
  const memberProposal = memberSuggestions.find(item => state.memberIds.every(id => item.evidence.some(e => e.id === id)));
  assert.ok(memberProposal);
  assert.ok(!ownerSuggestions.some(item => item.id === memberProposal.id), "Owner A must not see User B proposals.");
  assert.equal((await raw("GET", `/api/memory/${state.memberIds[0]}`, owner)).status, 404,
    "Owner A must not read User B memory by ID.");
  const ownerHits = await call("GET", `/api/memory?q=${encodeURIComponent(state.memberText)}`, owner);
  assert.ok(ownerHits.every(hit => !state.memberIds.includes(hit.id) && hit.text !== state.memberText),
    "Semantic/keyword retrieval must never return the other owner's memory or vector result.");
  console.log("PASS: encrypted proposal queue and completed checkpoint survived Backend restart without duplicate work");
  console.log("PASS: valid state transition was not misclassified as contradiction");
  console.log("PASS: User A cannot read, search, list, or approve User B memory suggestions");

  // A source edit invalidates the proposal while it waits for review.
  const evidence = duplicate.evidence[0];
  await call("PATCH", `/api/memory/${evidence.id}`, owner,
    { text: evidence.text + " edited while waiting", version: evidence.version });
  assert.equal((await raw("POST", `/api/memory/review/${duplicate.id}`, owner, { action: "approve" })).status, 409,
    "Approval must fail with conflict when evidence has changed.");
  console.log("PASS: changed evidence invalidates a pending proposal instead of applying stale data");

  const approvalText = `Gộp hai ghi chú trùng máy in M3 ${state.marker}.`;
  const approvalIds = [await create(owner, approvalText), await create(owner, approvalText)];
  const ready = await waitForSuggestion(owner, approvalIds);
  assert.equal(ready.kind, "merge", "Exact duplicate pair should produce an explicit merge proposal.");
  const approvals = await Promise.all([
    raw("POST", `/api/memory/review/${ready.id}`, owner, { action: "approve" }),
    raw("POST", `/api/memory/review/${ready.id}`, owner, { action: "approve" }),
  ]);
  assert.deepEqual(approvals.map(result => result.status).sort(), [204, 404], "Concurrent double approval must apply only once.");
  const keeper = await call("GET", `/api/memory/${approvalIds[0]}/history`, owner);
  assert.ok(keeper.some(item => item.action === "dreaming-merge-approved"));
  const archived = await call("GET", `/api/memory/${approvalIds[1]}`, owner);
  assert.equal(archived.status, "archived", "Archived duplicate remains owner-readable for audit but leaves normal search.");
  assert.ok((await call("GET", `/api/memory/${approvalIds[1]}/history`, owner))
    .some(item => item.action === "dreaming-archive-approved"));
  console.log("PASS: one concurrent approval applied; memory history records the approved merge and duplicate is archived");
}
else throw new Error("Use phase seed, verify, checkpoint-seed or checkpoint-resume.");
