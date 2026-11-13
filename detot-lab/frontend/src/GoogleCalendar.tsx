import { useEffect, useState, type FormEvent } from "react";

type Account = { id: string; email: string };
type EventTime = { date?: string; dateTime?: string };
type CalendarEvent = {
  id: string; etag: string; summary?: string; start: EventTime; end: EventTime;
  organizer?: { self?: boolean }; attendees?: unknown[]; recurringEventId?: string;
  eventType?: string; locked?: boolean;
};
const zone = new Intl.DateTimeFormat("sv-SE", { timeZone: "Asia/Bangkok", dateStyle: "short", timeStyle: "short" });
const display = (time: EventTime) => time.date ?? (time.dateTime ? zone.format(new Date(time.dateTime)) : "");

async function calendarApi<T>(path: string, options?: RequestInit): Promise<T> {
  const response = await fetch("/api/google/" + path, options);
  if (response.status === 204) return undefined as T;
  const data = await response.json().catch(() => null);
  if (!response.ok) throw new Error(data?.detail ?? (response.status === 503
    ? "Hãy khởi động Đệ Tốt với -EnableGoogle." : `Google Calendar gặp lỗi (HTTP ${response.status}).`));
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
  const [error, setError] = useState("");
  const [editing, setEditing] = useState<CalendarEvent | "new">();
  const [requestId, setRequestId] = useState("");
  const [revision, reload] = useState(0);
  const [busy, setBusy] = useState(false);
  const [truncated, setTruncated] = useState(false);

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
          setTruncated(data.truncated);
        }
      } catch (cause) {
        if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : "Không tải được lịch.");
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
    void change(item ? "PUT" : "POST", item, {
      title: fields.get("title"), start: start + ":00+07:00", end: end + ":00+07:00", requestId,
    });
  }

  const selected = editing === "new" ? undefined : editing;
  return <>
    <p>Lịch chính · 30 ngày trước đến 90 ngày tới · UTC+07:00</p>
    <button disabled={busy} onClick={() => { setError(""); reload(value => value + 1); }}>Tải lại lịch</button>{" "}
    <button disabled={busy} onClick={() => { setRequestId(crypto.randomUUID()); setEditing("new"); }}>Tạo sự kiện</button>
    {error && <p role="alert">{error}</p>}
    {truncated && <p>Đang hiển thị 1.000 sự kiện đầu tiên trong khoảng này.</p>}
    {editing && <form key={selected?.id ?? "new"} onSubmit={save}>
      <fieldset disabled={busy}>
        <legend>{selected ? "Sửa sự kiện" : "Tạo sự kiện"}</legend>
        <label>Tên sự kiện<input name="title" required maxLength={120} defaultValue={selected?.summary} /></label>
        <label>Bắt đầu<input name="start" type="datetime-local" required defaultValue={selected && display(selected.start).replace(" ", "T")} /></label>
        <label>Kết thúc<input name="end" type="datetime-local" required defaultValue={selected && display(selected.end).replace(" ", "T")} /></label>
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
