"use client";
import { useState } from "react";
import { useRouter } from "next/navigation";
import { api, setToken } from "@/lib/api";

const ERR: Record<string, string> = {
  invalid_credentials: "Email hoặc mật khẩu không đúng",
  email_taken: "Email đã được đăng ký",
  validation_error: "Thông tin không hợp lệ (mật khẩu tối thiểu 8 ký tự)",
};

export default function LoginPage() {
  const router = useRouter();
  const [mode, setMode] = useState<"login" | "register">("login");
  const [f, setF] = useState({ email: "", password: "", name: "" });
  const [err, setErr] = useState("");
  const [busy, setBusy] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setErr("");
    try {
      const r = await api<{ token: string }>(`/api/auth/${mode}`, {
        body: mode === "login" ? { email: f.email, password: f.password } : f,
      });
      setToken(r.token);
      router.replace("/");
    } catch (e) {
      const m = (e as Error).message;
      setErr(ERR[m] ?? m);
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="flex min-h-screen flex-col justify-center px-5">
      <h1 className="mb-1 text-3xl font-bold text-blue-700">KidSafe</h1>
      <p className="mb-6 text-slate-500">Bảo vệ máy tính của con</p>
      <form onSubmit={submit} className="card space-y-3">
        {mode === "register" && (
          <input className="input" placeholder="Tên của bạn" required value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} />
        )}
        <input className="input" type="email" placeholder="Email" required autoComplete="email" value={f.email} onChange={(e) => setF({ ...f, email: e.target.value })} />
        <input className="input" type="password" placeholder="Mật khẩu" required minLength={8} autoComplete={mode === "login" ? "current-password" : "new-password"} value={f.password} onChange={(e) => setF({ ...f, password: e.target.value })} />
        {err && <p className="text-sm text-red-600" role="alert">{err}</p>}
        <button className="btn w-full bg-blue-600 text-white" disabled={busy}>
          {mode === "login" ? "Đăng nhập" : "Đăng ký"}
        </button>
        <button type="button" className="w-full py-2 text-sm text-blue-700" onClick={() => setMode(mode === "login" ? "register" : "login")}>
          {mode === "login" ? "Chưa có tài khoản? Đăng ký" : "Đã có tài khoản? Đăng nhập"}
        </button>
      </form>
    </main>
  );
}
