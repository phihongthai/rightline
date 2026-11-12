import { afterAll, beforeAll, beforeEach, describe, expect, it } from "vitest";
import { prisma } from "../src/lib/db.js";
import { uninstallPin } from "../src/lib/tokens.js";
import { bearer, createDevice, pairAgent, register, resetDb, startApp } from "./helpers.js";

let ctx: Awaited<ReturnType<typeof startApp>>;
beforeAll(async () => (ctx = await startApp()));
afterAll(async () => {
  await ctx.app.close();
  await prisma.$disconnect();
});
beforeEach(resetDb);

describe("uninstall authorisation", () => {
  it("pin is stable per day, 8 digits, and fixed test vector matches the C# agent", () => {
    expect(uninstallPin("abc", "2026-01-01")).toMatch(/^\d{8}$/);
    expect(uninstallPin("abc", "2026-01-01")).toBe(uninstallPin("abc", "2026-01-01"));
    expect(uninstallPin("abc", "2026-01-02")).not.toBe(uninstallPin("abc", "2026-01-01"));
    console.log("VECTOR abc 2026-01-01 =", uninstallPin("abc", "2026-01-01"));
  });

  it("parent gets the PIN only for a paired device; agent can verify password", async () => {
    const t = await register(ctx.app);
    const d = await createDevice(ctx.app, t);
    expect((await ctx.app.inject({ method: "GET", url: `/api/devices/${d.id}/uninstall-pin`, headers: bearer(t) })).statusCode).toBe(409);
    const token = (await pairAgent(ctx.app, d.pairing_code)).json().device_token as string;
    const pin = await ctx.app.inject({ method: "GET", url: `/api/devices/${d.id}/uninstall-pin`, headers: bearer(t) });
    expect(pin.json().pin).toMatch(/^\d{8}$/);

    // The agent recomputes the same PIN from sha256(token).
    const { hashToken } = await import("../src/lib/tokens.js");
    expect(pin.json().pin).toBe(uninstallPin(hashToken(token), pin.json().valid_for_date_utc));

    const h = { authorization: `Bearer ${token}` };
    expect((await ctx.app.inject({ method: "POST", url: "/api/agent/uninstall-auth", headers: h, payload: { password: "wrongpass" } })).statusCode).toBe(401);
    expect((await ctx.app.inject({ method: "POST", url: "/api/agent/uninstall-auth", headers: h, payload: { password: "password123" } })).json().ok).toBe(true);
    expect((await ctx.app.inject({ method: "POST", url: "/api/agent/uninstall-auth", payload: { password: "password123" } })).statusCode).toBe(401);
  });

  it("another parent cannot read the PIN", async () => {
    const a = await register(ctx.app, "a@example.com");
    const b = await register(ctx.app, "b@example.com");
    const d = await createDevice(ctx.app, a);
    await pairAgent(ctx.app, d.pairing_code);
    expect((await ctx.app.inject({ method: "GET", url: `/api/devices/${d.id}/uninstall-pin`, headers: bearer(b) })).statusCode).toBe(404);
  });
});
