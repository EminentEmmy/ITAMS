import { api, el, label, fmtDate, ApiError, watchInactivity } from './api.js';
import { initShell } from './shell.js';

const STATUSES = ['InStock', 'Deployed', 'UnderRepair', 'Decommissioned'];
const TYPES = ['SystemUnit', 'BoardingPrinter', 'CargoScaleInterface', 'Monitor', 'Other'];
const ROLE_HELP = {
  TenantAdmin: 'Manages assets and sees the audit trail for their organization.',
  Technician: 'Adds and edits assets and logs maintenance.',
  Auditor: 'Read-only access, including the audit trail.',
  Requester: 'Reports faults and follows their own tickets. Cannot see assets.'
};
const PAGE = 15;
const INACTIVITY_MS = 10 * 60 * 1000;   // shorter than staff: this account can see every customer

const $ = (id) => document.getElementById(id);
const state = { me: null, org: null, userNames: new Map(), assetsPage: 1, activityPage: 1 };

// ---------------------------------------------------------------- helpers
const num = (n) => Number(n).toLocaleString();
const actionText = (a) => { const t = a.toLowerCase().replaceAll('_', ' '); return t[0].toUpperCase() + t.slice(1); };
const orgUrl = (suffix = '') => `/api/platform/tenants/${encodeURIComponent(state.org.id)}${suffix}`;
const statusBlock = (text) => el('span', { class: `status status-${text}` }, text);
const debounce = (fn, ms) => { let t; return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); }; };
const slug = (s) => s.toLowerCase().normalize('NFKD').replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 50);

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

function fillOptions(select, values) {
  for (const v of values) select.append(el('option', { value: v }, label(v)));
}

function showView(name) {
  $('view-list').hidden = name !== 'list';
  $('view-detail').hidden = name !== 'detail';
  window.scrollTo(0, 0);
}

function emptyState(title, text) {
  return el('div', { class: 'table-wrap empty' }, el('h2', {}, title), el('p', { class: 'muted' }, text));
}

function table(headers, rows) {
  return el('div', { class: 'table-wrap' },
    el('table', {},
      el('thead', {}, el('tr', {}, headers.map(h => el('th', { scope: 'col' }, h)))),
      el('tbody', {}, rows)));
}

function setPager(prefix, data) {
  const from = data.total === 0 ? 0 : (data.page - 1) * data.pageSize + 1;
  const to = data.total === 0 ? 0 : from + data.items.length - 1;
  $(`${prefix}-range`).textContent = `Showing ${from} to ${to} of ${data.total}`;
  $(`${prefix}-prev`).disabled = data.page <= 1;
  $(`${prefix}-next`).disabled = data.page * data.pageSize >= data.total;
}

// ---------------------------------------------------------------- session
async function logout() {
  try { await api('/api/auth/logout', { method: 'POST' }); } catch { /* leave anyway */ }
  location.replace('/login.html');
}

// ---------------------------------------------------------------- organizations list
let allOrgs = [];   // everything the server sent; searching, filtering and sorting happen here

async function loadOrgs() {
  $('list-error').hidden = true;
  try {
    const [overview, orgs] = await Promise.all([api('/api/platform/overview'), api('/api/platform/tenants')]);
    allOrgs = orgs;
    renderLedger(overview);
    renderOrgs();
  } catch (err) {
    if (err.status !== 401) showError($('list-error'), err);
  }
}

function renderLedger(o) {
  const item = (value, text) => el('div', { class: 'ledger-item' }, el('dt', {}, text), el('dd', {}, value));
  $('ledger').replaceChildren(
    item(`${o.activeTenants} of ${o.tenants}`, 'Organizations active'),
    item(num(o.users), 'Active users'),
    item(num(o.assets), 'Assets tracked'),
    item(num(o.assetsUnderRepair), 'Assets under repair'),
    item(num(o.failedSignIns24h), o.lockedAccounts > 0
      ? `Failed sign-ins in 24 hours (${o.lockedAccounts} locked)`
      : 'Failed sign-ins in 24 hours'));
}

const byName = (a, b) => a.name.localeCompare(b.name);
const time = (iso) => (iso ? Date.parse(iso) : -Infinity);
const SORTS = {
  name: byName,
  newest: (a, b) => time(b.createdUtc) - time(a.createdUtc),
  oldest: (a, b) => time(a.createdUtc) - time(b.createdUtc),
  activity: (a, b) => (time(b.lastSignIn) - time(a.lastSignIn)) || byName(a, b),   // NaN (both never) falls through to the name
  users: (a, b) => (b.users - a.users) || byName(a, b),
  assets: (a, b) => (b.assets - a.assets) || byName(a, b),
  repair: (a, b) => (b.underRepair - a.underRepair) || byName(a, b)
};

function renderOrgs() {
  const total = allOrgs.length;
  $('org-tools').hidden = total === 0;

  if (total === 0) {
    $('orgs-host').replaceChildren(emptyState('No organizations yet',
      'Click New organization to set up your first client workspace and assign their administrator.'));
    return;
  }

  const query = $('org-search').value.trim().toLowerCase();
  const status = $('org-status-filter').value;

  const shown = allOrgs
    .filter(o => !query || o.name.toLowerCase().includes(query) || o.id.toLowerCase().includes(query))
    .filter(o => !status || (status === 'active') === o.isActive)
    .sort(SORTS[$('org-sort').value] ?? byName);

  $('org-count').textContent = shown.length === total
    ? `${total} ${total === 1 ? 'organization' : 'organizations'}`
    : `Showing ${shown.length} of ${total} organizations`;

  if (shown.length === 0) {
    $('orgs-host').replaceChildren(el('div', { class: 'table-wrap empty' },
      el('h2', {}, 'No organizations match'),
      el('p', { class: 'muted' }, 'Try a different name or company code, or another status.'),
      el('button', { class: 'btn btn-quiet', type: 'button', onclick: clearOrgFilters }, 'Clear search and filters')));
    return;
  }

  const rows = shown.map(t => el('tr', {},
    el('td', {},
      el('button', { class: 'link', type: 'button', onclick: () => { location.hash = `organizations/${encodeURIComponent(t.id)}`; } }, t.name),
      el('div', { class: 'muted code' }, t.id)),
    el('td', {}, statusBlock(t.isActive ? 'Active' : 'Suspended')),
    el('td', {}, num(t.users)),
    el('td', {}, num(t.assets)),
    el('td', {}, num(t.underRepair)),
    el('td', {}, t.lastSignIn ? fmtDate(t.lastSignIn) : 'Never'),
    el('td', {}, fmtDate(t.createdUtc))));

  const card = table(['Organization', 'Status', 'Users', 'Assets', 'Under repair', 'Last sign-in', 'Created'], rows);
  card.append(onboardPrompt());   // sits at the bottom of the table card
  $('orgs-host').replaceChildren(card);
}

function clearOrgFilters() {
  $('org-search').value = '';
  $('org-status-filter').value = '';
  $('org-sort').value = 'name';
  renderOrgs();
}

/** A quiet nudge under the list, so a short list never looks unfinished. */
function onboardPrompt() {
  return el('div', { class: 'onboard' },
    el('div', {},
      el('strong', {}, 'Ready to onboard your next client?'),
      el('p', { class: 'muted' }, 'Click New organization to set up their workspace and assign their first administrator.')),
    el('button', { class: 'btn btn-sign btn-small', type: 'button', onclick: openOrgDialog }, 'New organization'));
}

// ---------------------------------------------------------------- new organization
function openOrgDialog() {
  $('org-form').reset();
  $('org-error').hidden = true;
  $('o-code').dataset.touched = '';
  $('org-dialog').showModal();
  $('o-name').focus();
}

async function saveOrg(e) {
  e.preventDefault();
  $('org-error').hidden = true;
  if (!$('org-form').checkValidity()) { $('org-form').reportValidity(); return; }

  $('org-save').disabled = true;
  try {
    const res = await api('/api/platform/tenants', {
      method: 'POST',
      body: {
        id: $('o-code').value,
        name: $('o-name').value,
        adminFullName: $('o-admin-name').value,
        adminEmail: $('o-admin-email').value
      }
    });
    $('org-dialog').close();
    showCredentials(`${res.tenant.name} is ready`, res.admin);
    await loadOrgs();
  } catch (err) {
    showError($('org-error'), err);
  } finally {
    $('org-save').disabled = false;
  }
}

// ---------------------------------------------------------------- one organization
async function openOrg(id) {
  try {
    state.org = { id };
    state.assetsPage = 1;
    state.activityPage = 1;
    await loadOrgDetail();
    $('a-search').value = '';
    $('a-status').value = '';
    $('a-type').value = '';
    selectTab('users');
    showView('detail');
  } catch (err) {
    if (err.status !== 401) { toast('That organization could not be opened.'); location.replace('#organizations'); }
  }
}

async function loadOrgDetail() {
  $('detail-error').hidden = true;
  const d = await api(orgUrl());
  state.org = d.summary;
  renderOrgHeader();
  renderUsers(d.users);
}

function renderOrgHeader() {
  const o = state.org;
  $('org-name').textContent = o.name;
  $('org-meta').textContent = `Company code ${o.id}. Created ${fmtDate(o.createdUtc)}.`;
  $('org-status').replaceChildren(statusBlock(o.isActive ? 'Active' : 'Suspended'));
  $('org-toggle').textContent = o.isActive ? 'Suspend organization' : 'Reactivate organization';
  $('org-toggle').className = o.isActive ? 'btn btn-danger' : 'btn';
}

async function toggleOrg() {
  const o = state.org;
  const suspend = o.isActive;
  const question = suspend
    ? `Suspend ${o.name}? Nobody there will be able to sign in, and current sessions end within 15 minutes. Their data is kept.`
    : `Reactivate ${o.name}? Their staff will be able to sign in again.`;
  if (!confirm(question)) return;

  try {
    await api(orgUrl('/status'), { method: 'PATCH', body: { isActive: !suspend } });
    toast(suspend ? 'Organization suspended' : 'Organization reactivated');
    await Promise.all([loadOrgDetail(), loadOrgs()]);
  } catch (err) { showError($('detail-error'), err); }
}

// ---- tabs
const TABS = ['users', 'assets', 'activity'];
function selectTab(name) {
  for (const t of TABS) {
    $(`tab-${t}`).setAttribute('aria-selected', String(t === name));
    $(`panel-${t}`).hidden = t !== name;
  }
  if (name === 'assets') loadAssets();
  if (name === 'activity') loadActivity();
}

// ---- users
function userStatus(u) {
  if (!u.isActive) return 'Disabled';
  if (u.isLocked) return 'Locked';
  if (u.mustChangePassword) return 'Invited';
  return 'Active';
}

function renderUsers(users) {
  state.userNames = new Map(users.map(u => [u.id, u.fullName]));

  if (users.length === 0) {
    $('users-host').replaceChildren(emptyState('No users yet', 'Add the first user so this organization can sign in.'));
    return;
  }

  const rows = users.map(u => el('tr', {},
    el('td', {}, el('div', {}, u.fullName), el('div', { class: 'muted' }, u.email)),
    el('td', {}, label(u.role)),
    el('td', {}, statusBlock(userStatus(u))),
    el('td', {}, u.lastSignIn ? fmtDate(u.lastSignIn) : 'Never'),
    el('td', {}, el('div', { class: 'actions' },
      el('button', { class: 'btn btn-quiet btn-small', type: 'button', onclick: () => resetPassword(u) }, 'Reset password'),
      el('button', { class: 'btn btn-quiet btn-small', type: 'button', onclick: () => toggleUser(u) }, u.isActive ? 'Disable' : 'Enable')))));

  $('users-host').replaceChildren(table(['User', 'Role', 'Status', 'Last sign-in', 'Actions'], rows));
}

function openUserDialog() {
  $('user-form').reset();
  $('user-error').hidden = true;
  $('u-role').value = 'Technician';
  $('u-role-help').textContent = ROLE_HELP.Technician;
  $('user-dialog').showModal();
  $('u-name').focus();
}

async function saveUser(e) {
  e.preventDefault();
  $('user-error').hidden = true;
  if (!$('user-form').checkValidity()) { $('user-form').reportValidity(); return; }

  $('user-save').disabled = true;
  try {
    const creds = await api(orgUrl('/users'), {
      method: 'POST',
      body: { fullName: $('u-name').value, email: $('u-email').value, role: $('u-role').value }
    });
    $('user-dialog').close();
    showCredentials('User created', creds);
    await Promise.all([loadOrgDetail(), loadOrgs()]);
  } catch (err) {
    showError($('user-error'), err);
  } finally {
    $('user-save').disabled = false;
  }
}

async function resetPassword(u) {
  if (!confirm(`Reset the password for ${u.fullName}? They will be signed out everywhere and must choose a new password.`)) return;
  try {
    const creds = await api(`/api/platform/users/${u.id}/reset-password`, { method: 'POST' });
    showCredentials(`Password reset for ${u.fullName}`, creds);
    await loadOrgDetail();
  } catch (err) { showError($('detail-error'), err); }
}

async function toggleUser(u) {
  if (u.isActive && !confirm(`Disable ${u.fullName}? They will be signed out and unable to sign in until you enable them again.`)) return;
  try {
    await api(`/api/platform/users/${u.id}/status`, { method: 'PATCH', body: { isActive: !u.isActive } });
    toast(u.isActive ? 'User disabled' : 'User enabled');
    await loadOrgDetail();
  } catch (err) { showError($('detail-error'), err); }
}

// ---- temporary credentials (shown once, cleared when closed)
function showCredentials(title, creds) {
  $('cred-title').textContent = title;
  $('cred-email').textContent = creds.email;
  $('cred-pass').textContent = creds.temporaryPassword;
  $('cred-dialog').showModal();
}

async function copyPassword() {
  try {
    await navigator.clipboard.writeText($('cred-pass').textContent);
    toast('Password copied');
  } catch {
    toast('Copy failed. Select the password and copy it by hand.');
  }
}

// ---- assets (read-only)
async function loadAssets() {
  $('assets-error').hidden = true;
  const params = new URLSearchParams({ page: state.assetsPage, pageSize: PAGE });
  if ($('a-search').value.trim()) params.set('search', $('a-search').value.trim());
  if ($('a-status').value) params.set('status', $('a-status').value);
  if ($('a-type').value) params.set('type', $('a-type').value);

  try {
    const data = await api(`${orgUrl('/assets')}?${params}`);
    setPager('assets', data);

    if (data.items.length === 0) {
      $('assets-host').replaceChildren(emptyState('No assets found', 'This organization has no assets that match.'));
      return;
    }

    const rows = data.items.map(a => el('tr', {},
      el('td', { class: 'code' }, a.serialNumber),
      el('td', {}, label(a.type)),
      el('td', {}, a.model ?? ''),
      el('td', {}, a.location ?? ''),
      el('td', {}, el('span', { class: `status status-${a.status}` }, label(a.status)))));
    $('assets-host').replaceChildren(table(['Serial number', 'Type', 'Model', 'Location', 'Status'], rows));
  } catch (err) {
    if (err.status !== 401) showError($('assets-error'), err);
  }
}

// ---- activity
function who(userId) {
  if (userId === state.me.id) return 'You (platform owner)';
  return state.userNames.get(userId) ?? userId;
}

async function loadActivity() {
  $('activity-error').hidden = true;
  try {
    const data = await api(`${orgUrl('/audit')}?page=${state.activityPage}&pageSize=25`);
    setPager('activity', data);

    if (data.items.length === 0) {
      $('activity-host').replaceChildren(emptyState('No activity yet', 'Events appear here as people sign in and work.'));
      return;
    }

    const rows = data.items.map(l => el('tr', {},
      el('td', {}, fmtDate(l.timestampUtc)),
      el('td', {}, actionText(l.action)),
      el('td', {}, who(l.userId)),
      el('td', { class: 'code' }, l.ipAddress)));
    $('activity-host').replaceChildren(table(['When', 'What happened', 'Who', 'From'], rows));
  } catch (err) {
    if (err.status !== 401) showError($('activity-error'), err);
  }
}

// ---------------------------------------------------------------- sidebar and routing (#organizations, #organizations/<code>)
let shell;

function setupShell() {
  shell = initShell({
    items: [{ id: 'organizations', label: 'Organizations', icon: 'organizations' }],
    user: state.me,
    orgLabel: 'Platform console',
    roleLabel: 'Platform owner',
    variant: 'platform',
    onSignOut: logout
  });
  addEventListener('hashchange', route);
  route();
}

async function route() {
  const [section, id] = location.hash.slice(1).split('/');
  if (section !== 'organizations') { location.replace('#organizations'); return; }   // fires hashchange again

  shell.setActive('organizations');
  shell.setTitle('Organizations');
  document.title = 'ITAMS Platform: Organizations';

  if (id) {
    await openOrg(decodeURIComponent(id));
  } else {
    showView('list');
    await loadOrgs();
  }
}

// ---------------------------------------------------------------- start
async function start() {
  try {
    state.me = await api('/api/auth/me');
  } catch { return; }   // api() already redirected

  if (state.me.mustChangePassword) { location.replace('/change-password.html'); return; }
  if (state.me.role !== 'SuperAdmin') { location.replace('/index.html'); return; }
  document.body.classList.remove('pending');


  fillOptions($('a-status'), STATUSES);
  fillOptions($('a-type'), TYPES);
  fillOptions($('u-role'), ['TenantAdmin', 'Technician', 'Auditor', 'Requester']);

  $('new-org').addEventListener('click', openOrgDialog);
  $('org-search').addEventListener('input', debounce(renderOrgs, 150));
  $('org-status-filter').addEventListener('change', renderOrgs);
  $('org-sort').addEventListener('change', renderOrgs);
  $('org-form').addEventListener('submit', saveOrg);
  $('back').addEventListener('click', () => { location.hash = 'organizations'; });
  $('org-toggle').addEventListener('click', toggleOrg);
  $('add-user').addEventListener('click', openUserDialog);
  $('user-form').addEventListener('submit', saveUser);
  $('u-role').addEventListener('change', () => { $('u-role-help').textContent = ROLE_HELP[$('u-role').value]; });
  $('cred-copy').addEventListener('click', copyPassword);
  $('cred-dialog').addEventListener('close', () => { $('cred-pass').textContent = ''; $('cred-email').textContent = ''; });
  document.querySelectorAll('[data-close]').forEach(b => b.addEventListener('click', () => b.closest('dialog').close()));

  // suggest a company code from the organization name until the owner types their own
  $('o-code').addEventListener('input', () => { $('o-code').dataset.touched = '1'; });
  $('o-name').addEventListener('input', () => { if (!$('o-code').dataset.touched) $('o-code').value = slug($('o-name').value); });

  for (const t of TABS) $(`tab-${t}`).addEventListener('click', () => selectTab(t));

  const reloadAssets = () => { state.assetsPage = 1; loadAssets(); };
  $('a-search').addEventListener('input', debounce(reloadAssets, 300));
  $('a-status').addEventListener('change', reloadAssets);
  $('a-type').addEventListener('change', reloadAssets);
  $('assets-prev').addEventListener('click', () => { state.assetsPage--; loadAssets(); });
  $('assets-next').addEventListener('click', () => { state.assetsPage++; loadAssets(); });
  $('activity-prev').addEventListener('click', () => { state.activityPage--; loadActivity(); });
  $('activity-next').addEventListener('click', () => { state.activityPage++; loadActivity(); });

  watchInactivity(INACTIVITY_MS, logout);
  setupShell();
}

start();
