const form = document.querySelector("form");
const button = form.querySelector("button");
const status = document.querySelector("#status");
const reply = document.querySelector("#reply");
const followup = document.querySelector("#calendar-followup");
let pending;
let calendarContext;

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  if (button.disabled) return;
  button.disabled = true;
  followup.disabled = true;
  status.textContent = "Đang chờ Đệ Tốt…";
  reply.textContent = "";
  try {
    const message = document.querySelector("#message").value;
    const context = event.submitter === followup ? calendarContext : undefined;
    if (pending?.message !== message || pending?.calendarContext !== context) {
      pending = { message, requestId: crypto.randomUUID(), calendarContext: context };
    }
    const response = await fetch("/api/chat", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(pending)
    });
    const data = await response.json();
    if (!response.ok) throw new Error(data.detail || data.error || "Yêu cầu thất bại.");
    pending = undefined;
    calendarContext = data.calendarContext ?? undefined;
    followup.hidden = !calendarContext;
    status.textContent = data.calendarCreated ? "Đã tạo sự kiện thử." : data.toolUsed ? "Đã gọi công cụ giả lập." : "Đã nhận phản hồi.";
    reply.textContent = data.reply;
  } catch (error) {
    status.textContent = error.message;
  } finally {
    button.disabled = false;
    followup.disabled = false;
  }
});
