(() => {
  'use strict';

  const { h, openDialog, signIn, textField } = MediaFlow;

  let open = null;

  // Asks for Depot's access token when the API refuses a request without it. One dialog at a
  // time: every request of a refresh fails the same way.
  function openSignInDialog() {
    if (open) return;

    const field = textField({
      label: 'Access token', type: 'password', autocomplete: 'current-password', name: 'token',
      hint: 'Not your qBittorrent password: Depot creates its own token on first start. Unless the Auth:Token setting provides it, run this in the Depot folder on the server and paste the result:',
    });
    const command = h('pre', { class: 'sign-in-command' }, h('code', { textContent: 'cat data/access-token' }));
    const submit = h('button', { class: 'btn btn-primary', type: 'submit', textContent: 'Continue' });
    let submitting = false;

    open = openDialog({
      title: 'Enter Depot\'s access token',
      body: [
        h('p', { textContent: 'Depot only answers this browser once it has Depot\'s access token. You enter it once; this browser keeps it.' }),
        field.el,
        command,
      ],
      footer: [submit],
      initialFocus: field.input,
      async onSubmit() {
        if (submitting) return;
        submitting = true;
        submit.setAttribute('aria-busy', 'true');
        field.setError(null);
        try {
          await signIn(field.input.value);
          // signIn announces the change, which refreshes the page.
          open.close();
        } catch (error) {
          field.setError(error.message);
          field.input.focus();
        } finally {
          submitting = false;
          submit.removeAttribute('aria-busy');
        }
      },
    });
    open.dialog.addEventListener('close', () => {
      open = null;
    });
  }

  Object.assign(MediaFlow, { openSignInDialog });
})();
