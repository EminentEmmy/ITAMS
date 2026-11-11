// Small fetch wrapper. Cookies carry the tokens (HttpOnly, so JS never sees them).
// The X-ITAMS-Request header is the CSRF guard the server requires on every POST/PUT/PATCH/DELETE.

export class ApiError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}

let refreshing = null;

function send(path, method, body) {
  const headers = { 'X-ITAMS-Request': '1' };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  return fetch(path, {
    method,
    headers,
    credentials: 'same-origin',
    body: body === undefined ? undefined : JSON.stringify(body)
  });
}

// One refresh at a time, shared by every request that hits a 401 together.
function refreshSession() {
  refreshing ??= send('/api/auth/refresh', 'POST')
    .then(r => r.ok)
    .catch(() => false)
    .finally(() => { refreshing = null; });
  return refreshing;
}

async function readError(res) {
  try {
    const data = await res.json();
    const message = data?.error ?? (data?.errors ? Object.values(data.errors).flat()[0] : null) ?? data?.title;
    if (message) return { message, code: data?.code };
  } catch { /* not JSON */ }
  if (res.status === 403) return { message: 'Your role does not allow this action.' };
  if (res.status === 429) return { message: 'Too many requests. Wait a minute and try again.' };
  return { message: 'Something went wrong. Try again.' };
}

export async function api(path, { method = 'GET', body } = {}) {
  let res = await send(path, method, body);

  const isAuthCall = path === '/api/auth/login' || path === '/api/auth/refresh';
  if (res.status === 401 && !isAuthCall) {
    if (await refreshSession()) res = await send(path, method, body);
    if (res.status === 401) {
      location.replace('/login.html');
      throw new ApiError(401, 'Your session has ended. Sign in again.');
    }
  }

  if (!res.ok) {
    const { message, code } = await readError(res);
    if (code === 'password_change_required') location.replace('/change-password.html');
    throw new ApiError(res.status, message);
  }
  return res.status === 204 ? null : res.json();
}

// Sign the user out after a period with no clicks, keys, scrolling or pointer movement.
export function watchInactivity(ms, onTimeout) {
  let timer;
  const reset = () => { clearTimeout(timer); timer = setTimeout(onTimeout, ms); };
  ['click', 'keydown', 'pointermove', 'scroll'].forEach(evt => addEventListener(evt, reset, { passive: true }));
  reset();
}

// ---- tiny DOM helpers (textContent only, never innerHTML) ----

export function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (value === false || value == null) continue;
    if (key === 'class') node.className = value;
    else if (key.startsWith('on')) node.addEventListener(key.slice(2), value);
    else node.setAttribute(key, value === true ? '' : value);
  }
  for (const child of children.flat()) node.append(child ?? '');
  return node;
}

export const label = (s) => s.replace(/([a-z])([A-Z])/g, '$1 $2');
export const fmtDate = (iso) => new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
export const homeFor = (me) => me.mustChangePassword ? '/change-password.html' : me.role === 'SuperAdmin' ? '/platform.html' : '/index.html';
