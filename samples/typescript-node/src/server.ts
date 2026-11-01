import { createServer } from "node:http";
import { existsSync } from "node:fs";
import { hostname, release, type } from "node:os";
import inspector from "node:inspector";

const port = Number(process.env.PORT ?? 3000);

// "localhost", as dev servers listen by default. The extension's port forwarding reaches it inside the container.
const host = process.env.LISTEN_HOST ?? "localhost";

const server = createServer((request, response) => {
  if (request.url === "/api/info") {
    const info = {
      os: `${type()} ${release()}`,
      machineName: hostname(),
      inContainer: existsSync("/.dockerenv") || existsSync("/run/.containerenv"),
      // The Node.js inspector, which a debugger attaches to: on when run with "Debug npm Script in Container".
      inspector: inspector.url() ?? null,
      node: process.version,
    };
    response.writeHead(200, { "content-type": "application/json" });
    response.end(JSON.stringify(info));
    return;
  }

  response.writeHead(200, { "content-type": "text/plain" });
  response.end("hello-node is running. Try GET /api/info.\n");
});

server.listen(port, host, () => console.log(`Listening on http://${host}:${port}`));
