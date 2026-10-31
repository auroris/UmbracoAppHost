// The application shell's behaviour: the menu toggle, remembered per browser. Narrow screens
// start with the menu hidden, wide screens with it shown, until the visitor chooses.

const STORAGE_KEY = 'app-nav-hidden';
const body = document.body;
const toggle = document.querySelector('.app-nav-toggle');
const narrow = window.matchMedia('(max-width: 48rem)');

/** @param {boolean} hidden */
function apply(hidden) {
  body.classList.toggle('app-nav-hidden', hidden);
  toggle?.setAttribute('aria-expanded', String(!hidden));
}

/** @returns {string|null} */
function stored() {
  try {
    return localStorage.getItem(STORAGE_KEY);
  } catch {
    return null;
  }
}

const preference = stored();
apply(preference === null ? narrow.matches : preference === 'true');

toggle?.addEventListener('click', () => {
  const hidden = !body.classList.contains('app-nav-hidden');
  apply(hidden);
  try {
    localStorage.setItem(STORAGE_KEY, String(hidden));
  } catch {
    // Storage is a convenience; the toggle still works without it.
  }
});

// Native details/summary supplies keyboard activation and expanded state.
const account = document.querySelector('.app-account');
document.addEventListener('click', (event) => {
  if (event.target instanceof Node && !account?.contains(event.target)) account?.removeAttribute('open');
});
document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && account?.hasAttribute('open')) {
    account.removeAttribute('open');
    account.querySelector('summary')?.focus();
  }
});
