import { useEffect, useRef, useState, type FormEvent } from "react";

type Account = { id: string; email: string };
type Mail = { id: string; subject: string; from: string };
const key = "detot.google.active";

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
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [results, setResults] = useState<Mail[]>();
  const selected = accounts.find(account => account.id === active);
  const canonical = location.origin === "http://127.0.0.1:5183";

  function choose(id: string) {
    localStorage.setItem(key, id);
    setActive(id);
    setResults(undefined);
  }

  async function load() {
    setEnabled(undefined);
    const data = await api<{ enabled: boolean; accounts: Account[] }>("accounts");
    setEnabled(data.enabled);
    setAccounts(data.accounts);
    const saved = localStorage.getItem(key);
    choose(data.accounts.find(account => account.id === saved)?.id ?? data.accounts[0]?.id ?? "");
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
    void run(load, outcome === "connected" ? "Đã kết nối Gmail." : "Google chưa cấp đủ quyền Gmail.");
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
      onClick={() => { dialog.current?.showModal(); if (canonical && !busy) void run(load); }}>
      {selected?.email[0]?.toUpperCase() ?? "G"}
    </button>
    <dialog className="account-dialog" ref={dialog} aria-labelledby="account-heading">
      <div className="account-heading">
        <h2 id="account-heading">Tài khoản Google</h2>
        <form method="dialog"><button aria-label="Đóng">×</button></form>
      </div>
      {!canonical ? <p><a href="http://127.0.0.1:5183/workspace">Mở Đệ Tốt từ backend để quản lý Gmail</a></p>
        : enabled === false && !busy && <p>Gmail thật chưa được bật trong lab. Bạn có thể lưu cấu hình OAuth trước. Kết nối tài khoản chỉ mở sau khi M1 đạt kiểm tra xóa dữ liệu.</p>}
      {canonical && enabled && <>
        <ul>{accounts.map(account => <li key={account.id}>
          <strong>{account.email}</strong>
          <div>
            {active === account.id ? <span>Đang chọn</span> : <button disabled={busy} onClick={() => choose(account.id)}>Chọn</button>}
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
        </li>)}</ul>}
      </>}
      {busy && <p role="status">Đang xử lý…</p>}
      {message && <p role="status">{message}</p>}
    </dialog>
  </>;
}
