"use client";
import { useCallback, useEffect, useRef, useState } from "react";
import { getToken } from "./api";
import { useRouter } from "next/navigation";

/** Redirects to /login when there is no token. Returns true once checked. */
export function useRequireAuth() {
  const router = useRouter();
  const [ok, setOk] = useState(false);
  useEffect(() => {
    if (!getToken()) router.replace("/login");
    else setOk(true);
  }, [router]);
  return ok;
}

/** Loads data and re-polls every `ms`. */
export function usePolling<T>(fn: () => Promise<T>, ms: number, enabled = true) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const fnRef = useRef(fn);
  fnRef.current = fn;
  const reload = useCallback(async () => {
    try {
      setData(await fnRef.current());
      setError(null);
    } catch (e) {
      setError((e as Error).message);
    }
  }, []);
  useEffect(() => {
    if (!enabled) return;
    reload();
    const t = setInterval(reload, ms);
    return () => clearInterval(t);
  }, [reload, ms, enabled]);
  return { data, error, reload, setData };
}

export const fmtMin = (m: number) => (m >= 60 ? `${Math.floor(m / 60)}g ${m % 60}p` : `${m}p`);
export const fmtTime = (iso: string) =>
  new Date(iso).toLocaleTimeString("vi-VN", { hour: "2-digit", minute: "2-digit" });
export const fmtDateTime = (iso: string) =>
  new Date(iso).toLocaleString("vi-VN", { hour: "2-digit", minute: "2-digit", day: "2-digit", month: "2-digit" });
export const localDate = (d = new Date()) =>
  `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
