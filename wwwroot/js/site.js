"use strict";

const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
const NS = "http://www.w3.org/2000/svg";
let uid = 0;
let lastResult = null;
let selectedBranchId = null;
let dragNode = null;

const examples = {
  lecture: {
    nodes: ["1", "2", "3", "4"], reference: "4",
    branches: [
      ["a", "1", "2", 2, 10, 0], ["b", "2", "3", 4, 0, 0],
      ["c", "3", "1", 2, 0, 0], ["d", "1", "4", 5, 0, 0],
      ["e", "4", "2", 1, 0, 0], ["f", "3", "4", 2, 0, 1]
    ]
  },
  bridge: {
    nodes: ["A", "B", "C", "D"], reference: "D",
    branches: [
      ["R1", "A", "B", 10, 0, 0], ["R2", "B", "C", 20, 0, 0],
      ["R3", "A", "C", 15, 0, 0], ["R4", "B", "D", 30, 0, 0],
      ["R5", "C", "D", 25, 0, 0], ["Vs", "D", "A", 5, 12, 0]
    ]
  }
};

const state = { nodes: [], branches: [], reference: "" };

function makeNode(name, index, total) {
  const angle = -Math.PI / 2 + index * Math.PI * 2 / total;
  return { name, x: 360 + 245 * Math.cos(angle), y: 222 + 155 * Math.sin(angle) };
}

function loadExample(key) {
  const example = examples[key];
  state.nodes = example.nodes.map((name, i) => makeNode(name, i, example.nodes.length));
  state.branches = example.branches.map(([name, from, to, impedance, voltageSource, currentSource]) => ({
    id: `branch-${++uid}`, name, from, to, impedance, voltageSource, currentSource
  }));
  state.reference = example.reference;
  selectedBranchId = null;
  lastResult = null;
  renderAll();
  clearResults();
  analyze();
}

function renderAll() {
  renderBranchTable();
  renderReferenceOptions();
  renderTreeOptions();
  renderGraph();
  $("#requiredTwigs").textContent = Math.max(0, state.nodes.length - 1);
  $("#graphStats").textContent = `${state.nodes.length} nodes · ${state.branches.length} branches`;
}

function renderBranchTable() {
  const body = $("#branchTableBody");
  body.innerHTML = state.branches.map(branch => `<tr data-branch-row="${branch.id}" class="${branch.id === selectedBranchId ? "highlight" : ""}">
    <td><input class="branch-name" data-field="name" data-id="${branch.id}" value="${escapeHtml(branch.name)}" aria-label="Branch name"></td>
    <td><input data-field="from" data-id="${branch.id}" value="${escapeHtml(branch.from)}" aria-label="From node" list="nodeNames"></td>
    <td><input data-field="to" data-id="${branch.id}" value="${escapeHtml(branch.to)}" aria-label="To node" list="nodeNames"></td>
    <td><input class="number-input" type="number" min="0.000001" step="any" data-field="impedance" data-id="${branch.id}" value="${branch.impedance}" aria-label="Impedance in ohms"></td>
    <td><input class="number-input" type="number" step="any" data-field="voltageSource" data-id="${branch.id}" value="${branch.voltageSource}" aria-label="Voltage source in volts"></td>
    <td><input class="number-input" type="number" step="any" data-field="currentSource" data-id="${branch.id}" value="${branch.currentSource}" aria-label="Current source in amperes"></td>
    <td><button class="remove-branch" type="button" data-remove="${branch.id}" aria-label="Remove branch ${escapeHtml(branch.name)}">×</button></td>
  </tr>`).join("");
  let dataList = $("#nodeNames");
  if (!dataList) { dataList = document.createElement("datalist"); dataList.id = "nodeNames"; document.body.append(dataList); }
  dataList.innerHTML = state.nodes.map(node => `<option value="${escapeHtml(node.name)}"></option>`).join("");
}

function renderReferenceOptions() {
  const select = $("#referenceSelect");
  select.innerHTML = state.nodes.map(node => `<option value="${escapeHtml(node.name)}">${escapeHtml(node.name)}${node.name === state.reference ? " (0 V)" : ""}</option>`).join("");
  select.value = state.reference;
}

function renderTreeOptions() {
  $("#treeOptions").innerHTML = state.branches.map(branch => `<label><input type="checkbox" value="${branch.id}"><span>${escapeHtml(branch.name)}</span></label>`).join("");
}

function svgElement(tag, attributes = {}, text = "") {
  const element = document.createElementNS(NS, tag);
  Object.entries(attributes).forEach(([key, value]) => element.setAttribute(key, value));
  if (text) element.textContent = text;
  return element;
}

function renderGraph() {
  const edgeLayer = $("#edgeLayer");
  const nodeLayer = $("#nodeLayer");
  edgeLayer.replaceChildren(); nodeLayer.replaceChildren();
  const nodeMap = Object.fromEntries(state.nodes.map(node => [node.name, node]));
  const treeSet = new Set(lastResult?.summary.treeBranchIds || []);
  const pairCounts = {};
  state.branches.forEach(branch => {
    const pair = [branch.from, branch.to].sort().join("|");
    pairCounts[pair] = (pairCounts[pair] || 0) + 1;
  });
  const pairSeen = {};
  state.branches.forEach(branch => {
    const start = nodeMap[branch.from], end = nodeMap[branch.to];
    if (!start || !end) return;
    const pair = [branch.from, branch.to].sort().join("|");
    const index = pairSeen[pair] || 0; pairSeen[pair] = index + 1;
    const count = pairCounts[pair];
    const dx = end.x - start.x, dy = end.y - start.y;
    const length = Math.hypot(dx, dy) || 1;
    const ux = dx / length, uy = dy / length;
    const curve = count > 1 ? (index - (count - 1) / 2) * 55 : 0;
    const x1 = start.x + ux * 25, y1 = start.y + uy * 25;
    const x2 = end.x - ux * 29, y2 = end.y - uy * 29;
    const mx = (x1 + x2) / 2 - uy * curve, my = (y1 + y2) / 2 + ux * curve;
    const labelX = (x1 + 2 * mx + x2) / 4, labelY = (y1 + 2 * my + y2) / 4;
    const d = `M ${x1} ${y1} Q ${mx} ${my} ${x2} ${y2}`;
    const group = svgElement("g", { class: `edge-group${branch.id === selectedBranchId ? " selected" : ""}`, "data-edge": branch.id });
    group.append(svgElement("path", { class: `edge-path${treeSet.has(branch.id) ? " twig" : ""}`, d }));
    group.append(svgElement("path", { class: "edge-hit", d }));
    const labelWidth = Math.max(38, branch.name.length * 8 + 14);
    group.append(svgElement("rect", { class: "edge-label-bg", x: labelX - labelWidth / 2, y: labelY - 21, width: labelWidth, height: 40, rx: 2 }));
    group.append(svgElement("text", { class: "edge-label", x: labelX, y: labelY - 8 }, branch.name));
    group.append(svgElement("text", { class: "edge-value", x: labelX, y: labelY + 10 }, `${formatCompact(branch.impedance)} Ω`));
    group.addEventListener("click", () => selectBranch(branch.id));
    edgeLayer.append(group);
  });
  state.nodes.forEach(node => {
    const group = svgElement("g", { class: `graph-node${node.name === state.reference ? " reference" : ""}`, transform: `translate(${node.x} ${node.y})`, "data-node": node.name, tabindex: "0", role: "button", "aria-label": `Node ${node.name}. Press Enter to rename.` });
    group.append(svgElement("circle", { class: "node-circle", r: 22 }));
    group.append(svgElement("text", { class: "node-label", y: 1 }, node.name));
    if (node.name === state.reference) group.append(svgElement("text", { class: "reference-mark", y: 37 }, "REF · 0 V"));
    group.addEventListener("pointerdown", event => beginDrag(event, node));
    group.addEventListener("keydown", event => { if (event.key === "Enter") renameNode(node); });
    group.addEventListener("dblclick", () => renameNode(node));
    nodeLayer.append(group);
  });
  $("#treeLegend").hidden = !lastResult;
}

function selectBranch(id) {
  selectedBranchId = selectedBranchId === id ? null : id;
  $$("[data-branch-row]").forEach(row => row.classList.toggle("highlight", row.dataset.branchRow === selectedBranchId));
  $$("[data-edge]").forEach(edge => edge.classList.toggle("selected", edge.dataset.edge === selectedBranchId));
  if (selectedBranchId) $(`[data-branch-row="${selectedBranchId}"]`)?.scrollIntoView({ block: "nearest", behavior: "smooth" });
}

function beginDrag(event, node) {
  if (event.button !== 0) return;
  dragNode = { node, pointerId: event.pointerId };
  event.currentTarget.setPointerCapture(event.pointerId);
  event.preventDefault();
}

function renameNode(node) {
  const value = window.prompt("Node name", node.name);
  if (!value) return;
  const name = value.trim();
  if (!name || (name !== node.name && state.nodes.some(item => item.name.toLowerCase() === name.toLowerCase()))) {
    showMessage("Node names must be non-empty and unique.", "error"); return;
  }
  state.branches.forEach(branch => { if (branch.from === node.name) branch.from = name; if (branch.to === node.name) branch.to = name; });
  if (state.reference === node.name) state.reference = name;
  node.name = name; lastResult = null; renderAll(); clearResults();
}

function autoLayout() {
  state.nodes.forEach((node, i) => Object.assign(node, makeNode(node.name, i, state.nodes.length)));
  renderGraph();
}

async function analyze() {
  hideMessage();
  const button = $("#analyzeButton");
  const payload = {
    nodes: state.nodes.map(node => node.name), referenceNode: state.reference,
    branches: state.branches.map(branch => ({
      id: branch.id, name: branch.name, from: branch.from.trim(), to: branch.to.trim(),
      impedance: Number(branch.impedance), voltageSource: Number(branch.voltageSource), currentSource: Number(branch.currentSource)
    })),
    treeBranchIds: $("input[name='treeMode']:checked").value === "manual" ? $$("#treeOptions input:checked").map(input => input.value) : null
  };
  button.disabled = true; button.querySelector("span").textContent = "Analyzing…";
  try {
    const response = await fetch("/api/circuit/analyze", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload) });
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || "The circuit could not be analyzed.");
    lastResult = data;
    renderResults(data); renderGraph();
    showMessage("Analysis complete. KCL, KVL, and loop-current checks passed.", "success");
  } catch (error) {
    lastResult = null; clearResults(); renderGraph(); showMessage(error.message, "error");
  } finally {
    button.disabled = false; button.querySelector("span").textContent = "Analyze circuit";
  }
}

function clearResults() {
  $("#verificationBadge").className = "verification";
  $("#verificationBadge").innerHTML = "<span></span>Awaiting analysis";
  $("#resultSummary").innerHTML = `<div><span>Reference</span><strong>—</strong></div><div><span>Twigs</span><strong>—</strong></div><div><span>Links</span><strong>—</strong></div><div><span>KCL residual</span><strong>—</strong></div>`;
  $$(".tab-panel").forEach(panel => panel.innerHTML = `<div class="empty-results">Run the analysis to populate this view.</div>`);
}

function renderResults(data) {
  const { summary, verification } = data;
  $("#resultSummary").innerHTML = `<div><span>Reference</span><strong>${escapeHtml(summary.referenceNode)} = 0 V</strong></div><div><span>Twigs</span><strong>${summary.twigCount}</strong></div><div><span>Links</span><strong>${summary.linkCount}</strong></div><div><span>KCL residual</span><strong>${formatScientific(verification.kclResidual)}</strong></div>`;
  const badge = $("#verificationBadge");
  badge.className = `verification ${verification.passed ? "passed" : "failed"}`;
  badge.innerHTML = `<span></span>${verification.passed ? "Solution verified" : "Check residuals"}`;
  $("#panel-matrices").innerHTML = [
    matrixSection("Complete incidence matrix A", `${summary.nodeCount} × ${summary.branchCount} · original branch order`, data.incidence),
    matrixSection("Reduced incidence matrix Aᵣ", `${summary.twigCount} × ${summary.branchCount} · reference removed`, data.reducedIncidence),
    matrixSection("Fundamental tie-set matrix B", `${summary.linkCount} × ${summary.branchCount} · twigs → links`, data.tieSet),
    matrixSection("Fundamental cut-set matrix C", `${summary.twigCount} × ${summary.branchCount} · twigs → links`, data.cutSet)
  ].join("");
  $("#panel-electrical").innerHTML = `<div class="electrical-grid"><div><h3 class="electrical-title">Branch response</h3><div class="table-wrap"><table class="result-table"><thead><tr><th>Branch</th><th>J (A)</th><th>V (V)</th><th>Z (Ω)</th></tr></thead><tbody>${data.branchResults.map(branch => `<tr><td>${escapeHtml(branch.name)}<span class="type-tag">${branch.isTwig ? "TWIG" : "LINK"}</span></td><td>${formatNumber(branch.current)}</td><td>${formatNumber(branch.voltage)}</td><td>${formatNumber(branch.impedance)}</td></tr>`).join("")}</tbody></table></div></div><div><h3 class="electrical-title">Node potentials</h3><dl class="value-list">${data.nodeResults.map(node => `<div><dt>Node ${escapeHtml(node.name)}${node.isReference ? " (reference)" : ""}</dt><dd>${formatNumber(node.voltage)} V</dd></div>`).join("")}</dl></div></div>`;
  $("#panel-equations").innerHTML = `<div class="equation-grid"><section class="equation-block"><h3>Loop formulation</h3><div class="big-formula">BZB<sup>T</sup>I<sub>L</sub> = BE − BZI</div><dl class="value-list">${data.loopCurrents.map(item => `<div><dt>${escapeHtml(item.name)}</dt><dd>${formatNumber(item.value)} A</dd></div>`).join("")}</dl></section><section class="equation-block"><h3>Cut-set formulation</h3><div class="big-formula">CYC<sup>T</sup>E<sub>N</sub> = C(I − YE)</div><dl class="value-list">${data.twigVoltages.map(item => `<div><dt>${escapeHtml(item.name)}</dt><dd>${formatNumber(item.value)} V</dd></div>`).join("")}</dl></section></div><div class="check-list"><div><span>max |A · J<sub>b</sub>| / KCL</span><strong>${formatScientific(verification.kclResidual)}</strong></div><div><span>max |B · V<sub>b</sub>| / KVL</span><strong>${formatScientific(verification.kvlResidual)}</strong></div><div><span>max |J<sub>b</sub> − B<sup>T</sup>I<sub>L</sub>|</span><strong>${formatScientific(verification.currentResidual)}</strong></div></div>`;
}

function matrixSection(title, subtitle, matrix) {
  return `<section class="matrix-section"><div class="matrix-title"><h3>${title}</h3><span>${subtitle}</span></div><div class="matrix-scroll"><table class="matrix-table"><thead><tr><th></th>${matrix.columnLabels.map(label => `<th>${escapeHtml(label)}</th>`).join("")}</tr></thead><tbody>${matrix.values.map((row, i) => `<tr><th>${escapeHtml(matrix.rowLabels[i])}</th>${row.map(value => `<td class="${value > 0 ? "positive" : value < 0 ? "negative" : ""}">${formatMatrix(value)}</td>`).join("")}</tr>`).join("")}</tbody></table></div></section>`;
}

function showMessage(text, type) { const message = $("#message"); message.textContent = text; message.className = `message ${type}`; message.hidden = false; }
function hideMessage() { $("#message").hidden = true; }
function formatMatrix(value) { return Math.abs(value) < 1e-10 ? "0" : Number.isInteger(value) ? String(value) : formatNumber(value); }
function formatNumber(value) { if (!Number.isFinite(value)) return "—"; if (value !== 0 && (Math.abs(value) >= 1e5 || Math.abs(value) < 1e-4)) return value.toExponential(4); return value.toFixed(6).replace(/\.?0+$/, ""); }
function formatScientific(value) { return value === 0 ? "0" : value.toExponential(2); }
function formatCompact(value) { return Number(value).toLocaleString("en-US", { maximumFractionDigits: 3 }); }
function escapeHtml(value) { return String(value).replace(/[&<>'"]/g, char => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" })[char]); }

$("#addNodeButton").addEventListener("click", () => {
  if (state.nodes.length >= 30) { showMessage("The editor supports up to 30 nodes.", "error"); return; }
  let index = state.nodes.length + 1;
  while (state.nodes.some(node => node.name === `N${index}`)) index++;
  const node = makeNode(`N${index}`, state.nodes.length, state.nodes.length + 1);
  state.nodes.push(node); autoLayout(); renderAll(); lastResult = null; clearResults();
});
$("#addBranchButton").addEventListener("click", () => {
  if (state.nodes.length < 2) { showMessage("Add at least two nodes first.", "error"); return; }
  let index = state.branches.length + 1;
  while (state.branches.some(branch => branch.name === `b${index}`)) index++;
  state.branches.push({ id: `branch-${++uid}`, name: `b${index}`, from: state.nodes[0].name, to: state.nodes[1].name, impedance: 1, voltageSource: 0, currentSource: 0 });
  lastResult = null; renderAll(); clearResults();
});
$("#branchTableBody").addEventListener("input", event => {
  const input = event.target.closest("input[data-field]"); if (!input) return;
  const branch = state.branches.find(item => item.id === input.dataset.id); if (!branch) return;
  branch[input.dataset.field] = ["impedance", "voltageSource", "currentSource"].includes(input.dataset.field) ? input.valueAsNumber : input.value;
  if (["from", "to", "name", "impedance"].includes(input.dataset.field)) { lastResult = null; renderGraph(); }
});
$("#branchTableBody").addEventListener("click", event => {
  const button = event.target.closest("button[data-remove]"); if (!button) return;
  state.branches = state.branches.filter(branch => branch.id !== button.dataset.remove);
  lastResult = null; renderAll(); clearResults();
});
$("#referenceSelect").addEventListener("change", event => { state.reference = event.target.value; lastResult = null; renderReferenceOptions(); renderGraph(); clearResults(); });
$("#exampleSelect").addEventListener("change", event => { if (event.target.value) { loadExample(event.target.value); event.target.value = ""; } });
$("#analyzeButton").addEventListener("click", analyze);
$("#autoLayoutButton").addEventListener("click", autoLayout);
$$("input[name='treeMode']").forEach(input => input.addEventListener("change", () => { $("#treePicker").hidden = input.value !== "manual" || !input.checked; lastResult = null; clearResults(); }));
$("#guideButton").addEventListener("click", () => $("#guideDialog").showModal());
$("#closeGuideButton").addEventListener("click", () => $("#guideDialog").close());
$("#guideDialog").addEventListener("click", event => { if (event.target === event.currentTarget) event.currentTarget.close(); });
$("#graphSvg").addEventListener("pointermove", event => {
  if (!dragNode) return;
  const svg = event.currentTarget, point = svg.createSVGPoint(); point.x = event.clientX; point.y = event.clientY;
  const local = point.matrixTransform(svg.getScreenCTM().inverse());
  dragNode.node.x = Math.max(30, Math.min(690, local.x)); dragNode.node.y = Math.max(30, Math.min(420, local.y)); renderGraph();
});
$("#graphSvg").addEventListener("pointerup", () => { dragNode = null; });
$("#graphSvg").addEventListener("pointercancel", () => { dragNode = null; });

function activateTab(tab) {
  $$("[role='tab']").forEach(button => { const active = button === tab; button.setAttribute("aria-selected", active); button.tabIndex = active ? 0 : -1; $(`#panel-${button.dataset.tab}`).hidden = !active; });
}
$$("[role='tab']").forEach(tab => {
  tab.addEventListener("click", () => activateTab(tab));
  tab.addEventListener("keydown", event => {
    if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
    event.preventDefault(); const tabs = $$("[role='tab']"); let index = tabs.indexOf(tab);
    index = event.key === "Home" ? 0 : event.key === "End" ? tabs.length - 1 : (index + (event.key === "ArrowRight" ? 1 : -1) + tabs.length) % tabs.length;
    activateTab(tabs[index]); tabs[index].focus();
  });
});

clearResults();
loadExample("lecture");
