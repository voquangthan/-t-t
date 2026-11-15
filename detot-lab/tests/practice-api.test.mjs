import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import test from "node:test";

const origin = process.env.DETOT_PRACTICE_TEST_ORIGIN;
if (!origin) throw new Error("Set DETOT_PRACTICE_TEST_ORIGIN to an isolated lab instance.");

async function turn(conversationId, message, startPractice = false) {
  const response = await fetch(new URL("/api/chat", origin), {
    method: "POST",
    headers: { "Content-Type": "application/json", Origin: origin },
    body: JSON.stringify({ conversationId, message, startPractice }),
  });
  assert.equal(response.status, 200);
  return (await response.json()).reply;
}

test("DHCP requires direct evidence and keeps replies consistent across turns", async () => {
  const id = randomUUID();
  assert.match(await turn(id, "Bắt đầu ca DHCP", true), /169\.254\.23\.9/);
  assert.match(await turn(id, "gợi ý"), /1\/3/);
  assert.match(await turn(id, "gợi ý"), /2\/3/);
  const first = await turn(id, "ipconfig");
  assert.equal(await turn(id, "ipconfig"), first);
  assert.match(await turn(id, "lệnh không có trong ca"), /vẫn đang mở/);
  assert.match(await turn(id, "xem log"), /DHCPDISCOVER/);
  assert.match(await turn(id, "Nguyên nhân là pool DHCP cạn"), /Chưa đủ bằng chứng/);
  assert.match(await turn(id, "xem lease"), /100\/100/);
  assert.match(await turn(id, "Tôi nghĩ không phải pool DHCP cạn"), /chưa khớp/);
  assert.match(await turn(id, "Nguyên nhân là pool DHCP cạn"), /đã được xác nhận/);
  assert.match(await turn(id, "ipconfig /renew"), /Không nhận được DHCPOFFER/);
  await turn(id, "mở rộng pool DHCP");
  assert.match(await turn(id, "ipconfig /renew"), /10\.20\.4\.120/);
  assert.match(await turn(id, "bỏ qua bàn giao"), /đã hoàn tất/);
});

test("interleaved case IDs keep separate incident data", async () => {
  const lan = randomUUID();
  const ad = randomUUID();
  await turn(lan, "ca LAN", true);
  await turn(ad, "ca Active Directory", true);
  assert.match(await turn(lan, "đèn link"), /Media disconnected/);
  assert.match(await turn(ad, "gpresult"), /Workstation Baseline/);
  assert.match(await turn(lan, "gpresult"), /vẫn đang mở/);
});

test("cross-origin mutation is denied", async () => {
  const response = await fetch(new URL("/api/chat", origin), {
    method: "POST",
    headers: { "Content-Type": "application/json", Origin: "https://invalid.example" },
    body: JSON.stringify({ conversationId: randomUUID(), message: "ca LAN", startPractice: true }),
  });
  assert.equal(response.status, 403);
});

test("LAN recovery requires diagnosis, repair and verification in that order", async () => {
  const id = randomUUID();
  await turn(id, "ca LAN", true);
  assert.match(await turn(id, "ping gateway"), /thất bại/);
  assert.match(await turn(id, "cắm lại cáp"), /xác nhận chẩn đoán/);
  await turn(id, "đèn link");
  await turn(id, "xem cáp mạng");
  assert.match(await turn(id, "Nguyên nhân là cáp lỏng"), /đã được xác nhận/);
  assert.match(await turn(id, "ping gateway"), /thất bại/);
  assert.match(await turn(id, "cắm lại cáp"), /kiểm tra lại/);
  assert.match(await turn(id, "ping gateway"), /4\/4/);
  assert.match(await turn(id, "ping gateway"), /4\/4/);
  assert.match(await turn(id, "bỏ qua bàn giao"), /Phục hồi: đã xác minh/);
});
