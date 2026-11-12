"use client";
import { useState } from "react";
import type { BlockedApp, Policy } from "@/lib/api";

const CATS: [string, string][] = [["adult", "Web đen"], ["gambling", "Cờ bạc"], ["social", "Mạng xã hội"], ["game", "Game"]];
const SUGGEST: [string, string][] = [
  ["Roblox", "RobloxPlayerBeta.exe"], ["Valorant", "Valorant.exe"], ["League of Legends", "LeagueClient.exe"], ["Steam", "Steam.exe"],
];

function DomainList({ title, items, onChange }: { title: string; items: string[]; onChange: (v: string[]) => void }) {
  const [v, setV] = useState("");
  const add = (e: React.FormEvent) => {
    e.preventDefault();
    const d = v.trim().toLowerCase().replace(/^https?:\/\//, "").replace(/\/.*$/, "");
    if (d.length >= 3 && !items.includes(d)) onChange([...items, d]);
    setV("");
  };
  return (
    <section className="card space-y-2">
      <h3 className="font-semibold">{title}</h3>
      <form onSubmit={add} className="flex gap-2">
        <input className="input" placeholder="vd: example.com" value={v} onChange={(e) => setV(e.target.value)} aria-label={title} />
        <button className="btn bg-blue-600 text-white">Thêm</button>
      </form>
      <ul className="flex flex-wrap gap-2">
        {items.map((d) => (
          <li key={d} className="flex items-center gap-1 rounded-full bg-slate-100 py-1 pl-3 pr-1 text-sm">
            {d}
            <button aria-label={`Xóa ${d}`} className="h-6 w-6 rounded-full text-slate-500" onClick={() => onChange(items.filter((x) => x !== d))}>×</button>
          </li>
        ))}
      </ul>
    </section>
  );
}

export function BlockTab({ policy, savePolicy }: { policy: Policy; savePolicy: (p: Partial<Policy>) => Promise<void> }) {
  const [proc, setProc] = useState("");
  const apps = policy.blocked_apps;
  const addApp = (name: string, process_name: string) => {
    if (!process_name || apps.some((a) => a.process_name.toLowerCase() === process_name.toLowerCase())) return;
    savePolicy({ blocked_apps: [...apps, { name: name || process_name, process_name, mode: "always" }] });
  };
  const setMode = (i: number, mode: BlockedApp["mode"]) =>
    savePolicy({ blocked_apps: apps.map((a, j) => (j === i ? { ...a, mode } : a)) });

  return (
    <>
      <section className="card space-y-2">
        <h3 className="font-semibold">Nhóm nội dung chặn</h3>
        {CATS.map(([k, label]) => {
          const on = policy.blocked_categories.includes(k);
          return (
            <label key={k} className="flex items-center justify-between py-1">
              <span>{label}</span>
              <input type="checkbox" className="h-6 w-6 accent-blue-600" checked={on}
                onChange={() => savePolicy({ blocked_categories: on ? policy.blocked_categories.filter((c) => c !== k) : [...policy.blocked_categories, k] })} />
            </label>
          );
        })}
      </section>

      <DomainList title="Chặn thêm domain" items={policy.custom_blocked_domains} onChange={(v) => savePolicy({ custom_blocked_domains: v })} />
      <DomainList title="Luôn cho phép domain" items={policy.custom_allowed_domains} onChange={(v) => savePolicy({ custom_allowed_domains: v })} />

      <section className="card space-y-3">
        <h3 className="font-semibold">App / Game bị chặn</h3>
        <form className="flex gap-2" onSubmit={(e) => { e.preventDefault(); addApp("", proc.trim()); setProc(""); }}>
          <input className="input" placeholder="Tên tiến trình, vd: Steam.exe" value={proc} onChange={(e) => setProc(e.target.value)} aria-label="Tên tiến trình" />
          <button className="btn bg-blue-600 text-white">Thêm</button>
        </form>
        <div className="flex flex-wrap gap-2">
          {SUGGEST.map(([n, p]) => (
            <button key={p} className="rounded-full bg-blue-50 px-3 py-1.5 text-sm text-blue-800" onClick={() => addApp(n, p)}>+ {p}</button>
          ))}
        </div>
        <ul className="divide-y divide-slate-100">
          {apps.map((a, i) => (
            <li key={a.process_name} className="flex items-center gap-2 py-2">
              <div className="min-w-0 flex-1">
                <div className="truncate text-sm font-medium">{a.name}</div>
                <div className="truncate font-mono text-xs text-slate-500">{a.process_name}</div>
              </div>
              <select className="rounded-lg border border-slate-300 p-1.5 text-sm" value={a.mode} onChange={(e) => setMode(i, e.target.value as BlockedApp["mode"])}>
                <option value="always">Luôn chặn</option>
                <option value="outside_schedule">Ngoài giờ</option>
              </select>
              <button aria-label={`Xóa ${a.name}`} className="h-8 w-8 text-slate-500" onClick={() => savePolicy({ blocked_apps: apps.filter((_, j) => j !== i) })}>×</button>
            </li>
          ))}
        </ul>
      </section>
    </>
  );
}
