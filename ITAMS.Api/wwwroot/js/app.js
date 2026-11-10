import { api, el, label, fmtDate, ApiError } from './api.js';

const STATUSES = ['InStock', 'Deployed', 'UnderRepair', 'Decommissioned'];
const TYPES = ['SystemUnit', 'BoardingPrinter', 'CargoScaleInterface', 'Monitor', 'Other'];
const INACTIVITY_MS = 15 * 60 * 1000;

const $ = (id) => document.getElementById(id);
const state = { me: null, page: 1, pageSize: 15, total: 0, current: null };

const canWrite = () => ['TenantAdmin', 'Technician'].includes(state.me.role);
const isAdmin = () => state.me.role === 'TenantAdmin';

// ---------------------------------------------------------------- helpers
function fillOptions(select, values, withBlank) {
  if (!withBlank) select.replaceChildren();
  for (const v of values) select.append(el('option', { value: v }, label(v)));
}

function showError(box, err) {
  box.textContent = err instanceof ApiError ? err.message : 'Something went wrong. Try again.';
  box.hidden = false;
}

let toastTimer;
function toast(message) {
  const t = $('toast');
  t.textContent = message;
  t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { t.hidden = true; }, 3500);
}

const debounce = (fn, ms) => { let t; return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); }; };

// ---------------------------------------------------------------- session
async function logout() {
  try { await api('/api/auth/logout', { method: 'POST' }); } catch { /* leave anyway */ }
  location.replace('/login.html');
}

function watchInactivity() {
  let timer;
  const reset = () => { clearTimeout(timer); timer = setTimeout(logout, INACTIVITY_MS); };
  ['click', 'keydown', 'pointermove', 'scroll'].forEach(evt => addEventListener(evt, reset, { passive: true }));
  reset();
}

// ---------------------------------------------------------------- asset list
async function loadAssets() {
  $('list-error').hidden = true;
  const params = new URLSearchParams({ page: state.page, pageSize: state.pageSize });
  if ($('search').value.trim()) params.set('search', $('search').value.trim());
  if ($('filter-status').value) params.set('status', $('filter-status').value);
  if ($('filter-type').value) params.set('type', $('filter-type').value);

  try {
    const data = await api(`/api/assets?${params}`);
    state.total = data.total;
    renderTable(data.items);

    const from = data.total === 0 ? 0 : (data.page - 1) * data.pageSize + 1;
    $('range').textContent = `Showing ${from} to ${from + data.items.length - (data.total === 0 ? 0 : 1)} of ${data.total}`;
    $('prev').disabled = data.page <= 1;
    $('next').disabled = data.page * data.pageSize >= data.total;
  } catch (err) {
    if (err.status !== 401) showError($('list-error'), err);
  }
}

function renderTable(items) {
  const host = $('table-host');

  if (items.length === 0) {
    host.replaceChildren(el('div', { class: 'table-wrap empty' },
      el('h2', {}, 'No assets match'),
      el('p', { class: 'muted' }, canWrite()
        ? 'Clear the filters, or add the first asset for this search.'
        : 'Clear the filters to see every asset.')));
    return;
  }

  const rows = items.map(a => el('tr', {},
    el('td', {}, el('button', { class: 'serial', type: 'button', onclick: () => openDetail(a.id) }, a.serialNumber)),
    el('td', {}, label(a.type)),
    el('td', {}, a.model ?? ''),
    el('td', {}, a.location ?? ''),
    el('td', {}, el('span', { class: `status status-${a.status}` }, label(a.status)))));

  host.replaceChildren(el('div', { class: 'table-wrap' },
    el('table', {},
      el('thead', {}, el('tr', {}, ['Serial number', 'Type', 'Model', 'Location', 'Status'].map(h => el('th', { scope: 'col' }, h)))),
      el('tbody', {}, rows))));
}

// ---------------------------------------------------------------- add asset
function openAddDialog() {
  $('asset-form').reset();
  $('asset-error').hidden = true;
  $('f-status').value = 'InStock';
  // Only admins may create an asset directly as decommissioned.
  [...$('f-status').options].forEach(o => { o.disabled = o.value === 'Decommissioned' && !isAdmin(); });
  $('asset-dialog').showModal();
  $('f-serial').focus();
}

async function saveAsset(e) {
  e.preventDefault();
  $('asset-error').hidden = true;
  if (!$('asset-form').checkValidity()) { $('asset-form').reportValidity(); return; }

  $('asset-save').disabled = true;
  try {
    await api('/api/assets', {
      method: 'POST',
      body: {
        serialNumber: $('f-serial').value,
        type: $('f-type').value,
        status: $('f-status').value,
        model: $('f-model').value || null,
        location: $('f-location').value || null,
        notes: $('f-notes').value || null
      }
    });
    $('asset-dialog').close();
    toast('Asset saved');
    state.page = 1;
    await loadAssets();
  } catch (err) {
    showError($('asset-error'), err);
  } finally {
    $('asset-save').disabled = false;
  }
}

// ---------------------------------------------------------------- asset detail
async function openDetail(id) {
  $('detail-error').hidden = true;
  try {
    const [asset, logs] = await Promise.all([api(`/api/assets/${id}`), api(`/api/assets/${id}/maintenance`)]);
    state.current = asset;
    renderDetail(asset, logs);
    if (!$('detail-dialog').open) $('detail-dialog').showModal();
  } catch (err) {
    if (err.status !== 401) showError($('list-error'), err);
  }
}

function renderDetail(asset, logs) {
  const retired = asset.status === 'Decommissioned';
  $('detail-title').textContent = asset.serialNumber;

  const fact = (k, v) => [el('dt', {}, k), el('dd', {}, v || 'Not set')];
  $('detail-facts').replaceChildren(
    ...fact('Type', label(asset.type)),
    ...fact('Model', asset.model),
    ...fact('Location', asset.location),
    el('dt', {}, 'Status'), el('dd', {}, el('span', { class: `status status-${asset.status}` }, label(asset.status))),
    ...fact('Notes', asset.notes),
    ...fact('Added', fmtDate(asset.createdUtc)));

  $('status-row').hidden = !canWrite() || retired;
  if (!$('status-row').hidden) {
    fillOptions($('d-status'), STATUSES.filter(s => isAdmin() || s !== 'Decommissioned'));
    $('d-status').value = asset.status;
  }

  $('log-form').hidden = !canWrite() || retired;
  $('decommission').hidden = !isAdmin() || retired;

  $('log-list').replaceChildren(...(logs.length === 0
    ? [el('li', { class: 'muted' }, 'No maintenance logged yet.')]
    : logs.map(l => el('li', {},
        el('div', {}, l.description),
        l.partReplaced ? el('div', {}, `Part replaced: ${l.partReplaced}`) : '',
        el('div', { class: 'log-meta' }, `${l.technicianName}, ${fmtDate(l.performedUtc)}`)))));
}

async function updateStatus() {
  const a = state.current;
  $('detail-error').hidden = true;
  try {
    await api(`/api/assets/${a.id}`, {
      method: 'PUT',
      body: { serialNumber: a.serialNumber, type: a.type, model: a.model, location: a.location, notes: a.notes, status: $('d-status').value }
    });
    toast('Status updated');
    await Promise.all([openDetail(a.id), loadAssets()]);
  } catch (err) { showError($('detail-error'), err); }
}

async function saveLog(e) {
  e.preventDefault();
  $('detail-error').hidden = true;
  if (!$('log-form').checkValidity()) { $('log-form').reportValidity(); return; }
  try {
    await api(`/api/assets/${state.current.id}/maintenance`, {
      method: 'POST',
      body: { description: $('l-desc').value, partReplaced: $('l-part').value || null }
    });
    $('log-form').reset();
    toast('Maintenance logged');
    await openDetail(state.current.id);
  } catch (err) { showError($('detail-error'), err); }
}

async function decommission() {
  const a = state.current;
  if (!confirm(`Decommission ${a.serialNumber}? Its history is kept, but it can no longer be changed or serviced.`)) return;
  try {
    await api(`/api/assets/${a.id}`, { method: 'DELETE' });
    toast('Asset decommissioned');
    await Promise.all([openDetail(a.id), loadAssets()]);
  } catch (err) { showError($('detail-error'), err); }
}

// ---------------------------------------------------------------- start
async function start() {
  try {
    state.me = await api('/api/auth/me');
  } catch { return; }   // api() already redirected to the sign-in page

  $('tenant-name').textContent = state.me.tenantId.toUpperCase();
  $('user-name').textContent = state.me.fullName;
  $('user-role').textContent = label(state.me.role);
  $('add-asset').hidden = !canWrite();

  fillOptions($('filter-status'), STATUSES, true);
  fillOptions($('filter-type'), TYPES, true);
  fillOptions($('f-status'), STATUSES);
  fillOptions($('f-type'), TYPES);

  $('logout').addEventListener('click', logout);
  $('add-asset').addEventListener('click', openAddDialog);
  $('asset-form').addEventListener('submit', saveAsset);
  $('d-status-save').addEventListener('click', updateStatus);
  $('log-form').addEventListener('submit', saveLog);
  $('decommission').addEventListener('click', decommission);
  document.querySelectorAll('[data-close]').forEach(b => b.addEventListener('click', () => b.closest('dialog').close()));

  const reload = () => { state.page = 1; loadAssets(); };
  $('search').addEventListener('input', debounce(reload, 300));
  $('filter-status').addEventListener('change', reload);
  $('filter-type').addEventListener('change', reload);
  $('prev').addEventListener('click', () => { state.page--; loadAssets(); });
  $('next').addEventListener('click', () => { state.page++; loadAssets(); });

  watchInactivity();
  await loadAssets();
}

start();
