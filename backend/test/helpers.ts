import type { FastifyInstance } from "fastify";
import WebSocket from "ws";
import { config } from "../src/config.js";
import { buildApp } from "../src/app.js";
import { prisma } from "../src/lib/db.js";
import type { Storage } from "../src/lib/storage.js";

export const fakeStorage = (existing = new Set<string>()): Storage & { existing: Set<string> } => ({
  existing,
  presignPut: async (k) => `http://storage.test/put/${k}`,
  presignGet: async (k) => `http://storage.test/get/${k}`,
  exists: async (k) => existing.has(k),
});

export async function startApp(storage = fakeStorage()) {
  const app = await buildApp({ storage, config: { ...config, pairRateLimitPerMin: 10_000, authRateLimitPerMin: 10_000 } });
  await app.listen({ port: 0, host: "127.0.0.1" });
  const addr = app.server.address() as { port: number };
  return { app, storage, base: `http://127.0.0.1:${addr.port}`, wsBase: `ws://127.0.0.1:${addr.port}` };
}

export async function resetDb() {
  await prisma.$executeRawUnsafe(
    "TRUNCATE users, devices, policies, time_requests, screenshots, activity_logs RESTART IDENTITY CASCADE",
  );
}

export async function register(app: FastifyInstance, email = "p@example.com") {
  const res = await app.inject({
    method: "POST",
    url: "/api/auth/register",
    payload: { email, password: "password123", name: "Parent" },
  });
  return res.json().token as string;
}

export const bearer = (t: string) => ({ authorization: `Bearer ${t}` });

export async function createDevice(app: FastifyInstance, token: string, name = "Máy bàn của Bo") {
  const res = await app.inject({ method: "POST", url: "/api/devices", headers: bearer(token), payload: { name } });
  return res.json() as { id: string; pairing_code: string };
}

export async function pairAgent(app: FastifyInstance, code: string) {
  const res = await app.inject({
    method: "POST",
    url: "/api/agent/pair",
    payload: { pairing_code: code, machine_name: "BO-PC", os_version: "Windows 11" },
  });
  return res;
}

export class AgentSocket {
  messages: { type: string; payload?: any }[] = [];
  private waiters: (() => void)[] = [];
  ws: WebSocket;
  constructor(url: string, token: string) {
    this.ws = new WebSocket(url, { headers: { authorization: `Bearer ${token}` } });
    this.ws.on("message", (d) => {
      this.messages.push(JSON.parse(d.toString()));
      this.waiters.splice(0).forEach((w) => w());
    });
  }
  opened() {
    return new Promise<void>((res, rej) => {
      this.ws.once("open", () => res());
      this.ws.once("error", rej);
      this.ws.once("unexpected-response", (_r, resp) => rej(new Error(`http ${resp.statusCode}`)));
    });
  }
  async next(type: string, timeoutMs = 3000) {
    const deadline = Date.now() + timeoutMs;
    for (;;) {
      const i = this.messages.findIndex((m) => m.type === type);
      if (i >= 0) return this.messages.splice(i, 1)[0];
      const left = deadline - Date.now();
      if (left <= 0) throw new Error(`timeout waiting for ${type}; got ${JSON.stringify(this.messages)}`);
      await new Promise<void>((r) => {
        this.waiters.push(r);
        setTimeout(r, left);
      });
    }
  }
  send(type: string, payload?: unknown) {
    this.ws.send(JSON.stringify({ type, payload }));
  }
  closed() {
    return new Promise<void>((r) => (this.ws.readyState === WebSocket.CLOSED ? r() : this.ws.once("close", () => r())));
  }
  close() {
    this.ws.close();
    return this.closed();
  }
}

export async function connectAgent(wsBase: string, token: string) {
  const a = new AgentSocket(`${wsBase}/ws/agent`, token);
  await a.opened();
  return a;
}
