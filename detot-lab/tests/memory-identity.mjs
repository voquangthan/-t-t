import assert from "node:assert/strict";
import http from "node:http";
import { randomUUID } from "node:crypto";
import { setTimeout } from "node:timers/promises";

const host = "memory-qa.example.test";
const port = Number(process.env.DETOT_MEMORY_TEST_PORT);
assert.ok(port);
function call(method, path, cookie, data, form = false) {
  const body = data === undefined ? "" : form ? new URLSearchParams(data).toString() : JSON.stringify(data);
  return new Promise((resolve, reject) => {
    const req = http.request({ hostname: "127.0.0.1", port, path, method,
      headers: { Host: host, Origin: "https://" + host, Cookie: cookie ?? "",
        "Content-Type": form ? "application/x-www-form-urlencoded" : "application/json",
        "Content-Length": Buffer.byteLength(body) } }, response => {
      let content = "";
      response.setEncoding("utf8");
      response.on("data", chunk => { content += chunk; });
      response.on("end", () => resolve({ code: response.statusCode, content, cookie: response.headers["set-cookie"]?.[0]?.split(";")[0] }));
    });
    req.on("error", reject);
    req.end(body);
  });
}
async function login(password) {
  const res = await call("POST", "/login", "", { password }, true);
  assert.equal(res.code, 302);
  assert.ok(res.cookie);
  return res.cookie;
}
const [, , phase, oldCookie] = process.argv;
if (phase === "migration") {
  const owner = await login(process.env.DETOT_REMOTE_PASSWORD);
  await setTimeout(2100);
  const member = await login(process.env.DETOT_MEMORY_MEMBER_PASSWORD);
  assert.equal((await call("POST", "/api/memory/migrate-legacy", member)).code, 403);
  assert.deepEqual(JSON.parse((await call("GET", "/api/memory", owner)).content), []);
  assert.equal((await call("POST", "/api/memory/migrate-legacy", owner)).code, 204);
  const [entry] = JSON.parse((await call("GET", "/api/memory", owner)).content);
  assert.equal(entry.status, "done");
  assert.equal(entry.history.length, 2);
  assert.deepEqual(JSON.parse((await call("GET", "/api/memory?q=legacy", member)).content), []);
  assert.equal((await call("GET", "/api/memory/" + entry.id, member)).code, 404);
  assert.equal((await call("GET", "/api/memory/" + entry.id + "/history", member)).code, 404);
  for (let attempt = 0; attempt < 3; attempt++) {
    assert.equal((await call("POST", "/api/memory/migrate-legacy", owner)).code, 409);
  }
  assert.deepEqual(JSON.parse((await call("GET", "/api/memory", owner)).content), [entry]);
  console.log("PASS: legacy migration, owner isolation and repeat migration protection");
} else if (phase === "expiry") {
  const owner = await login(process.env.DETOT_REMOTE_PASSWORD);
  assert.equal((await call("GET", "/api/memory", owner)).code, 200);
  await setTimeout(5500);
  assert.equal((await call("GET", "/api/memory", owner)).code, 401);
  console.log("PASS: expired session denied");
} else if (phase === "restart") {
  assert.equal((await call("GET", "/api/memory", oldCookie)).code, 401);
  const owner = await login(process.env.DETOT_REMOTE_PASSWORD);
  assert.match((await call("GET", "/api/memory", owner)).content, /owner QA record/);
  console.log("PASS: restart invalidated old session and preserved owner records");
} else {
  assert.equal((await call("GET", "/api/memory")).code, 401);
  const owner = await login(process.env.DETOT_REMOTE_PASSWORD);
  await setTimeout(2100);
  const member = await login(process.env.DETOT_MEMORY_MEMBER_PASSWORD);
  assert.equal((await call("GET", "/api/google/accounts", member)).code, 403);
  const memberText = "member QA record";
  for (const [cookie, text] of [[owner, "owner QA record"], [member, memberText]]) {
    assert.equal((await call("POST", "/api/memory", cookie, { text, category: "task", requestId: randomUUID() })).code, 200);
  }
  const memberList = await call("GET", "/api/memory?q=member", member);
  const [entry] = JSON.parse(memberList.content);
  assert.equal(entry.text, memberText);
  assert.equal(entry.ownerId, "memory-member");
  assert.doesNotMatch((await call("GET", "/api/memory", owner)).content, /member QA record/);
  assert.deepEqual(JSON.parse((await call("GET", "/api/memory?q=member", owner)).content), []);
  for (const [method, suffix] of [["GET", ""], ["GET", "/history"], ["PATCH", ""], ["DELETE", "?version=1"], ["POST", "/complete"]]) {
    assert.equal((await call(method, "/api/memory/" + entry.id + suffix, owner, { text: "wrong", version: 1 })).code, 404);
  }
  assert.equal((await call("POST", "/api/memory/" + entry.id + "/complete", member, { version: 1 })).code, 204);
  assert.equal((await call("PATCH", "/api/memory/" + entry.id, member, { text: "member edited", version: 2 })).code, 204);
  const history = JSON.parse((await call("GET", "/api/memory/" + entry.id + "/history", member)).content);
  assert.deepEqual(history.map(item => item.action), ["created", "completed", "edited"]);
  assert.ok(history.every(item => item.ownerId === "memory-member"));
  assert.equal((await call("DELETE", "/api/memory/" + entry.id + "?version=3", member)).code, 204);
  assert.equal((await call("GET", "/api/memory/" + entry.id, member)).code, 404);
  assert.equal((await call("POST", "/logout", member)).code, 302);
  assert.equal((await call("GET", "/api/memory", member)).code, 401);
  if (phase === "prepare") {
    const { writeFile } = await import("node:fs/promises");
    await writeFile(process.argv[3], owner, { flag: "wx" });
  }
  console.log("PASS: two logins, owner-scoped CRUD/history, cross-owner denial and logout");
}
