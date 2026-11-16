import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import test from "node:test";

const origin = process.env.DETOT_CONSENT_TEST_ORIGIN;
const apiTest = origin ? test : test.skip;

async function request(path, method = "GET", body) {
  const response = await fetch(new URL(path, origin), {
    method,
    headers: { ...(body ? { "Content-Type": "application/json" } : {}), ...(method === "GET" ? {} : { Origin: origin }) },
    ...(body ? { body: JSON.stringify(body) } : {}),
  });
  return { response, data: await response.json().catch(() => null) };
}

async function setConsent(source, purpose, destination, allowed) {
  const { response } = await request("/api/consent", "POST", {
    source, purpose, destination, allowed, requestId: randomUUID(),
  });
  assert.equal(response.status, 200, `Could not set synthetic consent ${source}/${purpose}/${destination}`);
}

apiTest("backend denies Memory reads until the exact owner/source/purpose/destination grant exists", async () => {
  const policy = await request("/api/consent");
  assert.equal(policy.response.status, 200);
  assert.ok(policy.data.grants.every(grant => grant.allowed === false), "new isolated owner should default deny");

  const noteList = await request("/api/memory?category=note");
  assert.equal(noteList.response.status, 403);
  const taskList = await request("/api/memory/agentic/tasks");
  assert.equal(taskList.response.status, 403);
  const dreaming = await request("/api/memory/dreaming");
  assert.equal(dreaming.response.status, 200);
  assert.equal(dreaming.data.consented, false);
  const reviews = await request("/api/memory/review");
  assert.equal(reviews.response.status, 403);

  await setConsent("personal_notes", "direct_request", "DeepSeek", true);
  assert.equal((await request("/api/memory?category=note")).response.status, 403,
    "external model permission must not grant local read access");
  await setConsent("personal_notes", "direct_request", "DeepSeek", false);
  await setConsent("personal_notes", "direct_request", "local_only", true);
  const save = await request("/api/memory", "POST", {
    requestId: randomUUID(), category: "note", text: "synthetic consent API note",
  });
  assert.equal(save.response.status, 200);
  assert.match(save.data.reply, /Đã lưu ký ức/);
  assert.equal((await request("/api/memory?category=note")).response.status, 200);

  await setConsent("personal_notes", "direct_request", "local_only", false);
  assert.equal((await request("/api/memory?category=note")).response.status, 403,
    "revocation must block the next backend read");
  const audit = await request("/api/consent");
  assert.ok(audit.data.audit.some(item => item.decision === "revoked" && item.source === "personal_notes"));
  assert.ok(!JSON.stringify(audit.data.audit).includes("synthetic consent API note"),
    "audit metadata must not contain note content");
});
