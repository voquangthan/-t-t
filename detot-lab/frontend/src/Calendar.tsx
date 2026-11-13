import { useEffect, useRef, useState, type FormEvent } from "react";

type Event = { id: string; title: string; start: string; end: string };
const formatter = new Intl.DateTimeFormat("sv-SE", {
  timeZone: "Asia/Bangkok", dateStyle: "short", timeStyle: "short",
});
const localInput = (value: string) => formatter.format(new Date(value)).replace(" ", "T");

export function Calendar() {
  const [events, setEvents] = useState<Event[]>([]);
  const [enabled, setEnabled] = useState<boolean>();
  const [editing, setEditing] = useState<Event>();
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(true);
  const loadVersion = useRef(0);
  const changing = useRef(false);
  const [refreshError, setRefreshError] = useState(false);

  async function load(signal?: AbortSignal) {
    const version = ++loadVersion.current;
    const response = await fetch("/api/calendar", { signal });
    if (!response.ok) throw new Error("Không tải được lịch thử.");
    const data = await response.json();
    if (signal?.aborted || version !== loadVersion.current) return;
    setEnabled(data.enabled);
    setEvents(data.events);
    setRefreshError(false);
  }

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal).catch(() => {
      if (!controller.signal.aborted) setError("Không tải được lịch thử.");
    }).finally(() => {
      if (!controller.signal.aborted) setBusy(false);
    });
    let refreshing = false;
    const refresh = async () => {
      if (document.hidden || refreshing || changing.current) return;
      refreshing = true;
      try { await load(controller.signal); }
      catch { if (!controller.signal.aborted) setRefreshError(true); }
      finally { refreshing = false; }
    };
    const timer = window.setInterval(() => void refresh(), 5000);
    document.addEventListener("visibilitychange", refresh);
    return () => {
      controller.abort();
      window.clearInterval(timer);
      document.removeEventListener("visibilitychange", refresh);
    };
  }, []);

  async function mutate(id: string, method: "PUT" | "DELETE", body?: object) {
    if (changing.current) return;
    changing.current = true;
    ++loadVersion.current;
    setBusy(true);
    setError("");
    try {
      const response = await fetch(`/api/calendar/${id}`, {
        method,
        headers: body ? { "Content-Type": "application/json" } : undefined,
        body: body ? JSON.stringify(body) : undefined,
      });
      if (!response.ok) {
        const data = await response.json().catch(() => ({}));
        throw new Error(data.error ?? "Không cập nhật được sự kiện. Hãy tải lại lịch.");
      }
      setEditing(undefined);
      await load();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Không kết nối được với lịch thử.");
    } finally {
      changing.current = false;
      setBusy(false);
    }
  }

  function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!editing || busy) return;
    const fields = new FormData(event.currentTarget);
    void mutate(editing.id, "PUT", {
      title: fields.get("title"),
      start: `${fields.get("start")}:00+07:00`,
      end: `${fields.get("end")}:00+07:00`,
    });
  }

  return <section className="transcript calendar" aria-busy={busy}>
    <h1>Lịch thử</h1>
    {error && <p role="alert">{error}</p>}
    {refreshError && <p role="status">Chưa lấy được cập nhật mới. Lịch sẽ tự thử lại.</p>}
    {busy && <p role="status">Đang cập nhật lịch…</p>}
    {enabled === false && <p>Khởi động lab với tùy chọn <code>-CalendarTrial</code> để dùng lịch thử.</p>}
    {enabled && <p>Dùng chat để tạo sự kiện thử. Giờ hiển thị theo UTC+07:00.</p>}
    {enabled && !busy && !events.length && <p>Chưa có sự kiện.</p>}
    {events.map(item => <article className="user" key={item.id}>
      <strong>{item.title}</strong>
      <p>{formatter.format(new Date(item.start))} → {formatter.format(new Date(item.end))}</p>
      <button disabled={busy} onClick={() => setEditing(item)}>Sửa</button>{" "}
      <button disabled={busy} onClick={() => {
        if (window.confirm(`Xóa sự kiện «${item.title}»?`)) void mutate(item.id, "DELETE");
      }}>Xóa</button>
      {editing?.id === item.id && <form key={item.id} onSubmit={save}>
        <fieldset disabled={busy}>
          <label>Tên sự kiện<input name="title" required maxLength={120} defaultValue={editing.title} /></label>
          <label>Bắt đầu<input name="start" type="datetime-local" required defaultValue={localInput(editing.start)} /></label>
          <label>Kết thúc<input name="end" type="datetime-local" required defaultValue={localInput(editing.end)} /></label>
          <button>Lưu</button>{" "}<button type="button" onClick={() => setEditing(undefined)}>Hủy</button>
        </fieldset>
      </form>}
    </article>)}
  </section>;
}
