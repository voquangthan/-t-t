import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import fs from "node:fs";

const origin = process.env.DETOT_AGENTIC_TEST_ORIGIN;
const statePath = process.env.DETOT_AGENTIC_TEST_STATE;
const phase = process.argv[2];
assert.ok(origin && statePath && ["seed", "resume"].includes(phase));
const headers = { "Content-Type": "application/json", Origin: origin };
const send = (path, method, body) => fetch(new URL(path, origin), {
  method, headers, ...(body === undefined ? {} : { body: JSON.stringify(body) }),
});

if (phase === "seed") {
  const create = await send("/api/memory/", "POST", {
    requestId: randomUUID(), category: "task", text: "M4 restart reservation test",
  });
  assert.equal(create.status, 200);
  const created = await create.json();
  const id = created.reply.match(/[0-9a-f]{8}-[0-9a-f-]{27}/i)?.[0];
  assert.ok(id);
  const task = (await (await send("/api/memory/agentic/tasks", "GET")).json())
    .find(item => item.id.toLowerCase() === id.toLowerCase());
  assert.ok(task);
  const requestId = randomUUID();
  const data = { version: task.version, requestId, start: "2099-03-01T10:00:00Z",
    end: "2099-03-01T10:15:00Z", reminderMinutes: [10] };
  const response = await send(`/api/memory/${id}/calendar-reminder`, "POST", data);
  assert.equal(response.status, 503, "missing OAuth in isolated instance cannot be reported as success");
  const pending = (await (await send("/api/memory/agentic/tasks", "GET")).json())
    .find(item => item.id.toLowerCase() === id.toLowerCase());
  assert.equal(pending.calendarRequestId, requestId);
  fs.writeFileSync(statePath, JSON.stringify({ id, data }));
  console.log("PASS: encrypted memory contains an idempotent pending Calendar reservation before restart");
} else {
  const { id, data } = JSON.parse(fs.readFileSync(statePath, "utf8"));
  const tasks = await (await send("/api/memory/agentic/tasks", "GET")).json();
  const pending = tasks.find(item => item.id.toLowerCase() === id.toLowerCase());
  assert.ok(pending, "task survives Backend restart");
  assert.equal(pending.calendarRequestId, data.requestId);
  assert.equal(pending.calendarEventId, null);
  const retry = await send(`/api/memory/${id}/calendar-reminder`, "POST", data);
  assert.equal(retry.status, 503, "same request resumes safely without a false success");
  const competitor = await send(`/api/memory/${id}/calendar-reminder`, "POST", {
    ...data, requestId: randomUUID(), version: pending.version,
    start: "2099-03-02T10:00:00Z", end: "2099-03-02T10:15:00Z",
  });
  assert.equal(competitor.status, 409, "concurrent competing request cannot create a second Calendar event");
  console.log("PASS: encrypted reservation survived restart; retry and competing request are idempotent");
}
