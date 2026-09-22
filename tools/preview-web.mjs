// UI-only simulator. NOT the desktop server. Never handles real audio or installs a service.
import http from "node:http";
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
const root = new URL("../src/VoiceKit.Remote/wwwroot/", import.meta.url);
let state = {
  revision: 1,
  engine: {
    running: true,
    panic: false,
    chainEnabled: true,
    microphoneMuted: false,
  },
  effects: {
    pitch: { enabled: true, semitones: -3 },
    robot: { enabled: false, hz: 70, mix: 0.8 },
    echo: { enabled: true, delayMs: 280, feedback: 0.3, mix: 0.25 },
    reverb: { enabled: false, size: 0.55, mix: 0.25 },
    wobble: { enabled: false, minSemitones: -2, maxSemitones: 2, rateHz: 3 },
  },
};
const assets = {
  "/": ["index.html", "text/html"],
  "/app.js": ["app.js", "text/javascript"],
  "/control.js": ["control.js", "text/javascript"],
  "/style.css": ["style.css", "text/css"],
  "/icon.svg": ["icon.svg", "image/svg+xml"],
};
const server = http.createServer(async (req, res) => {
  res.setHeader("Cache-Control", "no-store");
  const json = (code, data) => {
    res.writeHead(code, { "Content-Type": "application/json" });
    res.end(JSON.stringify(data));
  };
  try {
    const path = new URL(req.url, "http://preview").pathname;
    if (path.startsWith("/api/")) {
      const body = async () => {
        let data = "";
        for await (const part of req) {
          data += part;
          if (data.length > 4096) throw new Error("Body too large");
        }
        return JSON.parse(data || "{}");
      };
      if (path === "/api/pair" && req.method === "POST") {
        const input = await body();
        return json(input.code === "12345678" ? 200 : 401, {
          token: "preview-only-not-a-real-session",
        });
      }
      if (req.headers["x-voicekit-token"] !== "preview-only-not-a-real-session")
        return json(401, {});
      if (path === "/api/state" && req.method === "GET")
        return json(200, state);
      const effect = path.split("/")[3];
      if (
        req.method === "PATCH" &&
        path.startsWith("/api/effects/") &&
        Object.hasOwn(state.effects, effect)
      ) {
        const patch = await body();
        if (patch.enabled !== undefined)
          state.effects[effect].enabled = patch.enabled;
        Object.assign(state.effects[effect], patch.parameters);
        state.revision++;
        return json(200, state);
      }
      return json(404, {});
    }
    const asset = assets[path];
    if (!asset) {
      res.writeHead(404);
      return res.end();
    }
    let content = await readFile(new URL(asset[0], root));
    if (path === "/")
      content = content
        .toString()
        .replace(
          "<head>",
          '<head><meta name="voicekit-demo" content="12345678">',
        );
    res.writeHead(200, { "Content-Type": `${asset[1]}; charset=utf-8` });
    res.end(content);
  } catch (error) {
    json(400, { error: error.message });
  }
});
const port = Number(process.env.PORT || 3000);
server.listen(port, "0.0.0.0", () =>
  console.log(
    `VoiceKit UI preview (simulated audio state): http://0.0.0.0:${port}\nAssets: ${fileURLToPath(root)}`,
  ),
);
