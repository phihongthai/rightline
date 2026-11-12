"use client";
import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import { api, type Device, type Policy, type TimeRequest } from "@/lib/api";
import { usePolling, useRequireAuth } from "@/lib/hooks";
import { ControlTab } from "@/components/ControlTab";
import { ScheduleTab } from "@/components/ScheduleTab";
import { BlockTab } from "@/components/BlockTab";
import { ActivityTab } from "@/components/ActivityTab";

const TABS = [
  ["control", "Điều khiển"],
  ["schedule", "Lịch & Giới hạn"],
  ["block", "Chặn Web & App"],
  ["activity", "Ảnh & Lịch sử"],
] as const;
type Tab = (typeof TABS)[number][0];

export default function DevicePage() {
  const ready = useRequireAuth();
  const { id } = useParams<{ id: string }>();
  const router = useRouter();
  const [tab, setTab] = useState<Tab>("control");
  const [policy, setPolicy] = useState<Policy | null>(null);
  const [saveErr, setSaveErr] = useState("");

  const dev = usePolling(() => api<Device>(`/api/devices/${id}`), 4000, ready);
  const reqs = usePolling(
    () => api<{ requests: TimeRequest[] }>(`/api/devices/${id}/time-requests?status=pending`),
    4000,
    ready,
  );

  useEffect(() => {
    if (ready) api<Policy>(`/api/devices/${id}/policy`).then(setPolicy).catch(() => {});
  }, [ready, id]);

  const savePolicy = useCallback(
    async (patch: Partial<Policy>) => {
      setSaveErr("");
      try {
        setPolicy(await api<Policy>(`/api/devices/${id}/policy`, { method: "PATCH", body: patch }));
      } catch (e) {
        setSaveErr((e as Error).message);
        throw e;
      }
    },
    [id],
  );

  if (!ready) return null;
  const d = dev.data;
  if (dev.error === "not_found") return <main className="p-6">Không tìm thấy máy. <Link className="text-blue-700" href="/">Quay lại</Link></main>;

  return (
    <main className="pb-10">
      <header className="sticky top-0 z-10 bg-slate-50/95 px-4 pt-4 backdrop-blur">
        <div className="mb-3 flex items-center gap-3">
          <Link href="/" aria-label="Quay lại" className="rounded-lg bg-white px-3 py-2 ring-1 ring-slate-200">←</Link>
          <h1 className="truncate text-xl font-bold">{d?.name ?? "…"}</h1>
        </div>
        <nav className="-mx-4 flex overflow-x-auto border-b border-slate-200 px-4" role="tablist">
          {TABS.map(([k, label]) => (
            <button key={k} role="tab" aria-selected={tab === k} onClick={() => setTab(k)}
              className={`whitespace-nowrap px-3 py-3 text-sm font-medium ${tab === k ? "border-b-2 border-blue-600 text-blue-700" : "text-slate-500"}`}>
              {label}
            </button>
          ))}
        </nav>
      </header>

      <div className="space-y-4 px-4 pt-4">
        {saveErr && <p className="rounded-xl bg-red-50 p-3 text-sm text-red-700" role="alert">Lưu thất bại: {saveErr}</p>}
        {!d || !policy ? (
          <p className="text-slate-500">Đang tải…</p>
        ) : (
          <>
            {tab === "control" && (
              <ControlTab device={d} policy={policy} requests={reqs.data?.requests ?? []} savePolicy={savePolicy}
                refresh={() => { dev.reload(); reqs.reload(); }}
                onDelete={async () => { if (confirm("Xóa máy này?")) { await api(`/api/devices/${id}`, { method: "DELETE" }); router.replace("/"); } }} />
            )}
            {tab === "schedule" && <ScheduleTab policy={policy} savePolicy={savePolicy} />}
            {tab === "block" && <BlockTab policy={policy} savePolicy={savePolicy} />}
            {tab === "activity" && <ActivityTab deviceId={id} />}
          </>
        )}
      </div>
    </main>
  );
}
