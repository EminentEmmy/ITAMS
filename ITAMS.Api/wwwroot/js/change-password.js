import { api, ApiError, homeFor } from './api.js';

const form = document.getElementById('pw-form');
const errorBox = document.getElementById('error');
const submit = document.getElementById('submit');
const cancel = document.getElementById('cancel');

function fail(message) { errorBox.textContent = message; errorBox.hidden = false; }

// Two situations share this page:
//  - forced: the person holds a temporary password and can only change it or sign out
//  - voluntary: a signed-in person changing their own password can cancel back to where they came from
let forced = true;
let home = '/login.html';
try {
  const me = await api('/api/auth/me');
  forced = me.mustChangePassword;
  home = homeFor(me);
} catch { /* api() redirects to sign-in when there is no session */ }

if (forced) {
  document.getElementById('pw-title').textContent = 'Choose your own password to continue.';
  document.getElementById('pw-intro').textContent = 'The password you were given is temporary. Pick one only you know.';
  document.getElementById('current-label').textContent = 'Temporary password';
  cancel.textContent = 'Sign out';
}

form.addEventListener('submit', async (e) => {
  e.preventDefault();
  errorBox.hidden = true;

  if (!form.checkValidity()) { form.reportValidity(); return; }
  if (document.getElementById('new').value !== document.getElementById('confirm').value) {
    fail('The two new passwords do not match.');
    return;
  }

  submit.disabled = true;
  try {
    await api('/api/auth/change-password', {
      method: 'POST',
      body: {
        currentPassword: document.getElementById('current').value,
        newPassword: document.getElementById('new').value
      }
    });
    // The server has ended every session. Sign in again with the new password.
    location.replace('/login.html?changed=1');
  } catch (err) {
    fail(err instanceof ApiError ? err.message : 'Cannot reach the server. Check your connection.');
  } finally {
    submit.disabled = false;
  }
});

cancel.addEventListener('click', async () => {
  if (!forced) { location.replace(home); return; }
  try { await api('/api/auth/logout', { method: 'POST' }); } catch { /* leave anyway */ }
  location.replace('/login.html');
});
