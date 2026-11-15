import { useEffect, useMemo, useState, type FormEvent } from "react";

type View = "overview" | "chat" | "tasks" | "calendar" | "notes" | "cloud" | "learning" | "dreaming" | "practice" | "skills";
type Task = { id: string; text: string; status: string; calendarReminderAt?: string | null };
type Note = { id: string; text: string; status?: string; createdAt?: string | null };
type CalendarAccount = { id: string; email?: string };
type ApiEvent = { id: string; summary?: string; title?: string; start?: { dateTime?: string; date?: string } | string; end?: { dateTime?: string; date?: string } | string };
type EventView = { id: string; title: string; start: string };

async function read<T>(path: string, signal: AbortSignal): Promise<T> {
  const response = await fetch(path, { signal });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  return response.json() as Promise<T>;
}

function eventDate(value: ApiEvent["start"]) {
  if (typeof value === "string") return value;
  return value?.dateTime ?? (value?.date ? `${value.date}T00:00:00+07:00` : "");
}

export function Overview({ onNavigate, onChat }: { onNavigate: (view: View) => void; onChat: (text: string) => void }) {
  const [tasks, setTasks] = useState<Task[]>([]);
  const [events, setEvents] = useState<EventView[]>([]);
  const [notes, setNotes] = useState<Note[]>([]);
  const [calendarMessage, setCalendarMessage] = useState("");
  const [taskMessage, setTaskMessage] = useState("");
  const [noteMessage, setNoteMessage] = useState("");
  const [draft, setDraft] = useState("");
  const [taskLoading, setTaskLoading] = useState(true);
  const [eventLoading, setEventLoading] = useState(true);
  const [noteLoading, setNoteLoading] = useState(true);
  const today = useMemo(() => new Intl.DateTimeFormat("vi-VN", {
    weekday: "long", day: "numeric", month: "long", timeZone: "Asia/Bangkok",
  }).format(new Date()), []);

  useEffect(() => {
    const controller = new AbortController();
    const { signal } = controller;
    const calendarController = new AbortController();
    const calendarTimeout = window.setTimeout(() => calendarController.abort(), 8000);
    signal.addEventListener("abort", () => calendarController.abort(), { once: true });
    void Promise.all([
      read<Task[]>("/api/memory/agentic/tasks", signal).then(value => setTasks(value.filter(task => task.status === "open"))).catch(() => setTaskMessage("Chưa tải được danh sách việc.")).finally(() => { if (!signal.aborted) setTaskLoading(false); }),
      read<Note[]>("/api/memory?category=note", signal).then(value => setNotes(value.filter(note => note.status !== "deleted" && note.status !== "archived").sort((a, b) => Date.parse(b.createdAt ?? "") - Date.parse(a.createdAt ?? "")).slice(0, 5))).catch(() => setNoteMessage("Chưa tải được ghi chú.")).finally(() => { if (!signal.aborted) setNoteLoading(false); }),
      (async () => {
        try {
          const accounts = await read<CalendarAccount[]>("/api/google/calendar/accounts", calendarController.signal);
          if (accounts.length > 0) {
            const data = await read<{ events: ApiEvent[] }>(`/api/google/calendar/${encodeURIComponent(accounts[0].id)}/events`, calendarController.signal);
            setEvents(data.events.map(item => ({ id: item.id, title: item.summary ?? "Sự kiện", start: eventDate(item.start) })).filter(item => item.start && new Date(item.start) >= new Date()).sort((a, b) => Date.parse(a.start) - Date.parse(b.start)).slice(0, 5));
          } else {
            const data = await read<{ events: ApiEvent[]; enabled?: boolean }>("/api/calendar", calendarController.signal);
            setEvents((data.events ?? []).map(item => ({ id: item.id, title: item.title ?? item.summary ?? "Sự kiện", start: eventDate(item.start) })).filter(item => item.start && new Date(item.start) >= new Date()).sort((a, b) => Date.parse(a.start) - Date.parse(b.start)).slice(0, 5));
            if (!data.enabled) setCalendarMessage("Kết nối Google Calendar để xem lịch sắp tới.");
          }
        } catch { if (!signal.aborted) setCalendarMessage(calendarController.signal.aborted ? "Tải lịch quá lâu. Bạn có thể mở Lịch để thử lại." : "Chưa tải được lịch. Bạn có thể mở Lịch để thử lại."); }
        finally { window.clearTimeout(calendarTimeout); if (!signal.aborted) setEventLoading(false); }
      })(),
    ]);
    return () => { controller.abort(); window.clearTimeout(calendarTimeout); };
  }, []);

  function submit(event: FormEvent) {
    event.preventDefault();
    if (!draft.trim()) return;
    onChat(draft.trim());
    setDraft("");
  }

  const dateTime = (value: string) => new Intl.DateTimeFormat("vi-VN", {
    weekday: "short", day: "numeric", month: "short", hour: "2-digit", minute: "2-digit", timeZone: "Asia/Bangkok",
  }).format(new Date(value));

  return <section className="overview-page">
    <div className="overview-welcome"><div><span className="eyebrow">KHÔNG GIAN CỦA BẠN</span><h1>Chào bạn.</h1><p>{today} · Mình có thể giúp bạn sắp xếp hôm nay.</p></div>
      <button className="overview-start-chat" type="button" onClick={() => onNavigate("chat")}><Icon name="chat"/> Mở trò chuyện</button></div>
    <form className="quick-chat" onSubmit={submit}>
      <Icon name="spark"/><textarea aria-label="Chat nhanh" rows={2} maxLength={1500} placeholder="Bạn muốn lên kế hoạch, ghi chú điều gì hay hỏi Đệ Tốt?" value={draft} onChange={event => setDraft(event.target.value)} onKeyDown={event => { if (event.key === "Enter" && !event.shiftKey && !event.nativeEvent.isComposing) { event.preventDefault(); if (draft.trim()) { onChat(draft.trim()); setDraft(""); } } }} />
      <button className="primary" disabled={!draft.trim()} aria-label="Gửi tin nhắn">Gửi</button>
    </form>
    <div className="overview-grid">
      <section className="overview-card overview-tasks">
        <div className="overview-card-head"><div><span className="overview-icon"><Icon name="tasks"/></span><h2>Việc hôm nay</h2></div><button className="text-link" type="button" onClick={() => onNavigate("tasks")}>Tất cả <Icon name="arrow"/></button></div>
        {taskLoading ? <p className="overview-muted">Đang tải việc…</p> : taskMessage ? <p className="overview-muted">{taskMessage}</p> : tasks.length ? <ul className="overview-list">{tasks.slice(0, 4).map(task => <li key={task.id}><span className="task-dot"/><span>{task.text}</span><small>{task.calendarReminderAt ? dateTime(task.calendarReminderAt) : "Đang mở"}</small></li>)}</ul> : <div className="overview-empty"><p>Chưa có việc đang mở.</p><button type="button" onClick={() => onChat("Giúp mình ghi lại một việc cần làm hôm nay.")}>Thêm việc qua Chat</button></div>}
      </section>
      <section className="overview-card overview-calendar">
        <div className="overview-card-head"><div><span className="overview-icon"><Icon name="calendar"/></span><h2>Lịch sắp tới</h2></div><button className="text-link" type="button" onClick={() => onNavigate("calendar")}>Mở lịch <Icon name="arrow"/></button></div>
        {eventLoading ? <p className="overview-muted">Đang tải lịch…</p> : calendarMessage ? <div className="overview-empty"><p>{calendarMessage}</p><button type="button" onClick={() => onNavigate("calendar")}>Mở Lịch</button></div> : events.length ? <ul className="overview-list">{events.slice(0, 4).map(item => <li key={item.id}><span className="event-time">{dateTime(item.start)}</span><span>{item.title}</span></li>)}</ul> : <div className="overview-empty"><p>Chưa có sự kiện sắp tới.</p><button type="button" onClick={() => onNavigate("calendar")}>Xem lịch</button></div>}
      </section>
      <section className="overview-card overview-notes">
        <div className="overview-card-head"><div><span className="overview-icon"><Icon name="notes"/></span><h2>Ghi chú gần đây</h2></div><button className="text-link" type="button" onClick={() => onNavigate("notes")}>Tất cả <Icon name="arrow"/></button></div>
        {noteLoading ? <p className="overview-muted">Đang tải ghi chú…</p> : noteMessage ? <p className="overview-muted">{noteMessage}</p> : notes.length ? <ul className="overview-list overview-note-list">{notes.slice(0, 4).map(note => <li key={note.id}><span className="note-mark"/><span>{note.text}</span></li>)}</ul> : <div className="overview-empty"><p>Ghi lại ý muốn nhớ để tìm lại trong Chat.</p><button type="button" onClick={() => onNavigate("notes")}>Tạo ghi chú</button></div>}
      </section>
    </div>
    <div className="overview-footer"><span>Đệ Tốt chỉ thay đổi lịch hoặc công việc khi bạn yêu cầu rõ ràng.</span><button type="button" onClick={() => onChat("Giúp mình chọn một việc nhỏ để bắt đầu hôm nay.")}>Gợi ý bước tiếp theo <Icon name="arrow"/></button></div>
  </section>;
}

export function Icon({ name }: { name: string }) {
  const common = { width: 20, height: 20, viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 1.8, strokeLinecap: "round" as const, strokeLinejoin: "round" as const, "aria-hidden": true as const };
  switch (name) {
    case "overview": return <svg {...common}><rect x="3.5" y="3.5" width="7" height="7" rx="1.5"/><rect x="13.5" y="3.5" width="7" height="7" rx="1.5"/><rect x="3.5" y="13.5" width="7" height="7" rx="1.5"/><rect x="13.5" y="13.5" width="7" height="7" rx="1.5"/></svg>;
    case "chat": return <svg {...common}><path d="M20 11.2a7.8 7.8 0 0 1-8 7.7 8.5 8.5 0 0 1-3.2-.6L4 20l1.5-4A7.4 7.4 0 0 1 4 11.2a7.8 7.8 0 0 1 8-7.7 7.8 7.8 0 0 1 8 7.7Z"/><path d="M8 11h8M8 14h5"/></svg>;
    case "tasks": return <svg {...common}><rect x="4" y="4" width="16" height="16" rx="3"/><path d="m8 12 2.5 2.5L16 9M8 7.5h2"/></svg>;
    case "calendar": return <svg {...common}><rect x="3.5" y="5" width="17" height="16" rx="2.5"/><path d="M7.5 3v4M16.5 3v4M4 9.5h16M8 13h2M14 13h2M8 16.5h2"/></svg>;
    case "notes": return <svg {...common}><path d="M6 4.5h12a2 2 0 0 1 2 2v13H6a2 2 0 0 1-2-2v-11a2 2 0 0 1 2-2Z"/><path d="M8 9h8M8 12.5h8M8 16h5M4 7v11"/></svg>;
    case "tools": return <svg {...common}><path d="m14 6 4 4M4 20l9.5-9.5M14.5 4.5a4 4 0 0 0 5 5L13 16l-5-5 6.5-6.5Z"/><path d="m5 14 5 5"/></svg>;
    case "cloud": return <svg {...common}><path d="M7 18h10a4 4 0 0 0 .5-8A6 6 0 0 0 6 9a4.5 4.5 0 0 0 1 9Z"/><path d="M12 12v6m-2-2 2 2 2-2"/></svg>;
    case "book": return <svg {...common}><path d="M4 5.5A2.5 2.5 0 0 1 6.5 3H20v16H6.5A2.5 2.5 0 0 0 4 21V5.5Z"/><path d="M4 17a2.5 2.5 0 0 1 2.5-2.5H20M8 7h8M8 10h6"/></svg>;
    case "dream": return <svg {...common}><path d="m12 3 1.7 5.3L19 10l-5.3 1.7L12 17l-1.7-5.3L5 10l5.3-1.7L12 3Z"/><path d="m19 15 .8 2.2L22 18l-2.2.8L19 21l-.8-2.2L16 18l2.2-.8L19 15Z"/></svg>;
    case "practice": return <svg {...common}><rect x="3" y="4" width="18" height="16" rx="2.5"/><path d="m7 9 2.5 2.5L7 14M12 15h5"/></svg>;
    case "skills": return <svg {...common}><path d="M4 19.5h16M6 16v-4M11 16V7M16 16V4M20 16v-7"/></svg>;
    case "spark": return <svg {...common}><path d="m12 3 1.9 5.8L20 11l-6.1 2.2L12 19l-1.9-5.8L4 11l6.1-2.2L12 3Z"/></svg>;
    case "arrow": return <svg {...common} width="16" height="16"><path d="M4 12h15M13 6l6 6-6 6"/></svg>;
    case "chevron": return <svg {...common} width="16" height="16"><path d="m7 10 5 5 5-5"/></svg>;
    case "collapse": return <svg {...common}><rect x="3" y="4" width="18" height="16" rx="2.5"/><path d="M9 4v16M14 9l-3 3 3 3"/></svg>;
    case "expand": return <svg {...common}><rect x="3" y="4" width="18" height="16" rx="2.5"/><path d="M9 4v16m5-7 3-3-3-3"/></svg>;
    case "menu": return <svg {...common}><path d="M4 7h16M4 12h16M4 17h16"/></svg>;
    case "search": return <svg {...common}><circle cx="10.8" cy="10.8" r="6.7"/><path d="m16 16 4.5 4.5"/></svg>;
    case "history": return <svg {...common}><path d="M4 8V3.5M4 8h4.5"/><path d="M4.6 8a8.2 8.2 0 1 1-.1 7M12 7v5l3.2 2"/></svg>;
    default: return <svg {...common}><circle cx="12" cy="12" r="8"/></svg>;
  }
}
