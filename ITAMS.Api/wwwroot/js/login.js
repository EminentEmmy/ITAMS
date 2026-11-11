import { api, ApiError, homeFor } from './api.js';

const form = document.getElementById('login-form');
const errorBox = document.getElementById('error');
const submit = document.getElementById('submit');
const tenant = document.getElementById('tenant');

// The company code is not a secret, so remembering it is a harmless convenience.
try { tenant.value = localStorage.getItem('itams.tenant') ?? ''; } catch { /* storage unavailable */ }

// Arriving here right after choosing a new password.
if (new URLSearchParams(location.search).get('changed') === '1') {
  document.getElementById('notice').hidden = false;
  history.replaceState(null, '', '/login.html');
  (tenant.value ? document.getElementById('email') : tenant).focus();
}

form.addEventListener('submit', async (e) => {
  e.preventDefault();
  errorBox.hidden = true;

  if (!form.checkValidity()) { form.reportValidity(); return; }

  submit.disabled = true;
  try {
    const me = await api('/api/auth/login', {
      method: 'POST',
      body: {
        tenantId: tenant.value,
        email: document.getElementById('email').value,
        password: document.getElementById('password').value
      }
    });
    try { localStorage.setItem('itams.tenant', tenant.value.trim().toLowerCase()); } catch { /* ignore */ }
    location.replace(homeFor(me));   // owner -> Platform console, staff -> assets, temporary password -> change it first
  } catch (err) {
    errorBox.textContent = err instanceof ApiError ? err.message : 'Cannot reach the server. Check your connection.';
    errorBox.hidden = false;
    document.getElementById('password').value = '';
  } finally {
    submit.disabled = false;
  }
});
