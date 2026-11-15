import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";

const origin = process.env.DETOT_MEMORY_SEMANTIC_ORIGIN;
if (origin !== "http://localhost:5192") throw new Error("Use the isolated Backend on port 5192.");
const phase = process.argv[2];
const fixtures = [
  ["printer", "Replace the toner cartridge in the accounting printer."],
  ["other-printer", "Clear the paper jam in the reception printer."],
  ["network", "Renew the TLS certificate for the public website."],
  ["vietnamese", "Thay hộp mực cho máy in của phòng kế toán."],
];
async function api(method, path, data) {
  const response = await fetch(origin + path, { method,
    headers: { Origin: origin, "Content-Type": "application/json" },
    body: data === undefined ? undefined : JSON.stringify(data),
    signal: AbortSignal.timeout(30000),
  });
  assert.ok(response.ok, `Unexpected HTTP ${response.status}`);
  return response.status === 204 ? null : response.json();
}
if (phase === "seed") {
  assert.deepEqual(await api("GET", "/api/memory"), []);
  for (const [, text] of fixtures) {
    const result = await api("POST", "/api/memory", { text, category: "task", requestId: randomUUID() });
    assert.match(result.reply, /Đã lưu ký ức/);
  }
}
if (phase === "chat") {
  assert.deepEqual(await api("GET", "/api/memory"), []);
  const conversationId = randomUUID();
  const marker = randomUUID().slice(0, 8);
  const saved = await api("POST", "/api/chat", { conversationId, requestId: randomUUID(),
    message: `Nhớ cho lần sau: cần hiệu chuẩn kính hiển vi ở phòng thí nghiệm, mã ${marker}.` });
  const id = saved.reply.match(/mã ([a-f0-9-]{36})/i)?.[1];
  assert.ok(id);
  const fresh = await api("POST", "/api/chat", { conversationId: randomUUID(), requestId: randomUUID(),
    message: "Mã của việc hiệu chuẩn kính hiển vi mà tôi đã nhờ lưu là gì?" });
  assert.ok(fresh.reply.includes(id), `Synthetic recall reply: ${fresh.reply}`);
  assert.ok(fresh.reply.includes("nguồn user-request"));
  assert.equal(fresh.memoryAnswer, "verified-excerpt");
  assert.ok(fresh.reply.includes(marker));
  console.log("PASS: DeepSeek selected a verified excerpt with exact memory citation from a fresh conversation");
  process.exit(0);
}
if (phase === "fallback") {
  const text = "Thay hộp mực máy in tầng ba.";
  await api("POST", "/api/memory", { text, category: "task", requestId: randomUUID() });
  const hits = await api("GET", "/api/memory?q=" + encodeURIComponent("hộp mực máy in"));
  assert.equal(hits[0]?.text, text);
  assert.deepEqual(await api("GET", "/api/memory?q=canh%20chua"), []);
  console.log("PASS: unavailable embedding model preserves keyword retrieval");
  process.exit(0);
}
if (phase === "ambiguity") {
  for (const room of ["kế toán", "nhân sự"]) {
    await api("POST", "/api/memory", { text: "Kiểm tra máy in phòng " + room,
      category: "task", requestId: randomUUID() });
  }
  const request = message => api("POST", "/api/chat", { conversationId: randomUUID(), requestId: randomUUID(), message });
  const multiple = await request("Tìm lại ký ức tôi đã lưu về việc kiểm tra máy in.");
  assert.match(multiple.reply, /Bạn đang hỏi về mục nào/);
  assert.match(multiple.reply, /kế toán/);
  assert.match(multiple.reply, /nhân sự/);
  const none = await request("Tìm lại ký ức đã lưu về công thức nấu canh chua cá.");
  assert.match(none.reply, /chưa tìm thấy ký ức phù hợp/);
  console.log("PASS: chat asks when ambiguous and reports no matching memory");
  process.exit(0);
}
const rows = await api("GET", "/api/memory");
const ids = new Map(fixtures.map(([key, value]) => [key, rows.find(row => row.text === value)?.id]));
assert.ok([...ids.values()].every(Boolean));
const cases = phase === "holdout" ? [
  ...[
    "What supply needs replacing on the finance department copier?",
    "The accounting office output device needs a fresh cartridge",
    "Which bookkeeping printer ran out of toner?",
    "Fresh ink for the printer used by accounts",
    "Máy in bên tài vụ cần thay vật tư gì?",
    "Phòng kế toán cần mua hộp mực cho thiết bị nào?",
    "Nhắc việc bổ sung mực cho máy bên bộ phận tài chính",
    "Thiết bị in của phòng kế toán cần thay gì?"
  ].map(query => ({ query, relevant: ["printer", "vietnamese"] })),
  ...[
    "Free the sheet trapped inside the reception copier",
    "Unblock the paper feed at the front desk",
    "Reception has a jammed printer",
    "Get the paper out of the printer in the lobby",
    "Xử lý tờ giấy mắc trong máy in sảnh đón khách",
    "Máy in chỗ lễ tân bị nghẽn đường giấy",
    "Lấy giấy kẹt ra khỏi thiết bị in quầy tiếp đón",
    "Việc gì cần làm với máy in ở khu tiếp khách?"
  ].map(query => ({ query, relevant: ["other-printer"] })),
  ...[
    "Rotate the site's expiring HTTPS certificate",
    "Replace the credential that secures the public website",
    "The web endpoint's TLS cert is nearing expiration",
    "Update the website encryption certificate",
    "Chứng chỉ trang công khai sắp hết hạn",
    "Thay chứng thư bảo mật của website",
    "Gia hạn bảo mật cho địa chỉ web công cộng",
    "Cần làm gì khi chứng chỉ HTTPS sắp hết hạn?"
  ].map(query => ({ query, relevant: ["network"] })),
  ...["Printer maintenance", "Việc liên quan đến máy in"].map(query =>
    ({ query, relevant: ["printer", "other-printer", "vietnamese"] })),
  ...[
    "How do I make sour fish soup?", "Cách nấu bún bò", "Công thức nấu canh chua cá",
    "Dự báo thời tiết Đà Lạt", "Con mèo nhà tôi nên ăn gì?", "Plan a weekend hike",
    "Calculate the area of a triangle", "Who wrote Hamlet?", "Lịch sử nhà Trần", "Tập đàn piano"
  ].map(query => ({ query, relevant: [] })),
] : [
  { query: "Which copier in finance needs new ink?", relevant: ["printer", "vietnamese"] },
  { query: "Printing device at the front desk has a stuck sheet", relevant: ["other-printer"] },
  { query: "Update the HTTPS security credential on the web server", relevant: ["network"] },
  { query: "Thiết bị in ở bộ phận tài vụ hết mực", relevant: ["vietnamese", "printer"] },
  { query: "medieval poetry and astronomy", relevant: [] },
  { query: "Máy in quầy lễ tân bị kẹt giấy", relevant: ["other-printer"] },
  { query: "Gia hạn chứng chỉ TLS của trang web", relevant: ["network"] },
  { query: "Công thức nấu canh chua cá", relevant: [] },
];
const metrics = [];
for (const { query, relevant } of cases) {
  const start = performance.now();
  const matches = (await api("GET", "/api/memory?q=" + encodeURIComponent(query))).slice(0, 2);
  const correct = matches.filter(hit => relevant.some(key => ids.get(key) === hit.id)).length;
  const measurement = { query, recallAt2: relevant.length ? correct / relevant.length : null,
    precisionAt2: correct / 2, returned: matches.length, latencyMs: Math.round(performance.now() - start) };
  metrics.push(measurement);
  console.log(JSON.stringify(measurement));
}
if (phase === "holdout") {
  const positives = metrics.filter(item => item.recallAt2 !== null);
  const negatives = metrics.filter(item => item.recallAt2 === null);
  const mean = (items, selector) => items.reduce((sum, item) => sum + selector(item), 0) / items.length;
  const latency = metrics.map(item => item.latencyMs).sort((a, b) => a - b);
  console.log(JSON.stringify({ queries: metrics.length,
    recallAt2: mean(positives, item => item.recallAt2),
    precisionAt2: mean(positives, item => item.precisionAt2),
    falsePositiveRate: mean(negatives, item => Number(item.returned > 0)),
    latencyP50Ms: latency[Math.ceil(latency.length * 0.5) - 1],
    latencyP95Ms: latency[Math.ceil(latency.length * 0.95) - 1] }));
}
if (phase === "mutations") {
  const id = ids.get("network");
  await api("PATCH", "/api/memory/" + id, { text: "Calibrate the laboratory microscope.", version: 1 });
  const old = await api("GET", "/api/memory?q=TLS%20certificate");
  assert.ok(!old.some(hit => hit.id === id));
  const updated = await api("GET", "/api/memory?q=laboratory%20microscope");
  assert.equal(updated[0]?.id, id);
  await api("POST", "/api/memory/" + id + "/complete", { version: 2 });
  assert.equal((await api("GET", "/api/memory?q=microscope&status=done"))[0]?.id, id);
  assert.ok(!(await api("GET", "/api/memory?q=microscope&status=open")).some(hit => hit.id === id));
  await api("DELETE", "/api/memory/" + id + "?version=3");
  assert.ok(!(await api("GET", "/api/memory?q=microscope")).some(hit => hit.id === id));
  console.log("PASS: edit, state filtering and deletion with real embeddings");
}
