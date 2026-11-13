import { useEffect, useRef, useState, type FormEvent } from "react";

type Account = { id: string; email: string; notes?: Note[]; selected?: boolean };
type Mail = { id: string; subject: string; from: string };
type Note = { id: string; text: string };

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch("/api/google/" + path, init);
  const body = await response.json().catch(() => null);
  if (path === "accounts" && response.status === 404)
    throw new Error("Backend đang chạy chưa có API tài khoản Google. Dừng lab rồi chạy lại Start-Lab.ps1 để nạp bản mới.");
  if (!response.ok) throw new Error(body?.detail ?? `Yêu cầu Google thất bại (HTTP ${response.status}).`);
  return body;
}

export function GoogleProfiles() {
  const dialog = useRef<HTMLDialogElement>(null);
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [active, setActive] = useState("");
  const [enabled, setEnabled] = useState<boolean>();
  const [disabledMessage, setDisabledMessage] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [results, setResults] = useState<Mail[]>();
  const [notes, setNotes] = useState<Note[]>([]);
  const [preview, setPreview] = useState("");
  const [draft, setDraft] = useState("");
  const [editing, setEditing] = useState<string>();
  const selected = accounts.find(account => account.id === active);
  const devWorkspace = import.meta.env.DEV;

  function choose(account?: Account) {
    setActive(account?.id ?? "");
    setResults(undefined);
    setPreview("");
    setDraft("");
    setEditing(undefined);
    setNotes(account?.notes ?? []);
  }

  async function saveNotes(next: Note[]) {
    await api(`accounts/${encodeURIComponent(active)}/notes`, {
      method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(next),
    });
    setNotes(next);
    setAccounts(old => old.map(account => account.id === active ? { ...account, notes: next } : account));
  }

  async function load() {
    setEnabled(undefined);
    const data = await api<{ enabled: boolean; accounts: Account[]; configured?: boolean; reason?: string }>("accounts");
    setDisabledMessage(data.reason === "platform"
        ? "Lưu thông tin xác thực Google cần Windows."
        : "Cần cấu hình Google OAuth trên máy chủ để kết nối Gmail.");
    setEnabled(data.enabled);
    setAccounts(data.accounts);
    choose(data.accounts.find(account => account.selected) ?? data.accounts[0]);
  }

  async function run(action: () => Promise<void>, initialMessage = "") {
    setBusy(true);
    setMessage(initialMessage);
    try { await action(); }
    catch (error) { setMessage(error instanceof Error ? error.message : "Không kết nối được với backend."); }
    finally { setBusy(false); }
  }

  useEffect(() => {
    const url = new URL(location.href);
    const outcome = url.searchParams.get("google");
    if (!outcome) return;
    url.searchParams.delete("google");
    history.replaceState(history.state, "", url);
    dialog.current?.showModal();
    const messages: Record<string, string> = {
      connected: "Đã kết nối Gmail.",
      denied: "Luồng cấp quyền đã bị hủy hoặc Google đã từ chối.",
      gmail_scope_missing: "Google chưa cấp quyền đọc Gmail. Kết nối lại và chọn quyền đọc thư trên màn hình Google.",
      unexpected_scope: "Google trả thêm quyền ngoài phạm vi Gmail chỉ đọc. Đệ Tốt chưa lưu kết nối.",
      refresh_missing: "Google không trả mã duy trì kết nối. Hãy thử cấp quyền lại.",
    };
    void run(load, messages[outcome] ?? "Chưa hoàn tất kết nối Gmail.");
  }, []);

  function search(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!selected || busy) return;
    const query = String(new FormData(event.currentTarget).get("query") ?? "");
    setResults(undefined);
    void run(async () => {
      setResults(await api<Mail[]>(`accounts/${encodeURIComponent(selected.id)}/search`, {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ query }),
      }));
    });
  }

  return <>
    <button className="account-avatar" aria-label="Quản lý tài khoản Google" aria-haspopup="dialog"
      onClick={() => { dialog.current?.showModal(); if (!devWorkspace && !busy) void run(load); }}>
      {selected?.email[0]?.toUpperCase() ?? "G"}
    </button>
    <dialog className="account-dialog" ref={dialog} aria-labelledby="account-heading"
      onClose={() => { setPreview(""); setDraft(""); setEditing(undefined); }}>
      <div className="account-heading">
        <h2 id="account-heading">Tài khoản Google</h2>
        <form method="dialog"><button aria-label="Đóng">×</button></form>
      </div>
      {devWorkspace ? <p><a href="/workspace">Mở Đệ Tốt để quản lý Gmail</a></p>
        : enabled === false && !busy && <p>{disabledMessage}</p>}
      {!devWorkspace && enabled && <>
        <ul>{accounts.map(account => <li key={account.id}>
          <strong>{account.email}</strong>
          <div>
            {active === account.id ? <span>Đang chọn</span> : <button disabled={busy} onClick={() => void run(async () => {
              await api(`accounts/${encodeURIComponent(account.id)}/selection`, { method: "PUT" });
              choose(account);
            })}>Chọn</button>}
            <button disabled={busy} onClick={() => {
              if (!window.confirm(`Thu hồi quyền Google của ${account.email}?`)) return;
              void run(async () => {
                await api(`accounts/${encodeURIComponent(account.id)}`, { method: "DELETE" });
                await load();
              });
            }}>Ngắt kết nối</button>
          </div>
        </li>)}</ul>
        <button disabled={busy} onClick={() => void run(async () => {
          const { url } = await api<{ url: string }>("connect", { method: "POST" });
          location.assign(url);
        })}>Kết nối Gmail</button>
        {selected && <form onSubmit={search}>
          <label htmlFor="gmail-query">Tìm thư trong {selected.email}</label>
          <input id="gmail-query" name="query" maxLength={200} placeholder="Ví dụ: is:unread" />
          <button disabled={busy}>Tìm thư</button>
        </form>}
        {results?.length === 0 && <p>Không tìm thấy thư.</p>}
        {results && <ul>{results.map(mail => <li key={mail.id}>
          <strong>{mail.subject || "(Không có tiêu đề)"}</strong><span>{mail.from}</span>
          {selected && <button disabled={busy} onClick={() => void run(async () => {
            setPreview("");
            setDraft("");
            setEditing(undefined);
            const { preview } = await api<{ preview: string }>(`accounts/${encodeURIComponent(selected.id)}/messages/${encodeURIComponent(mail.id)}/preview`);
            setPreview(preview || "Thư không có đoạn xem trước.");
            const preference = preview.match(/(?:^|[.!?]\s+)(?:tôi|mình)\s+(?:thích|ưu tiên|muốn)\s+[^.!?\n]{3,180}/iu);
            setDraft(preference?.[0].trim().replace(/^[.!?]\s+/, "") ?? "");
          })}>Chọn thư làm ghi chú</button>}
        </li>)}</ul>}
        {selected && <section aria-label="Ghi chú cá nhân">
          <h3>Ghi chú cá nhân</h3>
          <p>Ghi chú lưu trên máy chủ Đệ Tốt và chỉ vào chat khi bạn tự gửi.</p>
          {preview && <blockquote>{preview}</blockquote>}
          <form onSubmit={event => {
            event.preventDefault();
            const value = draft.trim();
            if (!value || busy) return;
            const next = editing ? notes.map(note => note.id === editing ? { ...note, text: value } : note)
              : [...notes, { id: crypto.randomUUID(), text: value }];
            void run(async () => {
              await saveNotes(next);
              setDraft(""); setPreview(""); setEditing(undefined);
            });
          }}>
            <label htmlFor="gmail-note">Sở thích hoặc bối cảnh đã kiểm tra</label>
            <textarea id="gmail-note" value={draft} maxLength={500} onChange={event => setDraft(event.target.value)} />
            <button disabled={busy || !draft.trim() || (!editing && notes.length >= 20)}>Lưu ghi chú</button>
          </form>
          <ul>{notes.map(note => <li key={note.id}><span>{note.text}</span><div>
            <button disabled={busy} onClick={() => void run(async () => {
              await navigator.clipboard.writeText(note.text);
              setMessage("Đã sao chép ghi chú. Bạn có thể dán vào chat. Khi gửi, nội dung sẽ được chuyển tới nhà cung cấp mô hình.");
            })}>Sao chép ghi chú</button>
            <button disabled={busy} onClick={() => { setDraft(note.text); setEditing(note.id); setPreview(""); }}>Sửa</button>
            <button disabled={busy} onClick={() => void run(async () => {
              await saveNotes(notes.filter(item => item.id !== note.id));
              if (editing === note.id) {
                setEditing(undefined);
                setDraft("");
              }
            })}>Xóa</button>
          </div></li>)}</ul>
        </section>}
      </>}
      {busy && <p role="status">Đang xử lý…</p>}
      {message && <p role="status">{message}</p>}
    </dialog>
  </>;
}
