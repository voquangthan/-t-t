import { useCallback, useEffect, useState } from "react";

type Task = {
  id: string; text: string; status: string; version: number;
  calendarEventId?: string | null; calendarReminderAt?: string | null; calendarRequestId?: string | null;
};

async function api<T>(path: string, options?: RequestInit): Promise<T> {
  const response = await fetch("/api/memory" + path, options);
  const body = await response.json().catch(() => null);
  if (!response.ok) throw new Error(body?.error ?? body?.detail ?? `Yêu cầu thất bại (HTTP ${response.status}).`);
  return body as T;
}

function localDate(value?: string) {
  const date = value ? new Date(value) : new Date(Date.now() + 60 * 60 * 1000);
  date.setMinutes(date.getMinutes() - date.getTimezoneOffset());
  return date.toISOString().slice(0, 16);
}

export function AgenticTasks() {
  const [tasks, setTasks] = useState<Task[]>([]);
  const [starts, setStarts] = useState<Record<string, string>>({});
  const [ends, setEnds] = useState<Record<string, string>>({});
  const [requests, setRequests] = useState<Record<string, string>>({});
  const [notice, setNotice] = useState("");
  const [loadError, setLoadError] = useState("");
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState("");
  const [reload, setReload] = useState(0);

  const refresh = useCallback(async () => {
    setLoading(true); setLoadError("");
    try {
      const rows = await api<Task[]>("/agentic/tasks");
      setTasks(rows);
      setRequests(previous => Object.fromEntries(rows.filter(task => task.calendarRequestId)
        .map(task => [task.id, task.calendarRequestId!])))
      setStarts(previous => Object.fromEntries(rows.map(task => [task.id,
        previous[task.id] ?? localDate(task.calendarReminderAt ?? undefined)])));
      setEnds(previous => Object.fromEntries(rows.map(task => [task.id,
        previous[task.id] ?? localDate(new Date(new Date(task.calendarReminderAt ?? Date.now() + 60 * 60 * 1000).getTime() + 15 * 60 * 1000).toISOString())])));
    } catch (error) { setLoadError(error instanceof Error ? error.message : "Không tải được danh sách việc."); }
    finally { setLoading(false); }
  }, []);

  useEffect(() => { void refresh(); }, [refresh, reload]);

  async function schedule(task: Task) {
    const start = starts[task.id];
    const end = ends[task.id];
    if (!start || !end || new Date(end) <= new Date(start)) {
      setNotice("Chọn giờ kết thúc sau giờ bắt đầu."); return;
    }
    const requestId = requests[task.id] ?? crypto.randomUUID();
    setRequests(previous => ({ ...previous, [task.id]: requestId }));
    setBusy(task.id); setNotice("");
    try {
      await api(`/${task.id}/calendar-reminder`, {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ version: task.version, requestId,
          start: new Date(start).toISOString(), end: new Date(end).toISOString(), reminderMinutes: [10] }),
      });
      setRequests(previous => { const next = { ...previous }; delete next[task.id]; return next; });
      setNotice("Đã tạo nhắc lịch và liên kết với công việc."); setReload(value => value + 1);
    } catch (error) { setNotice(error instanceof Error ? error.message : "Chưa tạo được nhắc lịch. Có thể thử lại an toàn."); }
    finally { setBusy(""); }
  }

  async function complete(task: Task) {
    setBusy(task.id); setNotice("");
    try {
      await api(`/${task.id}/complete`, { method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ version: task.version, requestId: crypto.randomUUID(), category: "task" }) });
      const saved = await api<Task>(`/${task.id}`);
      if (saved.status !== "done") throw new Error("Backend chưa xác nhận trạng thái hoàn thành. Hãy tải lại danh sách trước khi thử lại.");
      setNotice("Đã đánh dấu công việc hoàn thành; nhắc lịch được giữ nguyên để bạn tự quản lý trong Google Calendar.");
      setReload(value => value + 1);
    } catch (error) { setNotice(error instanceof Error ? error.message : "Chưa cập nhật được công việc."); }
    finally { setBusy(""); }
  }

  return <section className="transcript agentic-tasks">
    <h1>Việc cần theo dõi</h1>
    <p>Chọn công việc và thời gian để tạo nhắc trên Google Calendar. Đệ Tốt chỉ tạo lịch sau thao tác này.</p>
    {notice && <p role="status">{notice}</p>}
    {loadError && <p role="alert">{loadError} <button type="button" onClick={() => setReload(value => value + 1)}>Thử tải lại</button></p>}
    {loading && tasks.length === 0 ? <p role="status">Đang tải công việc…</p> : !loading && !loadError && tasks.length === 0 ? <p>Chưa có công việc nào trong Memory.</p> : null}
    {tasks.map(task => <article className="agentic-task" key={task.id}>
      <h2>{task.text}</h2><small>{task.status === "open" ? "Đang mở" : "Hoàn thành"} · mã {task.id} · v{task.version}</small>
      {task.calendarEventId && <p>Đã liên kết Google Calendar · {task.calendarReminderAt ? new Date(task.calendarReminderAt).toLocaleString() : task.calendarEventId}</p>}
      {!task.calendarEventId && task.calendarRequestId && <p>Đang chờ xác nhận từ Calendar. Có thể thử lại cùng yêu cầu để tránh tạo trùng.</p>}
      {task.status === "open" && !task.calendarEventId && <div className="agentic-schedule">
        <label>Bắt đầu <input type="datetime-local" value={starts[task.id] ?? ""}
          onChange={event => setStarts(value => ({ ...value, [task.id]: event.target.value }))} /></label>
        <label>Kết thúc <input type="datetime-local" value={ends[task.id] ?? ""}
          onChange={event => setEnds(value => ({ ...value, [task.id]: event.target.value }))} /></label>
        <button disabled={busy === task.id} onClick={() => void schedule(task)}>{busy === task.id ? "Đang lưu…" : "Tạo nhắc lịch Google"}</button>
      </div>}
      {task.status === "open" && <button disabled={busy === task.id} onClick={() => void complete(task)}>Đánh dấu hoàn thành</button>}
    </article>)}
  </section>;
}
