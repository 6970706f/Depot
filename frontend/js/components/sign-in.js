import { api, setToken } from '../api.js';
import { h } from '../dom.js';
import { openDialog } from './dialog.js';
import { field } from './form.js';

let open = null;

// Asks for Depot's access token when the API refuses a request without it. One dialog at a
// time: every request of a refresh fails the same way.
export function openSignIn() {
  if (open) return;

  const token = field({
    label: 'Access token', name: 'token', type: 'password', autocomplete: 'current-password',
    hint: "Not your qBittorrent password. Unless Auth:Token is set, run this in Depot's folder on the server and paste the result:",
  });
  const submit = h('button', { class: 'btn primary', type: 'submit' }, 'Continue');
  let busy = false;

  open = openDialog({
    title: "Enter Depot's access token",
    body: [
      h('p', { class: 'muted', text: 'This browser keeps the token after you enter it once.' }),
      token.el,
      h('code', { class: 'mono', text: 'cat data/access-token' }),
    ],
    footer: [submit],
    initialFocus: token.input,
    onClose: () => { open = null; },
    async onSubmit() {
      if (busy) return;
      busy = true;
      submit.disabled = true;
      token.setError(null);
      const value = token.value.trim();
      try {
        await api.checkToken(value);
        setToken(value);
        open.close();
      } catch (error) {
        token.setError(error.status === 401 ? "That isn't Depot's access token." : error.message);
        token.input.focus();
      } finally {
        busy = false;
        submit.disabled = false;
      }
    },
  });
}
