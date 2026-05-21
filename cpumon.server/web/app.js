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

function reconnectDelay() { return 1000 + Math.floor(Math.random() * 1500); }

function connectStateWs() {
  const ws = new WebSocket(wsUrl('/ws/state'));
  ws.onopen = () => setWs('state', true);
  ws.onclose = () => { setWs('state', false); setTimeout(connectStateWs, reconnectDelay()); };
  ws.onmessage = (event) => {
    const msg = JSON.parse(event.data);
    if (msg.type === 'state') render(msg.state);
  };
}

function connectLogWs() {
  const ws = new WebSocket(wsUrl('/ws/log'));
  ws.onopen = () => setWs('log', true);
  ws.onclose = () => { setWs('log', false); setTimeout(connectLogWs, reconnectDelay()); };
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

  state.selected = new Set(data.selectedMachineNames || []);
  $('selectionCount').innerHTML = '';
  const c1 = countFragment(clients.length, 'connected'), c2 = countFragment(offline.length, 'offline'),
        c3 = countFragment(state.selected.size, 'selected');
  $('selectionCount').append(c1, sep(), c2, sep(), c3);

  const showUpdate = state.selected.size > 0 && !!data.stagedReleaseDir;
  $('updateSelected').hidden = !showUpdate;
  $('updateSelectedCount').textContent = state.selected.size;

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
  const report = client.report || {};
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

  metricCpu(tpl, report.load);
  metricRam(tpl, report.ramUsed, report.ramTotal);
  metricTemp(tpl, report.temp);
  metricNet(tpl, report.netDn, report.netUp);

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
    kv('OS', client.osLabel || report.os || '?'),
    kv('CPU', report.cpuName || '?'),
    kv('Cores', report.coreCount ? `${report.coreCount}` : '?'),
    kv('RAM', ramText(report.ramUsed, report.ramTotal)),
  ];
  const drives = Array.isArray(report.drvs) ? report.drvs : [];
  if (drives.length) body.push(buildDrives(drives));
  return body;
}

function buildDrives(drives) {
  const wrap = document.createElement('div');
  wrap.className = 'drives';
  for (const d of drives) {
    const total = Number(d.t) || 0;
    const free = Number(d.f) || 0;
    const used = Math.max(0, total - free);
    const pct = total > 0 ? Math.min(100, Math.round((used / total) * 100)) : 0;
    const cls = pct >= 90 ? 'crit' : pct >= 75 ? 'warn' : '';
    const row = document.createElement('div');
    row.className = 'drive';
    row.innerHTML = `<span class="letter"></span><span class="size"></span><span class="pct"></span><div class="bar"><span></span></div>`;
    row.querySelector('.letter').textContent = d.n || '?';
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
  if (client.canTerminal)  inspect.push(action('Terminal',   () => openTerminalDialog(client.machineName)));
  if (client.canScreenshot) inspect.push(action('Screenshot', () => openScreenshotDialog(client.machineName)));
  if (client.canCpuDetail) inspect.push(action('CPU detail', () => openCpuDetailDialog(client.machineName)));
  inspect.push(action('Files', () => openFilesDialog(client.machineName)));

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
  ];
  if (state.data?.stagedReleaseDir) {
    const v = state.data?.availableUpdate?.version;
    manage.push(action('Update', () => confirmAction(
      v ? `Push staged update v${v} to ${client.machineName}?`
        : `Push staged update to ${client.machineName}?`,
      `/api/clients/${m}/update`), 'warn'));
  }
  manage.push(
    action('Restart',  () => confirmAction('Restart client?',  `/api/clients/${m}/restart`),  'warn'),
    action('Shutdown', () => confirmAction('Shutdown client?', `/api/clients/${m}/shutdown`), 'danger'),
  );

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
  const ms = Number(report.ts);
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
  if (list.matches(':hover')) return;
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
function ramText(used, total) {
  return Number(total) > 0 ? `${num(used)}/${num(total)} GB` : '—';
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
  return !!report.name
    || !!report.os
    || Number.isFinite(Number(report.load))
    || Number.isFinite(Number(report.ramTotal))
    || Number.isFinite(Number(report.ts));
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
$('updateSelected').addEventListener('click', async () => {
  const machineNames = [...state.selected];
  if (!machineNames.length) return;
  const v = state.data?.availableUpdate?.version;
  if (!confirm(v
    ? `Push staged update v${v} to ${machineNames.length} client(s)?`
    : `Push staged update to ${machineNames.length} client(s)?`)) return;
  const r = await api('/api/updates/push', { method: 'POST', body: JSON.stringify({ machineNames }) });
  if (r?.ok) await loadState();
});
$('btnAlerts').addEventListener('click', () => openAlertsDialog());
$('btnApproved').addEventListener('click', () => openApprovedDialog());
$('btnInstall').addEventListener('click', () => openInstallLinkDialog());

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

// ── screenshot dialog ────────────────────────────────────────────
function openScreenshotDialog(machine) {
  openModal({
    title: `Screenshot · ${machine}`,
    mount: (body, ctx) => {
      body.classList.add('flush');
      body.innerHTML = `
        <div class="modal-toolbar">
          <button class="a-btn primary" type="button" data-k="refresh">Refresh</button>
          <button class="a-btn" type="button" data-k="minus">-</button>
          <span class="modal-status" data-k="zoom">100%</span>
          <button class="a-btn" type="button" data-k="plus">+</button>
          <span class="modal-status" data-k="status">Fetching…</span>
        </div>
        <div class="modal-scroll screenshot-scroll">
          <div class="screenshot-stage"><img alt=""></div>
        </div>`;
      const img = body.querySelector('img');
      const stage = body.querySelector('.screenshot-stage');
      const status = body.querySelector('[data-k="status"]');
      const zoomLabel = body.querySelector('[data-k="zoom"]');
      let zoom = 1;
      let hasImage = false;
      function applyZoom() {
        img.style.width = `${Math.round(zoom * 100)}%`;
        zoomLabel.textContent = `${Math.round(zoom * 100)}%`;
      }
      function setShot(shot, receivedAt) {
        if (shot?.error) {
          status.textContent = shot.error;
          status.classList.add('err');
          return;
        }
        if (!shot?.data) return;
        hasImage = true;
        img.src = `data:image/jpeg;base64,${shot.data}`;
        img.alt = `${machine} screenshot`;
        stage.classList.add('loaded');
        status.textContent = `${shot.w || '?'}x${shot.h || '?'} · updated ${timeAgo(receivedAt)}`;
        status.classList.remove('err');
      }
      const stop = pollSnapshot(
        `/api/clients/${encodeURIComponent(machine)}/screenshot`,
        1500,
        setShot,
        (msg) => { status.textContent = msg; status.classList.add('err'); },
      );
      body.querySelector('[data-k="refresh"]').addEventListener('click', async () => {
        status.textContent = hasImage ? 'Refreshing…' : 'Fetching…';
        status.classList.remove('err');
        const r = await api(`/api/clients/${encodeURIComponent(machine)}/screenshot?force=true`);
        if (r?.status === 200) {
          const j = await r.json();
          setShot(j.snapshot, j.receivedAt);
        }
      });
      body.querySelector('[data-k="minus"]').addEventListener('click', () => { zoom = Math.max(0.25, zoom - 0.25); applyZoom(); });
      body.querySelector('[data-k="plus"]').addEventListener('click', () => { zoom = Math.min(3, zoom + 0.25); applyZoom(); });
      img.addEventListener('dblclick', () => { zoom = zoom === 1 ? 2 : 1; applyZoom(); });
      applyZoom();
      api(`/api/clients/${encodeURIComponent(machine)}/screenshot?force=true`)
        .then(async r => { if (r?.status === 200) { const j = await r.json(); setShot(j.snapshot, j.receivedAt); } })
        .catch(() => {});
      ctx.onClose(stop);
    },
  });
}

// terminal dialog
function openTerminalDialog(machine) {
  openModal({
    title: `Terminal · ${machine}`,
    mount: (body, ctx) => {
      body.classList.add('flush');
      body.innerHTML = `
        <div class="modal-toolbar">
          <span class="modal-status" data-k="shell">opening…</span>
          <span class="modal-status" data-k="status">connecting</span>
        </div>
        <div class="terminal-pane">
          <pre class="terminal-output" aria-live="polite"></pre>
          <form class="terminal-inputbar">
            <span>&gt;</span>
            <input class="terminal-input" type="text" autocomplete="off" spellcheck="false" placeholder="command">
          </form>
        </div>`;
      const out = body.querySelector('.terminal-output');
      const form = body.querySelector('.terminal-inputbar');
      const input = body.querySelector('.terminal-input');
      const shellLabel = body.querySelector('[data-k="shell"]');
      const status = body.querySelector('[data-k="status"]');
      const m = encodeURIComponent(machine);
      let termId = null;
      let seq = 0;
      let stopped = false;
      let timer = null;

      function append(text) {
        out.textContent += text;
        if (out.textContent.length > 120000) {
          out.textContent = '[…earlier output trimmed…]\n' + out.textContent.slice(-100000);
        }
        out.scrollTop = out.scrollHeight;
      }

      async function poll() {
        if (stopped || !termId) return;
        try {
          const r = await api(`/api/clients/${m}/terminal/${encodeURIComponent(termId)}/output?since=${seq}`);
          if (stopped) return;
          if (r?.ok) {
            const j = await r.json();
            for (const c of (j.chunks || [])) append(c.text || '');
            seq = j.nextSeq ?? seq;
            status.textContent = j.closed ? 'closed' : 'live';
            status.classList.toggle('err', !!j.closed);
            if (j.closed) return;
          } else {
            status.textContent = r?.status === 404 ? 'disconnected' : 'error';
            status.classList.add('err');
          }
        } catch {
          status.textContent = 'connection error';
          status.classList.add('err');
        }
        if (!stopped) timer = setTimeout(poll, 650);
      }

      async function open() {
        const r = await api(`/api/clients/${m}/terminal/open`, {
          method: 'POST',
          body: JSON.stringify({ shell: 'cmd' }),
        });
        if (!r?.ok) {
          status.textContent = r?.status === 404 ? 'client unavailable' : 'open failed';
          status.classList.add('err');
          return;
        }
        const j = await r.json();
        termId = j.termId;
        shellLabel.textContent = j.shell || 'terminal';
        status.textContent = 'live';
        input.disabled = false;
        input.focus();
        poll();
      }

      input.disabled = true;
      form.addEventListener('submit', async (e) => {
        e.preventDefault();
        if (!termId || input.disabled) return;
        const text = input.value;
        input.value = '';
        const r = await api(`/api/clients/${m}/terminal/${encodeURIComponent(termId)}/input`, {
          method: 'POST',
          body: JSON.stringify({ input: `${text}\n` }),
        });
        if (r && !r.ok) {
          const j = await r.json().catch(() => null);
          status.textContent = j?.message || `send failed (${r.status})`;
          status.classList.add('err');
          input.value = text;
        }
      });
      input.addEventListener('keydown', async (e) => {
        if (e.ctrlKey && e.key.toLowerCase() === 'c' && termId) {
          e.preventDefault();
          await api(`/api/clients/${m}/terminal/${encodeURIComponent(termId)}/input`, {
            method: 'POST',
            body: JSON.stringify({ input: '\u0003' }),
          });
        }
      });
      ctx.onClose(() => {
        stopped = true;
        if (timer) clearTimeout(timer);
        if (termId) api(`/api/clients/${m}/terminal/${encodeURIComponent(termId)}/close`, { method: 'POST' }).catch(() => {});
      });
      open().catch(() => {
        status.textContent = 'open failed';
        status.classList.add('err');
      });
    },
  });
}

// ── files dialog ─────────────────────────────────────────────────
function openFilesDialog(machine) {
  openModal({
    title: `Files · ${machine}`,
    mount: async (body, ctx) => {
      body.classList.add('flush');
      body.innerHTML = `
        <div class="modal-toolbar files-toolbar">
          <button class="a-btn" data-k="up" type="button" title="Up one level">▲ Up</button>
          <button class="a-btn" data-k="drives" type="button" title="Drives / root">⏏ Root</button>
          <button class="a-btn" data-k="refresh" type="button" title="Refresh">↻</button>
          <input class="modal-filter" data-k="path" type="text" placeholder="path" autocomplete="off" spellcheck="false">
          <button class="a-btn" data-k="go" type="button">Go</button>
          <span class="modal-status" data-k="status">opening…</span>
        </div>
        <div class="modal-scroll">
          <table class="modal-table files-table">
            <thead><tr>
              <th>Name</th>
              <th class="right">Size</th>
              <th>Modified</th>
              <th class="right">Actions</th>
            </tr></thead>
            <tbody></tbody>
          </table>
        </div>
        <div class="files-uploader" data-k="dropzone">
          <input type="file" data-k="picker" hidden>
          <button class="a-btn" data-k="upload" type="button">⇡ Upload…</button>
          <button class="a-btn" data-k="mkdir" type="button">+ New folder</button>
          <span class="files-hint">drag & drop into the table to upload here</span>
          <span class="files-progress" data-k="progress" hidden></span>
        </div>`;

      const m       = encodeURIComponent(machine);
      const pathBox = body.querySelector('[data-k="path"]');
      const tbody   = body.querySelector('tbody');
      const status  = body.querySelector('[data-k="status"]');
      const progEl  = body.querySelector('[data-k="progress"]');
      const dz      = body.querySelector('[data-k="dropzone"]');
      const picker  = body.querySelector('[data-k="picker"]');
      let sessionId = null;
      let currentPath = '';
      let lastListingSeq = 0;
      let lastResultSeq  = 0;
      let pollTimer = null;
      let stopped = false;
      let pendingPath = '';   // set when issuing a list; latched into currentPath when listing arrives

      async function openSession() {
        const r = await api(`/api/clients/${m}/files/open`, { method: 'POST' });
        if (!r?.ok) {
          status.textContent = r?.status === 404 ? 'client unavailable' : 'open failed';
          status.classList.add('err');
          return null;
        }
        const j = await r.json();
        return j.sessionId;
      }

      async function nav(path) {
        pendingPath = path ?? '';
        pathBox.value = pendingPath;
        status.textContent = 'loading…';
        status.classList.remove('err');
        const r = await api(`/api/clients/${m}/files/${sessionId}/list`, {
          method: 'POST',
          body: JSON.stringify({ path: pendingPath }),
        });
        if (r?.status === 404) { status.textContent = 'client unavailable'; status.classList.add('err'); }
      }

      function parent(path) {
        if (!path) return '';
        if (path.startsWith('/')) {
          const trimmed = path.replace(/\/+$/, '');
          if (trimmed.length <= 1) return '/';
          const slash = trimmed.lastIndexOf('/');
          return slash <= 0 ? '/' : trimmed.slice(0, slash);
        }
        const m1 = path.match(/^([a-zA-Z]:\\)$/);
        if (m1) return '';
        const trimmed = path.replace(/[\\/]+$/, '');
        const slash = Math.max(trimmed.lastIndexOf('\\'), trimmed.lastIndexOf('/'));
        if (slash < 0) return '';
        if (trimmed[slash - 1] === ':') return trimmed.slice(0, slash + 1);
        return trimmed.slice(0, slash);
      }

      function joinPath(dir, name) {
        if (!dir) return name;
        if (dir.startsWith('/')) return dir === '/' ? '/' + name : dir.replace(/\/+$/, '') + '/' + name;
        return dir.replace(/[\\/]+$/, '') + '\\' + name;
      }

      function fmtSize(b) {
        if (!Number.isFinite(Number(b))) return '';
        b = Number(b);
        if (b < 1024) return `${b} B`;
        if (b < 1048576) return `${(b / 1024).toFixed(1)} KB`;
        if (b < 1073741824) return `${(b / 1048576).toFixed(1)} MB`;
        return `${(b / 1073741824).toFixed(2)} GB`;
      }

      function fmtTime(ms) {
        const n = Number(ms);
        if (!Number.isFinite(n) || n <= 0) return '';
        return new Date(n).toLocaleString([], { year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' });
      }

      function renderListing(listing) {
        tbody.replaceChildren();
        if (listing?.error) {
          status.textContent = `error: ${listing.error}`;
          status.classList.add('err');
          return;
        }
        status.classList.remove('err');
        currentPath = listing?.path ?? '';
        pathBox.value = currentPath;
        if (listing?.drives) {
          for (const d of listing.drives) {
            const used = d.ready ? `${d.freeGB?.toFixed?.(1) ?? d.freeGB}/${d.totalGB?.toFixed?.(1) ?? d.totalGB} GB free` : '';
            const tr = document.createElement('tr');
            tr.innerHTML = `<td class="files-name files-drive"><span class="files-icon">⛁</span><a href="#">${escapeHtml(d.name)}</a> <span class="dim">${escapeHtml(d.label || '')}</span></td><td class="right mono">${escapeHtml(used)}</td><td>${escapeHtml(d.format || '')}</td><td class="right"></td>`;
            tr.querySelector('a').addEventListener('click', (e) => { e.preventDefault(); nav(d.name); });
            tbody.appendChild(tr);
          }
          status.textContent = `${listing.drives.length} drives`;
          return;
        }
        const entries = (listing?.entries || []).slice();
        entries.sort((a, b) => Number(b.isDir) - Number(a.isDir) || a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }));
        if (currentPath) {
          const up = document.createElement('tr');
          up.innerHTML = `<td class="files-name"><span class="files-icon">↰</span><a href="#">..</a></td><td></td><td></td><td class="right"></td>`;
          up.querySelector('a').addEventListener('click', (e) => { e.preventDefault(); nav(parent(currentPath)); });
          tbody.appendChild(up);
        }
        let dirs = 0, fileCount = 0;
        for (const e of entries) {
          const tr = document.createElement('tr');
          const childPath = joinPath(currentPath, e.name);
          const icon = e.isDir ? '▤' : '▢';
          const nameCls = e.hidden ? 'dim' : (e.isDir ? 'files-dir' : '');
          tr.innerHTML = `<td class="files-name ${nameCls}"><span class="files-icon">${icon}</span><a href="#">${escapeHtml(e.name)}</a></td>
            <td class="right mono">${e.isDir ? '' : fmtSize(e.size)}</td>
            <td class="mono dim">${escapeHtml(fmtTime(e.modified))}</td>
            <td class="right files-actions"></td>`;
          const link = tr.querySelector('a');
          link.addEventListener('click', (ev) => { ev.preventDefault(); if (e.isDir) nav(childPath); else download(childPath, e.name); });
          const cell = tr.querySelector('.files-actions');
          if (!e.isDir) {
            const dl = document.createElement('button');
            dl.className = 'a-btn';
            dl.textContent = '⇣';
            dl.title = 'Download';
            dl.addEventListener('click', () => download(childPath, e.name));
            cell.appendChild(dl);
          }
          const rn = document.createElement('button');
          rn.className = 'a-btn';
          rn.textContent = '✎';
          rn.title = 'Rename';
          rn.addEventListener('click', () => renamePrompt(childPath, e.name));
          cell.appendChild(rn);
          const del = document.createElement('button');
          del.className = 'a-btn danger';
          del.textContent = '✕';
          del.title = 'Delete';
          del.addEventListener('click', () => deletePrompt(childPath, e.isDir));
          cell.appendChild(del);
          (e.isDir ? ++dirs : ++fileCount);
          tbody.appendChild(tr);
        }
        status.textContent = `${dirs} folder · ${fileCount} files`;
      }

      async function tick() {
        if (stopped || !sessionId) return;
        try {
          const r = await api(`/api/clients/${m}/files/${sessionId}`);
          if (!r?.ok) {
            if (r?.status === 404) {
              status.textContent = 'session expired';
              status.classList.add('err');
              stopped = true;
              return;
            }
          } else {
            const snap = await r.json();
            if (snap.listingSeq && snap.listingSeq !== lastListingSeq) {
              lastListingSeq = snap.listingSeq;
              renderListing(snap.listing);
            }
            if (snap.resultSeq && snap.resultSeq !== lastResultSeq) {
              lastResultSeq = snap.resultSeq;
              status.textContent = snap.result || '';
              status.classList.toggle('err', !snap.resultOk);
              if (snap.resultOk) setTimeout(() => nav(currentPath), 250);
            }
          }
        } catch { /* keep polling */ }
        if (!stopped) pollTimer = setTimeout(tick, 700);
      }

      async function renamePrompt(path, currentName) {
        const next = prompt(`Rename "${currentName}" to:`, currentName);
        if (!next || next === currentName) return;
        await api(`/api/clients/${m}/files/${sessionId}/rename`, {
          method: 'POST',
          body: JSON.stringify({ path, newName: next }),
        });
      }

      async function deletePrompt(path, isDir) {
        if (!confirm(`Delete ${isDir ? 'folder (recursive)' : 'file'} "${path}"?`)) return;
        await api(`/api/clients/${m}/files/${sessionId}/delete`, {
          method: 'POST',
          body: JSON.stringify({ path, recursive: !!isDir }),
        });
      }

      async function mkdirPrompt() {
        if (!currentPath) { status.textContent = 'navigate to a folder first'; status.classList.add('err'); return; }
        const name = prompt('New folder name:');
        if (!name) return;
        await api(`/api/clients/${m}/files/${sessionId}/mkdir`, {
          method: 'POST',
          body: JSON.stringify({ path: joinPath(currentPath, name) }),
        });
      }

      async function download(path, name) {
        progEl.hidden = false;
        progEl.textContent = `download ${name}: starting`;
        const r = await api(`/api/clients/${m}/files/${sessionId}/download`, {
          method: 'POST',
          body: JSON.stringify({ path }),
        });
        if (!r?.ok) {
          progEl.textContent = `download failed (${r?.status})`;
          return;
        }
        const { transferId } = await r.json();
        const dl = `/api/clients/${m}/files/${sessionId}/download/${encodeURIComponent(transferId)}`;
        while (true) {
          await new Promise(res => setTimeout(res, 400));
          const probe = await api(dl);
          if (probe?.status === 204) {
            const snap = await fetchSnapshot();
            const t = snap?.transfers?.find(x => x.transferId === transferId);
            if (t) progEl.textContent = `download ${name}: ${fmtSize(t.received)}${t.total ? ' / ' + fmtSize(t.total) : ''}`;
            continue;
          }
          if (probe?.status === 200) {
            const blob = await probe.blob();
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = name;
            document.body.appendChild(a);
            a.click();
            a.remove();
            URL.revokeObjectURL(url);
            progEl.textContent = `download complete: ${name}`;
            return;
          }
          progEl.textContent = `download failed (${probe?.status})`;
          return;
        }
      }

      async function fetchSnapshot() {
        const r = await api(`/api/clients/${m}/files/${sessionId}`);
        return r?.ok ? r.json() : null;
      }

      async function uploadFile(file) {
        if (!currentPath) { status.textContent = 'navigate to a folder first'; status.classList.add('err'); return; }
        progEl.hidden = false;
        progEl.textContent = `upload ${file.name}: sending ${fmtSize(file.size)}`;
        const url = `/api/clients/${m}/files/${sessionId}/upload?dest=${encodeURIComponent(currentPath)}&name=${encodeURIComponent(file.name)}`;
        try {
          const r = await fetch(url, {
            method: 'POST',
            headers: { 'X-CSRF-Token': csrf(), 'Content-Type': 'application/octet-stream', 'Content-Length': String(file.size) },
            body: file,
          });
          if (r.status === 204) {
            progEl.textContent = `upload complete: ${file.name}`;
            setTimeout(() => nav(currentPath), 200);
          } else {
            const j = await r.json().catch(() => null);
            progEl.textContent = j?.message || `upload failed (${r.status})`;
          }
        } catch (e) {
          progEl.textContent = `upload error: ${e.message || e}`;
        }
      }

      body.querySelector('[data-k="up"]').addEventListener('click', () => nav(parent(currentPath)));
      body.querySelector('[data-k="drives"]').addEventListener('click', () => nav(''));
      body.querySelector('[data-k="refresh"]').addEventListener('click', () => nav(currentPath));
      body.querySelector('[data-k="go"]').addEventListener('click', () => nav(pathBox.value.trim()));
      pathBox.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); nav(pathBox.value.trim()); } });
      body.querySelector('[data-k="mkdir"]').addEventListener('click', mkdirPrompt);
      body.querySelector('[data-k="upload"]').addEventListener('click', () => picker.click());
      picker.addEventListener('change', () => {
        const f = picker.files?.[0];
        if (f) uploadFile(f);
        picker.value = '';
      });
      ['dragenter', 'dragover'].forEach(t => dz.addEventListener(t, (e) => { e.preventDefault(); dz.classList.add('drag'); }));
      ['dragleave', 'drop'].forEach(t => dz.addEventListener(t, (e) => { e.preventDefault(); dz.classList.remove('drag'); }));
      dz.addEventListener('drop', (e) => {
        const f = e.dataTransfer?.files?.[0];
        if (f) uploadFile(f);
      });

      ctx.onClose(() => {
        stopped = true;
        if (pollTimer) clearTimeout(pollTimer);
        if (sessionId) api(`/api/clients/${m}/files/${sessionId}/close`, { method: 'POST' }).catch(() => {});
      });

      sessionId = await openSession();
      if (!sessionId) return;
      status.textContent = 'ready';
      tick();
      nav('');
    },
  });
}

function escapeHtml(s) {
  return String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
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

// ── install link dialog ─────────────────────────────────────────
async function openInstallLinkDialog() {
  openModal({
    title: 'Install link',
    mount: async (body, ctx) => {
      body.innerHTML = `
        <div class="form-grid" style="margin-bottom: 8px;">
          <div class="form-row"><label>Server address</label><input name="serverIp" placeholder="e.g. 192.168.1.10"></div>
          <div class="form-row">
            <label>Link valid for</label>
            <select name="ttl">
              <option value="1">1 hour</option>
              <option value="24" selected>24 hours</option>
              <option value="168">7 days</option>
            </select>
          </div>
          <div class="form-row wide">
            <button type="button" class="a-btn primary" data-k="generate">Generate install link</button>
            <span class="hint" data-k="hint">A one-time URL the recipient downloads. The bundle includes the staged client exe and pins this server's TLS thumbprint.</span>
          </div>
          <div class="form-row wide" data-k="result" hidden>
            <label>Install URL</label>
            <div style="display:flex; gap:6px; align-items:stretch;">
              <input data-k="url" readonly style="flex:1;">
              <button type="button" class="a-btn" data-k="copy">Copy</button>
            </div>
            <span class="hint" data-k="resultMeta"></span>
          </div>
        </div>
        <div class="section-bar" style="margin-top:14px;"><span class="label">Active links</span><span class="rule"></span></div>
        <div data-k="links"></div>`;
      const errorEl = document.createElement('div');
      errorEl.className = 'form-error-line';
      body.appendChild(errorEl);

      const get = (k) => body.querySelector(`[data-k="${k}"]`);
      const ipInput = body.querySelector('[name="serverIp"]');
      const ttlSelect = body.querySelector('[name="ttl"]');
      ipInput.value = guessServerHost();

      async function refreshList() {
        const r = await api('/api/install-links');
        if (!r?.ok) return;
        const items = await r.json();
        const wrap = get('links');
        wrap.replaceChildren();
        if (!items.length) {
          const empty = document.createElement('div');
          empty.className = 'empty-side';
          empty.textContent = 'no install links issued';
          wrap.appendChild(empty);
          return;
        }
        for (const item of items) wrap.appendChild(linkRow(item));
      }

      function linkRow(item) {
        const row = document.createElement('div');
        row.className = 'approved-row';
        row.style.gridTemplateColumns = 'minmax(0, 1fr) 110px 110px 80px auto';
        row.innerHTML = `
          <div style="min-width:0;">
            <div style="font-size:11px;color:var(--brter); overflow:hidden; text-overflow:ellipsis; white-space:nowrap;" data-k="url"></div>
            <div class="meta" data-k="meta"></div>
          </div>
          <span class="meta" data-k="created"></span>
          <span class="meta" data-k="expires"></span>
          <span class="toggle" data-k="state"></span>
          <div class="actions">
            <button type="button" class="a-btn" data-k="copy">Copy</button>
            <button type="button" class="a-btn danger" data-k="revoke">Revoke</button>
          </div>`;
        row.querySelector('[data-k="url"]').textContent = item.url;
        row.querySelector('[data-k="url"]').title = item.url;
        row.querySelector('[data-k="meta"]').textContent = `${item.serverIp || '?'} · by ${item.createdBy || '?'}`;
        row.querySelector('[data-k="created"]').textContent = timeAgo(item.createdAt);
        row.querySelector('[data-k="expires"]').textContent = item.usedAt
          ? `used ${timeAgo(item.usedAt)}`
          : `in ${timeUntil(item.expiresAt)}`;
        const stateEl = row.querySelector('[data-k="state"]');
        stateEl.textContent = item.active ? 'ACTIVE' : item.usedAt ? 'USED' : 'EXPIRED';
        if (item.active) stateEl.classList.add('on');
        row.querySelector('[data-k="copy"]').addEventListener('click', () => navigator.clipboard?.writeText(item.url));
        row.querySelector('[data-k="revoke"]').addEventListener('click', async () => {
          if (!confirm(`Revoke this install link?`)) return;
          const r = await api(`/api/install-links/${encodeURIComponent(item.code)}`, { method: 'DELETE' });
          if (r?.status === 204) await refreshList();
        });
        return row;
      }

      get('generate').addEventListener('click', async () => {
        errorEl.textContent = '';
        const ttlHours = parseInt(ttlSelect.value, 10) || 24;
        const r = await api('/api/install-links', { method: 'POST', body: JSON.stringify({
          serverIp: ipInput.value.trim() || null,
          ttlHours,
        })});
        if (r?.status !== 200) {
          const j = await r?.json().catch(() => null);
          errorEl.textContent = j?.message || `Generate failed (${r?.status})`;
          return;
        }
        const link = await r.json();
        get('result').hidden = false;
        get('url').value = link.url;
        get('resultMeta').textContent = `Expires in ${timeUntil(link.expiresAt)} · single use · share this URL with the target user`;
        await refreshList();
      });

      get('copy').addEventListener('click', () => {
        const url = get('url').value;
        if (url) navigator.clipboard?.writeText(url);
      });

      await refreshList();
      setTimeout(() => ipInput.focus(), 0);
    },
  });
}

function guessServerHost() {
  // Prefer the host part of the current page URL (sans port). Operator can override.
  return location.hostname || '';
}

function timeUntil(input) {
  const ts = typeof input === 'number' ? input : Date.parse(input);
  if (!Number.isFinite(ts)) return '?';
  const seconds = Math.max(0, Math.round((ts - Date.now()) / 1000));
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.round(minutes / 60);
  if (hours < 48) return `${hours}h`;
  const days = Math.round(hours / 24);
  return `${days}d`;
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
