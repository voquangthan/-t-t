import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import test from "node:test";

const origin = process.env.DETOT_AGENTIC_TEST_ORIGIN;
if (!origin) throw new Error("Set DETOT_AGENTIC_TEST_ORIGIN to an isolated Backend instance.");
const json = { "Content-Type": "application/json", Origin: origin };

test("task list is persistent and reminder mutations require an explicit safe request", async () => {
  const requestId = randomUUID();
  const created = await fetch(new URL("/api/memory/", origin), {
    method: "POST", headers: json,
    body: JSON.stringify({ requestId, category: "task", text: "M4 isolated reminder test" }),
  });
  assert.equal(created.status, 200);
  const response = await created.json();
  const id = response.reply.match(/[0-9a-f]{8}-[0-9a-f-]{27}/i)?.[0];
  assert.ok(id, "create response contains its persistent Memory ID");

  const listed = await fetch(new URL("/api/memory/agentic/tasks", origin));
  assert.equal(listed.status, 200);
  const tasks = await listed.json();
  const task = tasks.find(item => item.id.toLowerCase() === id.toLowerCase());
  assert.ok(task);
  assert.equal(task.status, "open");
  assert.equal(task.calendarEventId, null);

  const past = await fetch(new URL(`/api/memory/${id}/calendar-reminder`, origin), {
    method: "POST", headers: json,
    body: JSON.stringify({ version: task.version, requestId: randomUUID(),
      start: "2000-01-01T10:00:00Z", end: "2000-01-01T10:15:00Z", reminderMinutes: [10] }),
  });
  assert.equal(past.status, 400, "past reminders must be rejected before calling Google");

  const wrongOrigin = await fetch(new URL(`/api/memory/${id}/calendar-reminder`, origin), {
    method: "POST", headers: { ...json, Origin: "https://invalid.example" },
    body: JSON.stringify({ version: task.version, requestId: randomUUID(),
      start: "2099-01-01T10:00:00Z", end: "2099-01-01T10:15:00Z", reminderMinutes: [10] }),
  });
  assert.equal(wrongOrigin.status, 403);

  const missingTask = await fetch(new URL(`/api/memory/${randomUUID()}/calendar-reminder`, origin), {
    method: "POST", headers: json,
    body: JSON.stringify({ version: task.version, requestId: randomUUID(),
      start: "2099-01-01T10:00:00Z", end: "2099-01-01T10:15:00Z", reminderMinutes: [10] }),
  });
  assert.equal(missingTask.status, 404, "unknown Memory ID must not reach Calendar");

  const reminderId = randomUUID();
  const scheduleBody = JSON.stringify({ version: task.version, requestId: reminderId,
    start: "2099-01-01T10:00:00Z", end: "2099-01-01T10:15:00Z", reminderMinutes: [10] });
  const firstAttempt = await fetch(new URL(`/api/memory/${id}/calendar-reminder`, origin), {
    method: "POST", headers: json, body: scheduleBody,
  });
  assert.equal(firstAttempt.status, 503, "isolated test has no Google account; no fake success is allowed");
  const afterReservation = (await (await fetch(new URL("/api/memory/agentic/tasks", origin))).json())
    .find(item => item.id.toLowerCase() === id.toLowerCase());
  assert.equal(afterReservation.calendarRequestId, reminderId, "pending request is durably reserved");
  assert.equal(afterReservation.calendarEventId, null, "Backend must not claim a Google event exists");

  const retry = await fetch(new URL(`/api/memory/${id}/calendar-reminder`, origin), {
    method: "POST", headers: json, body: scheduleBody,
  });
  assert.equal(retry.status, 503, "same idempotency key can retry without claiming success");
  const competing = await fetch(new URL(`/api/memory/${id}/calendar-reminder`, origin), {
    method: "POST", headers: json, body: JSON.stringify({ version: afterReservation.version,
      requestId: randomUUID(), start: "2099-01-02T10:00:00Z", end: "2099-01-02T10:15:00Z", reminderMinutes: [10] }),
  });
  assert.equal(competing.status, 409, "concurrent second reminder cannot race the reserved request");

  const complete = await fetch(new URL(`/api/memory/${id}/complete`, origin), {
    method: "POST", headers: json,
    body: JSON.stringify({ version: afterReservation.version, requestId: randomUUID(), category: "task" }),
  });
  assert.equal(complete.status, 409, "task changes remain blocked until a pending Calendar write is reconciled");
  const after = await (await fetch(new URL("/api/memory/agentic/tasks", origin))).json();
  assert.equal(after.find(item => item.id.toLowerCase() === id.toLowerCase()).status, "open");
});
