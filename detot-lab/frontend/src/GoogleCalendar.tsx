import { useEffect, useState, type FormEvent } from "react";

type Account = { id: string; email: string };
type EventTime = { date?: string; dateTime?: string };
type CalendarEvent = {
  id: string; etag: string; summary?: string; start: EventTime; end: EventTime;
  organizer?: { self?: boolean }; attendees?: unknown[]; recurringEventId?: string;
  eventType?: string; locked?: boolean;
  reminders?: { useDefault: boolean; overrides?: { method: string; minutes: number }[] };
};
const zone = new Intl.DateTimeFormat("sv-SE", { timeZone: "Asia/Bangkok", dateStyle: "short", timeStyle: "short" });
const display = (time: EventTime) => time.date ?? (time.dateTime ? zone.format(new Date(time.dateTime)) : "");

export async function calendarApi<T>(path: string, options?: RequestInit): Promise<T> {
  const response = await fetch("/api/google/" + path, options);
  if (response.status === 204) return undefined as T;
  const data = await response.json().catch(() => null);
  if (!response.ok) throw new Error(data?.detail ?? (response.status === 503
    ? "Kiểm tra cấu hình Google OAuth trên máy chủ Đệ Tốt." : `Google Calendar gặp lỗi (HTTP ${response.status}).`));
  return data;
}

export function GoogleCalendar() {
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [account, setAccount] = useState("");
  const [notice, setNotice] = useState("");
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    const url = new URL(location.href);
    const outcome = url.searchParams.get("calendar");
    if (outcome) {
      url.searchParams.delete("calendar");
      history.replaceState(history.state, "", url);
      setNotice(outcome === "connected" ? "Đã kết nối Google Calendar." : "Chưa hoàn tất cấp quyền Google Calendar.");
    }
    void calendarApi<Account[]>("calendar/accounts", { signal: controller.signal }).then(items => {
      setAccounts(items);
      setAccount(items[0]?.id ?? "");
    }).catch(error => { if (!controller.signal.aborted) setNotice(error.message); });
    return () => controller.abort();
  }, []);

  return <section className="transcript calendar">
    <h1>Google Calendar</h1>
    {notice && <p role="status">{notice}</p>}
    {accounts.length > 0 && <label>Tài khoản <select value={account} onChange={event => setAccount(event.target.value)}>
      {accounts.map(item => <option key={item.id} value={item.id}>{item.email}</option>)}
    </select></label>}
    <button disabled={busy} onClick={async () => {
      setBusy(true);
      try {
        const { url } = await calendarApi<{ url: string }>("connect?service=calendar", { method: "POST" });
        location.assign(url);
      } catch (error) {
        setNotice(error instanceof Error ? error.message : "Không thể kết nối.");
        setBusy(false);
      }
    }}>Kết nối Google Calendar</button>
    {account && <CalendarEvents key={account} account={account} />}
  </section>;
}

function CalendarEvents({ account }: { account: string }) {
  const path = `calendar/${encodeURIComponent(account)}/events`;
  const [events, setEvents] = useState<CalendarEvent[]>([]);
  const [loadError, setLoadError] = useState("");
  const [error, setError] = useState("");
  const [editing, setEditing] = useState<CalendarEvent | "new">();
  const [requestId, setRequestId] = useState("");
  const [revision, reload] = useState(0);
  const [busy, setBusy] = useState(false);
  const [truncated, setTruncated] = useState(false);
  const [scheduleReady, setScheduleReady] = useState(false);
  const [suggestion, setSuggestion] = useState("");

  useEffect(() => {
    if (busy) return;
    const controller = new AbortController();
    let loading = false;
    async function refresh() {
      if (document.hidden || loading || controller.signal.aborted) return;
      loading = true;
      try {
        const data = await calendarApi<{ events: CalendarEvent[]; truncated: boolean }>(path, { signal: controller.signal });
        if (!controller.signal.aborted) {
          setEvents(data.events);
          setLoadError("");
          setTruncated(data.truncated);
          setScheduleReady(!data.truncated);
        }
      } catch (cause) {
        if (!controller.signal.aborted) {
          setScheduleReady(false);
          setLoadError(cause instanceof Error ? cause.message : "Không tải được lịch.");
        }
      } finally { loading = false; }
    }
    void refresh();
    const timer = window.setInterval(() => void refresh(), 30000);
    document.addEventListener("visibilitychange", refresh);
    return () => {
      controller.abort();
      window.clearInterval(timer);
      document.removeEventListener("visibilitychange", refresh);
    };
  }, [path, revision, busy]);

  async function change(method: string, item?: CalendarEvent, body?: object) {
    if (busy) return;
    setBusy(true);
    setError("");
    try {
      const headers = new Headers();
      if (item) headers.set("If-Match", item.etag);
      if (body) headers.set("Content-Type", "application/json");
      await calendarApi(path + (item ? "/" + encodeURIComponent(item.id) : ""), {
        method, headers, body: body && JSON.stringify(body),
      });
      setEditing(undefined);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Không lưu được sự kiện.");
    } finally { setBusy(false); }
  }

  function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const fields = new FormData(event.currentTarget);
    const start = String(fields.get("start"));
    const end = String(fields.get("end"));
    if (end <= start) { setError("Giờ kết thúc phải sau giờ bắt đầu."); return; }
    const item = editing === "new" ? undefined : editing;
    const raw = String(fields.get("reminders") ?? "").trim();
    const parts = raw ? raw.split(",").map(value => value.trim()) : [];
    const minutes = parts.map(Number);
    if (parts.some(value => !/^\d+$/.test(value)) || minutes.some(value => value > 40320) ||
      minutes.length > 5 || new Set(minutes).size !== minutes.length) {
      setError("Nhập tối đa 5 mốc nhắc khác nhau, từ 0 đến 40320 phút, cách nhau bằng dấu phẩy.");
      return;
    }
    void change(item ? "PUT" : "POST", item, {
      title: fields.get("title"), start: start + ":00+07:00", end: end + ":00+07:00", requestId,
      reminderMinutes: raw ? minutes : fields.get("disableReminders") ? [] : undefined,
    });
  }

  const selected = editing === "new" ? undefined : editing;
  return <>
    <section aria-labelledby="calendar-coach">
      <h2 id="calendar-coach">Giờ làm gì vừa sức?</h2>
      <form onChange={() => setSuggestion("")} onSubmit={event => {
        event.preventDefault();
        const fields = new FormData(event.currentTarget);
        if (fields.get("energy") === "busy") {
          setSuggestion("Để lúc khác nhé.");
          return;
        }
        setSuggestion(calendarSuggestion(events, Number(fields.get("minutes")), fields.get("energy") === "low", scheduleReady));
      }}>
        <label>Số phút bạn có<input name="minutes" type="number" min={1} max={120} defaultValue={10} required /></label>
        <label>Hôm nay bạn thấy thế nào?<select name="energy" defaultValue="normal">
          <option value="normal">Bình thường</option>
          <option value="low">Hơi mệt</option>
          <option value="busy">Đang bận</option>
        </select></label>
        <button disabled={busy}>Gợi ý một việc</button>
      </form>
      {suggestion && <p role="status">{suggestion} <button type="button" onClick={() => setSuggestion("")}>Bỏ qua</button></p>}
    </section>
    <p>Lịch chính · 30 ngày trước đến 90 ngày tới · UTC+07:00</p>
    <button disabled={busy} onClick={() => reload(value => value + 1)}>Tải lại lịch</button>{" "}
    <button disabled={busy} onClick={() => { setRequestId(crypto.randomUUID()); setEditing("new"); }}>Tạo sự kiện</button>
    {loadError && <p role="alert">{loadError}</p>}
    {error && <p role="alert">{error}</p>}
    {truncated && <p>Đang hiển thị 1.000 sự kiện đầu tiên trong khoảng này.</p>}
    {editing && <form key={selected?.id ?? "new"} onSubmit={save}>
      <fieldset disabled={busy}>
        <legend>{selected ? "Sửa sự kiện" : "Tạo sự kiện"}</legend>
        <label>Tên sự kiện<input name="title" required maxLength={120} defaultValue={selected?.summary} /></label>
        <label>Bắt đầu<input name="start" type="datetime-local" required defaultValue={selected && display(selected.start).replace(" ", "T")} /></label>
        <label>Kết thúc<input name="end" type="datetime-local" required defaultValue={selected && display(selected.end).replace(" ", "T")} /></label>
        <label>Thay lời nhắc bằng thông báo trước (phút)<input name="reminders" placeholder="Ví dụ 15, 1440" maxLength={50} /></label>
        <label><input name="disableReminders" type="checkbox" /> Tắt nhắc nếu ô mốc nhắc để trống</label>
        <button>Lưu</button>{" "}<button type="button" onClick={() => setEditing(undefined)}>Hủy</button>
      </fieldset>
    </form>}
    {events.map(item => {
      const writable = item.organizer?.self && item.start.dateTime && !item.recurringEventId &&
        !item.attendees?.length && (!item.eventType || item.eventType === "default") && !item.locked;
      return <article className="user" key={item.id}>
        <strong>{item.summary || "(Không có tiêu đề)"}</strong>
        <p>{item.start.date ? `Cả ngày · ${item.start.date}${item.end.date ? ` → ${item.end.date} (không gồm ngày cuối)` : ""}`
          : `${display(item.start)} → ${display(item.end)}`}</p>
        {item.reminders && <p>{item.reminders.useDefault ? "Nhắc theo mặc định của lịch" :
          item.reminders.overrides?.length ? item.reminders.overrides.map(reminder =>
            `${reminder.method === "email" ? "Email" : "Thông báo"} trước ${reminder.minutes} phút`).join(" · ") : "Đã tắt nhắc"}</p>}
        {writable && <>
          <button disabled={busy} onClick={() => { setRequestId(crypto.randomUUID()); setEditing(item); }}>Sửa</button>{" "}
          <button disabled={busy} onClick={() => {
            if (window.confirm(`Xóa «${item.summary || "sự kiện"}» khỏi Google Calendar?`)) void change("DELETE", item);
          }}>Xóa</button>
        </>}
      </article>;
    })}
  </>;
}

function calendarSuggestion(events: CalendarEvent[], available: number, low: boolean, ready: boolean) {
  const now = Date.now();
  const time = (value: EventTime) => Date.parse(value.dateTime ?? (value.date ? value.date + "T00:00:00+07:00" : ""));
  const blocks = events.map(item => ({ item, start: time(item.start), end: time(item.end) }));
  if (!ready || blocks.some(block => !Number.isFinite(block.start) || !Number.isFinite(block.end))) {
    return "Chưa đủ dữ liệu lịch để gợi ý lúc này.";
  }
  if (blocks.some(block => block.start <= now && now < block.end)) {
    return "Lịch đang có sự kiện. Cứ dành thời gian cho việc hiện tại.";
  }
  const next = blocks.filter(block => block.start > now).sort((a, b) => a.start - b.start)[0];
  const gap = next ? Math.floor((next.start - now) / 60000) : Infinity;
  const minutes = Math.min(available, gap);
  if (minutes < 3) {
    return "Dành vài phút này để nghỉ hoặc chuyển sang việc kế tiếp.";
  }
  const light = low || minutes < 10;
  const title = next?.item.summary?.trim();
  const upcoming = title && gap <= 120;
  const task = upcoming
    ? light ? `Xem lại mục tiêu của “${title}” và ghi một điều bạn muốn nhớ.`
      : `Chuẩn bị cho “${title}” bằng cách ghi ba ý bạn muốn trao đổi.`
    : light ? "Ghi một việc nhỏ bạn muốn bắt đầu hôm nay."
      : "Chọn một việc đang dở và làm bước đầu tiên của việc đó.";
  return (`${task} Dành khoảng ${Math.min(minutes, light ? 5 : 10)} phút.`);
}

export async function suggestFromChat(message: string, signal: AbortSignal): Promise<string | undefined> {
  const text = message.toLocaleLowerCase("vi").trim();
  const asked = /^(?:giờ )?làm gì vừa sức[?.!]*$/u.test(text);
  const available = /^(?:(?:t|tôi|mình|em)\s+)?(?:chỉ\s+)?(?:có|còn|rảnh)\s+(\d{1,3})\s*phút(?:\s*[,.;!?]\s*|\s+)?(?:hơi mệt|mệt|hơi đuối|đang bận|làm gì|làm gì vừa sức)?[?.!]*$/u.exec(text);
  if (!asked && !available) return undefined;
  if (text.includes("đang bận")) return "Để lúc khác nhé.";
  const minutes = available ? Number(available[1]) : 10;
  if (minutes < 1 || minutes > 120) return "Bạn có bao nhiêu phút trong khoảng 1 đến 120 phút?";
  const permission = await fetch("/api/consent/check", { method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ requirements: [{ source: "calendar", purpose: "direct_request", destination: "local_only" }], requestId: crypto.randomUUID() }), signal });
  if (permission.status === 403) return "Mình chưa được cấp quyền đọc lịch để cá nhân hóa gợi ý. Bạn có thể cấp quyền trong mục Quyền riêng tư; hiện mình sẽ không đọc lịch.";
  if (!permission.ok) throw new Error("Không thể kiểm tra quyền truy cập lịch.");
  const accounts = await calendarApi<Account[]>("calendar/accounts", { signal });
  if (!accounts.length) return "Bạn kết nối tài khoản trong Lịch → Google Calendar để mình xem lịch sắp tới nhé.";
  if (accounts.length > 1) return "Bạn mở Lịch → Google Calendar, chọn tài khoản rồi bấm Gợi ý một việc nhé.";
  const data = await calendarApi<{ events: CalendarEvent[]; truncated: boolean }>(
    `calendar/${encodeURIComponent(accounts[0].id)}/events`, { signal });
  return calendarSuggestion(data.events, minutes, /mệt|đuối/u.test(text), !data.truncated);
}
