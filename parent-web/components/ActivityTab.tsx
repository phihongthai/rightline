"use client";
import { useEffect, useState } from "react";
import { api, type LogRow, type Shot } from "@/lib/api";
import { fmtDateTime, fmtTime, localDate } from "@/lib/hooks";

export function ActivityTab({ deviceId }: { deviceId: string }) {
  const [date, setDate] = useState(localDate());
  const [shots, setShots] = useState<Shot[] | null>(null);
  const [logs, setLogs] = useState<LogRow[] | null>(null);
  const [type, setType] = useState<"" | "web" | "app" | "system">("");
  const [zoom, setZoom] = useState<Shot | null>(null);

  useEffect(() => {
    // Backend filters by UTC day; ask for a wide page and filter by local day client-side.
    const start = new Date(`${date}T00:00:00`);
    const end = new Date(start.getTime() + 86400_000);
    setShots(null);
    api<{ screenshots: Shot[] }>(`/api/devices/${deviceId}/screenshots?limit=200&before=${end.toISOString()}`)
      .then((r) => setShots(r.screenshots.filter((s) => new Date(s.captured_at) >= start)))
      .catch(() => setShots([]));
  }, [deviceId, date]);

  useEffect(() => {
    setLogs(null);
    api<{ logs: LogRow[] }>(`/api/devices/${deviceId}/logs?limit=100${type ? `&type=${type}` : ""}`)
      .then((r) => setLogs(r.logs))
      .catch(() => setLogs([]));
  }, [deviceId, type]);

  return (
    <>
      <section className="space-y-2">
        <div className="flex items-center justify-between">
          <h2 className="font-semibold">Ảnh chụp màn hình</h2>
          <input type="date" className="rounded-lg border border-slate-300 p-1.5 text-sm" value={date} max={localDate()} onChange={(e) => e.target.value && setDate(e.target.value)} aria-label="Ngày" />
        </div>
        {shots === null ? <p className="text-slate-500">Đang tải…</p> : shots.length === 0 ? <p className="card text-center text-slate-500">Không có ảnh trong ngày này.</p> : (
          <div className="grid grid-cols-3 gap-2">
            {shots.map((s) => (
              <button key={s.id} onClick={() => setZoom(s)} className="relative overflow-hidden rounded-lg bg-slate-200">
                {/* eslint-disable-next-line @next/next/no-img-element */}
                <img src={s.url} alt={`Ảnh lúc ${fmtTime(s.captured_at)}`} loading="lazy" className="aspect-video w-full object-cover" />
                <span className="absolute bottom-0 right-0 rounded-tl bg-black/60 px-1 text-[10px] text-white">{fmtTime(s.captured_at)}</span>
              </button>
            ))}
          </div>
        )}
      </section>

      <section className="space-y-2">
        <div className="flex items-center justify-between">
          <h2 className="font-semibold">Lịch sử hoạt động</h2>
          <select className="rounded-lg border border-slate-300 p-1.5 text-sm" value={type} onChange={(e) => setType(e.target.value as typeof type)} aria-label="Loại">
            <option value="">Tất cả</option><option value="web">Web</option><option value="app">App</option><option value="system">Hệ thống</option>
          </select>
        </div>
        {logs === null ? <p className="text-slate-500">Đang tải…</p> : logs.length === 0 ? <p className="card text-center text-slate-500">Chưa có hoạt động.</p> : (
          <ul className="card divide-y divide-slate-100 !p-0">
            {logs.map((l) => (
              <li key={l.id} className="flex items-start gap-3 p-3">
                <span className="mt-0.5 rounded bg-slate-100 px-1.5 text-xs uppercase text-slate-600">{l.log_type}</span>
                <div className="min-w-0 flex-1">
                  <div className="truncate text-sm font-medium">{l.target}</div>
                  {l.title && <div className="truncate text-xs text-slate-500">{l.title}</div>}
                </div>
                <div className="text-right text-xs text-slate-500">
                  <div>{fmtDateTime(l.recorded_at)}</div>
                  {l.duration_sec > 0 && <div>{Math.ceil(l.duration_sec / 60)}p</div>}
                </div>
              </li>
            ))}
          </ul>
        )}
      </section>

      {zoom && (
        <div className="fixed inset-0 z-50 flex flex-col items-center justify-center bg-black/90 p-3" onClick={() => setZoom(null)} role="dialog">
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img src={zoom.url} alt="Ảnh phóng to" className="max-h-[85vh] max-w-full rounded" />
          <p className="mt-2 text-sm text-white">{fmtDateTime(zoom.captured_at)} · chạm để đóng</p>
        </div>
      )}
    </>
  );
}
