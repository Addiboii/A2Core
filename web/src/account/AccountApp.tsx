import { useEffect, useState, type FormEvent } from 'react';
import { api, ApiError } from '../api';
import { Button, Status, type StatusMessage } from '../ui/components';

interface Character { name: string; level: number }

function describe(error: unknown, signingIn = false): string {
  if (!(error instanceof ApiError)) return 'Something went wrong.';
  if (error.status === 0) return 'The server could not be reached.';
  if (error.status === 401) return signingIn ? 'Sign-in failed. Check your username and password.' : 'Your session expired. Sign in again.';
  if (error.status === 409) return 'That username is unavailable.';
  if (error.status === 429) return 'Too many attempts. Please wait a minute.';
  return error.code || 'The server could not complete this request.';
}

export function AccountApp() {
  const [token, setToken] = useState<string | null>(null);
  const [username, setUsername] = useState('');
  const [characters, setCharacters] = useState<Character[]>([]);
  const [message, setMessage] = useState<StatusMessage | null>(null);
  const [configured, setConfigured] = useState(true);
  const [registrationOpen, setRegistrationOpen] = useState(false);
  const [launcherAvailable, setLauncherAvailable] = useState(false);

  useEffect(() => {
    api<{ accountsConfigured: boolean; registrationOpen?: boolean; launcherAvailable?: boolean }>('/api/status')
      .then(status => {
        setRegistrationOpen(Boolean(status.registrationOpen));
        setLauncherAvailable(Boolean(status.launcherAvailable));
        if (!status.accountsConfigured) { setConfigured(false); setMessage({ text: 'The account service is not configured yet.', kind: 'error' }); }
      })
      .catch(error => setMessage({ text: describe(error), kind: 'error' }));
  }, []);

  function signedOut(text: string, kind: StatusMessage['kind'] = '') {
    setToken(null); setCharacters([]); setMessage({ text, kind });
  }

  async function signIn(name: string, password: string, register: boolean) {
    if (register) await api('/api/accounts/register', { method: 'POST', body: { username: name, password } });
    const session = await api<{ token: string }>('/api/accounts/login', { method: 'POST', body: { username: name, password } });
    const list = await api<Character[]>('/api/characters', { token: session.token });
    setToken(session.token); setUsername(name); setCharacters(list);
    setMessage({ text: register ? 'Account created. Welcome!' : '', kind: 'ok' });
  }

  return (
    <div className="account-page">
      <main className="card account-card">
        <div className="card-body stack">
          <div className="stack-sm">
            <h1>{token ? `Welcome, ${username}` : registrationOpen ? 'Your account' : 'Sign in'}</h1>
            <p className="muted">{token ? 'Use A2Core Launcher with this account to play.'
              : registrationOpen ? 'Create an account or sign in to see your characters.' : 'Sign in to see your characters. Accounts are created by an admin.'}</p>
          </div>
          {token
            ? <Signed token={token} characters={characters} onSignedOut={signedOut} onMessage={setMessage} />
            : <SignIn disabled={!configured} registrationOpen={registrationOpen} onSubmit={signIn} onError={error => setMessage({ text: describe(error, true), kind: 'error' })} />}
          <Status message={message} />
          {token && launcherAvailable && <LauncherDownload token={token} onMessage={setMessage} />}
        </div>
      </main>
    </div>
  );
}

/** The launcher download: signed-in accounts get a short-lived link from the server, then the browser downloads it. */
function LauncherDownload({ token, onMessage }: { token: string; onMessage: (m: StatusMessage) => void }) {
  const [busy, setBusy] = useState(false);
  async function download() {
    setBusy(true);
    try {
      const { url } = await api<{ url: string }>('/api/downloads/launcher', { method: 'POST', token });
      const link = document.createElement('a');
      link.href = url;
      link.download = 'A2Core Launcher.exe';
      link.click();
    } catch (error) { onMessage({ text: describe(error), kind: 'error' }); }
    finally { setBusy(false); }
  }
  return (
    <div className="download">
      <div className="stack-sm">
        <h4>Play</h4>
        <p className="muted small">Download A2Core Launcher for Windows. It sets up the game and signs you in.</p>
      </div>
      <Button variant="primary" busy={busy} onClick={download}>Download A2Core Launcher</Button>
    </div>
  );
}

function SignIn({ disabled, registrationOpen, onSubmit, onError }:
  { disabled: boolean; registrationOpen: boolean; onSubmit: (name: string, password: string, register: boolean) => Promise<void>; onError: (error: unknown) => void }) {
  const [mode, setMode] = useState<'signin' | 'register'>('signin');
  const [busy, setBusy] = useState(false);
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    setBusy(true);
    try { await onSubmit(String(data.get('username')), String(data.get('password')), mode === 'register'); }
    catch (error) { onError(error); }
    finally {
      setBusy(false);
      const password = form.elements.namedItem('password');
      if (password instanceof HTMLInputElement) password.value = '';
    }
  }
  const register = registrationOpen && mode === 'register';
  return (
    <form className="stack" onSubmit={submit}>
      {registrationOpen && (
        <div className="segmented" role="tablist" aria-label="Sign in or create an account">
          <button type="button" role="tab" aria-selected={!register} onClick={() => setMode('signin')}>Sign in</button>
          <button type="button" role="tab" aria-selected={register} onClick={() => setMode('register')}>Create account</button>
        </div>
      )}
      <div className="field">
        <label htmlFor="username">Username</label>
        <input id="username" name="username" className="input" autoComplete="username" required maxLength={32}
          {...(register ? { minLength: 3, pattern: '[A-Za-z0-9_]+' } : {})} />
        {register && <small>3–32 letters, numbers or underscores.</small>}
      </div>
      <div className="field">
        <label htmlFor="password">Password</label>
        <input id="password" name="password" type="password" className="input" autoComplete={register ? 'new-password' : 'current-password'} required maxLength={256}
          {...(register ? { minLength: 12 } : {})} />
        {register && <small>At least 12 characters.</small>}
      </div>
      <Button type="submit" variant="primary" block busy={busy} disabled={disabled}>{register ? 'Create account' : 'Sign in'}</Button>
    </form>
  );
}

function Signed({ token, characters, onSignedOut, onMessage }:
  { token: string; characters: Character[]; onSignedOut: (text: string, kind?: StatusMessage['kind']) => void; onMessage: (m: StatusMessage) => void }) {
  const [busy, setBusy] = useState<string | null>(null);

  async function run(name: string, action: () => Promise<void>) {
    setBusy(name);
    try { await action(); }
    catch (error) { onMessage({ text: describe(error), kind: 'error' }); }
    finally { setBusy(null); }
  }

  async function changePassword(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    await run('password', async () => {
      await api('/api/accounts/password', { method: 'POST', token, body: { currentPassword: data.get('current'), newPassword: data.get('next') } });
      onSignedOut('Password changed. Sign in with your new password.', 'ok');
    });
    form.reset();
  }

  return (
    <div className="stack">
      <section className="stack-sm">
        <h4>Characters</h4>
        {characters.length
          ? <ul className="character-list">{characters.map(c => <li key={c.name}><span>{c.name}</span><span className="badge">Level {c.level}</span></li>)}</ul>
          : <p className="empty">No characters yet. Create one in the game.</p>}
      </section>
      <hr className="divider" />
      <ContactStaff token={token} characters={characters} />
      <hr className="divider" />
      <details className="security">
        <summary>Account security</summary>
        <form className="stack" onSubmit={changePassword}>
          <div className="field">
            <label htmlFor="current">Current password</label>
            <input id="current" name="current" type="password" className="input" autoComplete="current-password" required maxLength={256} />
          </div>
          <div className="field">
            <label htmlFor="next">New password</label>
            <input id="next" name="next" type="password" className="input" autoComplete="new-password" required minLength={12} maxLength={256} />
            <small>At least 12 characters. Changing it signs you out everywhere.</small>
          </div>
          <div className="row wrap">
            <Button type="submit" variant="primary" busy={busy === 'password'}>Change password</Button>
            <Button variant="danger" busy={busy === 'revoke'} onClick={() => run('revoke', async () => {
              await api('/api/accounts/revoke-sessions', { method: 'POST', token });
              onSignedOut('Signed out on every device.', 'ok');
            })}>Sign out everywhere</Button>
          </div>
        </form>
      </details>
      <div className="row">
        <Button busy={busy === 'logout'} onClick={() => run('logout', async () => {
          await api('/api/accounts/logout', { method: 'POST', token });
          onSignedOut('Signed out.');
        })}>Sign out</Button>
      </div>
    </div>
  );
}

interface MyTicket { id: number; createdAt: string; category: string; content: string; status: string; staffNote: string; handledAt: string | null }
const TICKET_KIND: Record<string, string> = { bug: 'Bug', player: 'Report a player', stuck: 'I am stuck', other: 'Other' };

/** Reports and questions for the staff (also "//report <text>" in game chat), with their answers. */
function ContactStaff({ token, characters }: { token: string; characters: Character[] }) {
  const [tickets, setTickets] = useState<MyTicket[]>([]);
  const [status, setStatus] = useState<StatusMessage | null>(null);
  const [busy, setBusy] = useState(false);
  const load = () => api<MyTicket[]>('/api/tickets', { token }).then(setTickets).catch(() => setTickets([]));
  useEffect(() => { load(); }, [token]);  // eslint-disable-line react-hooks/exhaustive-deps
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    setBusy(true);
    try {
      const r = await api<{ id: number }>('/api/tickets', { method: 'POST', token, body: { category: data.get('category'), character: data.get('character') || null, content: data.get('content') } });
      setStatus({ text: `Sent as #${r.id}. The staff answer here.`, kind: 'ok' });
      form.reset();
      load();
    } catch (error) {
      const code = error instanceof ApiError ? error.code : undefined;
      setStatus({ text: code === 'too_many_tickets' ? 'You have 10 open reports already. Please wait for an answer.' : code === 'invalid_ticket' ? 'Write between 3 and 1000 characters.' : describe(error), kind: 'error' });
    } finally { setBusy(false); }
  }
  return (
    <details className="security">
      <summary>Contact the staff{tickets.some(t => t.status === 'closed' && t.staffNote) ? ' (answered)' : ''}</summary>
      <form className="stack" onSubmit={submit}>
        <div className="row wrap">
          <select name="category" className="select" defaultValue="bug" aria-label="Kind">
            {Object.entries(TICKET_KIND).map(([id, label]) => <option key={id} value={id}>{label}</option>)}
          </select>
          {characters.length > 0 && (
            <select name="character" className="select" defaultValue={characters[0].name} aria-label="Character">
              {characters.map(c => <option key={c.name} value={c.name}>{c.name}</option>)}
            </select>
          )}
        </div>
        <textarea name="content" className="input" rows={4} required minLength={3} maxLength={1000} placeholder="What happened? For a player report, include their name." />
        <div className="row"><Button type="submit" variant="primary" busy={busy}>Send</Button></div>
        <Status message={status} />
      </form>
      {tickets.length > 0 && (
        <ul className="ticket-history">
          {tickets.map(t => (
            <li key={t.id}>
              <div className="row-between"><strong>#{t.id} · {TICKET_KIND[t.category] ?? t.category}</strong><span className="badge">{t.status === 'open' ? 'Waiting' : 'Answered'}</span></div>
              <p className="muted">{t.content}</p>
              {t.staffNote && <p><strong>Staff:</strong> {t.staffNote}</p>}
            </li>
          ))}
        </ul>
      )}
    </details>
  );
}
