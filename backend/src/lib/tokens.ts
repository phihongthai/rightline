import { createHash, createHmac, randomBytes, randomInt } from "node:crypto";

// No 0/O/1/I/L to avoid read-aloud mistakes.
const ALPHABET = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

export function generatePairingCode() {
  let s = "";
  for (let i = 0; i < 6; i++) s += ALPHABET[randomInt(ALPHABET.length)];
  return s;
}
export const newDeviceToken = () => randomBytes(32).toString("base64url");
export const hashToken = (t: string) => createHash("sha256").update(t).digest("hex");

/**
 * Offline uninstall PIN: HMAC(sha256(device_token) as hex string, "uninstall:" + UTC date) -> 8 digits.
 * The agent can recompute it from its own token, so uninstall works without network.
 */
export function uninstallPin(tokenHash: string, utcDate: string) {
  const mac = createHmac("sha256", tokenHash).update(`uninstall:${utcDate}`).digest();
  return String(mac.readUInt32BE(0) % 100_000_000).padStart(8, "0");
}
