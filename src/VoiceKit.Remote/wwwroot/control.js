// Shared protocol/client, deliberately independent from the DOM so race conditions can be tested.
export const EFFECTS = [
  {
    id: "pitch",
    name: "Питч",
    subtitle: "Выше. Ниже. По-твоему.",
    kind: "PITCH SHIFT",
    index: "01",
    params: [
      {
        key: "semitones",
        label: "Высота голоса",
        min: -12,
        max: 12,
        step: 0.1,
        unit: "st",
        initial: 0,
      },
    ],
  },
  {
    id: "robot",
    name: "Робот",
    subtitle: "Немного не человек.",
    kind: "RING MODULATION",
    index: "02",
    params: [
      {
        key: "hz",
        label: "Частота",
        min: 10,
        max: 300,
        step: 1,
        unit: "Гц",
        initial: 70,
      },
      {
        key: "mix",
        label: "Доля эффекта",
        min: 0,
        max: 1,
        step: 0.01,
        unit: "%",
        initial: 0.8,
      },
    ],
  },
  {
    id: "echo",
    name: "Эхо",
    subtitle: "Слова с продолжением.",
    kind: "DELAY",
    index: "03",
    params: [
      {
        key: "delayMs",
        label: "Задержка",
        min: 20,
        max: 1500,
        step: 5,
        unit: "мс",
        initial: 280,
      },
      {
        key: "feedback",
        label: "Обратная связь",
        min: 0,
        max: 0.85,
        step: 0.01,
        unit: "%",
        initial: 0.3,
      },
      {
        key: "mix",
        label: "Уровень эха",
        min: 0,
        max: 1,
        step: 0.01,
        unit: "%",
        initial: 0.25,
      },
    ],
  },
  {
    id: "reverb",
    name: "Реверберация",
    subtitle: "Добавь пространству голос.",
    kind: "REVERB",
    index: "04",
    params: [
      {
        key: "size",
        label: "Размер / затухание",
        min: 0,
        max: 0.9,
        step: 0.01,
        unit: "%",
        initial: 0.55,
      },
      {
        key: "mix",
        label: "Уровень эффекта",
        min: 0,
        max: 1,
        step: 0.01,
        unit: "%",
        initial: 0.25,
      },
    ],
  },
  {
    id: "wobble",
    name: "Воббл",
    subtitle: "Голос на своей волне.",
    kind: "WOBBLE PITCH",
    index: "05",
    params: [
      {
        key: "minSemitones",
        label: "Нижняя граница",
        min: -12,
        max: 12,
        step: 0.1,
        unit: "st",
        initial: -2,
      },
      {
        key: "maxSemitones",
        label: "Верхняя граница",
        min: -12,
        max: 12,
        step: 0.1,
        unit: "st",
        initial: 2,
      },
      {
        key: "rateHz",
        label: "Частота колебаний",
        min: 0.1,
        max: 12,
        step: 0.1,
        unit: "Гц",
        initial: 3,
      },
    ],
  },
];
export function formatValue(value, parameter) {
  if (parameter.unit === "%") return `${Math.round(value * 100)}%`;
  if (parameter.unit === "st")
    return `${value > 0 ? "+" : ""}${value.toFixed(1)} st`;
  return `${parameter.step < 1 ? value.toFixed(1) : Math.round(value)} ${parameter.unit}`;
}
// Treat the two bounds as one atomic setting, including when they cross during a drag.
export function parameterPatch(effect, key, value, current) {
  if (
    effect === "wobble" &&
    (key === "minSemitones" || key === "maxSemitones")
  ) {
    return {
      parameters:
        key === "minSemitones"
          ? {
              minSemitones: value,
              maxSemitones: Math.max(value, current.maxSemitones),
            }
          : {
              minSemitones: Math.min(value, current.minSemitones),
              maxSemitones: value,
            },
    };
  }
  return { parameters: { [key]: value } };
}
export function mergePatch(first = {}, next) {
  const result = { ...first, ...next };
  if (first.parameters || next.parameters)
    result.parameters = { ...first.parameters, ...next.parameters };
  return result;
}
function overlay(state, effect, patch) {
  if (patch.enabled !== undefined)
    state.effects[effect].enabled = patch.enabled;
  Object.assign(state.effects[effect], patch.parameters);
}
export class ControlClient {
  constructor({
    fetchImpl = (...args) => fetch(...args),
    onState = () => {},
    onStatus = () => {},
    onAuthLost = () => {},
    pollMs = 800,
  } = {}) {
    Object.assign(this, { fetchImpl, onState, onStatus, onAuthLost, pollMs });
    this.pending = new Map();
    this.controllers = new Set();
    this.generation = 0;
    this.connected = false;
    this.running = false;
    this.base = null;
    this.inflight = null;
  }
  get view() {
    if (!this.base) return null;
    const state = structuredClone(this.base);
    if (this.connected) {
      if (this.inflight)
        overlay(state, this.inflight.effect, this.inflight.patch);
      for (const [effect, patch] of this.pending) overlay(state, effect, patch);
    }
    return state;
  }
  emit() {
    this.onState(this.view, this.pending.size > 0 || !!this.inflight);
  }
  async request(path, options = {}) {
    const controller = new AbortController();
    this.controllers.add(controller);
    const timeout = setTimeout(() => controller.abort(), 4000);
    try {
      const response = await this.fetchImpl(path, {
        ...options,
        signal: controller.signal,
        cache: "no-store",
        headers: {
          "X-VoiceKit-Token": this.token,
          ...(options.body ? { "Content-Type": "application/json" } : {}),
        },
      });
      if (!response.ok) {
        const error = new Error(`HTTP ${response.status}`);
        error.status = response.status;
        throw error;
      }
      return await response.json();
    } finally {
      clearTimeout(timeout);
      this.controllers.delete(controller);
    }
  }
  async start(token) {
    this.stop(false);
    this.token = token;
    this.running = true;
    this.onStatus("connecting");
    await this.poll(this.generation);
  }
  stop(notify = true) {
    this.generation++;
    this.running = this.connected = false;
    clearTimeout(this.pollTimer);
    clearTimeout(this.sendTimer);
    this.sendTimer = null;
    for (const controller of this.controllers) controller.abort();
    this.controllers.clear();
    this.pending.clear();
    this.inflight = null;
    this.base = null;
    if (notify) {
      this.onStatus("stopped");
      this.emit();
    }
  }
  accept(state) {
    if (!this.base || state.revision >= this.base.revision) this.base = state;
  }
  fail(error, generation) {
    if (generation !== this.generation) return;
    if (error.status === 401 || error.status === 403) {
      this.stop(false);
      this.onStatus("unauthorized");
      this.emit();
      this.onAuthLost();
    } else {
      // Never replay buffered gestures after a disconnect. GET reconciles possibly applied writes.
      this.connected = false;
      this.pending.clear();
      clearTimeout(this.sendTimer);
      this.sendTimer = null;
      this.onStatus("offline");
      this.emit();
    }
  }
  async poll(generation) {
    if (!this.running || generation !== this.generation) return;
    clearTimeout(this.pollTimer);
    this.pollTimer = null;
    try {
      const state = await this.request("/api/state");
      if (generation !== this.generation) return;
      this.accept(state);
      this.connected = true;
      this.onStatus("connected");
      this.emit();
    } catch (error) {
      this.fail(error, generation);
    } finally {
      if (this.running && generation === this.generation)
        this.pollTimer = setTimeout(
          () => this.poll(generation),
          this.connected ? this.pollMs : 1500,
        );
    }
  }
  enqueue(effect, patch) {
    if (!this.connected || !this.base || !EFFECTS.some((x) => x.id === effect))
      return false;
    this.pending.set(effect, mergePatch(this.pending.get(effect), patch));
    this.emit();
    if (!this.inflight && !this.sendTimer)
      this.sendTimer = setTimeout(() => this.flush(), 65);
    return true;
  }
  async flush() {
    clearTimeout(this.sendTimer);
    this.sendTimer = null;
    if (this.inflight || !this.connected || !this.running || !this.pending.size)
      return;
    const generation = this.generation;
    const [effect, patch] = this.pending.entries().next().value;
    this.pending.delete(effect);
    this.inflight = { effect, patch };
    try {
      const state = await this.request(`/api/effects/${effect}`, {
        method: "PATCH",
        body: JSON.stringify(patch),
      });
      if (generation !== this.generation) return;
      this.accept(state);
    } catch (error) {
      this.fail(error, generation);
    } finally {
      if (generation === this.generation) {
        this.inflight = null;
        this.emit();
        if (this.connected && this.pending.size)
          this.sendTimer = setTimeout(() => this.flush(), 0);
      }
    }
  }
}
