"use client";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { api, setToken, type Device } from "@/lib/api";
import { fmtMin, usePolling, useRequireAuth } from "@/lib/hooks";

function Status({ d }: { d: Device }) {
  if (!d.paired) return <span className="rounded-full bg-amber-100 px-2 py-0.5 text-xs text-amber-800">Chờ ghép nối</span>;
  return (
    <span className="flex gap-1.5 text-xs">
      <span className={`rounded-full px-2 py-0.5 ${d.is_online ? "bg-green-100 text-green-800" : "bg-slate-200 text-slate-600"}`}>
        {d.is_online ? "Online" : "Offline"}
      </span>
      <span className={`rounded-full px-2 py-0.5 ${d.is_locked_now || d.manual_locked ? "bg-red-100 text-red-800" : "bg-blue-100 text-blue-800"}`}>
        {d.is_locked_now || d.manual_locked ? "Đang khóa" : "Đang mở"}
      </span>
    </span>
  );
}

export default function Home() {
  const ready = useRequireAuth();
  const router = useRouter();
  const { data, reload } = usePolling(() => api<{ devices: Device[] }>("/api/devices"), 5000, ready);
  const [name, setName] = useState("");
  const [adding, setAdding] = useState(false);
  const [busy, setBusy] = useState(false);

  async function add(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    try {
      const d = await api<Device>("/api/devices", { body: { name } });
      setName("");
      setAdding(false);
      await reload();
      router.push(`/devices/${d.id}`);
    } finally {
      setBusy(false);
    }
  }

  if (!ready) return null;
  return (
    <main className="space-y-4 px-4 pb-24 pt-6">
      <header className="flex items-center justify-between">
        <h1 className="text-2xl font-bold text-blue-700">Máy của con</h1>
        <button className="text-sm text-slate-500" onClick={() => { setToken(null); router.replace("/login"); }}>
          Đăng xuất
        </button>
      </header>

      {data?.devices.length === 0 && !adding && (
        <p className="card text-center text-slate-500">Chưa có máy nào. Bấm “Thêm máy Windows” để bắt đầu.</p>
      )}
      {data?.devices.map((d) => (
        <Link key={d.id} href={`/devices/${d.id}`} className="card block active:bg-slate-50">
          <div className="flex items-start justify-between gap-2">
            <div>
              <div className="text-lg font-semibold">{d.name}</div>
              {d.paired && <div className="text-sm text-slate-500">Hôm nay đã dùng {fmtMin(d.active_minutes_today)}</div>}
            </div>
            <Status d={d} />
          </div>
          {!d.paired && d.pairing_code && (
            <div className="mt-2 text-sm text-slate-600">Mã ghép nối: <b className="font-mono text-lg tracking-widest">{d.pairing_code}</b></div>
          )}
        </Link>
      ))}

      {adding ? (
        <form onSubmit={add} className="card space-y-3">
          <input className="input" placeholder="Tên máy (vd: Máy bàn của Bo)" required autoFocus value={name} onChange={(e) => setName(e.target.value)} />
          <div className="flex gap-2">
            <button className="btn flex-1 bg-blue-600 text-white" disabled={busy}>Tạo mã ghép nối</button>
            <button type="button" className="btn bg-slate-200" onClick={() => setAdding(false)}>Hủy</button>
          </div>
        </form>
      ) : (
        <button className="btn fixed inset-x-4 bottom-4 mx-auto max-w-xl bg-blue-600 text-white shadow-lg" onClick={() => setAdding(true)}>
          + Thêm máy Windows
        </button>
      )}
    </main>
  );
}
