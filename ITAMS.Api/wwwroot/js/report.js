// Public "report a fault" page. No sign-in: the organization is identified by the private token in the link (#token).
// The token is sent in a header, never in the URL path, and this page can only SUBMIT a report.

const $ = (id) => document.getElementById(id);
const CATEGORIES = ['Hardware', 'Printer', 'Network', 'Software', 'Other'];
const token = location.hash.slice(1);

function show(name) {
  for (const s of ['loading', 'invalid', 'form', 'done']) $(`state-${s}`).hidden = s !== name;
}

function call(method, body) {
  return fetch('/api/public/report', {
    method,
    headers: {
      'X-ITAMS-Request': '1',
      'X-Report-Token': token,
      ...(body ? { 'Content-Type': 'application/json' } : {})
    },
    credentials: 'omit',     // this page never uses (or needs) a session
    body: body ? JSON.stringify(body) : undefined
  });
}

async function firstError(res) {
  if (res.status === 429) return 'Too many reports were sent from this network just now. Wait a few minutes and try again, or call IT support.';
  try {
    const data = await res.json();
    if (data?.errors) return Object.values(data.errors).flat()[0];
    if (data?.title) return data.title;
  } catch { /* not JSON */ }
  return 'Your report could not be sent. Please try again, or call IT support.';
}

async function start() {
  for (const c of CATEGORIES) $('r-category').append(new Option(c, c));

  if (!/^[A-Za-z0-9_-]{16,64}$/.test(token)) { show('invalid'); return; }

  try {
    const res = await call('GET');
    if (!res.ok) { show('invalid'); return; }
    const { organization } = await res.json();
    $('org').textContent = organization;
    $('org-title').textContent = organization;
    document.title = `Report a fault: ${organization}`;
    show('form');
  } catch {
    $('state-loading').textContent = 'Cannot reach the server. Check your connection and refresh this page.';
  }
}

$('report-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  $('error').hidden = true;
  const form = $('report-form');
  if (!form.checkValidity()) { form.reportValidity(); return; }

  $('r-submit').disabled = true;
  try {
    const res = await call('POST', {
      name: $('r-name').value,
      contact: $('r-contact').value || null,
      location: $('r-location').value || null,
      assetReference: $('r-asset').value || null,
      category: $('r-category').value,
      impact: form.elements.impact.value,
      title: $('r-title').value,
      description: $('r-desc').value,
      website: $('r-website').value
    });

    if (res.status === 404) { show('invalid'); return; }
    if (!res.ok) { $('error').textContent = await firstError(res); $('error').hidden = false; return; }

    const { reference } = await res.json();
    $('ref').textContent = reference;
    show('done');
  } catch {
    $('error').textContent = 'Cannot reach the server. Check your connection and try again.';
    $('error').hidden = false;
  } finally {
    $('r-submit').disabled = false;
  }
});

$('again').addEventListener('click', () => {
  // keep the reporter's name and contact, clear what was specific to the last fault
  for (const id of ['r-title', 'r-desc', 'r-asset']) $(id).value = '';
  show('form');
  $('r-title').focus();
});

start();
