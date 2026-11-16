import { useCallback, useEffect, useState, type FormEvent } from "react";

type Suggestion = {
  id: string; kind: string; title: string; startsAt: string; endsAt: string;
  probability: number; certainty: string; reason: string; advice: string; source: string; sourceUrl: string;
};
type Snapshot = {
  location: string | null; fetchedAt: string | null; suggestions: Suggestion[];
  forecastCurrent: boolean; error: string | null; feedbackAllowed: boolean;
};

export function WeatherIntelligence({ compact = false, onNavigate }: {
  compact?: boolean; onNavigate?: (view: "privacy" | "weather") => void;
}) {
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null);
  const [city, setCity] = useState("");
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");

  const reload = useCallback(async (force = false) => {
    const response = force
      ? await fetch("/api/weather/refresh", { method: "POST", headers: { Origin: location.origin } })
      : await fetch("/api/weather", { cache: "no-store" });
    if (!response.ok) {
      if (response.status === 403) throw new Error("Để bật Weather Intelligence, hãy cấp đủ ba quyền Thời tiết trong mục Quyền riêng tư.");
      const data = await response.json().catch(() => null);
      throw new Error(data?.error ?? (response.status === 409 ? "Chọn thành phố để bắt đầu." : "Chưa tải được dự báo thời tiết."));
    }
    const data = await response.json() as Snapshot;
    setSnapshot(data);
    setCity(data.location ?? "");
    setError(data.error ?? "");
    return data;
  }, []);

  useEffect(() => {
    let active = true;
    setLoading(true);
    void reload().catch(cause => { if (active) setError(cause instanceof Error ? cause.message : "Chưa tải được dự báo."); })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [reload]);

  async function saveLocation(event: FormEvent) {
    event.preventDefault();
    setBusy(true); setError(""); setNotice("");
    try {
      const response = await fetch("/api/weather/location", { method: "POST", headers: { "Content-Type": "application/json", Origin: location.origin },
        body: JSON.stringify({ city: city.trim() }) });
      const data = await response.json().catch(() => null);
      if (!response.ok) throw new Error(data?.error ?? (response.status === 403
        ? "Thiếu quyền gửi vị trí tới Open-Meteo, lưu cục bộ hoặc phân tích bằng DeepSeek." : "Không lưu được địa điểm."));
      setSnapshot(data as Snapshot);
      setCity((data as Snapshot).location ?? "");
      setNotice("Đã lưu địa điểm và kiểm tra dự báo mới.");
      setError((data as Snapshot).error ?? "");
    } catch (cause) { setError(cause instanceof Error ? cause.message : "Không lưu được địa điểm."); }
    finally { setBusy(false); }
  }

  async function refresh() {
    setBusy(true); setError(""); setNotice("");
    try { await reload(true); setNotice("Đã kiểm tra dự báo mới."); }
    catch (cause) { setError(cause instanceof Error ? cause.message : "Không tải được dự báo."); }
    finally { setBusy(false); }
  }

  async function clearLocation() {
    setBusy(true); setError(""); setNotice("");
    try {
      const response = await fetch("/api/weather/location", { method: "DELETE", headers: { Origin: location.origin } });
      if (!response.ok) throw new Error("Không xóa được địa điểm thời tiết.");
      setSnapshot({ location: null, fetchedAt: null, suggestions: [], forecastCurrent: false, error: null, feedbackAllowed: false });
      setCity(""); setNotice("Đã xóa địa điểm, cache và gợi ý thời tiết cục bộ.");
    } catch (cause) { setError(cause instanceof Error ? cause.message : "Không xóa được địa điểm."); }
    finally { setBusy(false); }
  }

  async function feedback(item: Suggestion, decision: "interested" | "dismissed") {
    setBusy(true); setError(""); setNotice("");
    try {
      const response = await fetch(`/api/weather/suggestions/${encodeURIComponent(item.id)}/feedback`, {
        method: "POST", headers: { "Content-Type": "application/json", Origin: location.origin }, body: JSON.stringify({ decision }),
      });
      if (response.status === 403) throw new Error("Hãy cấp quyền “Lưu phản hồi thời tiết cục bộ” trong Quyền riêng tư trước.");
      if (!response.ok) throw new Error("Không lưu được phản hồi.");
      await reload();
      setNotice(decision === "interested" ? "Đã ghi nhận phản hồi hữu ích trên máy này." : "Đã bỏ qua gợi ý này; không hiện lại cùng cảnh báo.");
    } catch (cause) { setError(cause instanceof Error ? cause.message : "Không lưu được phản hồi."); }
    finally { setBusy(false); }
  }

  const alertList = snapshot?.suggestions ?? [];
  const time = (value: string) => new Intl.DateTimeFormat("vi-VN", {
    weekday: "short", day: "numeric", month: "short", hour: "2-digit", minute: "2-digit", timeZone: "Asia/Bangkok",
  }).format(new Date(value));

  return <section className={"weather-intelligence " + (compact ? "weather-compact" : "weather-full")} aria-label="Weather Intelligence">
    <div className="weather-heading">
      <div><span className="eyebrow">NGUỒN CHỦ ĐỘNG ĐẦU TIÊN</span><h2>{compact ? "Cảnh báo thời tiết" : "Weather Intelligence"}</h2>
        {!compact && <p>Chỉ quan sát thành phố bạn tự nhập. Khi lưu, tên thành phố và tọa độ sẽ tới Open-Meteo; DeepSeek chỉ nhận các chỉ số dự báo, không nhận tên hay tọa độ.</p>}</div>
      {compact && <button className="text-link" type="button" onClick={() => onNavigate?.("weather")}>Mở →</button>}
    </div>
    {error && <p className="weather-message weather-error" role="alert">{error}{onNavigate && error.includes("cấp đủ ba quyền") && <> <button type="button" onClick={() => onNavigate("privacy")}>Mở Quyền riêng tư</button></>}</p>}
    {notice && <p className="weather-message weather-notice" role="status">{notice}</p>}
    {loading ? <p className="overview-muted" role="status">Đang kiểm tra dự báo…</p> : <>
      {!compact && <form className="weather-location" onSubmit={saveLocation}>
        <label>Thành phố hoặc khu vực đã chọn<input maxLength={100} value={city} onChange={event => setCity(event.target.value)} placeholder="Ví dụ: Đà Nẵng, Việt Nam" /></label>
        <button className="primary" type="submit" disabled={busy || !city.trim()}>{busy ? "Đang kiểm tra…" : "Lưu và lấy dự báo"}</button>
        {snapshot?.location && <button type="button" disabled={busy} onClick={() => void clearLocation()}>Xóa địa điểm</button>}
      </form>}
      {snapshot?.location && <p className="weather-meta">Địa điểm: <strong>{snapshot.location}</strong>
        {snapshot.fetchedAt && <> · Lấy lúc {new Date(snapshot.fetchedAt).toLocaleString("vi-VN", { timeZone: "Asia/Bangkok" })}</>}
        {!snapshot.forecastCurrent && " · Dữ liệu có thể đã cũ"}</p>}
      {alertList.length > 0 ? <div className="weather-alerts">{alertList.slice(0, compact ? 1 : 2).map(item => <article className="weather-alert" key={item.id}>
        <div className="weather-alert-heading"><strong>{item.title}</strong><span>Xác suất mưa {item.probability}% · tín hiệu {item.certainty}</span></div>
        <p><strong>Thời gian:</strong> {time(item.startsAt)}–{time(item.endsAt)}</p>
        <p><strong>Vì sao:</strong> {item.reason}</p><p><strong>Gợi ý:</strong> {item.advice}</p>
        <div className="weather-source"><a href="https://open-meteo.com/" target="_blank" rel="noreferrer">Weather data by Open-Meteo.com</a><span> · </span>
          <a href="https://creativecommons.org/licenses/by/4.0/" target="_blank" rel="noreferrer">CC BY 4.0</a><span> · </span>
          <a href={item.sourceUrl} target="_blank" rel="noreferrer">Chi tiết dữ liệu dự báo</a><span> · Gợi ý đã được Đệ Tốt lọc và diễn giải; thời tiết có thể thay đổi.</span></div>
        {!compact && <div className="weather-feedback"><span>{snapshot?.feedbackAllowed ? "Phản hồi chỉ lưu cục bộ" : "Phản hồi đang tắt"}</span>
          <button disabled={busy || !snapshot?.feedbackAllowed} onClick={() => void feedback(item, "interested")}>Hữu ích</button>
          <button disabled={busy || !snapshot?.feedbackAllowed} onClick={() => void feedback(item, "dismissed")}>Bỏ qua</button>
        </div>}
      </article>)}</div> : <p className="weather-quiet">{snapshot?.location ? "Chưa có tín hiệu thời tiết đủ rõ để gợi ý. Đệ Tốt sẽ im lặng khi không có điều đáng chú ý." : "Chọn thành phố để bắt đầu; chưa có địa điểm nào được quan sát."}</p>}
      {!compact && <div className="weather-actions"><button disabled={busy || !snapshot?.location} onClick={() => void refresh()}>Kiểm tra ngay</button>
        {snapshot?.feedbackAllowed ? <span>Đã bật học phản hồi cục bộ.</span> : <button className="text-link" onClick={() => onNavigate?.("privacy")}>Bật học từ phản hồi</button>}</div>}
      {!compact && <p className="weather-attribution"><a href="https://open-meteo.com/" target="_blank" rel="noreferrer">Weather data by Open-Meteo.com</a> · <a href="https://creativecommons.org/licenses/by/4.0/" target="_blank" rel="noreferrer">CC BY 4.0</a> · Đệ Tốt lọc dữ liệu theo ngưỡng mưa và chuyển thành gợi ý AI. Đây không phải cảnh báo chính thức; dự báo có thể thay đổi và nhà cung cấp không bảo đảm độ chính xác hoặc tính liên tục.</p>}
    </>}
  </section>;
}
