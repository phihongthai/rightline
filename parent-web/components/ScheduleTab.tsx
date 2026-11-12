"use client";
import { useRef, useState } from "react";
import type { Policy } from "@/lib/api";

// schedule_grid[day][hour]; day 0 = Chủ nhật (khớp DayOfWeek của .NET / Date.getDay()).
const DAYS: [number, string][] = [[1, "T2"], [2, "T3"], [3, "T4"], [4, "T5"], [5, "T6"], [6, "T7"], [0, "CN"]];
const clone = (g: boolean[][]) => g.map((r) => [...r]);

export function ScheduleTab({ policy, savePolicy }: { policy: Policy; savePolicy: (p: Partial<Policy>) => Promise<void> }) {
  const [grid, setGrid] = useState(() => clone(policy.schedule_grid));
  const [limit, setLimit] = useState(policy.daily_limit_minutes?.toString() ?? "");
  const [saved, setSaved] = useState(false);
  const paint = useRef<boolean | null>(null);

  const dirty = JSON.stringify(grid) !== JSON.stringify(policy.schedule_grid) || limit !== (policy.daily_limit_minutes?.toString() ?? "");
  const set = (d: number, h: number, v: boolean) =>
    setGrid((g) => (g[d][h] === v ? g : g.map((r, i) => (i === d ? r.map((c, j) => (j === h ? v : c)) : r))));

  const cellAt = (e: React.PointerEvent) => {
    const el = document.elementFromPoint(e.clientX, e.clientY) as HTMLElement | null;
    const c = el?.closest<HTMLElement>("[data-d]");
    return c ? { d: +c.dataset.d!, h: +c.dataset.h! } : null;
  };
  const down = (e: React.PointerEvent) => {
    const c = cellAt(e);
    if (!c) return;
    paint.current = !grid[c.d][c.h];
    set(c.d, c.h, paint.current);
  };
  const move = (e: React.PointerEvent) => {
    if (paint.current === null) return;
    const c = cellAt(e);
    if (c) set(c.d, c.h, paint.current);
  };
  const up = () => (paint.current = null);

  const fill = (f: (d: number, h: number) => boolean) => setGrid(grid.map((r, d) => r.map((_, h) => f(d, h))));
  const toggleDay = (d: number) => { const v = !grid[d].every(Boolean); setGrid(grid.map((r, i) => (i === d ? r.map(() => v) : r))); };
  const toggleHour = (h: number) => { const v = !grid.every((r) => r[h]); setGrid(grid.map((r) => r.map((c, j) => (j === h ? v : c)))); };

  async function save() {
    const n = limit.trim() === "" ? null : Math.max(0, Math.min(1440, parseInt(limit, 10) || 0));
    await savePolicy({ schedule_grid: grid, daily_limit_minutes: n });
    setSaved(true);
    setTimeout(() => setSaved(false), 1500);
  }

  return (
    <>
      <section className="card space-y-2">
        <label className="block text-sm font-medium">Tổng số phút tối đa mỗi ngày</label>
        <input className="input" inputMode="numeric" placeholder="Không giới hạn" value={limit} onChange={(e) => setLimit(e.target.value.replace(/\D/g, ""))} />
      </section>

      <section className="card">
        <div className="mb-2 flex flex-wrap gap-2 text-sm">
          <button className="rounded-lg bg-slate-100 px-3 py-1.5" onClick={() => fill(() => true)}>Cả ngày</button>
          <button className="rounded-lg bg-slate-100 px-3 py-1.5" onClick={() => fill((d, h) => h >= 8 && h < 21)}>8h–21h</button>
          <button className="rounded-lg bg-slate-100 px-3 py-1.5" onClick={() => fill(() => false)}>Khóa hết</button>
        </div>
        <div className="mb-1 flex items-center gap-3 text-xs text-slate-500">
          <span><i className="mr-1 inline-block h-3 w-3 rounded bg-green-500 align-middle" />Được dùng</span>
          <span><i className="mr-1 inline-block h-3 w-3 rounded bg-slate-300 align-middle" />Khóa</span>
          <span className="ml-auto">Chạm/kéo để tô</span>
        </div>
        <div className="grid select-none gap-px" style={{ gridTemplateColumns: "2rem repeat(7, 1fr)" }}>
          <div />
          {DAYS.map(([d, l]) => (
            <button key={d} className="py-1 text-center text-xs font-semibold" onClick={() => toggleDay(d)}>{l}</button>
          ))}
        </div>
        <div data-testid="grid" className="grid select-none gap-px" style={{ gridTemplateColumns: "2rem repeat(7, 1fr)", touchAction: "none" }}
          onPointerDown={down} onPointerMove={move} onPointerUp={up} onPointerCancel={up} onPointerLeave={up}>
          {Array.from({ length: 24 }, (_, h) => (
            <div key={h} className="contents">
              <button className="pr-1 text-right text-[10px] leading-[22px] text-slate-500" onClick={() => toggleHour(h)}>{h}</button>
              {DAYS.map(([d]) => (
                <div key={d} data-d={d} data-h={h} data-on={grid[d][h]} className={`h-[22px] rounded-sm ${grid[d][h] ? "bg-green-500" : "bg-slate-300"}`} />
              ))}
            </div>
          ))}
        </div>
        {!policy.protection_flags.schedule_lock && (
          <p className="mt-3 text-xs text-amber-700">Công tắc “Lịch dùng máy” đang tắt — lịch chưa có hiệu lực.</p>
        )}
      </section>

      <button disabled={!dirty} className="btn sticky bottom-4 w-full bg-blue-600 text-white shadow-lg" onClick={save}>
        {saved ? "Đã lưu ✓" : "Lưu thay đổi"}
      </button>
    </>
  );
}
