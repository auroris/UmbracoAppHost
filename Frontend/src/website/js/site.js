// The website theme's behaviour: the menu folded on narrow screens, and its submenus for keyboard and touch.
// Everything else is CSS. A page without this script still shows the whole menu, opened by hovering.

const openClass = 'is-open';

/**
 * @param {Element} item
 * @param {boolean} open
 */
function setSubmenu(item, open) {
  item.classList.toggle(openClass, open);
  item.querySelector(':scope > .site-submenu-toggle')?.setAttribute('aria-expanded', String(open));
}

/** @param {Element} [except] the open submenu, and so its ancestors, to leave open */
function closeSubmenus(except) {
  for (const item of document.querySelectorAll('.site-menu .has-submenu.' + openClass)) {
    if (!item.contains(except ?? null)) setSubmenu(item, false);
  }
}

document.documentElement.classList.add('js');

const menuToggle = document.querySelector('.site-nav-toggle');
const nav = document.querySelector('.site-nav');
menuToggle?.addEventListener('click', () => {
  const open = !nav?.classList.contains(openClass);
  nav?.classList.toggle(openClass, open);
  menuToggle.setAttribute('aria-expanded', String(open));
});

// A group's title has no address, so it opens the group as its button does.
for (const item of document.querySelectorAll('.site-menu .has-submenu')) {
  const opener = (/** @type {Event} */ event) => {
    event.stopPropagation();
    const open = !item.classList.contains(openClass);
    closeSubmenus(item);
    setSubmenu(item, open);
  };
  item.querySelector(':scope > .site-submenu-toggle')?.addEventListener('click', opener);
  item.querySelector(':scope > a:not([href])')?.addEventListener('click', opener);
}

document.addEventListener('click', (event) => {
  if (event.target instanceof Element && !event.target.closest('.site-menu')) closeSubmenus();
});

document.addEventListener('keydown', (event) => {
  if (event.key !== 'Escape') return;
  const open = document.activeElement?.closest('.has-submenu.' + openClass);
  if (open === null || open === undefined) return;
  setSubmenu(open, false);
  /** @type {HTMLElement | null} */ (open.querySelector(':scope > .site-submenu-toggle'))?.focus();
});
