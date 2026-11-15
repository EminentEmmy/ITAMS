// Sidebar app shell shared by the customer dashboard and the platform console.
// Builds the sidebar and the mobile top bar with DOM calls only (no innerHTML), so the strict CSP still holds.
import { el, fmtDate } from './api.js';

const NS = 'http://www.w3.org/2000/svg';

// Simple 24x24 line icons (stroke only). Add a new section's icon here.
const ICONS = {
  assets: 'M3 5h18v11H3z M8 20h8 M12 16v4',
  staff: 'M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8z M4 21c0-4 3.6-7 8-7s8 3 8 7',
  audit: 'M8 4h8v3H8z M6 5H5a1 1 0 0 0-1 1v14a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1V6a1 1 0 0 0-1-1h-1 M8 12h8 M8 16h8',
  organizations: 'M4 21V4h10v17 M14 9h6v12 M8 8h2 M8 12h2 M8 16h2 M3 21h18',
  key: 'M15 5a4 4 0 1 0 0 8 4 4 0 0 0 0-8z M12 12 4 20 M6.5 17.5l2 2',
  logout: 'M10 4H5a1 1 0 0 0-1 1v14a1 1 0 0 0 1 1h5 M14 8l4 4-4 4 M18 12H9',
  collapse: 'M11 7l-5 5 5 5 M18 7l-5 5 5 5',
  menu: 'M4 6h16 M4 12h16 M4 18h16',
  tickets: 'M4 7h16v3a2 2 0 0 0 0 4v3H4v-3a2 2 0 0 0 0-4z M14 7v10',
  bell: 'M6 9a6 6 0 0 1 12 0c0 6 2 7 2 7H4s2-1 2-7 M10 20a2 2 0 0 0 4 0'
};

function icon(name) {
  const svg = document.createElementNS(NS, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('class', 'icon');
  svg.setAttribute('aria-hidden', 'true');
  svg.setAttribute('focusable', 'false');
  const path = document.createElementNS(NS, 'path');
  path.setAttribute('d', ICONS[name]);
  svg.append(path);
  return svg;
}

/**
 * @param items      [{ id, label, icon }] shown in the sidebar; each links to #id
 * @param user       the signed-in user ({ fullName })
 * @param orgLabel   text under the logo (company name, or "Platform console")
 * @param roleLabel  readable role shown under the user's name
 * @param variant    optional class on the shell ("platform" gives the owner console its own look)
 * @param onSignOut  called when "Sign out" is pressed
 * @param bell       optional notification bell: { onOpen(), onItemClick(item), onMarkAllRead() }. Omit for no bell.
 * @returns { setActive(id), setBadge(id, count), setTitle(text), setNotifications({ unread, items }) }
 */
export function initShell({ items, user, orgLabel, roleLabel, variant, onSignOut, bell }) {
  const shell = document.querySelector('.shell');
  if (variant) shell.classList.add(variant);

  // Remembered between visits (desktop only; phones always use the slide-out menu).
  try { if (localStorage.getItem('itams.sidebar') === 'collapsed') shell.classList.add('collapsed'); } catch { /* storage unavailable */ }

  // ---- navigation
  const links = new Map();
  const nav = el('nav', { class: 'side-nav', 'aria-label': 'Main' },
    items.map(item => {
      const link = el('a', { class: 'nav-item', href: `#${item.id}`, title: item.label, onclick: () => setDrawer(false) },
        icon(item.icon), el('span', { class: 'nav-label' }, item.label));
      links.set(item.id, link);
      return link;
    }));

  // ---- desktop: collapse to an icon rail
  const collapseBtn = el('button', {
    class: 'side-link collapse-btn', type: 'button', title: 'Collapse menu',
    'aria-pressed': String(shell.classList.contains('collapsed')),
    onclick: () => {
      const collapsed = shell.classList.toggle('collapsed');
      collapseBtn.setAttribute('aria-pressed', String(collapsed));
      try { localStorage.setItem('itams.sidebar', collapsed ? 'collapsed' : 'expanded'); } catch { /* ignore */ }
    }
  }, icon('collapse'), el('span', { class: 'nav-label' }, 'Collapse menu'));

  document.getElementById('sidebar').replaceChildren(
    el('div', { class: 'side-head' },
      el('span', { class: 'mark', 'aria-hidden': 'true' }),
      el('span', { class: 'brand-name nav-label' }, 'ITAMS')),
    el('div', { class: 'side-org nav-label' }, orgLabel),
    nav,
    el('div', { class: 'side-foot' },
      el('div', { class: 'side-user nav-label' },
        el('div', { class: 'user-name' }, user.fullName),
        el('div', { class: 'role' }, roleLabel)),
      el('a', { class: 'side-link', href: '/change-password.html', title: 'Change password' },
        icon('key'), el('span', { class: 'nav-label' }, 'Change password')),
      el('button', { class: 'side-link', type: 'button', title: 'Sign out', onclick: onSignOut },
        icon('logout'), el('span', { class: 'nav-label' }, 'Sign out')),
      collapseBtn));

  // ---- phones: top bar with a menu button, and a slide-out drawer
  const menuBtn = el('button', {
    class: 'menu-btn', type: 'button', 'aria-label': 'Open menu', 'aria-expanded': 'false', 'aria-controls': 'sidebar',
    onclick: () => setDrawer(!shell.classList.contains('open'))
  }, icon('menu'));

  // ---- top bar: menu button (phones), page title, and the notification bell
  const title = el('span', { class: 'topbar-title', 'aria-live': 'polite' });
  let setNotifications = () => {};
  const bellNodes = [];

  if (bell) {
    const countEl = el('span', { class: 'bell-count', hidden: true });
    const bellBtn = el('button', {
      class: 'bell-btn', type: 'button', 'aria-label': 'Notifications',
      'aria-haspopup': 'true', 'aria-expanded': 'false', 'aria-controls': 'notif-panel',
      onclick: () => toggleBell(panel.hidden)
    }, icon('bell'), countEl);

    const markBtn = el('button', { class: 'btn btn-quiet btn-small', type: 'button', disabled: true, onclick: () => bell.onMarkAllRead?.() }, 'Mark all read');
    const list = el('ul', { class: 'notif-list' });
    const panel = el('div', { class: 'notif-panel', id: 'notif-panel', hidden: true },
      el('div', { class: 'notif-head' }, el('h2', { class: 'notif-title' }, 'Notifications'), markBtn),
      list);

    function toggleBell(open) {
      panel.hidden = !open;
      bellBtn.setAttribute('aria-expanded', String(open));
      if (open) bell.onOpen?.();
    }

    setNotifications = ({ unread, items: entries }) => {
      countEl.textContent = unread > 99 ? '99+' : String(unread);
      countEl.hidden = unread === 0;
      bellBtn.setAttribute('aria-label', unread > 0 ? `Notifications, ${unread} unread` : 'Notifications');
      markBtn.disabled = unread === 0;

      list.replaceChildren(...(entries.length === 0
        ? [el('li', { class: 'notif-empty' }, 'You are all caught up.')]
        : entries.map(item => el('li', {},
            el('button', {
              class: `notif-item${item.unread ? ' unread' : ''}`, type: 'button',
              onclick: () => { toggleBell(false); bell.onItemClick?.(item); }
            },
              el('span', { class: 'notif-line' }, item.title),
              el('span', { class: 'notif-text' }, item.text),
              el('span', { class: 'notif-time' }, fmtDate(item.whenUtc)))))));
    };

    // close on a click anywhere else, and on Escape (focus returns to the bell)
    document.addEventListener('click', (e) => {
      if (!panel.hidden && !panel.contains(e.target) && !bellBtn.contains(e.target)) toggleBell(false);
    });
    document.addEventListener('keydown', (e) => {
      if (e.key === 'Escape' && !panel.hidden) { toggleBell(false); bellBtn.focus(); }
    });

    bellNodes.push(el('div', { class: 'bell' }, bellBtn, panel));
    setNotifications({ unread: 0, items: [] });
  }

  document.getElementById('topbar').replaceChildren(menuBtn, title, el('span', { class: 'topbar-spacer' }), ...bellNodes);

  shell.append(el('div', { class: 'scrim', onclick: () => setDrawer(false) }));

  function setDrawer(open) {
    const wasOpen = shell.classList.contains('open');
    shell.classList.toggle('open', open);
    menuBtn.setAttribute('aria-expanded', String(open));
    menuBtn.setAttribute('aria-label', open ? 'Close menu' : 'Open menu');
    if (open && !wasOpen) nav.querySelector('a')?.focus();
    if (!open && wasOpen) menuBtn.focus();
  }

  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && shell.classList.contains('open')) setDrawer(false);
  });

  // ---- keyboard users can jump past the menu
  document.body.prepend(el('button', {
    class: 'skip-link', type: 'button',
    onclick: () => document.getElementById('main').focus()
  }, 'Skip to content'));

  return {
    setActive(id) {
      for (const [key, link] of links) {
        if (key === id) link.setAttribute('aria-current', 'page');
        else link.removeAttribute('aria-current');
      }
    },

    // The current page's name in the top bar.
    setTitle(text) { title.textContent = text; },

    // Fill the bell: { unread: number, items: [{ title, text, whenUtc, unread }] }
    setNotifications(data) { setNotifications(data); },

    // A small count (for example new tickets) beside a menu item. 0 removes it.
    setBadge(id, count) {
      const link = links.get(id);
      if (!link) return;
      link.querySelector('.nav-badge')?.remove();
      if (count > 0) link.append(el('span', { class: 'nav-badge', 'aria-label': `${count} new` }, count > 99 ? '99+' : String(count)));
    }
  };
}
