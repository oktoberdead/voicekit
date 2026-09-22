import test from "node:test";
import assert from "node:assert/strict";
import {
  ControlClient,
  EFFECTS,
  mergePatch,
  parameterPatch,
  formatValue,
} from "../../src/VoiceKit.Remote/wwwroot/control.js";
const initial = () => ({
  revision: 1,
  engine: {
    running: true,
    panic: false,
    chainEnabled: true,
    microphoneMuted: false,
  },
  effects: {
    pitch: { enabled: false, semitones: 0 },
    robot: { enabled: false, hz: 70, mix: 0.8 },
    echo: { enabled: false, delayMs: 280, feedback: 0.3, mix: 0.25 },
    reverb: { enabled: false, size: 0.55, mix: 0.25 },
    wobble: { enabled: false, minSemitones: -2, maxSemitones: 2, rateHz: 3 },
  },
});
const response = (data, status = 200) => ({
  ok: status === 200,
  status,
  json: async () => structuredClone(data),
});
const deferred = () => {
  let resolve;
  const promise = new Promise((r) => (resolve = r));
  return { promise, resolve };
};
function harness(t, handler) {
  const state = initial(),
    writes = [],
    statuses = [];
  const client = new ControlClient({
    pollMs: 100000,
    fetchImpl: async (path, options) => {
      if (options.method === "PATCH") {
        writes.push(JSON.parse(options.body));
        return handler?.(state, writes.at(-1), path) ?? response(state);
      }
      return response(state);
    },
    onStatus: (status) => statuses.push(status),
  });
  t.after(() => client.stop(false));
  return { state, writes, statuses, client };
}
test("Only the five supported effects are exposed, with DSP-compatible ranges", () => {
  assert.deepEqual(
    EFFECTS.map((x) => x.id),
    ["pitch", "robot", "echo", "reverb", "wobble"],
  );
  assert.equal(
    EFFECTS.find((x) => x.id === "echo").params.find(
      (x) => x.key === "feedback",
    ).max,
    0.85,
  );
  assert.equal(formatValue(-3.5, EFFECTS[0].params[0]), "-3.5 st");
  assert.deepEqual(
    mergePatch(
      { enabled: true, parameters: { hz: 100 } },
      { parameters: { mix: 0.2 } },
    ),
    { enabled: true, parameters: { hz: 100, mix: 0.2 } },
  );
});
test("Slider events coalesce per parameter, preserving independent parameters", async (t) => {
  const { client, writes } = harness(t);
  await client.start("test");
  client.enqueue("robot", { enabled: true });
  for (let hz = 80; hz < 110; hz++)
    client.enqueue("robot", { parameters: { hz } });
  client.enqueue("robot", { parameters: { mix: 0.4 } });
  await client.flush();
  assert.equal(writes.length, 1);
  assert.deepEqual(writes[0], {
    enabled: true,
    parameters: { hz: 109, mix: 0.4 },
  });
});
test("A stale poll never replaces a newer acknowledged revision", async (t) => {
  const { client } = harness(t);
  await client.start("test");
  const newer = initial();
  newer.revision = 9;
  newer.effects.pitch.semitones = 7;
  client.accept(newer);
  client.accept(initial());
  assert.equal(client.view.effects.pitch.semitones, 7);
});
test("An in-flight response does not reset edits made during the request", async (t) => {
  const gate = deferred();
  const { client, state } = harness(t, () => gate.promise);
  await client.start("test");
  client.enqueue("pitch", { parameters: { semitones: 3 } });
  const sending = client.flush();
  client.enqueue("pitch", { parameters: { semitones: 6 } });
  state.revision = 2;
  state.effects.pitch.semitones = 3;
  gate.resolve(response(state));
  await sending;
  assert.equal(client.view.effects.pitch.semitones, 6);
});
test("Two quick toggles are idempotent desired states, not deferred toggle commands", async (t) => {
  const { client, writes } = harness(t);
  await client.start("test");
  client.enqueue("echo", { enabled: !client.view.effects.echo.enabled });
  client.enqueue("echo", { enabled: !client.view.effects.echo.enabled });
  await client.flush();
  assert.deepEqual(writes, [{ enabled: false }]);
});
test("Network failure drops queued gestures, blocks controls and never replays after reconnect", async (t) => {
  const { client, writes } = harness(t, () => {
    throw new TypeError("offline");
  });
  await client.start("test");
  client.enqueue("pitch", { enabled: true });
  client.enqueue("echo", { enabled: true });
  await client.flush();
  assert.equal(client.connected, false);
  assert.equal(client.pending.size, 0);
  assert.equal(client.enqueue("robot", { enabled: true }), false);
  await client.poll(client.generation);
  assert.equal(client.connected, true);
  await client.flush();
  assert.equal(writes.length, 1);
});
test("Old requests cannot resurrect a stopped session", async (t) => {
  const gate = deferred();
  const { client, state } = harness(t, () => gate.promise);
  await client.start("test");
  client.enqueue("pitch", { enabled: true });
  const sending = client.flush();
  client.stop(false);
  state.effects.pitch.enabled = true;
  gate.resolve(response(state));
  await sending;
  assert.equal(client.view, null);
  assert.equal(client.connected, false);
});
test("Unauthorized session clears state and asks for pairing", async (t) => {
  let asked = 0;
  const { client } = harness(t, () => response({}, 401));
  client.onAuthLost = () => asked++;
  await client.start("test");
  client.enqueue("pitch", { enabled: true });
  await client.flush();
  assert.equal(asked, 1);
  assert.equal(client.view, null);
  assert.equal(client.running, false);
});
test("Continuous slider movement sends before the user releases it (throttle, not debounce)", async (t) => {
  const { client, writes } = harness(t);
  await client.start("test");
  for (let n = 0; n < 12; n++) {
    client.enqueue("pitch", { parameters: { semitones: n } });
    await new Promise((resolve) => setTimeout(resolve, 12));
  }
  assert.ok(writes.length >= 1);
});

test("Wobble bounds move together atomically when they cross", () => {
  const current = { minSemitones: -2, maxSemitones: 2, rateHz: 3 };
  assert.deepEqual(parameterPatch("wobble", "minSemitones", 5, current), {
    parameters: { minSemitones: 5, maxSemitones: 5 },
  });
  assert.deepEqual(parameterPatch("wobble", "maxSemitones", -5, current), {
    parameters: { minSemitones: -5, maxSemitones: -5 },
  });
  assert.deepEqual(parameterPatch("wobble", "minSemitones", -4, current), {
    parameters: { minSemitones: -4, maxSemitones: 2 },
  });
  assert.deepEqual(parameterPatch("wobble", "rateHz", 0.1, current), {
    parameters: { rateHz: 0.1 },
  });
});
test("Wobble frequency displays sub-Hz values, not rounded zero", () => {
  const parameter = EFFECTS.find((x) => x.id === "wobble").params.find(
    (x) => x.key === "rateHz",
  );
  assert.equal(formatValue(0.1, parameter), "0.1 Гц");
});
test("Wobble bounds and rate coalesce without inverted ranges", async (t) => {
  const { client, writes } = harness(t);
  await client.start("test");
  client.enqueue(
    "wobble",
    parameterPatch("wobble", "minSemitones", 5, client.view.effects.wobble),
  );
  client.enqueue(
    "wobble",
    parameterPatch("wobble", "maxSemitones", -5, client.view.effects.wobble),
  );
  client.enqueue("wobble", { parameters: { rateHz: 8 } });
  await client.flush();
  assert.deepEqual(writes, [
    { parameters: { minSemitones: -5, maxSemitones: -5, rateHz: 8 } },
  ]);
});
