// Minimal S3 stand-in for dev/tests without MinIO: PUT stores, HEAD/GET read back. Ignores signatures.
// Usage: node test/fake-s3.mjs [port]   (default 9000)
import http from "node:http";
const store = new Map();
http.createServer((req, res) => {
  const key = new URL(req.url, "http://x").pathname;
  if (req.method === "PUT") {
    const chunks = [];
    req.on("data", (c) => chunks.push(c));
    req.on("end", () => { store.set(key, Buffer.concat(chunks)); res.writeHead(200, { etag: '"x"' }).end(); });
  } else if (req.method === "HEAD" || req.method === "GET") {
    const v = store.get(key);
    if (!v) return res.writeHead(404).end();
    res.writeHead(200, { "content-length": v.length });
    res.end(req.method === "GET" ? v : undefined);
  } else res.writeHead(405).end();
}).listen(Number(process.argv[2] ?? 9000), "127.0.0.1", () => console.log("fake s3 up"));
