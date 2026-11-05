// Password visibility is optional; the native form works without JavaScript.
for (const field of document.querySelectorAll('.app-password-field')) {
  const input = field.querySelector('input');
  const toggle = field.querySelector('button');
  if (!(input instanceof HTMLInputElement) || !(toggle instanceof HTMLButtonElement)) continue;

  toggle.hidden = false;
  toggle.setAttribute('aria-controls', input.id);
  toggle.addEventListener('click', () => {
    const show = input.type === 'password';
    input.type = show ? 'text' : 'password';
    toggle.textContent = show ? 'Hide' : 'Show';
    toggle.setAttribute('aria-label', show ? 'Hide password' : 'Show password');
  });
}
