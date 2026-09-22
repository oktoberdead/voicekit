import {
  ControlClient,
  EFFECTS,
  formatValue,
  parameterPatch,
} from "./control.js";

const $ = (id) => document.getElementById(id);
const demo = document.querySelector('meta[name="voicekit-demo"]');
const POWER =
  '<svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M12 3v9M6.3 5.8a8 8 0 1 0 11.4 0"/></svg>';
const CHEVRON =
  '<svg viewBox="0 0 20 20" fill="none" aria-hidden="true"><path d="m6 8 4 4 4-4"/></svg>';
const shapes = {
  pitch:
    '<path d="M12 42v12M24 29v38M36 17v62M48 30v36M60 10v76M72 24v48M84 38v20"/>',
  wobble:
    '<path d="M10 48c7 0 7-25 15-25s8 50 16 50 8-50 16-50 8 50 16 50 7-25 13-25"/><path d="M11 12h74M11 84h74" stroke-dasharray="3 6" stroke-width="1"/>',
  robot:
    '<rect x="21" y="29" width="54" height="46" rx="13"/><path d="M48 29V15M43 15h10M12 43v18M84 43v18M36 60h24"/><circle cx="36" cy="45" r="3"/><circle cx="60" cy="45" r="3"/>',
  echo: '<path d="M15 42v12M28 29a28 28 0 0 1 0 38M43 20a41 41 0 0 1 0 56M59 11a53 53 0 0 1 0 74"/>',
  reverb:
    '<path d="M13 30 48 11l35 19v38L48 87 13 68V30Z M13 30l35 19 35-19M48 49v38 M31 21l35 19v38M31 78V40l35-19"/>',
};
const cards = new Map();
const dragging = new Set();
let snapshot = null,
  online = false,
  busy = false,
  pairing = false;

for (const effect of EFFECTS) {
  const card = document.createElement("article");
  card.className = `effect-card ${effect.id}`;
  // All template strings below are bundled constants, never server- or user-provided HTML.
  card.innerHTML = `
    <button class="effect-toggle" id="toggle-${effect.id}" aria-pressed="false" aria-label="${effect.name}: включить или выключить" disabled>
      <span class="card-top"><span class="effect-kind">${effect.index}<span>/</span>${effect.kind}</span><span class="power-icon">${POWER}</span></span>
      <span class="effect-body"><span class="effect-art"><svg viewBox="0 0 96 96" fill="none" aria-hidden="true">${shapes[effect.id]}</svg></span><span class="effect-copy"><span class="effect-name">${effect.name}</span><span class="effect-subtitle">${effect.subtitle}</span></span></span>
      <span class="card-bottom"><span class="effect-state"><span></span><span class="state-label">Выключен</span></span><span class="effect-summary">—</span></span>
    </button>
    <button class="settings-toggle" id="settings-${effect.id}" aria-expanded="false" aria-controls="panel-${effect.id}"><span class="settings-label"><svg viewBox="0 0 20 20" fill="none" aria-hidden="true"><path d="M3 5h14M3 15h14M7 2v6M13 12v6"/></svg>Параметры</span>${CHEVRON}</button>
    <div class="parameters" id="panel-${effect.id}" hidden></div>`;
  const panel = card.querySelector(".parameters");
  for (const parameter of effect.params) {
    const row = document.createElement("div");
    row.className = "parameter";
    const id = `${effect.id}-${parameter.key}`;
    row.innerHTML = `<div class="parameter-label"><label for="${id}">${parameter.label}</label><output for="${id}" id="value-${id}">—</output></div><input type="range" id="${id}" min="${parameter.min}" max="${parameter.max}" step="${parameter.step}" value="${parameter.initial}" disabled><div class="range-ends"><span>${formatValue(parameter.min, parameter)}</span><span>${formatValue(parameter.max, parameter)}</span></div>`;
    panel.append(row);
    const input = row.querySelector("input");
    input.addEventListener("pointerdown", () => dragging.add(id));
    const release = () => {
      dragging.delete(id);
      render();
    };
    input.addEventListener("pointerup", release);
    input.addEventListener("pointercancel", release);
    input.addEventListener("blur", release);
    input.addEventListener("input", () => {
      const value = Number(input.value);
      paintSlider(input, parameter, value);
      const current = client.view?.effects[effect.id];
      if (current)
        client.enqueue(
          effect.id,
          parameterPatch(effect.id, parameter.key, value, current),
        );
    });
  }
  card.querySelector(".effect-toggle").addEventListener("click", () => {
    const state = client.view?.effects[effect.id];
    if (state) client.enqueue(effect.id, { enabled: !state.enabled });
  });
  card.querySelector(".settings-toggle").addEventListener("click", (event) => {
    const button = event.currentTarget,
      expanded = button.getAttribute("aria-expanded") !== "true";
    button.setAttribute("aria-expanded", String(expanded));
    panel.hidden = !expanded;
  });
  if (effect.id === "wobble") {
    const note = document.createElement("p");
    note.className = "parameter-note";
    note.textContent =
      "Границы добавляются к обычному питчу. При пересечении соседняя граница подтягивается; одинаковые дают постоянный сдвиг.";
    panel.append(note);
  }
  cards.set(effect.id, card);
  $("effects").append(card);
}
function paintSlider(input, parameter, value) {
  if (!dragging.has(input.id)) input.value = value;
  input.style.setProperty(
    "--fill",
    `${((value - parameter.min) / (parameter.max - parameter.min)) * 100}%`,
  );
  input.setAttribute("aria-valuetext", formatValue(value, parameter));
  $(`value-${input.id}`).textContent = formatValue(value, parameter);
}
function render() {
  let count = 0;
  for (const effect of EFFECTS) {
    const card = cards.get(effect.id),
      state = snapshot?.effects[effect.id];
    const enabled = !!state?.enabled;
    if (enabled) count++;
    card.classList.toggle("is-on", enabled);
    card.classList.toggle("is-offline", !online);
    const button = card.querySelector(".effect-toggle");
    button.disabled = !online || !state;
    button.setAttribute("aria-pressed", String(enabled));
    card.querySelector(".state-label").textContent = enabled
      ? "Включён"
      : "Выключен";
    const summary = effect.params[0];
    card.querySelector(".effect-summary").textContent = state
      ? effect.id === "wobble"
        ? `${state.rateHz.toFixed(1)} Гц`
        : formatValue(state[summary.key], summary)
      : "—";
    for (const parameter of effect.params) {
      const input = $(`${effect.id}-${parameter.key}`);
      input.disabled = !online || !state;
      if (!dragging.has(input.id))
        paintSlider(
          input,
          parameter,
          state?.[parameter.key] ?? parameter.initial,
        );
    }
  }
  $("active-count").textContent = snapshot
    ? `${String(count).padStart(2, "0")} / ${String(EFFECTS.length).padStart(2, "0")} включено`
    : `— / ${String(EFFECTS.length).padStart(2, "0")} включено`;
  $("sync-label").textContent = !online
    ? "Ожидание подключения"
    : busy
      ? "Применяем…"
      : "Настройки синхронизированы";
  const engine = snapshot?.engine;
  let notice = "";
  if (engine && online) {
    if (!engine.running)
      notice =
        "Аудиодвижок остановлен. Настройки сохраняются — запусти аудио в VoiceKit на ПК.";
    else if (engine.panic)
      notice =
        "На ПК включён PANIC. Изменение эффектов не вернёт звук: останови и запусти аудио заново.";
    else if (!engine.chainEnabled)
      notice =
        "Цепочка эффектов обойдена на ПК. Включи общую обработку в приложении или её хоткеем.";
    else if (engine.microphoneMuted)
      notice =
        "Микрофон заглушён на ПК. Эффекты настраиваются, но голос сейчас не передаётся.";
  }
  $("engine-notice").textContent = notice;
  $("engine-notice").hidden = !notice;
}
function readToken() {
  try {
    return sessionStorage.getItem("voicekit-token");
  } catch {
    return null;
  }
}
function saveToken(token) {
  try {
    if (token) sessionStorage.setItem("voicekit-token", token);
    else sessionStorage.removeItem("voicekit-token");
  } catch {
    /* session storage can be disabled */
  }
}
let token = readToken();
const client = new ControlClient({
  onState(state, pending) {
    snapshot = state;
    busy = pending;
    render();
  },
  onStatus(status) {
    online = status === "connected";
    const labels = {
      connected: demo ? "Демо-подключение" : "Подключено к ПК",
      connecting: "Подключаемся…",
      offline: "Связь потеряна · повторяем",
      unauthorized: "Нужен новый код",
      stopped: "Не подключено",
    };
    $("connection-label").textContent = labels[status];
    $("connection-dot").classList.toggle("connected", online);
    $("session-button").textContent = token
      ? "Отключиться ↗"
      : "Подключить ↗";
    render();
  },
  onAuthLost() {
    token = null;
    saveToken(null);
    $("session-button").textContent = "Подключить ↗";
    showPair("Сессия завершена. Введи текущий код с экрана ПК.");
  },
});
function showPair(message = "") {
  $("pair-error").textContent = message;
  $("pair-code").value = "";
  if (!$("pair-dialog").open) $("pair-dialog").showModal();
}
async function pair(code) {
  if (pairing) return;
  pairing = true;
  $("pair-submit").disabled = true;
  $("pair-error").textContent = "";
  try {
    const response = await fetch("/api/pair", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ code }),
      signal: AbortSignal.timeout(5000),
    });
    if (!response.ok)
      throw new Error(
        response.status === 429
          ? "Слишком много попыток. Подожди минуту."
          : response.status === 401
            ? "Код не подошёл. Проверь его на ПК."
            : "Сервер не принял подключение. Проверь адрес.",
      );
    token = (await response.json()).token;
    saveToken(token);
    $("pair-dialog").close();
    await client.start(token);
  } catch (error) {
    $("pair-error").textContent =
      error.name === "TimeoutError" || error.name === "TypeError"
        ? "ПК не отвечает. Проверь Wi-Fi и включён ли веб-пульт."
        : error.message;
  } finally {
    pairing = false;
    $("pair-submit").disabled = false;
  }
}
$("pair-form").addEventListener("submit", (event) => {
  event.preventDefault();
  pair($("pair-code").value.replace(/\s/g, ""));
});
$("pair-close").addEventListener("click", () => $("pair-dialog").close());
$("session-button").addEventListener("click", () => {
  if (token) {
    token = null;
    saveToken(null);
    client.stop();
  }
  showPair();
});
document.addEventListener("visibilitychange", () => {
  dragging.clear();
  if (document.hidden) client.stop();
  else if (token) client.start(token);
});
window.addEventListener("pointerup", () => {
  dragging.clear();
  render();
});
window.addEventListener("pointercancel", () => {
  dragging.clear();
  render();
});
window.addEventListener("online", () => {
  if (token && !document.hidden) client.start(token);
});
window.addEventListener("offline", () => client.stop());
render();
if (demo) {
  $("demo-banner").hidden = false;
  pair(demo.content);
} else if (token) client.start(token);
else showPair();
