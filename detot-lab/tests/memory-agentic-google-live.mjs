import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import http from "node:http";

const origin = process.env.DETOT_AGENTIC_TEST_ORIGIN;
assert.ok(origin, "Set DETOT_AGENTIC_TEST_ORIGIN to the isolated Backend.");
const headers = { "Content-Type": "application/json", Origin: origin };
const call = (path, options = {}) => fetch(new URL(path, origin), options);
const marker = `DETOT-M4-${randomUUID()}`;
const requestId = randomUUID();
let calendarId;
let event;
let taskId;
let gateway;

async function eventList() {
  const response = await call(`/api/google/calendar/${encodeURIComponent(calendarId)}/events`);
  assert.equal(response.status, 200, "Google Calendar read must work with the cloned credentials.");
  return (await response.json()).events;
}

async function cleanExactEvent() {
  if (!calendarId) return;
  const matches = (await eventList()).filter(item => item.summary === `Nhắc việc: ${marker}`);
  for (const item of matches) {
    const deleted = await call(`/api/google/calendar/${encodeURIComponent(calendarId)}/events/${encodeURIComponent(item.id)}`, {
      method: "DELETE", headers: { Origin: origin, "If-Match": item.etag },
    });
    assert.equal(deleted.status, 204, "Delete only the uniquely named test event using its current ETag.");
  }
}

try {
  const accountsResponse = await call("/api/google/calendar/accounts");
  assert.equal(accountsResponse.status, 200);
  const accounts = await accountsResponse.json();
  assert.equal(accounts.length, 1, "Use exactly one Calendar account from the isolated token copy.");
  calendarId = accounts[0].id;

  const created = await call("/api/memory/", {
    method: "POST", headers,
    body: JSON.stringify({ requestId: randomUUID(), category: "task", text: marker }),
  });
  assert.equal(created.status, 200);
  taskId = (await created.json()).reply.match(/[0-9a-f]{8}-[0-9a-f-]{27}/i)?.[0];
  assert.ok(taskId);
  const tasks = await (await call("/api/memory/agentic/tasks")).json();
  const task = tasks.find(item => item.id.toLowerCase() === taskId.toLowerCase());
  assert.ok(task);

  const start = new Date(Date.now() + 7 * 86400000);
  start.setUTCMinutes(0, 0, 0);
  start.setUTCHours(10);
  const end = new Date(start.getTime() + 15 * 60000);
  const formatted = date => date.toISOString().slice(0, 19) + "+00:00";
  const chatRequestId = randomUUID();
  gateway = http.createServer(async (request, response) => {
    let raw = "";
    for await (const chunk of request) raw += chunk;
    const payload = JSON.parse(raw);
    const tool = payload.tools?.find(item => item.name === "detot_memory_calendar_reminder");
    assert.ok(tool, "Chat passes the gated memory-reminder tool only after task retrieval.");
    const memoryId = tool.parameters.properties.memoryId.enum[0];
    response.writeHead(200, { "Content-Type": "application/json" });
    response.end(JSON.stringify({ output: [{ type: "function_call", name: tool.name,
      arguments: JSON.stringify({ memoryId, start: formatted(start), end: formatted(end), reminderMinutes: [] }) }] }));
  });
  await new Promise(resolve => gateway.listen(Number(process.env.DETOT_MOCK_GATEWAY_PORT ?? 18793), "127.0.0.1", resolve));
  const chat = await call("/api/chat", {
    method: "POST", headers,
    body: JSON.stringify({ message: `Nhắc mình kiểm tra ${marker} vào lúc ${formatted(start)} trong 15 phút.`,
      requestId: chatRequestId, conversationId: randomUUID(), history: [] }),
  });
  assert.equal(chat.status, 200, "Chat must use DeepSeek intent validation and the constrained Gateway tool.");
  const reply = await chat.json();
  assert.match(reply.reply, /Đã tạo nhắc/);
  const currentTasks = await (await call("/api/memory/agentic/tasks")).json();
  const current = currentTasks.find(item => item.id.toLowerCase() === taskId.toLowerCase());
  const linked = Boolean(current?.calendarEventId);
  assert.ok(linked, "The task reminder must link only after Google confirms event creation.");
  event = (await eventList()).find(item => item.summary === `Nhắc việc: ${marker}`);
  assert.ok(event, "Google API must list the newly created unique test event.");
  console.log("PASS: real Google Calendar accepted the task reminder and returned a verifiable event.");
} finally {
  try { await cleanExactEvent(); } finally {
    if (gateway?.listening) await new Promise(resolve => gateway.close(resolve));
  }
}

assert.ok(event, "Test event was created before cleanup.");
assert.equal((await eventList()).some(item => item.summary === `Nhắc việc: ${marker}`), false,
  "The exact test event was removed and verified absent.");
console.log("PASS: exact test event deleted by ID + ETag and verified absent; no other event was touched.");
