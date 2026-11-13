import { useEffect, useRef, useState, type FormEvent } from "react";

type Profile = { id: string; name: string };
type Profiles = { active: string | null; items: Profile[] };
const storageKey = "detot.google-profiles.lab.v1";

function readProfiles(): Profiles {
  try {
    const saved = JSON.parse(localStorage.getItem(storageKey) ?? "null");
    if (Array.isArray(saved?.items)) {
      const items: Profile[] = saved.items.filter((item: Profile) =>
        typeof item?.id === "string" && typeof item?.name === "string");
      return { items, active: items.some(item => item.id === saved.active) ? saved.active : items[0]?.id ?? null };
    }
  } catch { /* Use an empty profile list when browser storage is unavailable. */ }
  return { items: [], active: null };
}

export function GoogleProfiles() {
  const [profiles, setProfiles] = useState(readProfiles);
  const [editing, setEditing] = useState<string>();
  const [error, setError] = useState("");
  const dialog = useRef<HTMLDialogElement>(null);
  const active = profiles.items.find(item => item.id === profiles.active);

  useEffect(() => {
    const sync = (event: StorageEvent) => {
      if (event.key === storageKey || event.key === null) setProfiles(readProfiles());
    };
    window.addEventListener("storage", sync);
    return () => window.removeEventListener("storage", sync);
  }, []);

  function save(next: Profiles) {
    try {
      localStorage.setItem(storageKey, JSON.stringify(next));
      setProfiles(next);
      setError("");
      return true;
    } catch {
      setError("Trình duyệt không lưu được hồ sơ.");
      return false;
    }
  }

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const name = String(new FormData(form).get("name") ?? "").trim();
    if (!name || name.length > 40 || name.includes("@")) {
      setError("Nhập biệt danh thử tối đa 40 ký tự và không chứa địa chỉ email.");
      return;
    }
    const current = readProfiles();
    const id = editing ?? crypto.randomUUID();
    if (editing && !current.items.some(item => item.id === editing)) {
      setProfiles(current);
      setEditing(undefined);
      setError("Hồ sơ đã bị xóa ở tab khác.");
      return;
    }
    const items = editing ? current.items.map(item => item.id === id ? { id, name } : item)
      : [...current.items, { id, name }];
    if (save({ items, active: current.active ?? id })) {
      form.reset();
      setEditing(undefined);
    }
  }

  return <>
    <button className="account-avatar" aria-label="Quản lý tài khoản Google" aria-haspopup="dialog"
      title={active?.name ?? "Tài khoản Google"} onClick={() => dialog.current?.showModal()}>
      {active?.name.slice(0, 1).toLocaleUpperCase("vi") ?? "G"}
    </button>
    <dialog className="account-dialog" ref={dialog} aria-labelledby="account-heading">
      <div className="account-heading">
        <h2 id="account-heading">Tài khoản Google</h2>
        <form method="dialog"><button aria-label="Đóng">×</button></form>
      </div>
      <p>Chưa kết nối Google. Hồ sơ thử chỉ lưu biệt danh trên trình duyệt.</p>
      <ul>
        {profiles.items.map(item => <li key={item.id}>
          <strong>{item.name}</strong>
          <div>
            {item.id === profiles.active ? <span>Đang chọn</span> : <button onClick={() => {
              const current = readProfiles();
              if (current.items.some(profile => profile.id === item.id)) save({ ...current, active: item.id });
            }}>Chọn</button>}
            <button onClick={() => setEditing(item.id)}>Đổi tên</button>
            <button onClick={() => {
              if (!window.confirm(`Xóa hồ sơ thử «${item.name}»?`)) return;
              const current = readProfiles();
              const items = current.items.filter(profile => profile.id !== item.id);
              if (save({ items, active: current.active === item.id ? items[0]?.id ?? null : current.active }) && editing === item.id)
                setEditing(undefined);
            }}>Xóa</button>
          </div>
        </li>)}
      </ul>
      <form key={editing ?? "new"} onSubmit={submit}>
        <label htmlFor="profile-name">Biệt danh hồ sơ thử</label>
        <input id="profile-name" name="name" maxLength={40} required defaultValue={profiles.items.find(item => item.id === editing)?.name} />
        <button>{editing ? "Lưu tên" : "Thêm hồ sơ thử"}</button>
        {editing && <button type="button" onClick={() => setEditing(undefined)}>Hủy</button>}
      </form>
      {error && <p role="alert">{error}</p>}
    </dialog>
  </>;
}
