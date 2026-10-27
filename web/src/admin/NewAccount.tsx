import { useEffect, useRef, useState, type FormEvent } from 'react';
import { ApiError } from '../api';
import { Button, Status, type StatusMessage } from '../ui/components';
import { describe, type Call } from './AdminApp';

/** Admins create player accounts (registration on the account page can be closed). */
export function NewAccountDialog({ open, call, onClose }: { open: boolean; call: Call; onClose: () => void }) {
  const ref = useRef<HTMLDialogElement>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<StatusMessage | null>(null);
  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    if (open && !dialog.open) { setMessage(null); dialog.showModal(); }
    if (!open && dialog.open) dialog.close();
  }, [open]);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    const username = String(data.get('username'));
    setBusy(true);
    try {
      await call('/api/admin/accounts', { method: 'POST', body: { username, password: data.get('password') } });
      form.reset();
      setMessage({ text: `Account ${username} created. Give the player their username and password.`, kind: 'ok' });
    } catch (error) {
      const code = error instanceof ApiError ? error.code : undefined;
      setMessage({ kind: 'error', text: code === 'username_taken' ? 'That username is taken.'
        : code === 'invalid_account' ? 'Use a 3–32 character username (letters, numbers, underscore) and a 12–256 character password.'
        : error instanceof ApiError && error.status === 403 ? 'Only admins can create accounts.' : describe(error) });
    } finally { setBusy(false); }
  }

  return (
    <dialog ref={ref} className="dialog" onClose={onClose} aria-labelledby="new-account-title">
      <form onSubmit={submit}>
        <div className="dialog-body">
          <h2 id="new-account-title">New player account</h2>
          <div className="field">
            <label htmlFor="new-username">Username</label>
            <input id="new-username" name="username" className="input" required minLength={3} maxLength={32} pattern="[A-Za-z0-9_]+" autoComplete="off" />
            <small>3–32 letters, numbers or underscores.</small>
          </div>
          <div className="field">
            <label htmlFor="new-password">Password</label>
            <input id="new-password" name="password" type="password" className="input" required minLength={12} maxLength={256} autoComplete="new-password" />
            <small>At least 12 characters. The player can change it on the account page.</small>
          </div>
          <Status message={message} />
        </div>
        <div className="dialog-actions">
          <Button variant="ghost" onClick={onClose}>Close</Button>
          <Button type="submit" variant="primary" busy={busy}>Create account</Button>
        </div>
      </form>
    </dialog>
  );
}
