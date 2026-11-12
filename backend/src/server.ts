import { buildApp, sweepOffline } from "./app.js";
import { config } from "./config.js";

const app = await buildApp();
const timer = setInterval(() => sweepOffline(app.deps).catch((e) => app.log.error(e)), 30_000);
app.addHook("onClose", async () => clearInterval(timer));
await app.listen({ port: config.port, host: "0.0.0.0" });
