import { afterAll, beforeAll, beforeEach, describe, expect, it } from "vitest";
import { prisma } from "../src/lib/db.js";
import { bearer, connectAgent, createDevice, pairAgent, register, resetDb, startApp } from "./helpers.js";

let ctx: Awaited<ReturnType<typeof startApp>>;
beforeAll(async () => (ctx = await startApp()));
afterAll(async () => {
  await ctx.app.close();
  await prisma.$disconnect();
});
beforeEach(resetDb);

describe("auth", () => {
  it("registers, rejects duplicates, logs in", async () => {
    const { app } = ctx;
    await register(app);
    const dup = await app.inject({
      method: "POST", url: "/api/auth/register",
      payload: { email: "P@example.com", password: "password123", name: "x" },
    });
    expect(dup.statusCode).toBe(409);
    const bad = await app.inject({ method: "POST", url: "/api/auth/login", payload: { email: "p@example.com", password: "wrongwrong" } });
    expect(bad.statusCode).toBe(401);
    const ok = await app.inject({ method: "POST", url: "/api/auth/login", payload: { email: "p@example.com", password: "password123" } });
    expect(ok.statusCode).toBe(200);
    expect(ok.json().token).toBeTruthy();
  });

  it("protects parent endpoints", async () => {
    const res = await ctx.app.inject({ method: "GET", url: "/api/devices" });
    expect(res.statusCode).toBe(401);
  });
});

describe("pairing", () => {
  it("creates a 6-char code with ~15 min TTL and a default policy", async () => {
    const t = await register(ctx.app);
    const d = await createDevice(ctx.app, t);
    expect(d.pairing_code).toMatch(/^[A-Z2-9]{6}$/);
    const row = await prisma.device.findUniqueOrThrow({ where: { id: d.id } });
    const ttl = row.pairingExpiresAt!.getTime() - Date.now();
    expect(ttl).toBeGreaterThan(14 * 60_000);
    expect(ttl).toBeLessThanOrEqual(15 * 60_000);
    const pol = await ctx.app.inject({ method: "GET", url: `/api/devices/${d.id}/policy`, headers: bearer(t) });
    expect(pol.json().version).toBe(1);
    expect(pol.json().schedule_grid).toHaveLength(7);
  });

  it("agent pairs with code, gets token; code is single-use", async () => {
    const t = await register(ctx.app);
    const d = await createDevice(ctx.app, t);
    const res = await pairAgent(ctx.app, d.pairing_code);
    expect(res.statusCode).toBe(200);
    const { device_id, device_token } = res.json();
    expect(device_id).toBe(d.id);
    expect(device_token.length).toBeGreaterThan(30);

    const row = await prisma.device.findUniqueOrThrow({ where: { id: d.id } });
    expect(row.pairingCode).toBeNull();
    expect(row.deviceTokenHash).not.toBe(device_token); // stored hashed
    expect(row.osVersion).toBe("Windows 11");

    expect((await pairAgent(ctx.app, d.pairing_code)).statusCode).toBe(400);
    const list = await ctx.app.inject({ method: "GET", url: "/api/devices", headers: bearer(t) });
    expect(list.json().devices[0].paired).toBe(true);
    expect(list.json().devices[0].pairing_code).toBeNull();
  });

  it("is case-insensitive and rejects wrong/expired codes", async () => {
    const t = await register(ctx.app);
    const d = await createDevice(ctx.app, t);
    expect((await pairAgent(ctx.app, "ZZZZZZ")).statusCode).toBe(400);
    await prisma.device.update({ where: { id: d.id }, data: { pairingExpiresAt: new Date(Date.now() - 1000) } });
    expect((await pairAgent(ctx.app, d.pairing_code)).statusCode).toBe(400);
    // regenerate -> works with lowercase
    const re = await ctx.app.inject({ method: "POST", url: `/api/devices/${d.id}/pairing-code`, headers: bearer(t) });
    expect((await pairAgent(ctx.app, re.json().pairing_code.toLowerCase())).statusCode).toBe(200);
  });

  it("concurrent pair attempts: exactly one wins", async () => {
    const t = await register(ctx.app);
    const d = await createDevice(ctx.app, t);
    const rs = await Promise.all(Array.from({ length: 5 }, () => pairAgent(ctx.app, d.pairing_code)));
    expect(rs.filter((r) => r.statusCode === 200)).toHaveLength(1);
  });

  it("re-pairing invalidates the old token", async () => {
    const t = await register(ctx.app);
    const d = await createDevice(ctx.app, t);
    const old = (await pairAgent(ctx.app, d.pairing_code)).json().device_token;
    const re = await ctx.app.inject({ method: "POST", url: `/api/devices/${d.id}/pairing-code`, headers: bearer(t) });
    const fresh = (await pairAgent(ctx.app, re.json().pairing_code)).json().device_token;
    expect(fresh).not.toBe(old);
    await expect(connectAgent(ctx.wsBase, old)).rejects.toThrow(/401/);
    const ok = await connectAgent(ctx.wsBase, fresh);
    await ok.close();
  });

  it("device token authenticates the websocket; bad/missing tokens rejected", async () => {
    const t = await register(ctx.app);
    const d = await createDevice(ctx.app, t);
    const token = (await pairAgent(ctx.app, d.pairing_code)).json().device_token;
    await expect(connectAgent(ctx.wsBase, "nope")).rejects.toThrow(/401/);
    const a = await connectAgent(ctx.wsBase, token);
    await a.next("policy:sync");
    const dev = await ctx.app.inject({ method: "GET", url: `/api/devices/${d.id}`, headers: bearer(t) });
    expect(dev.json().is_online).toBe(true);
    await a.close();
    await new Promise((r) => setTimeout(r, 200));
    const dev2 = await ctx.app.inject({ method: "GET", url: `/api/devices/${d.id}`, headers: bearer(t) });
    expect(dev2.json().is_online).toBe(false);
  });

  it("isolates devices between parents", async () => {
    const a = await register(ctx.app, "a@example.com");
    const b = await register(ctx.app, "b@example.com");
    const d = await createDevice(ctx.app, a);
    for (const url of [`/api/devices/${d.id}`, `/api/devices/${d.id}/policy`]) {
      expect((await ctx.app.inject({ method: "GET", url, headers: bearer(b) })).statusCode).toBe(404);
    }
    const patch = await ctx.app.inject({ method: "PATCH", url: `/api/devices/${d.id}/policy`, headers: bearer(b), payload: { daily_limit_minutes: 5 } });
    expect(patch.statusCode).toBe(404);
  });
});

describe("rate limit", () => {
  it("throttles pairing brute force", async () => {
    const { buildApp } = await import("../src/app.js");
    const { config } = await import("../src/config.js");
    const app = await buildApp({ storage: (await import("./helpers.js")).fakeStorage(), config: { ...config, pairRateLimitPerMin: 3 } });
    const codes: number[] = [];
    for (let i = 0; i < 5; i++) codes.push((await pairAgent(app, "ZZZZZZ")).statusCode);
    expect(codes).toEqual([400, 400, 400, 429, 429]);
    await app.close();
  });
});
