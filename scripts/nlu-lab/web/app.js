"use strict";
// Read-only NLU Lab diff viewer. One fetch per view:
// /state.json?locale=..&lang=..&runA=..&runB=.. returns the run list, the
// change-driven matrix, the intent legend and the resolved chrome strings;
// probe content (utterances, intent names, slots) is rendered untouched.

// Chrome language: the stored manual override wins; otherwise the browser's
// language when it is one the webapp ships strings for (en/it), else en. The
// resolved value is always sent to /state.json, so the server never has to
// guess from the run locale.
function defaultLang() {
  const stored = localStorage.getItem("nlu-lab-lang");
  if (stored) return stored;
  const prefix = (navigator.language || "").slice(0, 2).toLowerCase();
  return (prefix === "it" || prefix === "en") ? prefix : "en";
}

const S = {
  locale: new URLSearchParams(location.search).get("locale") || "it-IT",
  lang: defaultLang(),
  runA: null,
  runB: null,
  state: null,
  selected: null,   // the matrix row whose detail card is open
};

const $ = (id) => document.getElementById(id);

function fmt(key, vars) {
  let text = S.state.strings[key] || key;
  for (const [name, value] of Object.entries(vars || {})) {
    text = text.split("{" + name + "}").join(String(value));
  }
  return text;
}

function shortIntent(name) {
  return name ? name.replace(/Intent$/, "") : null;
}

function showNotice(text) {
  $("notice").hidden = false;
  $("notice").textContent = text;
}

async function refresh() {
  const params = new URLSearchParams({ locale: S.locale });
  if (S.lang) params.set("lang", S.lang);
  if (S.runA) params.set("runA", S.runA);
  if (S.runB) params.set("runB", S.runB);
  let response, state;
  try {
    response = await fetch("/state.json?" + params.toString());
    state = await response.json();
  } catch (err) {
    showNotice("fetch failed: " + err);
    return;
  }
  if (!response.ok) {
    showNotice((state && state.error) ? state.error : "HTTP " + response.status);
    return;
  }
  S.state = state;
  render();
}

function render() {
  const st = S.state;
  document.title = st.strings.title + " · " + st.meta.locale;
  $("locale-chip").textContent = "· " + st.meta.locale;
  document.querySelectorAll("[data-i18n]").forEach((el) => {
    el.textContent = st.strings[el.dataset.i18n] || el.dataset.i18n;
  });
  $("lang-btn").textContent = st.meta.lang === "it" ? "EN" : "IT";
  renderSelectors(st);
  renderMatrix(st);
  renderLegend(st);
  renderDetail(st);
  renderNotices(st);
}

function buildSelect(select, st, selectedId) {
  select.textContent = "";
  for (const run of st.runs) {
    const option = document.createElement("option");
    const ts = run.timestamp ? " · " + run.timestamp.slice(0, 16).replace("T", " ") : "";
    let suffix = "";
    if (run.corrupt) {
      suffix = " · " + st.strings.corrupt;
      option.disabled = true;
    } else if (run.expectation_failures) {
      suffix = " · ✗" + run.expectation_failures;
    }
    option.value = run.id;
    option.textContent = run.label + ts + suffix;
    if (run.id === selectedId) option.selected = true;
    select.appendChild(option);
  }
  select.onchange = () => {
    if (select === $("sel-a")) S.runA = select.value;
    else S.runB = select.value;
    refresh();
  };
}

function renderSelectors(st) {
  buildSelect($("sel-a"), st, st.meta.run_a);
  buildSelect($("sel-b"), st, st.meta.run_b);
}

function sideCell(side, which, kind) {
  const td = document.createElement("td");
  td.className = which;
  if (!side) {
    td.classList.add("absent");
    td.textContent = S.state.strings.absent;
    return td;
  }
  if (side.status === "error") {
    td.classList.add("err");
    td.textContent = S.state.strings.error;
    if (side.error) td.title = side.error;  // native tooltip: the why on the badge
    return td;
  }
  td.textContent = shortIntent(side.selected_intent) || "-";
  if (which === "new" && (kind === "green" || kind === "red" || kind === "amber")) {
    td.classList.add(kind, "win");
  }
  return td;
}

function renderMatrix(st) {
  const body = $("matrix-body");
  body.textContent = "";
  for (const row of st.matrix) {
    const tr = document.createElement("tr");
    if (row.kind !== "same") tr.className = "changed";
    const u = document.createElement("td");
    u.className = "u";
    const utterance = document.createElement("span");
    utterance.className = "utt";
    utterance.textContent = row.utterance;
    const source = document.createElement("span");
    source.className = "src";
    source.textContent = row.source;
    u.appendChild(utterance);
    u.appendChild(source);
    tr.appendChild(u);
    tr.appendChild(sideCell(row.old, "old", row.kind));
    tr.appendChild(sideCell(row.new, "new", row.kind));
    tr.onclick = () => { S.selected = row; renderDetail(st); };
    body.appendChild(tr);
  }
  $("summary").textContent = st.matrix.length
    ? fmt("summary", st.meta) + " · " + st.strings.tap_hint
    : "";
}

function renderLegend(st) {
  const title = $("legend-title");
  const body = $("legend-body");
  body.textContent = "";
  if (!st.legend.length) {
    title.textContent = st.strings.legend_empty;
    return;
  }
  title.textContent = fmt("legend_title",
    { count: st.legend.length, locale: st.meta.locale });
  for (const entry of st.legend) {
    const div = document.createElement("div");
    div.className = "legend-row";
    const name = document.createElement("span");
    name.className = "legend-name";
    name.textContent = shortIntent(entry.intent) || entry.intent;
    const rest = document.createElement("span");
    rest.className = "legend-rest";
    rest.textContent = entry.intent + (entry.sample ? " · " + entry.sample : "");
    div.appendChild(name);
    div.appendChild(rest);
    body.appendChild(div);
  }
}

function sideName(side, st) {
  if (!side) return st.strings.absent;
  if (side.status === "error") return st.strings.error;
  return side.selected_intent || "-";
}

function renderDetail(st) {
  const card = $("detail");
  const body = $("detail-body");
  if (!S.selected) { card.hidden = true; return; }
  const row = st.matrix.find((r) => r.utterance === S.selected.utterance
    && r.source === S.selected.source);
  if (!row) { S.selected = null; card.hidden = true; return; }
  card.hidden = false;
  $("detail-title").textContent =
    fmt("detail_title", { utterance: row.utterance, source: row.source });
  const side = row.new || row.old;
  // Slots arrive as {name, value} objects (the run schema stores them in the
  // shape SmapiClient.parse_profile_nlu_result emits); render the VALUE, and
  // any other shape falls back to JSON instead of "[object Object]".
  const slots = Object.entries(side.slots || {})
    .map(([k, v]) => k + ": " +
      (v && typeof v === "object" && "value" in v ? v.value : JSON.stringify(v)))
    .join(", ") || st.strings.detail_none;
  const lines = [
    [st.strings.detail_expected, row.expected_intent || st.strings.detail_none],
    [st.strings.before + " -> " + st.strings.after,
     sideName(row.old, st) + " -> " + sideName(row.new, st)],
    [st.strings.detail_slots, slots],
    [st.strings.detail_considered,
     (side.considered_intents || []).map(shortIntent).join(" · ")
       || st.strings.detail_none],
  ];
  // Each erroring side's failure text (the ask CLI's stderr rides the run
  // file's row error); rendered like the other lines, so it wraps anywhere
  // and is inserted as text, never HTML.
  for (const [label, s] of [[st.strings.before, row.old], [st.strings.after, row.new]]) {
    if (s && s.error) lines.push([label + " · " + st.strings.detail_error, s.error]);
  }
  body.textContent = "";
  for (const [key, value] of lines) {
    const p = document.createElement("p");
    p.className = "detail-line";
    const k = document.createElement("span");
    k.className = "k";
    k.textContent = key + ": ";
    p.appendChild(k);
    p.appendChild(document.createTextNode(value));
    body.appendChild(p);
  }
}

function renderNotices(st) {
  const readable = st.runs.filter((r) => !r.corrupt);
  if (!st.runs.length) {
    showNotice(fmt("no_runs", { locale: st.meta.locale }));
  } else if (readable.length < 2) {
    showNotice(st.strings.need_two);
  } else {
    $("notice").hidden = true;
  }
}

$("lang-btn").onclick = () => {
  S.lang = S.state.meta.lang === "it" ? "en" : "it";
  localStorage.setItem("nlu-lab-lang", S.lang);
  refresh();
};

$("legend-btn").onclick = () => {
  $("legend").hidden = !$("legend").hidden;
};

refresh();
