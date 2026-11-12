import type { FastifyPluginAsync } from "fastify";
import type { WebSocket } from "ws";
import { z } from "zod";
import { serializePolicy } from "../lib/policy.js";

const Heartbeat = z.object({
  active_minutes_today: z.number().int().min(0).max(1440),
  current_app: z.string().max(260).nullish(),
  is_locked: z.boolean(),
});
const TimeRequestCreate = z.object({
  requested_minutes: z.number().int().min(1).max(480),
  reason: z.string().max(500).default(""),
});
const Envelope = z.object({ type: z.string(), payload: z.unknown().optional() });

export const agentWs: FastifyPluginAsync = async (app) => {
  const { db, hub } = app.deps;

  app.get("/ws/agent", { websocket: true, onRequest: app.authAgent }, async (socket: WebSocket, req) => {
    const deviceId = req.agentDevice!.id;
    hub.register(deviceId, socket);

    // Attach handlers first so no inbound message is lost during initial sync.
    socket.on("message", async (raw: Buffer) => {
      try {
        const env = Envelope.parse(JSON.parse(raw.toString()));
        await handle(env.type, env.payload);
      } catch (err) {
        socket.send(JSON.stringify({ type: "error", payload: { message: "bad_message" } }));
        app.log.debug({ err }, "bad agent message");
      }
    });

    socket.on("close", async () => {
      if (!hub.unregister(deviceId, socket)) return; // replaced by a newer connection
      await db.device.update({ where: { id: deviceId }, data: { isOnline: false } }).catch(() => {});
    });

    async function handle(type: string, payload: unknown) {
      switch (type) {
        case "heartbeat": {
          const h = Heartbeat.parse(payload);
          await db.device.update({
            where: { id: deviceId },
            data: {
              isOnline: true,
              lastSeenAt: new Date(),
              activeMinutesToday: h.active_minutes_today,
              currentApp: h.current_app ?? null,
              isLockedNow: h.is_locked,
            },
          });
          break;
        }
        case "time_request:create": {
          const t = TimeRequestCreate.parse(payload);
          await db.timeRequest.create({
            data: { deviceId, requestedMinutes: t.requested_minutes, reason: t.reason },
          });
          break;
        }
        case "alert:tamper": {
          const p = z.object({ detail: z.string().max(500).default("") }).parse(payload ?? {});
          await db.activityLog.create({
            data: { deviceId, logType: "system", target: "tamper", title: p.detail, recordedAt: new Date() },
          });
          break;
        }
        default:
          socket.send(JSON.stringify({ type: "error", payload: { message: "unknown_type" } }));
      }
    }

    // Initial state: full policy + any standing lock / extra time.
    const dev = await db.device.update({
      where: { id: deviceId },
      data: { isOnline: true, lastSeenAt: new Date() },
    });
    const policy = await db.policy.findUnique({ where: { deviceId } });
    if (policy) socket.send(JSON.stringify({ type: "policy:sync", payload: serializePolicy(policy) }));
    if (dev.manualLocked) socket.send(JSON.stringify({ type: "command:lock" }));
    if (dev.extraTimeUntil && dev.extraTimeUntil > new Date())
      socket.send(
        JSON.stringify({ type: "command:grant_time", payload: { extra_time_until: dev.extraTimeUntil.toISOString() } }),
      );
  });
};
