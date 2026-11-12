import { api, ApiError, homeFor } from './api.js';
import { initSlideshow, initPasswordToggle } from './auth-ui.js';

initSlideshow();
initPasswordToggle();

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

const submitLabel = submit.textContent;

function setLoading(on) {
    submit.disabled = on;
    submit.classList.toggle('is-loading', on);
    submit.textContent = on ? 'Signing in…' : submitLabel;
}

function showError(message) {
    errorBox.textContent = message;
    errorBox.hidden = false;
}

function hideError() {
    errorBox.hidden = true;
    errorBox.textContent = '';
}

// Clear the error as soon as the user starts correcting something.
form.addEventListener('input', hideError);

form.addEventListener('submit', async (e) => {
    e.preventDefault();
    if (submit.disabled) return;          // ignore double-clicks / Enter spam
    hideError();
    document.getElementById('notice').hidden = true;

    if (!form.checkValidity()) { form.reportValidity(); return; }

    setLoading(true);
    let redirecting = false;
    try {
        const me = await api('/api/auth/login', {
            method: 'POST',
            body: {
                tenantId: tenant.value.trim(),
                email: document.getElementById('email').value.trim(),
                password: document.getElementById('password').value
            }
        });
        try { localStorage.setItem('itams.tenant', tenant.value.trim().toLowerCase()); } catch { /* ignore */ }
        redirecting = true;                 // keep the button in its loading state while the page changes
        location.replace(homeFor(me));
    } catch (err) {
        showError(err instanceof ApiError ? err.message : 'Cannot reach the server. Check your connection.');
        const pwField = document.getElementById('password');
        pwField.value = '';
        pwField.focus();
    } finally {
        if (!redirecting) setLoading(false);
    }
});

