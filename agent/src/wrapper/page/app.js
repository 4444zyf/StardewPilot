// StardewPilot copilot settings page. Plain JS, no dependencies. All server text is set with textContent.
"use strict";

const TOKEN_KEY = "stardewpilot-token";
const STATE_TEXT = {
  running: "Running",
  starting: "Starting",
  needs_setup: "Needs setup",
  error: "Error",
  stopped: "Not running",
};

// The link carries the access key in the fragment, which is never sent to servers. Move it out of the address bar.
(function takeToken() {
  const m = /[#&]t=([A-Za-z0-9_-]+)/.exec(location.hash);
  if (m) {
    sessionStorage.setItem(TOKEN_KEY, m[1]);
    history.replaceState(null, "", location.pathname);
  }
})();
const token = sessionStorage.getItem(TOKEN_KEY);

const $ = (id) => document.getElementById(id);

function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (k === "class") node.className = v;
    else if (k === "text") node.textContent = v;
    else if (k.startsWith("on")) node.addEventListener(k.slice(2), v);
    else node.setAttribute(k, v);
  }
  for (const c of children) if (c != null) node.append(c);
  return node;
}

/** Only real web links become clickable. */
function safeLink(url, label) {
  try {
    const u = new URL(url);
    if (u.protocol === "https:" || u.protocol === "http:")
      return el("a", { href: u.href, target: "_blank", rel: "noopener noreferrer", text: label || u.href });
  } catch { /* not a URL */ }
  return el("span", { text: url });
}

async function api(path, body) {
  const res = await fetch("/api/" + path, {
    method: body === undefined ? "GET" : "POST",
    headers: Object.assign({ "X-Pilot-Token": token || "" }, body === undefined ? {} : { "Content-Type": "application/json" }),
    body: body === undefined ? undefined : JSON.stringify(body),
    cache: "no-store",
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || `Request failed (${res.status})`);
  return data;
}

let state = null;
let providers = [];
let models = [];

// ---------- status ----------

function renderState(s) {
  state = s;
  const badge = $("badge");
  badge.className = "badge " + s.state;
  badge.textContent = STATE_TEXT[s.state] || s.state;
  $("status-message").textContent = s.message || STATE_TEXT[s.state] || "";
  $("status-model").textContent = s.settings.model || "none";
  $("pidir").textContent = s.piDir;
  $("piversion").textContent = s.piVersion;
  for (const r of document.querySelectorAll("input[name=pifolder]")) r.checked = r.value === s.settings.piFolder;
  const box = $("errors-box");
  box.hidden = s.recentErrors.length === 0;
  $("errors").textContent = s.recentErrors.join("\n");
  $("restart").disabled = s.state === "stopped";
}

async function refreshState() {
  try {
    renderState(await api("state"));
  } catch (e) {
    $("status-message").textContent = "Cannot reach the copilot wrapper: " + e.message;
    $("badge").className = "badge error";
    $("badge").textContent = "Offline";
  }
}

// ---------- providers ----------

function signInState(p) {
  if (!p.signedIn) return "Not signed in";
  return p.source ? `Signed in (${p.source})` : "Signed in";
}

function renderProviders() {
  const oauth = $("oauth-list");
  oauth.replaceChildren();
  for (const p of providers.filter((p) => p.oauth)) {
    oauth.append(el("button", {
      type: "button",
      class: "tile" + (p.signedIn ? " on" : ""),
      onclick: () => startLogin(p, "oauth"),
    }, el("span", { class: "name", text: p.oauth }), el("span", { class: "state", text: `${p.name} · ${signInState(p)}` })));
  }
  renderKeyProviders();

  const list = $("signed-in");
  list.replaceChildren();
  const on = providers.filter((p) => p.signedIn);
  if (on.length === 0) list.append(el("li", { class: "muted", text: "Nothing yet." }));
  for (const p of on) {
    const actions = el("span");
    if (p.stored)
      actions.append(el("button", { type: "button", class: "small", text: "Sign out", onclick: () => logout(p) }));
    else
      actions.append(el("span", { class: "muted", text: p.custom ? "set in models.json" : "from your environment" }));
    list.append(el("li", {}, el("span", {}, el("strong", { text: p.name }), " ", el("span", { class: "muted", text: `· ${p.modelCount} models · ${p.source || "configured"}` })), actions));
  }
}

function renderKeyProviders() {
  const q = $("key-search").value.trim().toLowerCase();
  const select = $("key-provider");
  const keep = select.value;
  select.replaceChildren();
  for (const p of providers.filter((p) => p.apiKey && !p.custom)) {
    if (q && !p.name.toLowerCase().includes(q) && !p.id.includes(q)) continue;
    select.append(el("option", { value: p.id, text: p.name + (p.signedIn ? "  ✓" : "") }));
  }
  if ([...select.options].some((o) => o.value === keep)) select.value = keep;
  $("key-add").disabled = select.options.length === 0;
}

async function refreshProviders() {
  const data = await api("providers");
  providers = data.providers;
  const apiSelect = document.querySelector("#custom-form select[name=api]");
  if (apiSelect.options.length === 0)
    for (const a of data.customApis) apiSelect.append(el("option", { value: a, text: a }));
  renderProviders();
}

async function logout(p) {
  if (!confirm(`Sign out of ${p.name}? The stored key or sign-in is removed from this computer.`)) return;
  try {
    await api("logout", { provider: p.id });
  } catch (e) {
    alert(e.message);
  }
  await refreshAll();
}

// ---------- models ----------

function renderModels(thinkingLevels) {
  const select = $("model");
  const current = state ? state.settings.model : "";
  select.replaceChildren();
  if (models.length === 0) select.append(el("option", { value: "", text: "Sign in first" }));
  const groups = new Map();
  for (const m of models) {
    if (!groups.has(m.providerName)) groups.set(m.providerName, []);
    groups.get(m.providerName).push(m);
  }
  for (const [name, list] of groups) {
    const group = el("optgroup", { label: name });
    for (const m of list) {
      const ref = `${m.provider}/${m.id}`;
      const ctx = m.contextWindow ? ` · ${Math.round(m.contextWindow / 1000)}k` : "";
      group.append(el("option", { value: ref, text: (m.name || m.id) + ctx }));
    }
    select.append(group);
  }
  if (current && [...select.options].some((o) => o.value === current)) select.value = current;
  else if (current) {
    select.prepend(el("option", { value: current, text: `${current} (not available)` }));
    select.value = current;
  }

  const thinking = $("thinking");
  if (thinking.options.length === 0)
    for (const t of thinkingLevels) thinking.append(el("option", { value: t, text: t }));
  if (state) thinking.value = state.settings.thinking;
  $("test").disabled = $("save").disabled = models.length === 0;
}

async function refreshModels() {
  const data = await api("models");
  models = data.models;
  renderModels(data.thinkingLevels);
}

function showResult(kind, text) {
  const r = $("test-result");
  r.hidden = false;
  r.className = "result " + kind;
  r.textContent = text;
}

async function testModel() {
  const model = $("model").value;
  if (!model) return;
  $("test").disabled = true;
  showResult("", `Testing ${model}…`);
  try {
    const r = await api("test", { model });
    if (!r.ok) showResult("bad", `Failed after ${r.seconds.toFixed(1)} s: ${r.error}`);
    else if (!r.toolCall) showResult("warn", `Answered in ${r.seconds.toFixed(1)} s, but did not call the test tool. The copilot may not work well with this model.`);
    else showResult("ok", `Works: answered in ${r.seconds.toFixed(1)} s and called the test tool.`);
  } catch (e) {
    showResult("bad", e.message);
  } finally {
    $("test").disabled = false;
  }
}

async function saveModel() {
  $("save").disabled = true;
  try {
    renderState(await api("settings", { model: $("model").value, thinking: $("thinking").value }));
    showResult("ok", "Saved. The copilot is restarting with this model.");
  } catch (e) {
    showResult("bad", e.message);
  } finally {
    $("save").disabled = false;
  }
}

async function setPiFolder(value) {
  if (!state || value === state.settings.piFolder) return;
  try {
    renderState(await api("settings", { piFolder: value }));
    await refreshAll();
  } catch (e) {
    alert(e.message);
  }
}

// ---------- custom endpoint ----------

async function saveCustom(ev) {
  ev.preventDefault();
  const form = ev.target;
  const body = Object.fromEntries(new FormData(form).entries());
  const out = $("custom-result");
  out.textContent = "Saving…";
  try {
    await api("custom-provider", body);
    form.apiKey.value = "";
    out.textContent = "Saved. Its models are now in the list below.";
    await refreshAll();
  } catch (e) {
    out.textContent = e.message;
  }
}

// ---------- sign-in dialog ----------

let flow = null;
let pollTimer = null;
const shownPrompts = new Set();

function startLogin(p, type) {
  const title = type === "oauth" ? `Sign in: ${p.oauth}` : `API key: ${p.name}`;
  $("login-title").textContent = title;
  $("login-events").replaceChildren();
  $("login-prompts").replaceChildren();
  $("login-status").textContent = "Starting…";
  $("login-cancel").hidden = false;
  $("login-close").hidden = true;
  shownPrompts.clear();
  $("login").showModal();
  api("login", { provider: p.id, type })
    .then((view) => { flow = view; renderFlow(view); poll(); })
    .catch((e) => endFlow("error", e.message));
}

function poll() {
  clearTimeout(pollTimer);
  pollTimer = setTimeout(async () => {
    if (!flow) return;
    try {
      const view = await api("login/" + flow.id);
      flow = view;
      renderFlow(view);
      if (view.state === "running") poll();
    } catch (e) {
      endFlow("error", e.message);
    }
  }, 1000);
}

function renderEvent(e) {
  switch (e.type) {
    case "auth_url":
      return el("div", { class: "event" },
        e.instructions ? el("p", { text: e.instructions }) : null,
        el("p", {}, safeLink(e.url, "Open the sign-in page →")),
        el("p", { class: "muted", text: "Sign in there, then come back to this tab." }));
    case "device_code":
      return el("div", { class: "event" },
        el("p", {}, "Go to ", safeLink(e.verificationUri), " and enter this code:"),
        el("span", { class: "code", text: e.userCode }));
    case "info":
      return el("div", { class: "event" }, el("p", { text: e.message }), ...(e.links || []).map((l) => el("p", {}, safeLink(l.url, l.label))));
    default:
      return el("p", { class: "event muted", text: e.message });
  }
}

function renderPrompt(p) {
  const form = el("div", { class: "prompt", "data-prompt": p.id });
  form.append(el("label", { text: p.message, for: "prompt-" + p.id }));
  let input;
  if (p.type === "select") {
    input = el("select", { id: "prompt-" + p.id });
    for (const o of p.options || []) input.append(el("option", { value: o.id, text: o.description ? `${o.label} — ${o.description}` : o.label }));
  } else {
    input = el("input", {
      id: "prompt-" + p.id,
      type: p.type === "secret" ? "password" : "text",
      autocomplete: "off",
      spellcheck: "false",
      placeholder: p.placeholder || (p.type === "manual_code" ? "Paste the final address from the browser here, if the page did not finish by itself" : ""),
    });
  }
  const submit = el("button", { type: "button", class: "primary", text: "Continue" });
  const answer = async () => {
    const value = input.value;
    if (p.type !== "select" && value.trim() === "") return;
    submit.disabled = true;
    try {
      flow = await api(`login/${flow.id}/answer`, { prompt: p.id, value });
      input.value = "";
      renderFlow(flow);
    } catch (e) {
      $("login-status").textContent = e.message;
      submit.disabled = false;
    }
  };
  submit.addEventListener("click", answer);
  input.addEventListener("keydown", (ev) => { if (ev.key === "Enter") { ev.preventDefault(); answer(); } });
  form.append(el("div", { class: "row" }, input, submit));
  return form;
}

function renderFlow(view) {
  const events = $("login-events");
  events.replaceChildren(...view.events.map(renderEvent));

  // Keep typed text in prompts that are still open; add new ones; drop answered ones.
  const box = $("login-prompts");
  const open = new Set(view.prompts.map((p) => p.id));
  for (const node of [...box.children]) if (!open.has(node.dataset.prompt)) node.remove();
  for (const p of view.prompts) {
    if (!box.querySelector(`[data-prompt="${p.id}"]`)) {
      const node = renderPrompt(p);
      box.append(node);
      if (!shownPrompts.has(p.id)) node.querySelector("input,select").focus();
      shownPrompts.add(p.id);
    }
  }

  if (view.state === "running") $("login-status").textContent = view.prompts.length ? "" : "Waiting…";
  else endFlow(view.state, view.error);
}

function endFlow(result, error) {
  clearTimeout(pollTimer);
  $("login-prompts").replaceChildren();
  $("login-cancel").hidden = true;
  $("login-close").hidden = false;
  $("login-status").textContent =
    result === "done" ? "Signed in. Now choose a model below." :
    result === "cancelled" ? "Cancelled." :
    "Sign-in failed: " + (error || "unknown error");
  refreshAll();
}

$("login-cancel").addEventListener("click", async () => {
  if (flow) await api(`login/${flow.id}/cancel`, {}).catch(() => {});
  flow = null;
  $("login").close();
});
$("login-close").addEventListener("click", () => { flow = null; $("login").close(); });
$("login").addEventListener("cancel", () => { if (flow && flow.state === "running") api(`login/${flow.id}/cancel`, {}).catch(() => {}); flow = null; });

// ---------- wiring ----------

async function refreshAll() {
  await refreshState();
  try {
    await refreshProviders();
    await refreshModels();
  } catch (e) {
    $("status-message").textContent = e.message;
  }
}

function init() {
  if (!token) {
    $("no-token").hidden = false;
    for (const c of document.querySelectorAll(".card")) c.hidden = true;
    $("badge").textContent = "Locked";
    return;
  }
  $("key-search").addEventListener("input", renderKeyProviders);
  $("key-add").addEventListener("click", () => {
    const p = providers.find((x) => x.id === $("key-provider").value);
    if (p) startLogin(p, "api_key");
  });
  $("custom-form").addEventListener("submit", saveCustom);
  $("test").addEventListener("click", testModel);
  $("save").addEventListener("click", saveModel);
  $("restart").addEventListener("click", async () => {
    try { renderState(await api("restart", {})); } catch (e) { alert(e.message); }
  });
  for (const r of document.querySelectorAll("input[name=pifolder]"))
    r.addEventListener("change", () => setPiFolder(r.value));
  refreshAll();
  setInterval(refreshState, 3000);
}

init();
