import React, { useEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import Markdown from "react-markdown";
import gfm from "remark-gfm";
import hljs from "highlight.js/lib/core";
import javascript from "highlight.js/lib/languages/javascript";
import typescript from "highlight.js/lib/languages/typescript";
import python from "highlight.js/lib/languages/python";
import json from "highlight.js/lib/languages/json";
import csharp from "highlight.js/lib/languages/csharp";
import { type Conversation, type Message, forkMessages, visibleConversations } from "./history";
import "./style.css";

const storageKey = "detot.conversations.v1";
Object.entries({ javascript, typescript, python, json, csharp }).forEach(([name, language]) => hljs.registerLanguage(name, language));
const uid = () => crypto.randomUUID();
const blank = (): Conversation => ({ id: uid(), title: "Cuộc trò chuyện mới", pinned: false, messages: [] });
const readHistory = (): Conversation[] => {
  try {
    const stored: unknown = JSON.parse(localStorage.getItem(storageKey) ?? "[]");
    return Array.isArray(stored) ? stored.filter(item => item && typeof item.id === "string" &&
      typeof item.title === "string" && Array.isArray(item.messages) &&
      item.messages.every((message: Message) => message && typeof message.text === "string")) : [];
  } catch { return []; }
};

function Code({ className, children }: React.ComponentProps<"code">) {
  const language = className?.match(/^language-([\w-]+)$/)?.[1];
  const text = String(children ?? "");
  if (language && hljs.getLanguage(language)) {
    return <code className={className} dangerouslySetInnerHTML={{ __html: hljs.highlight(text, { language }).value }} />;
  }
  return <code className={className}>{children}</code>;
}

function App() {
  const [items, setItems] = useState(readHistory);
  const [active, setActive] = useState<string>();
  const [query, setQuery] = useState("");
  const [draft, setDraft] = useState("");
  const [side, setSide] = useState(false);
  const [dark, setDark] = useState(() => localStorage.getItem("detot.theme") !== "light");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const request = useRef<AbortController | null>(null);
  const retry = useRef<(() => void) | null>(null);
  const end = useRef<HTMLDivElement>(null);
  const input = useRef<HTMLTextAreaElement>(null);
  const conversation = items.find(item => item.id === active);
  useEffect(() => {
    try { localStorage.setItem(storageKey, JSON.stringify(items)); }
    catch { setError("Không thể lưu lịch sử trên trình duyệt này."); }
  }, [items]);
  useEffect(() => {
    document.documentElement.dataset.theme = dark ? "dark" : "light";
    localStorage.setItem("detot.theme", dark ? "dark" : "light");
  }, [dark]);
  useEffect(() => { end.current?.scrollIntoView({ block: "end" }); }, [active, conversation?.messages.length, busy]);

  const save = (value: Conversation) => setItems(old => [value, ...old.filter(item => item.id !== value.id)]);
  const select = (id?: string) => { setActive(id); setDraft(""); setSide(false); setError(""); retry.current = null; };

  async function send(text: string, base = conversation ?? blank(), prefix = base.messages, requestId = uid(), context?: string) {
    if (!text.trim() || request.current) return;
    const user: Message = { id: requestId, role: "user", text: text.trim() };
    const target = { ...base, title: prefix.length ? base.title : text.trim().slice(0, 52), messages: [...prefix, user] };
    save(target);
    setActive(target.id);
    setDraft("");
    setError("");
    setBusy(true);
    const controller = new AbortController();
    request.current = controller;
    retry.current = () => void send(text, base, prefix, requestId, context);
    try {
      const response = await fetch("/api/chat", {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ message: user.text, requestId, calendarContext: context,
          history: prefix.slice(-8).map(message => ({ role: message.role, text: message.text.slice(0, 450) })) }),
        signal: controller.signal,
      });
      const data = await response.json().catch(() => ({}));
      if (!response.ok || typeof data.reply !== "string") throw new Error(data.detail || data.error || (response.status === 429 ? "Đệ Tốt đang xử lý một yêu cầu. Hãy thử lại." : "Không nhận được phản hồi từ Gateway."));
      const assistant: Message = { id: uid(), role: "assistant", text: data.reply,
        tool: data.calendarCreated ? "Đã tạo sự kiện trong lịch thử" : data.toolUsed ? "Công cụ lab đã hoàn tất" : undefined,
        calendarContext: data.calendarContext };
      setItems(old => old.map(item => item.id === target.id ? { ...item, messages: [...target.messages, assistant] } : item));
      retry.current = null;
    } catch (cause) {
      setError(controller.signal.aborted ? "Đã dừng yêu cầu." : cause instanceof Error ? cause.message : "Lỗi kết nối.");
    } finally {
      request.current = null;
      setBusy(false);
    }
  }

  const last = conversation?.messages.at(-1);
  return <div className="app">
    {side && <button className="scrim" aria-label="Đóng sidebar" onClick={() => setSide(false)} />}
    <aside className={side ? "open" : ""}>
      <div className="brand"><span className="mark">✦</span><div>Đệ Tốt<small>Không gian cá nhân</small></div></div>
      <button className="new" disabled={busy} onClick={() => { select(); input.current?.focus(); }}>＋ Cuộc trò chuyện mới</button>
      <input aria-label="Tìm cuộc trò chuyện" placeholder="Tìm cuộc trò chuyện…" value={query} onChange={event => setQuery(event.target.value)} />
      <nav aria-label="Lịch sử">
        {visibleConversations(items, query).map(item => <div className={item.id === active ? "row selected" : "row"} key={item.id}>
          <button className="title" disabled={busy} onClick={() => select(item.id)}>{item.pinned ? "◆ " : ""}{item.title}</button>
          <details><summary aria-label={`Thao tác với ${item.title}`}>⋯</summary><div className="menu">
            <button disabled={busy} onClick={() => save({ ...item, pinned: !item.pinned })}>{item.pinned ? "Bỏ ghim" : "Ghim"}</button>
            <button disabled={busy} onClick={() => {
              const title = window.prompt("Tên cuộc trò chuyện", item.title)?.trim();
              if (title) save({ ...item, title: title.slice(0, 100) });
            }}>Đổi tên</button>
            <button disabled={busy} onClick={() => {
              if (!window.confirm("Xóa bản lưu cuộc trò chuyện trên trình duyệt?")) return;
              setItems(old => old.filter(value => value.id !== item.id));
              if (active === item.id) select();
            }}>Xóa bản lưu</button>
          </div></details>
        </div>)}
      </nav>
      <div className="sidebar-footer"><button onClick={() => setDark(!dark)}>{dark ? "☼ Giao diện sáng" : "☾ Giao diện tối"}</button></div>
    </aside>
    <main>
      <header><button className="mobile" aria-label="Mở lịch sử" onClick={() => setSide(true)}>☰</button><strong>{conversation?.title ?? "Cuộc trò chuyện mới"}</strong><span className="lab">Lab · dữ liệu thử</span></header>
      <section className="transcript" aria-label="Nội dung trò chuyện" aria-busy={busy}>
        {!conversation?.messages.length && <div className="welcome"><div className="mark">✦</div><h1>Hôm nay bạn muốn làm gì?</h1><p>Trò chuyện với Đệ Tốt hoặc tạo một sự kiện trong lịch thử.</p></div>}
        {conversation?.messages.map((message, index) => <article key={message.id} className={message.role}>
          <div className="author">{message.role === "user" ? "Bạn" : "Đệ Tốt"}</div>
          {message.tool && <div className="tool">✓ {message.tool}</div>}
          {message.role === "assistant" ? <Markdown remarkPlugins={[gfm]} components={{
            code: Code, img: ({ alt }) => <span>{alt || "[Hình ảnh]"}</span>,
            a: ({ href, children }) => <a href={href} target="_blank" rel="noopener noreferrer">{children}</a>,
          }}>{message.text}</Markdown> : <div className="user-text">{message.text}</div>}
          {message.role === "user" && <button className="action" disabled={busy} onClick={() => {
            const text = window.prompt("Sửa tin nhắn và tạo nhánh mới", message.text)?.trim();
            if (text && conversation) void send(text, { ...conversation, id: uid(), pinned: false }, forkMessages(conversation.messages, index));
          }}>Sửa và tạo nhánh</button>}
          {message.role === "assistant" && index === conversation.messages.length - 1 && !message.tool && !message.calendarContext && <button className="action" disabled={busy} onClick={() => {
            const previous = conversation.messages[index - 1];
            if (previous?.role === "user") void send(previous.text, conversation, forkMessages(conversation.messages, index - 1));
          }}>Tạo lại câu trả lời</button>}
        </article>)}
        {busy && <p className="progress" role="status">Đệ Tốt đang xử lý…</p>}
        <div ref={end} />
      </section>
      <footer>
        {error && <div className="error" role="alert">{error} {retry.current && <button onClick={() => retry.current?.()}>Thử lại</button>}</div>}
        <form onSubmit={event => { event.preventDefault(); void send(draft); }}>
          <textarea ref={input} aria-label="Tin nhắn" rows={2} maxLength={1500} placeholder="Nhắn cho Đệ Tốt…" value={draft} onChange={event => setDraft(event.target.value)}
            onKeyDown={event => { if (event.key === "Enter" && !event.shiftKey && !event.nativeEvent.isComposing) { event.preventDefault(); void send(draft); } }} />
          <div className="controls">
            {last?.calendarContext && <button type="button" disabled={busy || !draft.trim()} onClick={() => void send(draft, conversation, conversation?.messages, uid(), last.calendarContext)}>Bổ sung cho lịch</button>}
            {busy ? <button type="button" onClick={() => request.current?.abort()}>Dừng</button> : <button className="primary" disabled={!draft.trim()}>Gửi ↑</button>}
          </div>
        </form>
        <p>Lịch sử lưu trên trình duyệt. Tin nhắn được gửi tới nhà cung cấp LLM.</p>
      </footer>
    </main>
  </div>;
}

createRoot(document.getElementById("root")!).render(<App />);
