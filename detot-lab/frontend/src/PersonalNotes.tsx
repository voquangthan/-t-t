import { useCallback, useEffect, useMemo, useState, type FormEvent } from "react";

type Note = {
  id: string; text: string; category: string; status: string; createdAt: string;
  history: unknown[];
};

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, init);
  const body = response.status === 204 ? null : await response.json().catch(() => null);
  if (!response.ok) throw new Error(body?.detail ?? body?.error ?? `Không hoàn tất yêu cầu (HTTP ${response.status}).`);
  return body as T;
}

function parseNote(value: string) {
  const split = value.indexOf("\n\n");
  return split < 0 ? { title: value, body: "" } : { title: value.slice(0, split), body: value.slice(split + 2) };
}

export function PersonalNotes() {
  const [notes, setNotes] = useState<Note[]>([]);
  const [query, setQuery] = useState("");
  const [title, setTitle] = useState("");
  const [body, setBody] = useState("");
  const [editing, setEditing] = useState<Note | null>(null);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState("");
  const [error, setError] = useState("");

  const reload = useCallback(async () => {
    const rows = await request<Note[]>("/api/memory?category=note");
    setNotes(rows.filter(item => item.status !== "deleted" && item.status !== "archived"));
  }, []);
  useEffect(() => { void reload().catch(cause => setError(cause instanceof Error ? cause.message : "Không tải được ghi chú.")); }, [reload]);

  const visible = useMemo(() => {
    const needle = query.trim().toLocaleLowerCase();
    return notes.filter(note => !needle || note.text.toLocaleLowerCase().includes(needle));
  }, [notes, query]);

  function reset() { setEditing(null); setTitle(""); setBody(""); }
  function compose(note?: Note) {
    if (!note) { reset(); return; }
    const parsed = parseNote(note.text);
    setEditing(note); setTitle(parsed.title); setBody(parsed.body);
  }

  async function save(event: FormEvent) {
    event.preventDefault();
    const text = `${title.trim()}${body.trim() ? `\n\n${body.trim()}` : ""}`;
    if (!title.trim() || text.length > 500) { setError("Ghi chú cần tiêu đề và tối đa 500 ký tự."); return; }
    setBusy(true); setError(""); setNotice("");
    try {
      if (editing) {
        await request(`/api/memory/${editing.id}`, { method: "PATCH", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ text, version: editing.history.length, requestId: crypto.randomUUID() }) });
        setNotice("Đã cập nhật ghi chú.");
      } else {
        const result = await request<{ reply: string }>("/api/memory", { method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ text, category: "note", requestId: crypto.randomUUID() }) });
        if (!result.reply.startsWith("Đã lưu ký ức")) throw new Error(result.reply || "Ghi chú chưa được lưu.");
        setNotice("Đã lưu ghi chú.");
      }
      reset(); await reload();
    } catch (cause) { setError(cause instanceof Error ? cause.message : "Không lưu được ghi chú."); }
    finally { setBusy(false); }
  }

  async function archive(note: Note) {
    if (!window.confirm(`Xóa ghi chú “${parseNote(note.text).title}” khỏi danh sách?`)) return;
    setBusy(true); setError(""); setNotice("");
    try {
      await request(`/api/memory/${note.id}?version=${note.history.length}`, { method: "DELETE" });
      setNotice("Đã xóa ghi chú khỏi danh sách.");
      if (editing?.id === note.id) reset();
      await reload();
    } catch (cause) { setError(cause instanceof Error ? cause.message : "Không thể lưu trữ ghi chú."); }
    finally { setBusy(false); }
  }

  return <section className="personal-notes transcript" aria-label="Ghi chú cá nhân">
    <div className="personal-notes-heading"><div><h1>Ghi chú cá nhân</h1><p>Lưu điều bạn muốn Đệ Tốt nhớ. Ghi chú được mã hóa và có thể tìm lại trong Chat.</p></div>
      <button type="button" onClick={() => compose()} disabled={busy}>Ghi chú mới</button></div>
    {error && <p className="cloud-error" role="alert">{error}</p>}{notice && <p className="cloud-notice" role="status">{notice}</p>}
    <div className="personal-notes-layout">
      <div className="personal-notes-list">
        <label>Tìm ghi chú<input value={query} onChange={event => setQuery(event.target.value)} placeholder="Tìm theo nội dung…" /></label>
        {visible.length === 0 ? <p className="muted">{notes.length ? "Không có ghi chú khớp." : "Chưa có ghi chú. Bạn có thể lưu bằng nút này hoặc nhắn “nhớ giúp mình…” trong Chat."}</p> : visible.map(note => {
          const parsed = parseNote(note.text);
          return <article className={`personal-note${editing?.id === note.id ? " selected" : ""}`} key={note.id}>
            <button className="personal-note-open" type="button" onClick={() => compose(note)} disabled={busy}>
              <strong>{parsed.title}</strong>{parsed.body && <span>{parsed.body}</span>}
              <small>{new Date(note.createdAt).toLocaleString()} · {note.id.slice(0, 8)}</small>
            </button>
            <button type="button" aria-label={`Xóa ${parsed.title}`} disabled={busy} onClick={() => void archive(note)}>Xóa</button>
          </article>;
        })}
      </div>
      <form className="personal-note-form" onSubmit={save}>
        <h2>{editing ? "Sửa ghi chú" : "Ghi chú mới"}</h2>
        <label>Tiêu đề<input required maxLength={100} value={title} onChange={event => setTitle(event.target.value)} /></label>
        <label>Nội dung<textarea maxLength={Math.max(0, 500 - title.length - (body ? 2 : 0))}
          value={body} onChange={event => setBody(event.target.value)} rows={7} /></label>
        <small>{title.length + (body ? body.length + 2 : 0)}/500 ký tự · Có thể tìm lại bằng cách diễn đạt khác trong Chat.</small>
        <div className="cloud-actions"><button disabled={busy || !title.trim()}>{busy ? "Đang lưu…" : editing ? "Lưu thay đổi" : "Lưu ghi chú"}</button>
          {editing && <button type="button" disabled={busy} onClick={reset}>Hủy sửa</button>}</div>
      </form>
    </div>
  </section>;
}
