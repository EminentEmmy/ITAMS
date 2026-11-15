import { api, el, label, fmtDate } from './api.js';
import { toast, showError, statusBlock, emptyState, table, setPager } from './ui.js';

const CATEGORIES = ['Hardware', 'Printer', 'Network', 'Software', 'Other'];
const IMPACTS = [
  ['Blocking', 'I cannot work / service is stopped'],
  ['Degraded', 'Working, but with problems'],
  ['Minor', 'Small issue, can wait']
];
const STATUSES = ['New', 'InProgress', 'Resolved', 'Closed'];
const PRIORITIES = ['Low', 'Normal', 'High', 'Critical'];
const PAGE = 15;

const $ = (id) => document.getElementById(id);
let me = null;
let onChange = () => {};
let page = 1;
let current = null;       // the ticket open in the detail dialog
let assignees = null;     // cached list of people tickets can be assigned to

const isHandler = () => ['TenantAdmin', 'Technician'].includes(me.role);
const isAdmin = () => me.role === 'TenantAdmin';
const isRequester = () => me.role === 'Requester';

export async function countNewTickets() {
  return (await api('/api/tickets?status=New&pageSize=1')).total;
}

/** Opens one ticket's detail (used when a notification is clicked). */
export function openTicket(id) { return openDetail(id); }

export function initTickets(currentUser, changed) {
  me = currentUser;
  onChange = changed;

  for (const c of CATEGORIES) $('t-category').append(el('option', { value: c }, c));
  for (const [value, text] of IMPACTS) $('t-impact').append(el('option', { value: value }, text));
  $('t-impact').value = 'Degraded';
  for (const s of STATUSES) $('td-status').append(el('option', { value: s }, label(s)));
  for (const p of PRIORITIES) $('td-priority').append(el('option', { value: p }, p));

  $('tickets-intro').textContent = isRequester()
    ? 'Report a fault and follow the tickets you have raised.'
    : 'Faults and requests reported by your people, newest first.';
  $('ticket-mine-wrap').hidden = !isHandler();

  $('new-ticket').addEventListener('click', openNew);
  $('ticket-form').addEventListener('submit', saveNew);
  $('td-save').addEventListener('click', saveDetail);

  const reload = () => { page = 1; loadTickets(); };
  let timer;
  $('ticket-search').addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(reload, 300); });
  $('ticket-filter').addEventListener('change', reload);
  $('ticket-mine').addEventListener('change', reload);
  $('tickets-prev').addEventListener('click', () => { page--; loadTickets(); });
  $('tickets-next').addEventListener('click', () => { page++; loadTickets(); });

  if (isAdmin()) initLinkCard();
}

// ---------------------------------------------------------------- list
export async function loadTickets() {
  $('tickets-error').hidden = true;
  const params = new URLSearchParams({ page, pageSize: PAGE });
  const search = $('ticket-search').value.trim();
  if (search) params.set('search', search);
  const f = $('ticket-filter').value;
  if (f === 'open') params.set('openOnly', 'true');
  else if (f) params.set('status', f);
  if (isHandler() && $('ticket-mine').checked) params.set('assignedToMe', 'true');

  try {
    const data = await api(`/api/tickets?${params}`);
    setPager('tickets', data);

    if (data.items.length === 0) {
      $('tickets-host').replaceChildren(emptyState('No tickets here',
        isRequester() ? 'Use "New ticket" to report a fault.' : 'Nothing matches. Clear the filters, or wait for the next report.'));
      return;
    }

    const rows = data.items.map(t => el('tr', {},
      el('td', {}, el('button', { class: 'serial', type: 'button', onclick: () => openDetail(t.id) }, t.reference)),
      el('td', {}, el('div', {}, t.title), el('div', { class: 'muted' }, `${t.reporterName}${t.location ? ', ' + t.location : ''}`)),
      el('td', {}, statusBlock(t.priority)),
      el('td', {}, statusBlock(t.status)),
      el('td', {}, t.assignedToName ?? 'Unassigned'),
      el('td', {}, fmtDate(t.createdUtc))));
    $('tickets-host').replaceChildren(table(['Reference', 'Ticket', 'Priority', 'Status', 'Assigned to', 'Logged'], rows));
  } catch (err) {
    if (err.status !== 401) showError($('tickets-error'), err);
  }
}

// ---------------------------------------------------------------- raise a ticket
function openNew() {
  $('ticket-form').reset();
  $('t-impact').value = 'Degraded';
  $('ticket-error').hidden = true;
  $('ticket-new-dialog').showModal();
  $('t-title').focus();
}

async function saveNew(e) {
  e.preventDefault();
  $('ticket-error').hidden = true;
  if (!$('ticket-form').checkValidity()) { $('ticket-form').reportValidity(); return; }

  $('ticket-save').disabled = true;
  try {
    const t = await api('/api/tickets', {
      method: 'POST',
      body: {
        title: $('t-title').value, description: $('t-desc').value,
        category: $('t-category').value, impact: $('t-impact').value,
        location: $('t-location').value || null, assetReference: $('t-asset').value || null
      }
    });
    $('ticket-new-dialog').close();
    toast(`Ticket ${t.reference} logged`);
    page = 1;
    await loadTickets();
    onChange();
  } catch (err) {
    showError($('ticket-error'), err);
  } finally {
    $('ticket-save').disabled = false;
  }
}

// ---------------------------------------------------------------- detail and handling
async function openDetail(id) {
  $('td-error').hidden = true;
  try {
    current = await api(`/api/tickets/${id}`);
    if (isHandler() && assignees === null) assignees = await api('/api/tickets/assignees');
    renderDetail();
    if (!$('ticket-detail-dialog').open) $('ticket-detail-dialog').showModal();
  } catch (err) {
    if (err.status !== 401) showError($('tickets-error'), err);
  }
}

function renderDetail() {
  const t = current;
  $('td-title').textContent = `${t.reference}: ${t.title}`;

  const fact = (k, v) => [el('dt', {}, k), el('dd', {}, v || 'Not given')];
  $('td-facts').replaceChildren(
    el('dt', {}, 'Status'), el('dd', {}, statusBlock(t.status), ' ', statusBlock(t.priority)),
    ...fact('Reported by', t.reporterContact ? `${t.reporterName} (${t.reporterContact})` : t.reporterName),
    ...fact('How', t.source === 'PublicLink' ? 'Public report link (no sign-in)' : 'Signed-in account'),
    ...fact('Logged', fmtDate(t.createdUtc)),
    ...fact('Category', t.category),
    ...fact('Impact', IMPACTS.find(i => i[0] === t.impact)?.[1]),
    ...fact('Location', t.location),
    ...fact('Device (as typed)', t.assetReference),
    ...fact('Linked asset', t.assetSerial),
    ...fact('Assigned to', t.assignedToName),
    ...(t.resolvedUtc ? fact('Resolved', fmtDate(t.resolvedUtc)) : []));

  $('td-description').textContent = t.description;
  $('td-resolution-wrap').hidden = !t.resolutionNotes || isHandler();
  $('td-resolution').textContent = t.resolutionNotes ?? '';

  $('td-edit').hidden = !isHandler();
  if (isHandler()) {
    $('td-status').value = t.status;
    $('td-priority').value = t.priority;
    $('td-serial').value = t.assetSerial ?? '';
    $('td-notes').value = t.resolutionNotes ?? '';

    const select = $('td-assignee');
    select.replaceChildren(el('option', { value: '' }, 'Unassigned'),
      ...assignees.map(a => el('option', { value: a.id }, a.fullName)));
    select.value = t.assignedToUserId ?? '';
  }
}

async function saveDetail() {
  $('td-error').hidden = true;
  $('td-save').disabled = true;
  try {
    await api(`/api/tickets/${current.id}`, {
      method: 'PUT',
      body: {
        status: $('td-status').value,
        priority: $('td-priority').value,
        assignedToUserId: $('td-assignee').value || null,
        assetSerial: $('td-serial').value || null,
        resolutionNotes: $('td-notes').value || null
      }
    });
    $('ticket-detail-dialog').close();
    toast('Ticket updated');
    await loadTickets();
    onChange();
  } catch (err) {
    showError($('td-error'), err);
  } finally {
    $('td-save').disabled = false;
  }
}

// ---------------------------------------------------------------- public report link (administrators)
function initLinkCard() {
  $('link-card').hidden = false;
  $('link-copy').addEventListener('click', async () => {
    try { await navigator.clipboard.writeText($('link-url').value); toast('Link copied'); }
    catch { $('link-url').select(); toast('Press Ctrl+C to copy the selected link'); }
  });
  $('link-create').addEventListener('click', () => changeLink('POST', 'Public link created'));
  $('link-rotate').addEventListener('click', () => {
    if (confirm('Create a new link? The old link stops working immediately, so anything you shared with it (posters, messages) must be replaced.'))
      changeLink('POST', 'New link created');
  });
  $('link-off').addEventListener('click', () => {
    if (confirm('Turn the public link off? Nobody will be able to report faults without signing in until you create a new link.'))
      changeLink('DELETE', 'Public link turned off');
  });
  refreshLink();
}

async function refreshLink() {
  try { showLink(await api('/api/settings/report-link')); } catch { /* the card simply stays empty */ }
}

async function changeLink(method, message) {
  try {
    const result = await api('/api/settings/report-link', { method });
    showLink(result ?? { enabled: false, token: null });
    toast(message);
  } catch (err) { showError($('tickets-error'), err); }
}

function showLink(link) {
  $('link-on').hidden = !link.enabled;
  $('link-off-state').hidden = link.enabled;
  $('link-url').value = link.enabled ? `${location.origin}/report.html#${link.token}` : '';
}
