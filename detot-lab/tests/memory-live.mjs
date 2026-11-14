import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { readFile, writeFile } from "node:fs/promises";

const [phase, checkpoint] = process.argv.slice(2);
const origin = process.env.DETOT_MEMORY_TEST_ORIGIN ?? "http://localhost:5196";
if (!["prepare", "resume"].includes(phase) || !checkpoint) throw new Error("Use prepare|resume and an isolated checkpoint path.");

async function ask(message, conversationId = randomUUID(), requestId = randomUUID()) {
  const response = await fetch(origin + "/api/chat", {
    method: "POST",
    headers: { "Content-Type": "application/json", Origin: origin },
    body: JSON.stringify({ conversationId, requestId, message }),
    signal: AbortSignal.timeout(60000),
  });
  assert.equal(response.status, 200);
  return (await response.json()).reply;
}

if (phase === "prepare") {
  const suffix = randomUUID().slice(0, 8);
  const first = `máy in phòng kế toán QA-${suffix}`;
  const second = `máy in phòng nhân sự QA-${suffix}`;
  const save = `Nhớ giúp tôi công việc kiểm tra ${first} vào lần tới.`;
  const saveChat = randomUUID();
  const saveRequest = randomUUID();
  const one = await ask(save, saveChat, saveRequest);
  const two = await ask(`Lưu lại việc cần thay giấy cho ${second}.`);
  const idPattern = /mã ([a-f0-9-]{36})/i;
  const firstId = one.match(idPattern)?.[1];
  const secondId = two.match(idPattern)?.[1];
  assert.ok(firstId && secondId && firstId !== secondId,
    `Missing distinct memory IDs. Synthetic replies: ${JSON.stringify([one, two])}`);
  assert.equal((await ask(save, saveChat, saveRequest)).match(idPattern)?.[1], firstId);
  const chat = randomUUID();
  const options = await ask("Đánh dấu việc máy in đã xong.", chat);
  assert.match(options, /Chọn đúng công việc/);
  assert.ok(options.includes(firstId) && options.includes(secondId));
  const selected = await ask("việc thứ hai", chat);
  assert.ok(selected.includes(second) && /Đã đánh dấu hoàn thành/.test(selected));
  assert.match(await ask(`Đánh dấu công việc ${secondId} đã hoàn thành.`), /đã được đánh dấu hoàn thành/);
  await writeFile(checkpoint, JSON.stringify({ first, second, firstId, secondId }), { flag: "wx" });
  console.log("PASS: two distinct memories, ambiguous update rejected, one completed");
} else {
  const { first, second } = JSON.parse(await readFile(checkpoint, "utf8"));
  const lines = (await ask("Tôi đã nhờ lưu những việc nào?")).split("\n");
  assert.equal(lines.filter(line => line.includes(first) && line.includes("đang mở")).length, 1);
  assert.equal(lines.filter(line => line.includes(second) && line.includes("đã xong")).length, 1);
  console.log("PASS: new conversation preserved both statuses");
}
