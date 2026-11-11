import { api, el, label } from './api.js';
import { toast, showError, statusBlock, emptyState, table, lastSeen, showCredentials, initCredentialsDialog } from './ui.js';

const ROLES = ['TenantAdmin', 'Technician', 'Auditor'];
const ROLE_HELP = {
  TenantAdmin: 'Manages staff, assets and sees the audit trail.',
  Technician: 'Adds and edits assets and logs maintenance.',
  Auditor: 'Read-only access to assets and the audit trail.'
};

const $ = (id) => document.getElementById(id);
let me = null;

export function initStaff(currentUser) {
  me = currentUser;
  initCredentialsDialog();

  for (const r of ROLES) $('u-role').append(el('option', { value: r }, label(r)));
  $('add-user').addEventListener('click', openDialog);
  $('user-form').addEventListener('submit', saveUser);
  $('u-role').addEventListener('change', () => { $('u-role-help').textContent = ROLE_HELP[$('u-role').value]; });
}

export async function loadStaff() {
  $('staff-error').hidden = true;
  try {
    render(await api('/api/users'));
  } catch (err) {
    if (err.status !== 401) showError($('staff-error'), err);
  }
}

function userStatus(u) {
  if (!u.isActive) return 'Disabled';
  if (u.isLocked) return 'Locked';
  if (u.mustChangePassword) return 'Invited';
  return 'Active';
}

function render(users) {
  if (users.length === 0) {
    $('staff-host').replaceChildren(emptyState('No staff yet', 'Add the first person so they can sign in.'));
    return;
  }

  const rows = users.map(u => {
    const isMe = u.id === me.id;

    const roleCell = isMe
      ? el('td', {}, label(u.role), ' ', el('span', { class: 'muted' }, '(you)'))
      : el('td', {}, el('select', {
          class: 'role-select',
          'aria-label': `Role for ${u.fullName}`,
          onchange: (e) => changeRole(u, e.target)
        }, ROLES.map(r => el('option', { value: r, selected: r === u.role }, label(r)))));

    const actions = isMe
      ? el('td', { class: 'muted' }, 'Use "Change password" in the top bar')
      : el('td', {}, el('div', { class: 'actions' },
          el('button', { class: 'btn btn-quiet btn-small', type: 'button', onclick: () => resetPassword(u) }, 'Reset password'),
          el('button', { class: 'btn btn-quiet btn-small', type: 'button', onclick: () => toggleUser(u) }, u.isActive ? 'Disable' : 'Enable')));

    return el('tr', {},
      el('td', {}, el('div', {}, u.fullName), el('div', { class: 'muted' }, u.email)),
      roleCell,
      el('td', {}, statusBlock(userStatus(u))),
      el('td', {}, lastSeen(u.lastSignIn)),
      actions);
  });

  $('staff-host').replaceChildren(table(['Person', 'Role', 'Status', 'Last sign-in', 'Actions'], rows));
}

// ---------------------------------------------------------------- add
function openDialog() {
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
    const creds = await api('/api/users', {
      method: 'POST',
      body: { fullName: $('u-name').value, email: $('u-email').value, role: $('u-role').value }
    });
    $('user-dialog').close();
    showCredentials('User created', creds);
    await loadStaff();
  } catch (err) {
    showError($('user-error'), err);
  } finally {
    $('user-save').disabled = false;
  }
}

// ---------------------------------------------------------------- manage
async function resetPassword(u) {
  if (!confirm(`Reset the password for ${u.fullName}? They will be signed out everywhere and must choose a new password.`)) return;
  try {
    const creds = await api(`/api/users/${u.id}/reset-password`, { method: 'POST' });
    showCredentials(`Password reset for ${u.fullName}`, creds);
    await loadStaff();
  } catch (err) { showError($('staff-error'), err); }
}

async function toggleUser(u) {
  if (u.isActive && !confirm(`Disable ${u.fullName}? They will be signed out and unable to sign in until you enable them again.`)) return;
  try {
    await api(`/api/users/${u.id}/status`, { method: 'PATCH', body: { isActive: !u.isActive } });
    toast(u.isActive ? 'User disabled' : 'User enabled');
    await loadStaff();
  } catch (err) { showError($('staff-error'), err); }
}

async function changeRole(u, select) {
  const next = select.value;
  if (!confirm(`Change ${u.fullName} from ${label(u.role)} to ${label(next)}? They will be signed out and the new role applies when they sign in again.`)) {
    select.value = u.role;
    return;
  }
  try {
    await api(`/api/users/${u.id}/role`, { method: 'PATCH', body: { role: next } });
    toast('Role changed');
  } catch (err) {
    showError($('staff-error'), err);
  }
  await loadStaff();
}
