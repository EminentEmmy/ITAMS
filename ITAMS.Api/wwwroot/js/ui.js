// Small shared UI pieces for the staff dashboard modules (toast, tables, credentials dialog).
import { el, fmtDate, ApiError } from './api.js';

const $ = (id) => document.getElementById(id);

let toastTimer;
export function toast(message) {
  const t = $('toast');
  t.textContent = message;
  t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { t.hidden = true; }, 3500);
}

export function showError(box, err) {
  box.textContent = err instanceof ApiError ? err.message : 'Something went wrong. Try again.';
  box.hidden = false;
}

export const statusBlock = (text) => el('span', { class: `status status-${text}` }, text);

export function emptyState(title, text) {
  return el('div', { class: 'table-wrap empty' }, el('h2', {}, title), el('p', { class: 'muted' }, text));
}

export function table(headers, rows) {
  return el('div', { class: 'table-wrap' },
    el('table', {},
      el('thead', {}, el('tr', {}, headers.map(h => el('th', { scope: 'col' }, h)))),
      el('tbody', {}, rows)));
}

export function setPager(prefix, data) {
  const from = data.total === 0 ? 0 : (data.page - 1) * data.pageSize + 1;
  const to = data.total === 0 ? 0 : from + data.items.length - 1;
  $(`${prefix}-range`).textContent = `Showing ${from} to ${to} of ${data.total}`;
  $(`${prefix}-prev`).disabled = data.page <= 1;
  $(`${prefix}-next`).disabled = data.page * data.pageSize >= data.total;
}

export const lastSeen = (iso) => (iso ? fmtDate(iso) : 'Never');

// ---- temporary credentials: shown once, wiped when the dialog closes ----
export function showCredentials(title, creds) {
  $('cred-title').textContent = title;
  $('cred-email').textContent = creds.email;
  $('cred-pass').textContent = creds.temporaryPassword;
  $('cred-dialog').showModal();
}

export function initCredentialsDialog() {
  $('cred-copy').addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText($('cred-pass').textContent);
      toast('Password copied');
    } catch {
      toast('Copy failed. Select the password and copy it by hand.');
    }
  });
  $('cred-dialog').addEventListener('close', () => {
    $('cred-pass').textContent = '';
    $('cred-email').textContent = '';
  });
}
