const state = {
  data: null,
  selected: new Set(),
  log: [],
};

const $ = (id) => document.getElementById(id);

function csrf() {
  return document.cookie.split(';').map(x => x.trim()).find(x => x.startsWith('cpumon_csrf='))?.split('=')[1] || '';
}

async function api(path, options = {}) {
  const method = options.method || 'GET';
  const headers = { ...(options.headers || {}) };
  if (method !== 'GET') headers['X-CSRF-Token'] = csrf();
  if (options.body && !headers['Content-Type']) headers['Content-Type'] = 'application/json';
  const response = await fetch(path, { ...options, headers });
  if (response.status === 401) {
    location.href = '/login';
    return null;
  }
  return response;
}

function wsUrl(path) {
  const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
  return `${proto}//${location.host}${path}`;
}

async function loadState() {
  const response = await api('/api/state');
  if (!response?.ok) return;
  render(await response.json());
}

function connectStateWs() {
  const ws = new WebSocket(wsUrl('/ws/state'));
  ws.onopen = () => $('wsState').textContent = 'state ws live';
  ws.onclose = () => {
    $('wsState').textContent = 'state ws reconnecting';
    setTimeout(connectStateWs, 1500);
  };
  ws.onmessage = (event) => {
    const msg = JSON.parse(event.data);
    if (msg.type === 'state') render(msg.state);
  };
}

function connectLogWs() {
  const ws = new WebSocket(wsUrl('/ws/log'));
  ws.onopen = () => $('wsLog').textContent = 'log ws live';
  ws.onclose = () => {
    $('wsLog').textContent = 'log ws reconnecting';
    setTimeout(connectLogWs, 1500);
  };
  ws.onmessage = (event) => {
    const msg = JSON.parse(event.data);
    if (msg.type === 'log') {
      state.log.push(msg.entry);
      state.log = state.log.slice(-120);
      renderLog();
    }
  };
}

function render(data) {
  state.data = data;
  const clients = data.clients || [];
  const pending = data.pendingApprovals || [];
  const offline = data.offlineClients || [];

  $('serverVersion').textContent = `v${data.serverVersion || ''}`;
  $('footerVersion').textContent = `v${data.serverVersion || ''}`;
  $('tokenValue').textContent = data.token || '----';
  $('statConn').textContent = clients.length;
  $('statPending').textContent = pending.length;
  $('statOffline').textContent = offline.length;
  $('connectedCount').textContent = clients.length;
  $('pendingCount').textContent = pending.length;
  $('offlineCount').textContent = offline.length;
  $('osFilterValue').textContent = data.osFilter || 'all';
  $('sortModeValue').textContent = data.sortMode || 'name';
  $('authCount').textContent = data.authenticatedClientCount ?? 0;
  $('connectionCount').textContent = data.connectionCount ?? 0;
  $('broadcastState').textContent = data.broadcastDisabled ? 'off' : 'on';
  $('selectionCount').textContent = `${(data.selectedMachineNames || []).length} selected`;
  state.selected = new Set(data.selectedMachineNames || []);

  renderClients(clients);
  renderOffline(offline);
  renderPending(pending);
  renderStage(data);
  if (Array.isArray(data.logEntries)) {
    state.log = data.logEntries.slice(-120);
    renderLog();
  }
}

function renderClients(clients) {
  const list = $('clientList');
  list.replaceChildren();
  $('emptyClients').style.display = clients.length ? 'none' : 'block';
  for (const client of clients) list.appendChild(clientCard(client));
}

function clientCard(client) {
  const tpl = $('clientTemplate').content.firstElementChild.cloneNode(true);
  const report = client.lastReport || client.report || {};
  const os = (client.osLabel || report.osVersion || '').toLowerCase();
  const linux = os.includes('linux') || (client.clientVersion || '').toLowerCase().includes('linux');
  const selected = state.selected.has(client.machineName);
  const expanded = !!client.isExpanded;
  const waiting = !!client.isWaitingForFirstReport && !hasReportData(report);
  tpl.classList.toggle('selected', selected);
  tpl.classList.toggle('expanded', expanded);
  tpl.classList.toggle('wait-card', waiting);
  tpl.classList.toggle('stale-card', client.isStale);
  tpl.classList.toggle('paw-card', client.isPaw);
  tpl.dataset.machine = client.machineName;
  tpl.querySelector('.expand-indicator').textContent = expanded ? '^' : 'v';

  const badge = tpl.querySelector('.os-badge');
  badge.textContent = linux ? 'L' : 'W';
  badge.classList.add(linux ? 'os-lnx' : 'os-win');
  tpl.querySelector('.name').textContent = client.displayName || client.machineName || '?';
  tpl.querySelector('.ip').textContent = `${client.ip || client.remote || ''} ${client.lastSeenText || ''}`.trim();
  const ver = tpl.querySelector('.ver');
  ver.textContent = client.clientVersion || '';
  ver.classList.toggle('outdated', !!client.isOutdated);

  const tag = tpl.querySelector('.state-tag');
  const text = client.isPaw ? 'PAW relay' : waiting ? 'awaiting report' : client.isStale ? 'stale' : 'connected';
  tag.classList.toggle('paw', client.isPaw);
  tag.classList.toggle('dead', false);
  tag.classList.toggle('wait', waiting);
  tag.classList.toggle('stale', client.isStale);
  tpl.querySelector('.state-text').textContent = text;

  metric(tpl, 'cpu', percent(report.totalLoadPercent), report.totalLoadPercent);
  metric(tpl, 'ram', ramText(report.ramUsedGB, report.ramTotalGB), ramPct(report.ramUsedGB, report.ramTotalGB));
  metric(tpl, 'temp', tempText(report.packageTemperatureC), report.packageTemperatureC);
  tpl.querySelector('.net .value').textContent = netText(report.netDownKBps, report.netUpKBps);

  tpl.querySelector('.card-body').replaceChildren(
    kv('OS', client.osLabel || report.osVersion || '?'),
    kv('CPU', report.cpuName || '?'),
    kv('RAM', ramText(report.ramUsedGB, report.ramTotalGB)),
    kv('Last seen', client.lastSeenText || '?'),
  );
  tpl.querySelector('.card-actions').replaceChildren(
    action('Select', () => post('/api/state/select', { machineNames: selected ? [...state.selected].filter(x => x !== client.machineName) : [...state.selected, client.machineName] })),
    action('PAW', () => post(`/api/clients/${encodeURIComponent(client.machineName)}/paw`)),
    action('Msg', () => sendMessage(client.machineName)),
    action('Restart', () => confirmAction('Restart client?', `/api/clients/${encodeURIComponent(client.machineName)}/restart`), 'warn'),
    action('Off', () => confirmAction('Shutdown client?', `/api/clients/${encodeURIComponent(client.machineName)}/shutdown`), 'danger'),
    action('Forget', () => confirmAction('Forget client?', `/api/clients/${encodeURIComponent(client.machineName)}/forget`), 'danger'),
  );
  tpl.addEventListener('click', (event) => {
    if (event.target.closest('button, a, input, select, textarea')) return;
    post(`/api/clients/${encodeURIComponent(client.machineName)}/expand`);
  });
  return tpl;
}

function renderOffline(items) {
  const list = $('offlineList');
  list.replaceChildren();
  for (const item of items) {
    const row = document.createElement('div');
    row.className = 'offline-row';
    row.innerHTML = `
      <span class="os-badge ${isLinux(item) ? 'os-lnx' : 'os-win'}">${isLinux(item) ? 'L' : 'W'}</span>
      <div class="name-block"><span class="name"></span><span class="ip"></span></div>
      <span>${escapeHtml(item.lastSeenText || '')}</span>
      <span class="ver">${escapeHtml(item.clientVersion || '')}</span>
      <span>${escapeHtml(item.mac || '- no MAC -')}</span>
      <div class="offline-actions"></div>`;
    row.querySelector('.name').textContent = item.displayName || item.machineName || '?';
    row.querySelector('.ip').textContent = item.ip || '';
    const actions = row.querySelector('.offline-actions');
    actions.append(
      action('Wake', () => post(`/api/offline/${encodeURIComponent(item.machineName)}/wake`), 'primary'),
      action('Set MAC', () => setMac(item.machineName)),
      action('Forget', () => confirmAction('Forget offline client?', `/api/offline/${encodeURIComponent(item.machineName)}/forget`), 'danger'),
    );
    list.appendChild(row);
  }
}

function renderPending(items) {
  const list = $('pendingList');
  list.replaceChildren();
  if (!items.length) {
    const empty = document.createElement('div');
    empty.className = 'empty-side';
    empty.textContent = 'no approvals waiting';
    list.appendChild(empty);
    return;
  }
  for (const item of items) {
    const card = document.createElement('div');
    card.className = 'pending-card';
    card.innerHTML = `<div class="name"></div><div class="meta"></div><div class="pending-actions"></div>`;
    card.querySelector('.name').textContent = item.machineName || '?';
    card.querySelector('.meta').textContent = `${item.ip || ''} ${item.clientVersion || ''} ${item.requestedText || ''}`.trim();
    card.querySelector('.pending-actions').append(
      action('Approve', () => post(`/api/pending/${encodeURIComponent(item.machineName)}/approve`), 'primary'),
      action('Reject', () => post(`/api/pending/${encodeURIComponent(item.machineName)}/reject`), 'danger'),
    );
    list.appendChild(card);
  }
}

function renderStage(data) {
  const panel = $('stagePanel');
  if (!data.availableUpdate && !data.stagedReleaseDir) {
    panel.textContent = 'No staged release';
    return;
  }
  panel.textContent = data.availableUpdate ? `Available: v${data.availableUpdate.version}` : 'Release staged';
}

function renderLog() {
  const pane = $('logPane');
  pane.replaceChildren();
  for (const entry of state.log.slice(-80)) {
    const row = document.createElement('div');
    row.className = `log-line ${logClass(entry.color)}`;
    row.innerHTML = `<span class="marker">.</span><span class="t"></span><span class="m"></span>`;
    row.querySelector('.t').textContent = time(entry.ts);
    row.querySelector('.m').textContent = entry.message || '';
    pane.appendChild(row);
  }
  pane.scrollTop = pane.scrollHeight;
}

function metric(root, cls, text, pct) {
  root.querySelector(`.${cls} .value`).textContent = text ?? '--';
  const bar = root.querySelector(`.${cls} .bar span`);
  const width = Number.isFinite(Number(pct)) ? Math.max(0, Math.min(100, Number(pct))) : 0;
  bar.style.width = `${width}%`;
  bar.className = width >= 90 ? 'crit' : width >= 75 ? 'warn' : '';
}

function kv(k, v) {
  const el = document.createElement('div');
  el.className = 'kv';
  el.innerHTML = `<span class="k"></span><span class="v"></span>`;
  el.querySelector('.k').textContent = k;
  el.querySelector('.v').textContent = v;
  return el;
}

function action(label, handler, kind = '') {
  const button = document.createElement('button');
  button.className = `a-btn ${kind}`;
  button.textContent = label;
  button.addEventListener('click', (event) => {
    event.stopPropagation();
    handler();
  });
  return button;
}

async function post(path, body = null) {
  const response = await api(path, { method: 'POST', body: body ? JSON.stringify(body) : undefined });
  if (response?.ok) await loadState();
}

function confirmAction(message, path) {
  if (confirm(message)) post(path);
}

async function sendMessage(machine) {
  const text = prompt(`Message to ${machine}`);
  if (text) await post(`/api/clients/${encodeURIComponent(machine)}/message`, { text });
}

async function setMac(machine) {
  const mac = prompt(`MAC address for ${machine}`);
  if (mac) await post(`/api/offline/${encodeURIComponent(machine)}/mac`, { mac });
}

function percent(v) {
  return Number.isFinite(Number(v)) ? `${Math.round(Number(v))}%` : '--';
}
function ramPct(used, total) {
  return Number(total) > 0 ? Number(used) / Number(total) * 100 : 0;
}
function ramText(used, total) {
  return Number(total) > 0 ? `${num(used)}/${num(total)}` : '--';
}
function tempText(v) {
  return Number.isFinite(Number(v)) ? `${Math.round(Number(v))}C` : '--';
}
function netText(down, up) {
  return `D ${num(down)} U ${num(up)}`;
}
function num(v) {
  return Number.isFinite(Number(v)) ? Number(v).toLocaleString(undefined, { maximumFractionDigits: 1 }) : '0';
}
function time(ts) {
  const d = ts ? new Date(ts) : new Date();
  return d.toLocaleTimeString([], { hour12: false });
}
function isLinux(item) {
  return `${item.osVersion || ''} ${item.clientVersion || ''}`.toLowerCase().includes('linux');
}
function hasReportData(report) {
  if (!report || typeof report !== 'object') return false;
  return !!report.machineName
    || !!report.osVersion
    || Number.isFinite(Number(report.totalLoadPercent))
    || Number.isFinite(Number(report.ramTotalGB))
    || Number.isFinite(Number(report.timestampUtcMs));
}
function logClass(color) {
  const c = (color || '').toLowerCase();
  if (c.includes('ff') || c.includes('d8')) return 'yel';
  if (c.includes('red') || c.includes('58')) return 'red';
  if (c.includes('3f') || c.includes('cyan')) return 'cyan';
  return 'grn';
}
function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>"']/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch]));
}

$('signOut').addEventListener('click', async () => {
  await api('/api/auth/logout', { method: 'POST' });
  location.href = '/login';
});
$('regenToken').addEventListener('click', async () => {
  const response = await api('/api/token/regenerate', { method: 'POST' });
  if (response?.ok) await loadState();
});
$('copyToken').addEventListener('click', () => navigator.clipboard?.writeText($('tokenValue').textContent || ''));
$('osFilter').addEventListener('click', async () => {
  const cur = state.data?.osFilter || 'all';
  const next = cur === 'all' ? 'windows' : cur === 'windows' ? 'linux' : 'all';
  await post('/api/state/filter/os', { value: next });
});
$('sortMode').addEventListener('click', async () => {
  const cur = state.data?.sortMode || 'name';
  await post('/api/state/filter/sort', { value: cur === 'name' ? 'os' : 'name' });
});
$('selectAll').addEventListener('click', () => post('/api/state/select', { machineNames: (state.data?.clients || []).map(c => c.machineName) }));
$('selectOutdated').addEventListener('click', () => post('/api/state/select', { machineNames: (state.data?.clients || []).filter(c => c.isOutdated).map(c => c.machineName) }));
$('clearSelection').addEventListener('click', () => post('/api/state/select', { machineNames: [] }));

loadState();
connectStateWs();
connectLogWs();
