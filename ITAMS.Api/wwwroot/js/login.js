import { api, ApiError } from './api.js';

const form = document.getElementById('login-form');
const errorBox = document.getElementById('error');
const submit = document.getElementById('submit');
const tenant = document.getElementById('tenant');

// The company code is not a secret, so remembering it is a harmless convenience.
try { tenant.value = localStorage.getItem('itams.tenant') ?? ''; } catch { /* storage unavailable */ }

form.addEventListener('submit', async (e) => {
  e.preventDefault();
  errorBox.hidden = true;

  if (!form.checkValidity()) { form.reportValidity(); return; }

  submit.disabled = true;
  try {
    await api('/api/auth/login', {
      method: 'POST',
      body: {
        tenantId: tenant.value,
        email: document.getElementById('email').value,
        password: document.getElementById('password').value
      }
    });
    try { localStorage.setItem('itams.tenant', tenant.value.trim().toLowerCase()); } catch { /* ignore */ }
    location.replace('/index.html');
  } catch (err) {
    errorBox.textContent = err instanceof ApiError ? err.message : 'Cannot reach the server. Check your connection.';
    errorBox.hidden = false;
    document.getElementById('password').value = '';
  } finally {
    submit.disabled = false;
  }
});
