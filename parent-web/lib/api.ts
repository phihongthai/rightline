export const API = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:3000";
const KEY = "kidsafe_token";

export const getToken = () => (typeof window === "undefined" ? null : localStorage.getItem(KEY));
export const setToken = (t: string | null) => (t ? localStorage.setItem(KEY, t) : localStorage.removeItem(KEY));

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
  }
}

export async function api<T = unknown>(path: string, init: { method?: string; body?: unknown } = {}): Promise<T> {
  const token = getToken();
  const res = await fetch(`${API}${path}`, {
    method: init.method ?? (init.body !== undefined ? "POST" : "GET"),
    headers: {
      ...(init.body !== undefined && { "content-type": "application/json" }),
      ...(token && { authorization: `Bearer ${token}` }),
    },
    body: init.body !== undefined ? JSON.stringify(init.body) : undefined,
  });
  if (res.status === 204) return undefined as T;
  const data = await res.json().catch(() => ({}));
  if (!res.ok) {
    if (res.status === 401 && token) {
      setToken(null);
      if (typeof window !== "undefined") window.location.href = "/login";
    }
    throw new ApiError(res.status, (data as { error?: string }).error ?? `HTTP ${res.status}`);
  }
  return data as T;
}

export interface Device {
  id: string;
  name: string;
  paired: boolean;
  pairing_code: string | null;
  pairing_expires_at: string | null;
  is_online: boolean;
  last_seen_at: string | null;
  is_locked_now: boolean;
  manual_locked: boolean;
  extra_time_until: string | null;
  active_minutes_today: number;
  current_app: string | null;
  license_expires_at: string;
}
export interface ProtectionFlags {
  web_filter: boolean;
  app_block: boolean;
  schedule_lock: boolean;
  screenshot: boolean;
}
export interface BlockedApp {
  name: string;
  process_name: string;
  mode: "always" | "outside_schedule";
}
export interface Policy {
  version: number;
  protection_flags: ProtectionFlags;
  schedule_grid: boolean[][];
  daily_limit_minutes: number | null;
  blocked_categories: string[];
  custom_blocked_domains: string[];
  custom_allowed_domains: string[];
  blocked_apps: BlockedApp[];
  screenshot_interval_sec: number;
}
export interface TimeRequest {
  id: string;
  requested_minutes: number;
  reason: string;
  status: "pending" | "approved" | "rejected";
  created_at: string;
}
export interface Shot { id: string; captured_at: string; url: string }
export interface LogRow { id: string; log_type: "web" | "app" | "system"; target: string; title: string; duration_sec: number; recorded_at: string }

