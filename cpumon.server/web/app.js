const state = {
  data: null,
  selected: new Set(),
  log: [],
  ws: { state: false, log: false },
};

const $ = (id) => document.getElementById(id);

const LogColors = {
  '#50DC8C': { cls: 'grn',  glyph: '+' },
  '#FFDC50': { cls: 'yel',  glyph: '●' },
  '#FFB43C': { cls: 'org',  glyph: '⚠' },
  '#FF5050': { cls: 'red',  glyph: '!' },
  '#50DCF0': { cls: 'cyan', glyph: '»' },
  '#C878FF': { cls: 'mag',  glyph: '◆' },
  '#8C8C9B': { cls: 'dim',  glyph: '·' },
};

let activeModalClose = null;

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
  ws.onopen = () => setWs('state', true);
  ws.onclose = () => { setWs('state', false); setTimeout(connectStateWs, 1500); };
  ws.onmessage = (event) => {
    const msg = JSON.parse(event.data);
    if (msg.type === 'state') render(msg.state);
  };
}

function connectLogWs() {
  const ws = new WebSocket(wsUrl('/ws/log'));
  ws.onopen = () => setWs('log', true);
  ws.onclose = () => { setWs('log', false); setTimeout(connectLogWs, 1500); };
  ws.onmessage = (event) => {
    const msg = JSON.parse(event.data);
    if (msg.type === 'log') {
      state.log.push(normalizeLog(msg.entry));
      state.log = state.log.slice(-120);
      renderLog();
    }
  };
}

function setWs(kind, ok) {
  state.ws[kind] = ok;
  const live = state.ws.state && state.ws.log;
  $('segListen').classList.toggle('ok', live);
  $('segListen').classList.toggle('warn', !live);
  $('segListenText').textContent = live ? 'listening' : 'reconnecting';
}

function normalizeLog(entry) {
  return {
    ts: entry.ts || entry.time || null,
    message: entry.message || '',
    color: (entry.color || argbToHex(entry.colorArgb) || '').toUpperCase(),
  };
}

function argbToHex(argb) {
  if (!Number.isFinite(argb)) return '';
  const r = (argb >> 16) & 0xff;
  const g = (argb >> 8) & 0xff;
  const b = argb & 0xff;
  return `#${[r, g, b].map(v => v.toString(16).padStart(2, '0')).join('').toUpperCase()}`;
}

function render(data) {
  state.data = data;
  const clients = data.clients || [];
  const pending = data.pendingApprovals || [];
  const offline = data.offlineClients || [];

  const version = data.serverVersion ? `v${data.serverVersion}` : '';
  $('serverVersion').textContent = version;
  $('footerVersion').textContent = version || '?';
  $('tokenValue').textContent = data.token || '----';
  $('statConn').textContent = clients.length;
  $('statPending').textContent = pending.length;
  $('statOffline').textContent = offline.length;
  $('statPendingWrap').classList.toggle('pulse', pending.length > 0);
  $('connectedCount').textContent = clients.length;
  $('pendingCount').textContent = pending.length;
  $('offlineCount').textContent = offline.length;
  $('osFilterValue').textContent = data.osFilter || 'all';
  $('sortModeValue').textContent = data.sortMode || 'name';
  $('authCount').textContent = data.authenticatedClientCount ?? clients.length;
  $('connectionCount').textContent = data.connectionCount ?? 0;
  $('broadcastState').textContent = data.broadcastDisabled ? 'off' : 'on';
  $('alertsState').textContent = data.alertsConfigured ? 'configured' : 'off';
  $('segBroadcast').textContent = data.broadcastDisabled ? 'off' : 'on';
  $('segAuthCount').textContent = data.authenticatedClientCount ?? clients.length;
  $('viewportLabel').textContent = (clients.length + pending.length + offline.length) > 0
    ? 'populated' : 'empty · first run';

  state.selected = new Set(data.selectedMachineNames || []);
  $('selectionCount').innerHTML = '';
  const c1 = countFragment(clients.length, 'connected'), c2 = countFragment(offline.length, 'offline'),
        c3 = countFragment(state.selected.size, 'selected');
  $('selectionCount').append(c1, sep(), c2, sep(), c3);

  renderClients(clients);
  renderOffline(offline);
  renderPending(pending);
  renderStage(data);
  renderUpdateCta(data);
  if (Array.isArray(data.logEntries)) {
    state.log = data.logEntries.map(normalizeLog).slice(-120);
    renderLog();
  }
}

function countFragment(n, label) {
  const span = document.createElement('span');
  const s = document.createElement('strong');
  s.textContent = String(n).padStart(2, '0');
  span.append(s, ` ${label}`);
  return span;
}
function sep() {
  const s = document.createElement('span');
  s.textContent = ' · ';
  return s;
}

function renderClients(clients) {
  const list = $('clientList');
  $('emptyClients').style.display = clients.length ? 'none' : 'block';
  const seen = new Set();

  for (let i = 0; i < clients.length; i++) {
    const client = clients[i];
    const sig = cardSignature(client);
    seen.add(client.machineName);
    let node = list.children[i];

    if (node?.dataset.machine === client.machineName && (node.dataset.sig === sig || (node.matches(':hover') && node.dataset.stableSig === stableCardSignature(client))))
      continue;

    const existing = [...list.children].find(el => el.dataset.machine === client.machineName);
    if (existing && (existing.dataset.sig === sig || (existing.matches(':hover') && existing.dataset.stableSig === stableCardSignature(client)))) {
      list.insertBefore(existing, node || null);
      continue;
    }

    const fresh = clientCard(client);
    fresh.dataset.sig = sig;
    fresh.dataset.stableSig = stableCardSignature(client);
    if (existing) existing.replaceWith(fresh);
    else list.insertBefore(fresh, node || null);
  }

  for (const child of [...list.children])
    if (!seen.has(child.dataset.machine))
      child.remove();
}

function clientCard(client) {
  const tpl = $('clientTemplate').content.firstElementChild.cloneNode(true);
  const report = client.lastReport || client.report || {};
  const linux = !!client.isLinux || (client.osLabel || '').toLowerCase().includes('linux');
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
  tpl.querySelector('.ip').textContent = composeIpLine(client, report);
  const ver = tpl.querySelector('.ver');
  ver.querySelector('.num').textContent = client.clientVersion || '';
  ver.classList.toggle('outdated', !!client.isOutdated);

  const tag = tpl.querySelector('.state-tag');
  const text = client.isPaw ? 'PAW relay' : waiting ? 'awaiting report' : client.isStale ? 'stale' : 'connected';
  tag.classList.toggle('paw', client.isPaw);
  tag.classList.toggle('wait', waiting);
  tag.classList.toggle('stale', client.isStale && !client.isPaw);
  tpl.querySelector('.state-text').textContent = text;

  metricCpu(tpl, report.totalLoadPercent);
  metricRam(tpl, report.ramUsedGB, report.ramTotalGB);
  metricTemp(tpl, report.packageTemperatureC);
  metricNet(tpl, report.netDownKBps, report.netUpKBps);

  tpl.querySelector('.card-body').replaceChildren(...buildCardBody(client, report));
  tpl.querySelector('.card-actions').replaceChildren(...buildCardActions(client, selected));
  tpl.addEventListener('click', (event) => {
    if (event.target.closest('button, a, input, select, textarea')) return;
    post(`/api/clients/${encodeURIComponent(client.machineName)}/expand`);
  });
  return tpl;
}

function buildCardBody(client, report) {
  const body = [
    kv('OS', client.osLabel || report.osVersion || '?'),
    kv('CPU', report.cpuName || '?'),
    kv('Cores', report.coreCount ? `${report.coreCount}` : '?'),
    kv('RAM', ramText(report.ramUsedGB, report.ramTotalGB)),
  ];
  const drives = Array.isArray(report.drives) ? report.drives : [];
  if (drives.length) body.push(buildDrives(drives));
  return body;
}

function buildDrives(drives) {
  const wrap = document.createElement('div');
  wrap.className = 'drives';
  for (const d of drives) {
    const total = Number(d.totalGB) || 0;
    const free = Number(d.freeGB) || 0;
    const used = Math.max(0, total - free);
    const pct = total > 0 ? Math.min(100, Math.round((used / total) * 100)) : 0;
    const cls = pct >= 90 ? 'crit' : pct >= 75 ? 'warn' : '';
    const row = document.createElement('div');
    row.className = 'drive';
    row.innerHTML = `<span class="letter"></span><span class="size"></span><span class="pct"></span><div class="bar"><span></span></div>`;
    row.querySelector('.letter').textContent = d.name || '?';
    row.querySelector('.size').textContent = `${fmtGB(used)} / ${fmtGB(total)}`;
    row.querySelector('.pct').textContent = `${pct}%`;
    const bar = row.querySelector('.bar span');
    bar.style.width = `${pct}%`;
    if (cls) bar.classList.add(cls);
    wrap.appendChild(row);
  }
  return wrap;
}

function buildCardActions(client, selected) {
  const m = encodeURIComponent(client.machineName);
  const inspect = [
    action('Procs', () => openProcsDialog(client.machineName)),
    action('Info',  () => openSysInfoDialog(client.machineName)),
  ];
  if (client.canServices)  inspect.push(action('Services',   () => openServicesDialog(client.machineName)));
  if (client.canEvents)    inspect.push(action('Events',     () => openEventsDialog(client.machineName)));
  if (client.canCpuDetail) inspect.push(action('CPU detail', () => openCpuDetailDialog(client.machineName)));

  const interact = [
    action('PAW', () => post(`/api/clients/${m}/paw`)),
    action('Msg', () => sendMessage(client.machineName)),
  ];

  const manage = [
    action(selected ? 'Deselect' : 'Select', () => post('/api/state/select', {
      machineNames: selected
        ? [...state.selected].filter(x => x !== client.machineName)
        : [...state.selected, client.machineName],
    })),
    action('Restart',  () => confirmAction('Restart client?',  `/api/clients/${m}/restart`),  'warn'),
    action('Shutdown', () => confirmAction('Shutdown client?', `/api/clients/${m}/shutdown`), 'danger'),
    action('Forget',   () => confirmAction('Forget client?',   `/api/clients/${m}/forget`),   'danger'),
  ];

  return [
    actionGroup('Inspect',  inspect),
    actionGroup('Interact', interact),
    actionGroup('Manage',   manage),
  ];
}

function actionGroup(label, buttons) {
  const wrap = document.createElement('div');
  wrap.className = 'actions-group';
  const lbl = document.createElement('span');
  lbl.className = 'lbl';
  lbl.textContent = label;
  wrap.append(lbl, ...buttons);
  return wrap;
}

function composeIpLine(client, report) {
  const ip = client.ip || client.remote || '';
  const seen = lastSeenText(report);
  if (ip && seen) return `${ip} · ${seen}`;
  return ip || seen || '';
}

function lastSeenText(report) {
  if (!report) return '';
  const ms = Number(report.timestampUtcMs);
  if (!Number.isFinite(ms) || ms <= 0) return '';
  return `seen ${timeAgo(ms)}`;
}

function cardSignature(client) {
  return JSON.stringify({
    client,
    selected: state.selected.has(client.machineName),
  });
}

function stableCardSignature(client) {
  return JSON.stringify({
    machineName: client.machineName,
    displayName: client.displayName,
    clientVersion: client.clientVersion,
    isExpanded: client.isExpanded,
    isStale: client.isStale,
    isWaitingForFirstReport: client.isWaitingForFirstReport,
    isOutdated: client.isOutdated,
    isPaw: client.isPaw,
    sendMode: client.sendMode,
    selected: state.selected.has(client.machineName),
  });
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
      <span class="seen">seen <span class="ago"></span></span>
      <span class="ver"></span>
      <span class="mac"></span>
      <div class="offline-actions"></div>`;
    row.querySelector('.name').textContent = item.displayName || item.machineName || '?';
    row.querySelector('.ip').textContent = item.ip ? `last ${item.ip}` : '';
    row.querySelector('.ago').textContent = item.seen ? timeAgo(item.seen) : '--';
    row.querySelector('.ver').textContent = item.clientVersion || '';
    row.querySelector('.mac').textContent = item.mac || '— no MAC —';
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
    const meta = [item.ip, item.clientVersion, item.requestedAt ? timeAgo(item.requestedAt) : null].filter(Boolean).join(' · ');
    card.querySelector('.meta').textContent = meta;
    card.querySelector('.pending-actions').append(
      action('Approve', () => post(`/api/pending/${encodeURIComponent(item.machineName)}/approve`), 'primary'),
      action('Reject', () => post(`/api/pending/${encodeURIComponent(item.machineName)}/reject`), 'danger'),
    );
    list.appendChild(card);
  }
}

function renderStage(data) {
  const panel = $('stagePanel');
  panel.replaceChildren();
  panel.classList.remove('ready');
  if (data.stagedReleaseDir && data.availableUpdate) {
    panel.classList.add('ready');
    const head = document.createElement('div');
    head.textContent = `▣ v${data.availableUpdate.version} ready`;
    const meta = document.createElement('div');
    meta.className = 'meta';
    meta.textContent = 'cpumon-server · cpumon-client · cpumon-linux';
    panel.append(head, meta);
    return;
  }
  if (data.availableUpdate) {
    const head = document.createElement('div');
    head.textContent = `↑ v${data.availableUpdate.version} downloading`;
    const meta = document.createElement('div');
    meta.className = 'meta';
    meta.textContent = 'staging release…';
    panel.append(head, meta);
    return;
  }
  panel.textContent = 'No staged release';
}

function renderUpdateCta(data) {
  const btn = $('updateCta');
  if (!data.availableUpdate) {
    btn.hidden = true;
    btn.onclick = null;
    return;
  }
  btn.hidden = false;
  btn.textContent = `▣ v${data.availableUpdate.version} ready · notes`;
  btn.onclick = () => {
    const url = data.availableUpdate.releaseUrl;
    if (url) window.open(url, '_blank', 'noopener');
  };
}

function renderLog() {
  const pane = $('logPane');
  pane.replaceChildren();
  for (const entry of state.log.slice(-80)) {
    const meta = LogColors[entry.color] || { cls: 'dim', glyph: '·' };
    const row = document.createElement('div');
    row.className = `log-line ${meta.cls}`;
    row.innerHTML = `<span class="marker"></span><span class="t"></span><span class="m"></span>`;
    row.querySelector('.marker').textContent = meta.glyph;
    row.querySelector('.t').textContent = time(entry.ts);
    row.querySelector('.m').textContent = entry.message || '';
    pane.appendChild(row);
  }
  pane.scrollTop = pane.scrollHeight;
}

function metricCpu(root, pct) {
  const v = root.querySelector('.cpu .value');
  const bar = root.querySelector('.cpu .bar span');
  if (Number.isFinite(Number(pct))) {
    const n = Math.round(Number(pct));
    v.classList.remove('dim');
    v.innerHTML = '';
    v.append(String(n));
    const u = document.createElement('span');
    u.className = 'unit';
    u.textContent = '%';
    v.append(u);
    setBar(bar, n);
  } else {
    v.classList.add('dim');
    v.textContent = '—';
    setBar(bar, 0);
  }
}

function metricRam(root, used, total) {
  const v = root.querySelector('.ram .value');
  const bar = root.querySelector('.ram .bar span');
  if (Number(total) > 0) {
    v.classList.remove('dim');
    v.textContent = `${num(used)}/${num(total)}`;
    setBar(bar, (Number(used) / Number(total)) * 100);
  } else {
    v.classList.add('dim');
    v.textContent = '—';
    setBar(bar, 0);
  }
}

function metricTemp(root, c) {
  const v = root.querySelector('.temp .value');
  const bar = root.querySelector('.temp .bar span');
  if (Number.isFinite(Number(c)) && Number(c) > 0) {
    const n = Math.round(Number(c));
    v.classList.remove('dim');
    v.textContent = `${n}°`;
    setBar(bar, Math.min(100, (n / 100) * 100));
  } else {
    v.classList.add('dim');
    v.textContent = '—';
    setBar(bar, 0);
  }
}

function metricNet(root, down, up) {
  const v = root.querySelector('.net .value');
  if (Number.isFinite(Number(down)) || Number.isFinite(Number(up))) {
    v.classList.remove('dim');
    v.innerHTML = '';
    const da = document.createElement('span'); da.className = 'arrow'; da.textContent = '↓';
    const ua = document.createElement('span'); ua.className = 'arrow'; ua.textContent = '↑';
    v.append(da, ` ${num(kbpsToMbps(down))} `, ua, ` ${num(kbpsToMbps(up))}`);
  } else {
    v.classList.add('dim');
    v.textContent = '—';
  }
}

function setBar(bar, pct) {
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
  button.className = `a-btn ${kind}`.trim();
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

function num(v) {
  return Number.isFinite(Number(v)) ? Number(v).toLocaleString(undefined, { maximumFractionDigits: 1 }) : '0';
}
function fmtGB(gb) {
  const n = Number(gb);
  if (!Number.isFinite(n)) return '?';
  if (n >= 1024) return `${(n / 1024).toLocaleString(undefined, { maximumFractionDigits: 1 })} TB`;
  return `${Math.round(n)} GB`;
}
function kbpsToMbps(kb) {
  const n = Number(kb);
  if (!Number.isFinite(n)) return 0;
  return n / 1024;
}
function time(ts) {
  const d = ts ? new Date(ts) : new Date();
  return d.toLocaleTimeString([], { hour12: false });
}
function timeAgo(input) {
  const ts = typeof input === 'number' ? input : Date.parse(input);
  if (!Number.isFinite(ts)) return '?';
  const seconds = Math.max(0, Math.round((Date.now() - ts) / 1000));
  if (seconds < 60) return `${seconds}s ago`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 48) return `${hours}h ago`;
  const days = Math.round(hours / 24);
  return `${days}d ago`;
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
$('btnAlerts').addEventListener('click', () => openAlertsDialog());
$('btnApproved').addEventListener('click', () => openApprovedDialog());
$('btnInstall').addEventListener('click', () => {
  alert('Install link generator is coming in a later slice.');
});

// ── modal substrate ──────────────────────────────────────────────
function openModal({ title, mount, size = 'wide' }) {
  closeAllModals();
  const overlay = document.createElement('div');
  overlay.className = 'modal-overlay';
  overlay.innerHTML = `<div class="modal modal-${size}" role="dialog" aria-modal="true">
    <header class="modal-head">
      <h2 class="modal-title"></h2>
      <button class="modal-close" type="button" aria-label="Close">×</button>
    </header>
    <div class="modal-body"></div>
  </div>`;
  overlay.querySelector('.modal-title').textContent = title;
  const body = overlay.querySelector('.modal-body');
  const cleanups = [];
  let closed = false;
  const ctx = {
    overlay,
    body,
    modal: overlay.querySelector('.modal'),
    onClose: (fn) => cleanups.push(fn),
    close: () => {
      if (closed) return;
      closed = true;
      for (const fn of cleanups) try { fn(); } catch {}
      cleanups.length = 0;
      if (activeModalClose === ctx.close) activeModalClose = null;
      overlay.remove();
    },
  };
  const onKey = (e) => { if (e.key === 'Escape') ctx.close(); };
  ctx.onClose(() => document.removeEventListener('keydown', onKey));
  overlay.querySelector('.modal-close').addEventListener('click', ctx.close);
  overlay.addEventListener('click', (e) => { if (e.target === overlay) ctx.close(); });
  document.addEventListener('keydown', onKey);
  $('modalRoot').appendChild(overlay);
  activeModalClose = ctx.close;
  mount(body, ctx);
  return ctx;
}

function closeAllModals() {
  if (activeModalClose) {
    activeModalClose();
    return;
  }
  const root = $('modalRoot');
  if (root) root.replaceChildren();
}

function modalFoot(ctx, ...children) {
  const foot = document.createElement('div');
  foot.className = 'modal-foot';
  foot.append(...children);
  ctx.modal.appendChild(foot);
  return foot;
}

function footStatus(text = '') {
  const span = document.createElement('span');
  span.className = 'left';
  span.textContent = text;
  return span;
}

// ── snapshot polling ─────────────────────────────────────────────
function pollSnapshot(path, intervalMs, onData, onError) {
  let stopped = false;
  let timer = null;
  async function tick() {
    if (stopped) return;
    try {
      const response = await api(path);
      if (stopped) return;
      if (response?.status === 204) {
        // server triggered an agent fetch; data not back yet
      } else if (response?.ok) {
        const json = await response.json();
        onData(json.snapshot, json.receivedAt);
      } else if (response?.status === 404) {
        onError?.('agent disconnected');
        stopped = true;
        return;
      } else if (response) {
        onError?.(`error ${response.status}`);
      }
    } catch { /* network error; keep retrying */ }
    if (!stopped) timer = setTimeout(tick, intervalMs);
  }
  tick();
  return () => { stopped = true; if (timer) clearTimeout(timer); };
}

// ── procs dialog ─────────────────────────────────────────────────
function openProcsDialog(machine) {
  openModal({
    title: `Processes · ${machine}`,
    mount: (body, ctx) => {
      body.classList.add('flush');
      body.innerHTML = `
        <div class="modal-toolbar">
          <input class="modal-filter" type="search" placeholder="Filter by name…" autocomplete="off">
          <span class="modal-status">Fetching…</span>
        </div>
        <div class="modal-scroll"><table class="modal-table">
          <thead><tr>
            <th class="right">PID</th><th>Name</th>
            <th class="right">CPU%</th><th class="right">RAM</th>
          </tr></thead>
          <tbody></tbody>
        </table></div>`;
      const filter = body.querySelector('.modal-filter');
      const status = body.querySelector('.modal-status');
      const tbody  = body.querySelector('tbody');
      let lastList = [];
      let filterText = '';
      function applyFilter() {
        const sorted = [...lastList].sort((a, b) => (b.cpu || 0) - (a.cpu || 0));
        const visible = filterText
          ? sorted.filter(p => (p.name || '').toLowerCase().includes(filterText))
          : sorted;
        tbody.replaceChildren(...visible.slice(0, 500).map(rowProc));
        return visible.length;
      }
      filter.addEventListener('input', () => {
        filterText = filter.value.toLowerCase();
        const n = applyFilter();
        status.textContent = `${n}/${lastList.length} processes`;
      });
      const stop = pollSnapshot(
        `/api/clients/${encodeURIComponent(machine)}/processes`,
        1000,
        (snap, receivedAt) => {
          lastList = Array.isArray(snap) ? snap : [];
          const n = applyFilter();
          status.textContent = filterText
            ? `${n}/${lastList.length} processes · updated ${timeAgo(receivedAt)}`
            : `${lastList.length} processes · updated ${timeAgo(receivedAt)}`;
          status.classList.remove('err');
        },
        (msg) => { status.textContent = msg; status.classList.add('err'); },
      );
      ctx.onClose(stop);
      setTimeout(() => filter.focus(), 0);
    },
  });
}
function rowProc(p) {
  const tr = document.createElement('tr');
  tr.innerHTML = `<td class="right mono"></td><td></td><td class="right mono"></td><td class="right mono"></td>`;
  tr.children[0].textContent = p.pid ?? '';
  tr.children[1].textContent = p.name || '';
  tr.children[2].textContent = Number.isFinite(Number(p.cpu)) ? Number(p.cpu).toFixed(1) : '';
  tr.children[3].textContent = formatBytes(p.mem);
  if (p.title) tr.title = p.title;
  return tr;
}

// ── sysinfo dialog ───────────────────────────────────────────────
function openSysInfoDialog(machine) {
  openModal({
    title: `System info · ${machine}`,
    mount: (body, ctx) => {
      body.classList.add('flush');
      body.innerHTML = `
        <div class="modal-toolbar">
          <span class="modal-status">Fetching…</span>
        </div>
        <div class="modal-scroll" style="padding: 12px 16px;">
          <div class="sysinfo-grid"></div>
        </div>`;
      const grid = body.querySelector('.sysinfo-grid');
      const status = body.querySelector('.modal-status');
      const stop = pollSnapshot(
        `/api/clients/${encodeURIComponent(machine)}/sysinfo`,
        5000,
        (snap, receivedAt) => { renderSysInfo(grid, snap); status.textContent = `updated ${timeAgo(receivedAt)}`; status.classList.remove('err'); },
        (msg) => { status.textContent = msg; status.classList.add('err'); },
      );
      ctx.onClose(stop);
    },
  });
}
function renderSysInfo(grid, info) {
  const ram = Number(info.ramTotalGB) > 0
    ? `${(Number(info.ramTotalGB) - Number(info.ramAvailGB || 0)).toFixed(1)} / ${Number(info.ramTotalGB).toFixed(1)} GB`
    : '?';
  const sections = [
    ['OS', [
      ['Hostname',  info.hostname || '?'],
      ['Domain',    info.domain || '—'],
      ['Name',      info.osName || '?'],
      ['Build',     info.osBuild || '?'],
      ['User',      info.userName || '?'],
      ['Uptime',    formatUptime(info.uptimeHours)],
      ['.NET',      info.dotnetVersion || '?'],
    ]],
    ['Hardware', [
      ['CPU',       info.cpuName || '?'],
      ['Cores',     `${info.cpuCores || '?'} cores · ${info.cpuThreads || '?'} threads`],
      ['RAM',       ram],
      ['GPU',       info.gpuName || '—'],
    ]],
    ['Network', [
      ['IP',        (info.ipAddresses || []).join(', ') || '?'],
      ['MAC',       (info.macAddresses || []).join(', ') || '?'],
    ]],
    ['Storage',   (info.disks || []).map(d => [
      d.name + (d.label ? ` (${d.label})` : ''),
      `${Number(d.freeGB).toFixed(0)} GB free of ${Number(d.totalGB).toFixed(0)} GB · ${d.format || '?'}`,
    ])],
  ];
  grid.replaceChildren(...sections.map(([title, rows]) => sysinfoSection(title, rows)));
}
function sysinfoSection(title, rows) {
  const wrap = document.createElement('div');
  wrap.className = 'sysinfo-section';
  const h = document.createElement('h4');
  h.textContent = title;
  const list = document.createElement('div');
  list.className = 'kv-list';
  for (const [k, v] of rows) {
    const item = document.createElement('div');
    item.className = 'kv';
    item.innerHTML = `<span class="k"></span><span class="v"></span>`;
    item.querySelector('.k').textContent = k;
    item.querySelector('.v').textContent = v;
    item.querySelector('.v').title = v;
    list.appendChild(item);
  }
  wrap.append(h, list);
  return wrap;
}

// ── services dialog ──────────────────────────────────────────────
function openServicesDialog(machine) {
  openModal({
    title: `Services · ${machine}`,
    mount: (body, ctx) => {
      body.classList.add('flush');
      body.innerHTML = `
        <div class="modal-toolbar">
          <input class="modal-filter" type="search" placeholder="Filter by name or display name…" autocomplete="off">
          <span class="modal-status">Fetching…</span>
        </div>
        <div class="modal-scroll"><table class="modal-table">
          <thead><tr><th>Name</th><th>Display</th><th>Status</th><th>Start</th></tr></thead>
          <tbody></tbody>
        </table></div>`;
      const filter = body.querySelector('.modal-filter');
      const status = body.querySelector('.modal-status');
      const tbody  = body.querySelector('tbody');
      let last = [];
      let text = '';
      function applyFilter() {
        const visible = text
          ? last.filter(s => (s.n || '').toLowerCase().includes(text) || (s.d || '').toLowerCase().includes(text))
          : last;
        tbody.replaceChildren(...visible.map(rowService));
        return visible.length;
      }
      filter.addEventListener('input', () => { text = filter.value.toLowerCase(); status.textContent = `${applyFilter()}/${last.length} services`; });
      const stop = pollSnapshot(
        `/api/clients/${encodeURIComponent(machine)}/services`,
        3000,
        (snap, receivedAt) => {
          last = Array.isArray(snap) ? snap : [];
          const n = applyFilter();
          status.textContent = text
            ? `${n}/${last.length} services · updated ${timeAgo(receivedAt)}`
            : `${last.length} services · updated ${timeAgo(receivedAt)}`;
          status.classList.remove('err');
        },
        (msg) => { status.textContent = msg; status.classList.add('err'); },
      );
      ctx.onClose(stop);
      setTimeout(() => filter.focus(), 0);
    },
  });
}
function rowService(s) {
  const tr = document.createElement('tr');
  tr.innerHTML = `<td></td><td></td><td></td><td></td>`;
  tr.children[0].textContent = s.n || '';
  tr.children[0].title       = s.n || '';
  tr.children[1].textContent = s.d || '';
  tr.children[1].title       = s.d || '';
  tr.children[2].textContent = s.s || '';
  tr.children[3].textContent = s.st || '';
  const status = (s.s || '').toLowerCase();
  if (status === 'running')      tr.children[2].classList.add('svc-running');
  else if (status === 'stopped') tr.children[2].classList.add('svc-stopped');
  else if (status)               tr.children[2].classList.add('svc-other');
  return tr;
}

// ── events dialog ────────────────────────────────────────────────
function openEventsDialog(machine) {
  openModal({
    title: `Events · ${machine}`,
    mount: (body, ctx) => {
      body.classList.add('flush');
      body.innerHTML = `
        <div class="modal-toolbar">
          <input class="modal-filter" type="search" placeholder="Filter by source or message…" autocomplete="off">
          <span class="modal-status">Fetching…</span>
        </div>
        <div class="modal-scroll"><table class="modal-table">
          <thead><tr><th>Time</th><th>Level</th><th>Source</th><th>Message</th></tr></thead>
          <tbody></tbody>
        </table></div>`;
      const filter = body.querySelector('.modal-filter');
      const status = body.querySelector('.modal-status');
      const tbody  = body.querySelector('tbody');
      let last = [];
      let text = '';
      function applyFilter() {
        const visible = text
          ? last.filter(e => (e.src || '').toLowerCase().includes(text) || (e.msg || '').toLowerCase().includes(text))
          : last;
        tbody.replaceChildren(...visible.flatMap(rowEvent));
        return visible.length;
      }
      filter.addEventListener('input', () => { text = filter.value.toLowerCase(); status.textContent = `${applyFilter()}/${last.length} events`; });
      const stop = pollSnapshot(
        `/api/clients/${encodeURIComponent(machine)}/events`,
        3000,
        (snap, receivedAt) => {
          last = Array.isArray(snap) ? snap : [];
          const n = applyFilter();
          status.textContent = text
            ? `${n}/${last.length} events · updated ${timeAgo(receivedAt)}`
            : `${last.length} events · updated ${timeAgo(receivedAt)}`;
          status.classList.remove('err');
        },
        (msg) => { status.textContent = msg; status.classList.add('err'); },
      );
      ctx.onClose(stop);
      setTimeout(() => filter.focus(), 0);
    },
  });
}
function rowEvent(e) {
  const tr = document.createElement('tr');
  tr.className = 'event-row';
  tr.innerHTML = `<td class="mono"></td><td></td><td></td><td></td>`;
  const cells = tr.children;
  cells[0].textContent = e.ts ? time(e.ts) : '';
  cells[1].textContent = e.level || '';
  cells[1].classList.add(`lvl-${(e.level || '').toLowerCase()}`);
  cells[2].textContent = e.src || '';
  cells[2].title       = e.src || '';
  cells[3].textContent = (e.msg || '').split('\n')[0];
  cells[3].title       = e.msg || '';
  const rows = [tr];
  if ((e.msg || '').includes('\n')) {
    const detail = document.createElement('tr');
    detail.className = 'event-detail';
    detail.hidden = true;
    detail.innerHTML = `<td colspan="4"></td>`;
    detail.firstElementChild.textContent = e.msg;
    tr.addEventListener('click', () => { detail.hidden = !detail.hidden; });
    rows.push(detail);
  }
  return rows;
}

// ── cpu detail dialog ────────────────────────────────────────────
function openCpuDetailDialog(machine) {
  openModal({
    title: `CPU detail · ${machine}`,
    mount: (body, ctx) => {
      body.classList.add('flush');
      body.innerHTML = `
        <div class="modal-toolbar"><span class="modal-status">Fetching…</span></div>
        <div class="modal-scroll" style="padding: 12px 16px;">
          <div class="cpu-package">
            <div><span class="k">CPU</span><span class="v" data-k="name">?</span></div>
            <div><span class="k">Load</span><span class="v" data-k="load">—</span></div>
            <div><span class="k">Temp</span><span class="v" data-k="temp">—</span></div>
            <div><span class="k">Power</span><span class="v" data-k="power">—</span></div>
          </div>
          <div class="cpu-cores"></div>
        </div>`;
      const status = body.querySelector('.modal-status');
      const pack   = body.querySelector('.cpu-package');
      const cores  = body.querySelector('.cpu-cores');
      const stop = pollSnapshot(
        `/api/clients/${encodeURIComponent(machine)}/cpu-detail`,
        1500,
        (snap, receivedAt) => {
          renderCpuDetail(pack, cores, snap);
          status.textContent = `updated ${timeAgo(receivedAt)}`;
          status.classList.remove('err');
        },
        (msg) => { status.textContent = msg; status.classList.add('err'); },
      );
      ctx.onClose(stop);
    },
  });
}
function renderCpuDetail(pack, cores, snap) {
  pack.querySelector('[data-k="name"]').textContent = snap.cpuName || '?';
  pack.querySelector('[data-k="load"]').textContent = Number.isFinite(snap.load)  ? `${Math.round(snap.load)}%` : '—';
  pack.querySelector('[data-k="temp"]').textContent = Number.isFinite(snap.temp)  ? `${Math.round(snap.temp)}°` : '—';
  pack.querySelector('[data-k="power"]').textContent = Number.isFinite(snap.power) ? `${snap.power.toFixed(1)} W` : '—';
  const list = (snap.cores || []).map(coreRow);
  cores.replaceChildren(...list);
}
function coreRow(c) {
  const row = document.createElement('div');
  row.className = 'cpu-core';
  row.innerHTML = `<span class="ix"></span><div class="bar"><span></span></div><span class="freq"></span><span class="temp"></span>`;
  row.querySelector('.ix').textContent = `core ${c.i ?? c.index ?? '?'}`;
  const load = Number.isFinite(c.l) ? Number(c.l) : (Number.isFinite(c.load) ? Number(c.load) : 0);
  const bar = row.querySelector('.bar span');
  bar.style.width = `${Math.max(0, Math.min(100, load))}%`;
  if (load >= 90) bar.classList.add('crit');
  else if (load >= 75) bar.classList.add('warn');
  const freq = Number.isFinite(c.f) ? c.f : c.freq;
  const temp = Number.isFinite(c.t) ? c.t : c.temp;
  row.querySelector('.freq').textContent = Number.isFinite(freq) ? `${Math.round(freq)} MHz` : '';
  row.querySelector('.temp').textContent = Number.isFinite(temp) ? `${Math.round(temp)}°` : '';
  return row;
}

// ── alerts dialog ────────────────────────────────────────────────
async function openAlertsDialog() {
  const response = await api('/api/alerts');
  if (!response?.ok) return;
  const cfg = await response.json();
  openModal({
    title: 'Alerts configuration',
    size: 'narrow',
    mount: (body, ctx) => {
      body.innerHTML = `
        <div class="form-grid">
          <div class="form-row"><label>SMTP host</label><input name="host"></div>
          <div class="form-row"><label>Port</label><input name="port" type="number" min="0" max="65535"></div>
          <div class="form-row">
            <label>Security</label>
            <select name="security">
              <option value="None">None</option>
              <option value="StartTls">StartTLS</option>
              <option value="Ssl">SMTPS</option>
            </select>
          </div>
          <div class="form-row"><label>Username</label><input name="username" autocomplete="username"></div>
          <div class="form-row wide">
            <label>Password</label>
            <input name="password" type="password" autocomplete="new-password" placeholder="">
            <span class="hint" data-k="passwordHint"></span>
          </div>
          <div class="form-row"><label>From address</label><input name="from"></div>
          <div class="form-row"><label>To address</label><input name="to"></div>
          <div class="form-row"><label>RAM threshold (%)</label><input name="ram" type="number" min="0" max="100"></div>
          <div class="form-row"><label>Disk threshold (%)</label><input name="disk" type="number" min="0" max="100"></div>
          <div class="form-row"><label>Temp threshold (°C)</label><input name="temp" type="number" step="0.1"></div>
          <div class="form-row"><label>Cooldown (minutes)</label><input name="cooldown" type="number" min="0"></div>
        </div>`;
      const errorEl = document.createElement('div');
      errorEl.className = 'form-error-line';
      body.appendChild(errorEl);

      const fields = body.querySelectorAll('[name]');
      const get = (n) => body.querySelector(`[name="${n}"]`);
      get('host').value     = cfg.host     || '';
      get('port').value     = cfg.port     ?? 587;
      get('security').value = cfg.security || 'StartTls';
      get('username').value = cfg.username || '';
      get('from').value     = cfg.from     || '';
      get('to').value       = cfg.to       || '';
      get('ram').value      = cfg.ram      ?? '';
      get('disk').value     = cfg.disk     ?? '';
      get('temp').value     = cfg.temp     ?? '';
      get('cooldown').value = cfg.cooldown ?? 30;
      body.querySelector('[data-k="passwordHint"]').textContent = cfg.passwordSet
        ? 'Stored. Leave blank to keep, type to replace, check Clear to remove.'
        : 'No password stored.';

      const clearWrap = document.createElement('label');
      clearWrap.style.display = 'inline-flex';
      clearWrap.style.gap = '6px';
      clearWrap.style.alignItems = 'center';
      clearWrap.style.color = 'var(--dim)';
      clearWrap.style.fontSize = '10px';
      clearWrap.style.letterSpacing = '0.14em';
      clearWrap.style.textTransform = 'uppercase';
      clearWrap.innerHTML = `<input type="checkbox" name="clearPassword"> clear password`;
      const passwordRow = get('password').closest('.form-row');
      passwordRow.appendChild(clearWrap);

      const testBtn = document.createElement('button');
      testBtn.className = 'a-btn';
      testBtn.textContent = 'Send test';
      const saveBtn = document.createElement('button');
      saveBtn.className = 'a-btn primary';
      saveBtn.textContent = 'Save';
      const cancelBtn = document.createElement('button');
      cancelBtn.className = 'a-btn';
      cancelBtn.textContent = 'Close';
      const status = footStatus('');
      modalFoot(ctx, status, testBtn, cancelBtn, saveBtn);
      cancelBtn.addEventListener('click', ctx.close);

      function collect() {
        return {
          host: get('host').value.trim() || null,
          port: parseInt(get('port').value, 10) || 587,
          security: get('security').value,
          username: get('username').value.trim() || null,
          password: get('password').value || null,
          clearPassword: body.querySelector('[name="clearPassword"]').checked,
          from: get('from').value.trim() || null,
          to: get('to').value.trim() || null,
          ram: get('ram').value === '' ? null : parseInt(get('ram').value, 10),
          disk: get('disk').value === '' ? null : parseInt(get('disk').value, 10),
          temp: get('temp').value === '' ? null : parseFloat(get('temp').value),
          cooldown: get('cooldown').value === '' ? 30 : parseInt(get('cooldown').value, 10),
        };
      }

      saveBtn.addEventListener('click', async () => {
        errorEl.textContent = '';
        status.textContent = 'Saving…';
        const r = await api('/api/alerts', { method: 'PUT', body: JSON.stringify(collect()) });
        if (r?.status === 204) { status.textContent = 'Saved.'; setTimeout(ctx.close, 600); }
        else {
          const j = await r?.json().catch(() => null);
          errorEl.textContent = j?.message || `Save failed (${r?.status})`;
          status.textContent = '';
        }
      });

      testBtn.addEventListener('click', async () => {
        errorEl.textContent = '';
        status.textContent = 'Sending test…';
        const r = await api('/api/alerts/test', { method: 'POST' });
        const j = await r?.json().catch(() => null);
        if (j?.ok) status.textContent = j.message || 'Test sent.';
        else { status.textContent = ''; errorEl.textContent = j?.message || 'Test failed.'; }
      });
    },
  });
}

// ── approved clients dialog ──────────────────────────────────────
async function openApprovedDialog() {
  openModal({
    title: 'Approved clients',
    mount: async (body, ctx) => {
      body.classList.add('flush');
      body.innerHTML = `
        <div class="modal-toolbar">
          <input class="modal-filter" type="search" placeholder="Filter…" autocomplete="off">
          <span class="modal-status">Loading…</span>
        </div>
        <div class="modal-scroll" style="padding: 8px 16px 14px;"><div class="approved-list"></div></div>`;
      const list = body.querySelector('.approved-list');
      const filter = body.querySelector('.modal-filter');
      const status = body.querySelector('.modal-status');
      let all = [];
      let text = '';

      async function reload() {
        const r = await api('/api/approved');
        if (!r?.ok) { status.textContent = `error ${r?.status || '?'}`; status.classList.add('err'); return; }
        all = await r.json();
        status.classList.remove('err');
        render();
      }
      function render() {
        const visible = text
          ? all.filter(e => (e.name || '').toLowerCase().includes(text) || (e.alias || '').toLowerCase().includes(text))
          : all;
        list.replaceChildren(...visible.map(approvedRow));
        status.textContent = `${visible.length}/${all.length} entries`;
      }
      function approvedRow(entry) {
        const row = document.createElement('div');
        row.className = 'approved-row' + (entry.isPaw ? ' paw' : '') + (entry.revoked ? ' revoked' : '');
        row.innerHTML = `
          <span class="os-badge">${isLinux(entry) ? 'L' : 'W'}</span>
          <div class="name-block"><span class="name"></span><span class="ip"></span></div>
          <input class="alias-input" placeholder="alias">
          <span class="mac"></span>
          <button class="toggle" type="button">PAW</button>
          <div class="actions">
            <button class="a-btn danger" type="button">Forget</button>
          </div>`;
        row.querySelector('.os-badge').classList.add(isLinux(entry) ? 'os-lnx' : 'os-win');
        row.querySelector('.name').textContent = entry.name;
        row.querySelector('.ip').textContent = [entry.ip, entry.lastSeen ? `seen ${timeAgo(entry.lastSeen)}` : null].filter(Boolean).join(' · ');
        row.querySelector('.mac').textContent = entry.mac || '— no MAC —';
        const alias = row.querySelector('.alias-input');
        alias.value = entry.alias || '';
        alias.addEventListener('blur', async () => {
          if (alias.value === (entry.alias || '')) return;
          const r = await api(`/api/approved/${encodeURIComponent(entry.name)}`, { method: 'PATCH', body: JSON.stringify({ alias: alias.value }) });
          if (r?.status !== 204) { alias.value = entry.alias || ''; return; }
          entry.alias = alias.value;
        });
        const paw = row.querySelector('.toggle');
        paw.classList.toggle('on', !!entry.isPaw);
        paw.addEventListener('click', async () => {
          const next = !entry.isPaw;
          const r = await api(`/api/approved/${encodeURIComponent(entry.name)}`, { method: 'PATCH', body: JSON.stringify({ isPaw: next }) });
          if (r?.status === 204) { entry.isPaw = next; paw.classList.toggle('on', next); row.classList.toggle('paw', next); }
        });
        row.querySelector('.a-btn.danger').addEventListener('click', async () => {
          if (!confirm(`Forget ${entry.name}?`)) return;
          const r = await api(`/api/approved/${encodeURIComponent(entry.name)}`, { method: 'DELETE' });
          if (r?.status === 204) { all = all.filter(e => e.name !== entry.name); render(); loadState(); }
        });
        return row;
      }

      filter.addEventListener('input', () => { text = filter.value.toLowerCase(); render(); });
      ctx.onClose(() => loadState());
      await reload();
      setTimeout(() => filter.focus(), 0);
    },
  });
}

// ── shared formatting helpers used by dialogs ────────────────────
function formatBytes(n) {
  const v = Number(n);
  if (!Number.isFinite(v)) return '';
  if (v < 1024) return `${v} B`;
  if (v < 1024 * 1024) return `${Math.round(v / 1024)} KB`;
  if (v < 1024 * 1024 * 1024) return `${Math.round(v / 1024 / 1024)} MB`;
  return `${(v / 1024 / 1024 / 1024).toFixed(1)} GB`;
}
function formatUptime(hours) {
  const h = Number(hours);
  if (!Number.isFinite(h)) return '?';
  const d = Math.floor(h / 24);
  const rh = Math.floor(h % 24);
  const m = Math.floor((h * 60) % 60);
  return d > 0 ? `${d}d ${rh}h ${m}m` : `${rh}h ${m}m`;
}

loadState();
connectStateWs();
connectLogWs();
