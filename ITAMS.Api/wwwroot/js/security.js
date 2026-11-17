import { api, el, fmtDate, ApiError, homeFor } from './api.js';
import { table, emptyState, statusBlock } from './ui.js';

const $ = (id) => document.getElementById(id);

// "Chrome on Windows" from the browser's own description. Good enough to recognise your own devices.
function describe(ua) {
  if (!ua) return 'Unknown device';
  const browser = /Edg\//.test(ua) ? 'Edge' : /OPR\//.test(ua) ? 'Opera' : /Firefox\//.test(ua) ? 'Firefox'
    : /Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : 'Browser';
  const system = /Windows/.test(ua) ? 'Windows' : /Android/.test(ua) ? 'Android' : /iPhone|iPad/.test(ua) ? 'iOS'
    : /Mac OS X/.test(ua) ? 'macOS' : /Linux/.test(ua) ? 'Linux' : 'an unknown system';
  return `${browser} on ${system}`;
}

let toastTimer;
function toast(message) {
  const t = $('toast');
  t.textContent = message;
  t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { t.hidden = true; }, 3500);
}

function showError(err) {
  $('error').textContent = err instanceof ApiError ? err.message : 'Something went wrong. Try again.';
  $('error').hidden = false;
}

async function load() {
  $('error').hidden = true;
  try {
    render(await api('/api/sessions'));
  } catch (err) {
    if (err.status !== 401) showError(err);
  }
}

function render(sessions) {
  if (sessions.length === 0) {
    $('sessions-host').replaceChildren(emptyState('No active sessions', 'Sign in again to continue.'));
    return;
  }

  const rows = sessions.map(s => el('tr', {},
    el('td', {}, el('div', { class: 'device' }, describe(s.userAgent)),
      s.current ? statusBlock('Active') : '', s.current ? ' This device' : ''),
    el('td', { class: 'code' }, s.ipAddress ?? ''),
    el('td', {}, fmtDate(s.startedUtc)),
    el('td', {}, fmtDate(s.lastActiveUtc)),
    el('td', {}, fmtDate(s.expiresUtc)),
    el('td', {}, el('button', { class: 'btn btn-quiet btn-small', type: 'button', onclick: () => signOut(s) },
      s.current ? 'Sign out here' : 'Sign out'))));

  $('sessions-host').replaceChildren(
    table(['Device', 'Network address', 'Signed in', 'Last active', 'Ends automatically', ''], rows));
}

async function signOut(session) {
  const question = session.current
    ? 'Sign out of this device now?'
    : `Sign out ${describe(session.userAgent)}? It will stop working within seconds.`;
  if (!confirm(question)) return;

  try {
    await api(`/api/sessions/${session.id}`, { method: 'DELETE' });
    if (session.current) { location.replace('/login.html'); return; }
    toast('Session signed out');
    await load();
  } catch (err) { showError(err); }
}

async function start() {
  let me;
  try { me = await api('/api/auth/me'); } catch { return; }   // api() already redirected to sign-in
  $('back').href = homeFor(me);
  document.body.classList.remove('pending');

  $('sign-out-others').addEventListener('click', async () => {
    if (!confirm('Sign out every other session? This device stays signed in.')) return;
    try {
      const r = await api('/api/sessions/revoke-others', { method: 'POST' });
      toast(r.signedOut === 0 ? 'There were no other sessions' : `Signed out ${r.signedOut} other session${r.signedOut === 1 ? '' : 's'}`);
      await load();
    } catch (err) { showError(err); }
  });

  $('sign-out-all').addEventListener('click', async () => {
    if (!confirm('Sign out everywhere, including this device?')) return;
    try {
      await api('/api/sessions/revoke-all', { method: 'POST' });
      location.replace('/login.html');
    } catch (err) { showError(err); }
  });

  await load();
}

start();
