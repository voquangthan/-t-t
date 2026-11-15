import assert from "node:assert/strict";
import http from "node:http";

const origin = process.env.DETOT_MEMORY_DREAMING_ORIGIN;
const publicOrigin = process.env.DETOT_MEMORY_DREAMING_PUBLIC_ORIGIN;
const host = new URL(publicOrigin).host;
const password = process.env.DETOT_REMOTE_PASSWORD;
async function raw(method, path, cookie, data, form = false) {
  const target = new URL(origin + path);
  const body = data === undefined ? "" : form ? new URLSearchParams(data).toString() : JSON.stringify(data);
  return await new Promise((resolve, reject) => {
    const request = http.request({ hostname: target.hostname, port: target.port, path: target.pathname + target.search,
      method, headers: { Host: host, Origin: publicOrigin, ...(cookie ? { Cookie: cookie } : {}),
        ...(data === undefined ? {} : { "Content-Type": form ? "application/x-www-form-urlencoded" : "application/json",
          "Content-Length": Buffer.byteLength(body) }) } }, response => {
      let responseBody = "";
      response.setEncoding("utf8");
      response.on("data", chunk => { responseBody += chunk; });
      response.on("end", () => resolve({ status: response.statusCode, body: responseBody,
        cookie: response.headers["set-cookie"]?.[0]?.split(";")[0] }));
    });
    request.setTimeout(45000, () => request.destroy(new Error("HTTP test request timed out.")));
    request.on("error", reject);
    request.end(body);
  });
}
const login = await raw("POST", "/login", undefined, { password }, true);
assert.equal(login.status, 302);
const cookie = login.cookie;
assert.ok(cookie);
let checked = 0;
const accounts = await raw("GET", "/api/google/accounts", cookie);
if (accounts.status !== 200) console.log(`BLOCKED Gmail API: account status returned HTTP ${accounts.status}`);
const gmail = accounts.status === 200 ? JSON.parse(accounts.body) : null;
if (!gmail?.enabled || !gmail.accounts?.length) console.log("BLOCKED Gmail API: no connected Gmail account in isolated credential copy");
else {
  const result = await raw("POST", `/api/google/accounts/${encodeURIComponent(gmail.accounts[0].id)}/search`, cookie, { query: "newer_than:90d" });
  assert.equal(result.status, 200, `Gmail read-only search returned HTTP ${result.status}`);
  checked++;
  console.log("PASS Gmail read-only search via Google API");
}
const calendarAccounts = await raw("GET", "/api/google/calendar/accounts", cookie);
if (calendarAccounts.status !== 200) console.log(`BLOCKED Calendar API: account status returned HTTP ${calendarAccounts.status}`);
const calendars = calendarAccounts.status === 200 ? JSON.parse(calendarAccounts.body) : [];
if (!calendars.length) console.log("BLOCKED Calendar API: no connected Calendar account in isolated credential copy");
else {
  const result = await raw("GET", `/api/google/calendar/${encodeURIComponent(calendars[0].id)}/events`, cookie);
  assert.equal(result.status, 200, `Calendar read returned HTTP ${result.status}`);
  checked++;
  console.log("PASS Calendar event read via Google API");
}
const driveStatus = await raw("GET", "/api/drive/status", cookie);
if (driveStatus.status !== 200) console.log(`BLOCKED Drive API: status returned HTTP ${driveStatus.status}`);
const drive = driveStatus.status === 200 ? JSON.parse(driveStatus.body) : null;
if (!drive?.connected) console.log("BLOCKED Drive API: no connected Drive account in isolated credential copy");
else {
  const refreshed = await raw("POST", "/api/drive/refresh", cookie, {});
  assert.equal(refreshed.status, 200, `Drive token refresh returned HTTP ${refreshed.status}`);
  const items = await raw("GET", "/api/drive/items", cookie);
  assert.equal(items.status, 200, `Drive list returned HTTP ${items.status}`);
  checked++;
  console.log("PASS Drive refresh and read-only item listing via Google API");
}
if (!checked) console.log("No Google API was available for live regression; this is BLOCKED, not PASS.");
