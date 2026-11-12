import { z } from "zod";
import type { Policy, Prisma } from "@prisma/client";

export const ProtectionFlags = z.object({
  web_filter: z.boolean(),
  app_block: z.boolean(),
  schedule_lock: z.boolean(),
  screenshot: z.boolean(),
});

export const ScheduleGrid = z
  .array(z.array(z.boolean()).length(24))
  .length(7);

export const BlockedApp = z.object({
  name: z.string().min(1).max(100),
  process_name: z.string().min(1).max(260),
  mode: z.enum(["always", "outside_schedule"]),
});

const domain = z.string().trim().toLowerCase().min(3).max(253);

export const PolicyPatch = z
  .object({
    protection_flags: ProtectionFlags.partial(),
    schedule_grid: ScheduleGrid,
    daily_limit_minutes: z.number().int().min(0).max(1440).nullable(),
    blocked_categories: z.array(z.enum(["adult", "gambling", "social", "game"])).max(20),
    custom_blocked_domains: z.array(domain).max(1000),
    custom_allowed_domains: z.array(domain).max(1000),
    blocked_apps: z.array(BlockedApp).max(500),
    screenshot_interval_sec: z.number().int().min(15).max(300),
  })
  .partial()
  .strict();
export type PolicyPatchT = z.infer<typeof PolicyPatch>;

export const defaultGrid = () => Array.from({ length: 7 }, () => Array.from({ length: 24 }, () => true));

export const defaultPolicyData = () => ({
  protectionFlags: { web_filter: true, app_block: true, schedule_lock: false, screenshot: true },
  scheduleGrid: defaultGrid(),
  dailyLimitMinutes: null as number | null,
  blockedCategories: ["adult", "gambling"],
  customBlockedDomains: [] as string[],
  customAllowedDomains: [] as string[],
  blockedApps: [] as Prisma.InputJsonValue,
  screenshotIntervalSec: 60,
});

/** Wire format (snake_case) sent to parents and agents. */
export function serializePolicy(p: Policy) {
  return {
    device_id: p.deviceId,
    version: p.version,
    protection_flags: p.protectionFlags,
    schedule_grid: p.scheduleGrid,
    daily_limit_minutes: p.dailyLimitMinutes,
    blocked_categories: p.blockedCategories,
    custom_blocked_domains: p.customBlockedDomains,
    custom_allowed_domains: p.customAllowedDomains,
    blocked_apps: p.blockedApps,
    screenshot_interval_sec: p.screenshotIntervalSec,
  };
}

export function patchToData(patch: PolicyPatchT, currentFlags: Record<string, boolean>) {
  const d: Record<string, unknown> = {};
  if (patch.protection_flags) d.protectionFlags = { ...currentFlags, ...patch.protection_flags };
  if (patch.schedule_grid) d.scheduleGrid = patch.schedule_grid;
  if (patch.daily_limit_minutes !== undefined) d.dailyLimitMinutes = patch.daily_limit_minutes;
  if (patch.blocked_categories) d.blockedCategories = patch.blocked_categories;
  if (patch.custom_blocked_domains) d.customBlockedDomains = patch.custom_blocked_domains;
  if (patch.custom_allowed_domains) d.customAllowedDomains = patch.custom_allowed_domains;
  if (patch.blocked_apps) d.blockedApps = patch.blocked_apps;
  if (patch.screenshot_interval_sec !== undefined) d.screenshotIntervalSec = patch.screenshot_interval_sec;
  return d;
}
