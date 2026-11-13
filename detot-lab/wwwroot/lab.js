const form = document.querySelector("form");
const button = form.querySelector("button");
const status = document.querySelector("#status");
const reply = document.querySelector("#reply");
let pending;

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  button.disabled = true;
  status.textContent = "Đang chờ Đệ Tốt…";
  reply.textContent = "";
  try {
    const message = document.querySelector("#message").value;
    if (pending?.message !== message) pending = { message, requestId: crypto.randomUUID() };
    const response = await fetch("/api/chat", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(pending)
    });
    const data = await response.json();
    if (!response.ok) throw new Error(data.detail || data.error || "Yêu cầu thất bại.");
    pending = undefined;
    status.textContent = data.calendarCreated ? "Đã tạo sự kiện thử." : data.toolUsed ? "Đã gọi công cụ giả lập." : "Đã nhận phản hồi.";
    reply.textContent = data.reply;
  } catch (error) {
    status.textContent = error.message;
  } finally {
    button.disabled = false;
  }
});
