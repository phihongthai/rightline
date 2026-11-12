"use client";
import { useEffect, useState } from "react";
import { api, type Device, type Policy, type ProtectionFlags, type TimeRequest } from "@/lib/api";
import { fmtMin, fmtTime } from "@/lib/hooks";

const FLAGS: [keyof ProtectionFlags, string][] = [
  ["web_filter", "Chặn Web"],
  ["app_block", "Chặn App/Game"],
  ["schedule_lock", "Lịch dùng máy"],
  ["screenshot", "Chụp màn hình"],
];

function Toggle({ on, label, onChange }: { on: boolean; label: string; onChange: (v: boolean) => void }) {
  return (
    <button role="switch" aria-checked={on} aria-label={label} onClick={() => onChange(!on)}
      className={`relative h-8 w-14 shrink-0 rounded-full transition ${on ? "bg-blue-600" : "bg-slate-300"}`}>
      <span className={`absolute top-1 h-6 w-6 rounded-full bg-white shadow transition-all ${on ? "left-7" : "left-1"}`} />
    </button>
  );
}

function PairingCard({ device, refresh }: { device: Device; refresh: () => void }) {
  const [left, setLeft] = useState(0);
  useEffect(() => {
    const tick = () => setLeft(device.pairing_expires_at ? new Date(device.pairing_expires_at).getTime() - Date.now() : 0);
    tick();
    const t = setInterval(tick, 1000);
    return () => clearInterval(t);
  }, [device.pairing_expires_at]);
  const valid = device.pairing_code && left > 0;
  return (
    <section className="card space-y-3 text-center">
      <p className="text-slate-600">Cài Agent trên máy Windows của con và nhập mã này:</p>
      {valid ? (
        <>
          <div data-testid="pairing-code" className="font-mono text-4xl font-bold tracking-[0.3em] text-blue-700">{device.pairing_code}</div>
          <p className="text-sm text-slate-500">Còn hiệu lực {Math.floor(left / 60000)}:{String(Math.floor((left % 60000) / 1000)).padStart(2, "0")}</p>
        </>
      ) : (
        <p className="text-amber-700">Mã đã hết hạn.</p>
      )}
      <button className="btn w-full bg-slate-200" onClick={async () => { await api(`/api/devices/${device.id}/pairing-code`, { method: "POST", body: {} }); refresh(); }}>
        Tạo mã mới
      </button>
    </section>
  );
}

export function ControlTab(p: {
  device: Device;
  policy: Policy;
  requests: TimeRequest[];
  savePolicy: (patch: Partial<Policy>) => Promise<void>;
  refresh: () => void;
  onDelete: () => void;
}) {
  const { device: d } = p;
  const [busy, setBusy] = useState(false);
  const act = async (fn: () => Promise<unknown>) => {
    setBusy(true);
    try { await fn(); p.refresh(); } finally { setBusy(false); }
  };
  const base = `/api/devices/${d.id}`;
  const extraActive = d.extra_time_until && new Date(d.extra_time_until) > new Date();

  if (!d.paired) return <PairingCard device={d} refresh={p.refresh} />;

  return (
    <>
      {p.requests.map((r) => (
        <section key={r.id} className="rounded-2xl border-2 border-amber-400 bg-amber-50 p-4" role="alert">
          <div className="font-semibold text-amber-900">Con xin thêm {r.requested_minutes} phút</div>
          {r.reason && <p className="mt-1 text-sm text-amber-800">“{r.reason}”</p>}
          <div className="mt-3 flex gap-2">
            <button disabled={busy} className="btn flex-1 bg-green-600 text-white" onClick={() => act(() => api(`${base}/time-requests/${r.id}/approve`, { body: {} }))}>Duyệt</button>
            <button disabled={busy} className="btn flex-1 bg-white ring-1 ring-slate-300" onClick={() => act(() => api(`${base}/time-requests/${r.id}/reject`, { body: {} }))}>Từ chối</button>
          </div>
        </section>
      ))}

      <section className="card space-y-1">
        <div className="flex items-center justify-between">
          <span className={`font-semibold ${d.is_online ? "text-green-700" : "text-slate-500"}`}>{d.is_online ? "● Online" : "○ Offline"}</span>
          <span className={d.is_locked_now || d.manual_locked ? "text-red-700" : "text-blue-700"}>{d.is_locked_now || d.manual_locked ? "Đang khóa" : "Đang mở"}</span>
        </div>
        <div className="text-sm text-slate-600">Hôm nay đã dùng: <b>{fmtMin(d.active_minutes_today)}</b>
          {p.policy.daily_limit_minutes != null && <> / {fmtMin(p.policy.daily_limit_minutes)}</>}</div>
        {d.current_app && <div className="text-sm text-slate-600">Đang dùng: {d.current_app}</div>}
        {extraActive && <div className="text-sm text-green-700">Giờ cộng thêm đến {fmtTime(d.extra_time_until!)}</div>}
      </section>

      <button disabled={busy}
        className={`btn w-full py-5 text-xl text-white ${d.manual_locked ? "bg-green-600" : "bg-red-600"}`}
        onClick={() => act(() => api(`${base}/${d.manual_locked ? "unlock" : "lock"}`, { body: {} }))}>
        {d.manual_locked ? "Mở khóa" : "Khóa máy ngay"}
      </button>
      <div className="grid grid-cols-2 gap-3">
        {[15, 30].map((m) => (
          <button key={m} disabled={busy} className="btn bg-blue-100 py-4 text-blue-800" onClick={() => act(() => api(`${base}/grant-time`, { body: { minutes: m } }))}>
            +{m} phút
          </button>
        ))}
      </div>

      <section className="card divide-y divide-slate-100">
        {FLAGS.map(([k, label]) => (
          <div key={k} className="flex items-center justify-between py-3 first:pt-0 last:pb-0">
            <span className="text-base">{label}</span>
            <Toggle on={p.policy.protection_flags[k]} label={label}
              onChange={(v) => p.savePolicy({ protection_flags: { ...p.policy.protection_flags, [k]: v } })} />
          </div>
        ))}
      </section>
      <button className="w-full py-3 text-sm text-red-600" onClick={p.onDelete}>Xóa máy này</button>
    </>
  );
}
