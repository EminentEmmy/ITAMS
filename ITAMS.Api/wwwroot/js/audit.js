import { api, el, label, fmtDate } from './api.js';
import { showError, emptyState, table, setPager } from './ui.js';

const $ = (id) => document.getElementById(id);
const PAGE = 25;
let page = 1;

// Friendly wording for the actions we record. Anything else falls back to a tidied version of its code.
const ACTIONS = {
  LOGIN_SUCCESS: 'Signed in',
  LOGIN_FAILED: 'Failed sign-in',
  LOGIN_BLOCKED_LOCKED: 'Sign-in blocked (account locked)',
  ACCOUNT_LOCKED: 'Account locked after repeated failures',
  LOGOUT: 'Signed out',
  PASSWORD_CHANGED: 'Password changed',
  PASSWORD_CHANGE_FAILED: 'Password change failed',
  REFRESH_TOKEN_REUSE_DETECTED: 'Possible stolen session: all sessions ended',
  ASSET_CREATED: 'Asset added',
  ASSET_UPDATED: 'Asset edited',
  ASSET_STATUS_CHANGED: 'Asset status changed',
  ASSET_DECOMMISSIONED: 'Asset decommissioned',
  MAINTENANCE_LOGGED: 'Maintenance logged',
  TENANT_USER_CREATED: 'User added',
  TENANT_USER_PASSWORD_RESET: 'Password reset',
  TENANT_USER_DISABLED: 'User disabled',
  TENANT_USER_ENABLED: 'User enabled',
  TENANT_USER_ROLE_CHANGED: 'Role changed',
  PLATFORM_USER_CREATED: 'Platform added a user',
  PLATFORM_PASSWORD_RESET: 'Platform reset a password',
  PLATFORM_USER_DISABLED: 'Platform disabled a user',
  PLATFORM_USER_ENABLED: 'Platform enabled a user',
  PLATFORM_TENANT_OPENED: 'Platform viewed this organization',
  PLATFORM_TENANT_SUSPENDED: 'Organization suspended',
  PLATFORM_TENANT_REACTIVATED: 'Organization reactivated',
  PLATFORM_TENANT_CREATED: 'Organization created'
};
// Events worth a second look get a red marker.
const ALERTS = new Set(['LOGIN_FAILED', 'LOGIN_BLOCKED_LOCKED', 'ACCOUNT_LOCKED', 'REFRESH_TOKEN_REUSE_DETECTED', 'PASSWORD_CHANGE_FAILED']);
const ENTITIES = { Asset: 'Asset', MaintenanceLog: 'Maintenance log', AppUser: 'User', Tenant: 'Organization' };
const HIDDEN_FIELDS = new Set(['Id', 'TenantId', 'CreatedUtc', 'AssetId', 'TechnicianId', 'PerformedUtc']);
const ENUM_FIELDS = new Set(['Status', 'Type', 'Role', 'role', 'from', 'to']);

const tidy = (code) => { const t = code.toLowerCase().replaceAll('_', ' '); return t[0].toUpperCase() + t.slice(1); };
const pretty = (key) => { const t = label(key); return t[0].toUpperCase() + t.slice(1).toLowerCase(); };
const parse = (json) => { try { return json ? JSON.parse(json) : null; } catch { return null; } };
const fmt = (key, v) => (v === null || v === undefined || v === '') ? 'empty'
  : ENUM_FIELDS.has(key) ? label(String(v)) : String(v).slice(0, 60);

// "Status: In Stock to Deployed" for edits, or the key facts of a new record.
function describe(l) {
  const before = parse(l.oldValues);
  const after = parse(l.newValues);

  if (before && after) {
    return Object.keys(after).filter(k => !HIDDEN_FIELDS.has(k))
      .map(k => `${pretty(k)}: ${fmt(k, before[k])} to ${fmt(k, after[k])}`).join('; ');
  }
  const only = after ?? before;
  if (!only) return '';
  return Object.entries(only)
    .filter(([k, v]) => !HIDDEN_FIELDS.has(k) && v !== null && v !== '')
    .slice(0, 5).map(([k, v]) => `${pretty(k)}: ${fmt(k, v)}`).join('; ');
}

export function initAudit() {
  $('audit-category').addEventListener('change', () => { page = 1; loadAudit(); });
  $('audit-prev').addEventListener('click', () => { page--; loadAudit(); });
  $('audit-next').addEventListener('click', () => { page++; loadAudit(); });
}

export async function loadAudit() {
  $('audit-error').hidden = true;
  const params = new URLSearchParams({ page, pageSize: PAGE });
  if ($('audit-category').value) params.set('category', $('audit-category').value);

  try {
    const data = await api(`/api/audit?${params}`);
    setPager('audit', data);

    if (data.items.length === 0) {
      $('audit-host').replaceChildren(emptyState('Nothing recorded here yet', 'Events appear as people sign in and work.'));
      return;
    }

    const rows = data.items.map(l => {
      const what = ACTIONS[l.action] ?? tidy(l.action);
      const item = l.entityLabel ?? (l.entityId === 'unknown' ? '' : `${ENTITIES[l.entityName] ?? l.entityName} ${l.entityId.length > 8 ? '' : '#' + l.entityId}`.trim());
      return el('tr', {},
        el('td', {}, fmtDate(l.timestampUtc)),
        el('td', {}, l.userName ?? l.userId),
        el('td', {}, ALERTS.has(l.action) ? el('span', { class: 'status status-Locked' }, what) : what),
        el('td', {}, item),
        el('td', { class: 'detail' }, describe(l)),
        el('td', { class: 'code' }, l.ipAddress));
    });

    $('audit-host').replaceChildren(table(['When', 'Who', 'What happened', 'Item', 'Details', 'From'], rows));
  } catch (err) {
    if (err.status !== 401) showError($('audit-error'), err);
  }
}
