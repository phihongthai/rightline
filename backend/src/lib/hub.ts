import { Redis } from "ioredis";
import type { WebSocket } from "ws";

export interface AgentMessage {
  type: string;
  payload?: unknown;
}

/**
 * Tracks live agent sockets and fans out messages. With Redis configured,
 * sends go through pub/sub so any backend instance can reach the agent.
 */
export class Hub {
  private sockets = new Map<string, WebSocket>();
  private pub?: Redis;
  private sub?: Redis;

  async start(redisUrl?: string) {
    if (!redisUrl) return;
    this.pub = new Redis(redisUrl);
    this.sub = new Redis(redisUrl);
    await this.sub.psubscribe("agent:*");
    this.sub.on("pmessage", (_p, channel, raw) => {
      this.deliverLocal(channel.slice("agent:".length), raw);
    });
  }

  async stop() {
    for (const ws of this.sockets.values()) ws.terminate();
    this.sockets.clear();
    await Promise.all([this.pub?.quit(), this.sub?.quit()].filter(Boolean) as Promise<unknown>[]);
  }

  /** Registers a socket; returns the previous one (already closed) if any. */
  register(deviceId: string, ws: WebSocket) {
    const old = this.sockets.get(deviceId);
    this.sockets.set(deviceId, ws);
    if (old && old !== ws) old.close(4000, "replaced");
  }

  /** Returns true if ws was the current socket for the device. */
  unregister(deviceId: string, ws: WebSocket) {
    if (this.sockets.get(deviceId) !== ws) return false;
    this.sockets.delete(deviceId);
    return true;
  }

  isConnectedLocally(deviceId: string) {
    return this.sockets.has(deviceId);
  }

  async send(deviceId: string, msg: AgentMessage) {
    const raw = JSON.stringify(msg);
    if (this.pub) await this.pub.publish(`agent:${deviceId}`, raw);
    else this.deliverLocal(deviceId, raw);
  }

  private deliverLocal(deviceId: string, raw: string) {
    const ws = this.sockets.get(deviceId);
    if (ws && ws.readyState === ws.OPEN) ws.send(raw);
  }
}
