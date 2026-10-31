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
  // Two-step accounts: the password gave a challenge; the code completes the sign-in.
  const [challenge, setChallenge] = useState<{ token: string; username: string; registered: boolean } | null>(null);

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
    const session = await api<{ token?: string; requiresTotp?: boolean; challengeToken?: string }>('/api/accounts/login', { method: 'POST', body: { username: name, password } });
    if (session.requiresTotp && session.challengeToken) {
      setChallenge({ token: session.challengeToken, username: name, registered: register });
      setMessage(null);
      return;
    }
    await signedIn(session.token!, name, register);
  }

  async function signedIn(sessionToken: string, name: string, registered: boolean) {
    const list = await api<Character[]>('/api/characters', { token: sessionToken });
    setToken(sessionToken); setUsername(name); setCharacters(list); setChallenge(null);
    setMessage({ text: registered ? 'Account created. Welcome!' : '', kind: 'ok' });
  }

  async function completeTwoStep(code: string, recoveryCode: string) {
    if (!challenge) return;
    const session = await api<{ token: string }>('/api/accounts/login/totp', {
      method: 'POST', body: { challengeToken: challenge.token, code: code || null, recoveryCode: recoveryCode || null },
    });
    await signedIn(session.token, challenge.username, challenge.registered);
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
            : challenge
              ? <TwoStepSignIn onSubmit={completeTwoStep} onCancel={() => { setChallenge(null); setMessage(null); }}
                  onError={error => setMessage({ text: error instanceof ApiError && error.status === 401
                    ? 'That code did not work, or the sign-in expired. After 5 wrong codes, code entry is locked for 15 minutes.' : describe(error), kind: 'error' })} />
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

/** Second sign-in step for accounts with two-step sign-in: a code from the app, or a recovery code. */
function TwoStepSignIn({ onSubmit, onCancel, onError }:
  { onSubmit: (code: string, recoveryCode: string) => Promise<void>; onCancel: () => void; onError: (error: unknown) => void }) {
  const [busy, setBusy] = useState(false);
  const [recovery, setRecovery] = useState(false);
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const value = String(new FormData(event.currentTarget).get('code') ?? '').trim();
    setBusy(true);
    try { await (recovery ? onSubmit('', value) : onSubmit(value, '')); }
    catch (error) { onError(error); }
    finally { setBusy(false); }
  }
  return (
    <form className="stack" onSubmit={submit}>
      <div className="field">
        <label htmlFor="two-step-code">{recovery ? 'Recovery code' : 'Code from your authenticator app'}</label>
        {recovery
          ? <input key="recovery" id="two-step-code" name="code" className="input" autoComplete="off" required minLength={10} maxLength={32} />
          : <input key="code" id="two-step-code" name="code" className="input" inputMode="numeric" pattern="[0-9]{6}" required maxLength={6} autoComplete="one-time-code" autoFocus />}
      </div>
      <Button type="submit" variant="primary" block busy={busy}>Continue</Button>
      <div className="row wrap">
        <Button onClick={() => setRecovery(!recovery)}>{recovery ? 'Use an app code' : 'Use a recovery code'}</Button>
        <Button onClick={onCancel}>Back</Button>
      </div>
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
      <TwoStepSettings token={token} onMessage={onMessage} />
      <div className="row">
        <Button busy={busy === 'logout'} onClick={() => run('logout', async () => {
          await api('/api/accounts/logout', { method: 'POST', token });
          onSignedOut('Signed out.');
        })}>Sign out</Button>
      </div>
    </div>
  );
}

interface TwoStepStatus { available: boolean; enabled: boolean; recoveryCodesRemaining: number }

/** Two-step sign-in (TOTP): turn on with the password and a first code, renew recovery codes, turn off. */
function TwoStepSettings({ token, onMessage }: { token: string; onMessage: (m: StatusMessage) => void }) {
  const [status, setStatus] = useState<TwoStepStatus | null>(null);
  const [setup, setSetup] = useState<{ secret: string; otpAuthUri: string } | null>(null);
  const [codes, setCodes] = useState<string[] | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const load = () => api<TwoStepStatus>('/api/accounts/totp/status', { token }).then(setStatus).catch(() => setStatus(null));
  useEffect(() => { load(); }, [token]);  // eslint-disable-line react-hooks/exhaustive-deps

  async function run(name: string, form: HTMLFormElement, action: (data: FormData) => Promise<void>, wrong: string) {
    setBusy(name);
    try { await action(new FormData(form)); form.reset(); }
    catch (error) { onMessage({ text: error instanceof ApiError && error.status === 403 ? wrong : describe(error), kind: 'error' }); }
    finally { setBusy(null); }
  }
  const verified = (data: FormData) => ({ password: data.get('password'), code: data.get('code') || null, recoveryCode: data.get('recovery') || null });
  function enroll(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    run('enroll', event.currentTarget, async data => {
      setSetup(await api<{ secret: string; otpAuthUri: string }>('/api/accounts/totp/enroll', { method: 'POST', token, body: { password: data.get('password') } }));
    }, 'Wrong password.');
  }
  function confirm(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    run('confirm', event.currentTarget, async data => {
      const r = await api<{ recoveryCodes: string[] }>('/api/accounts/totp/confirm', { method: 'POST', token, body: { code: data.get('code') } });
      setSetup(null); setCodes(r.recoveryCodes); await load();
      onMessage({ text: 'Two-step sign-in is on.', kind: 'ok' });
    }, 'That code is not valid. Check the time on your phone.');
  }
  function change(name: 'renew' | 'disable', form: HTMLFormElement | null) {
    if (!form?.reportValidity()) return;
    run(name, form, async data => {
      if (name === 'renew') {
        const r = await api<{ recoveryCodes: string[] }>('/api/accounts/totp/recovery/regenerate', { method: 'POST', token, body: verified(data) });
        setCodes(r.recoveryCodes);
      } else {
        await api('/api/accounts/totp/disable', { method: 'POST', token, body: verified(data) });
        setCodes(null);
        onMessage({ text: 'Two-step sign-in is off.', kind: 'ok' });
      }
      await load();
    }, 'Wrong password or code.');
  }

  if (!status) return null;
  return (
    <details className="security" id="two-step">
      <summary>Two-step sign-in{status.enabled ? ' (on)' : ''}</summary>
      {!status.available && <p className="muted">Two-step sign-in is not available on this server.</p>}
      {codes && (
        <div className="stack-sm">
          <p><strong>Recovery codes.</strong> Each one signs you in once if you lose your phone. Save them now; they are not shown again.</p>
          <ul className="character-list">{codes.map(c => <li key={c}><code>{c}</code></li>)}</ul>
          <div className="row"><Button onClick={() => setCodes(null)}>I saved them</Button></div>
        </div>
      )}
      {status.available && !status.enabled && !setup && (
        <form className="stack" onSubmit={enroll}>
          <p className="muted small">Sign-ins will also ask for a code from an authenticator app on your phone.</p>
          <div className="field">
            <label htmlFor="totp-enroll-password">Password</label>
            <input id="totp-enroll-password" name="password" type="password" className="input" autoComplete="current-password" required maxLength={256} />
          </div>
          <div className="row"><Button type="submit" variant="primary" busy={busy === 'enroll'}>Set up two-step sign-in</Button></div>
        </form>
      )}
      {status.available && !status.enabled && setup && (
        <form className="stack" onSubmit={confirm}>
          <p className="muted small">Add this key to your authenticator app (or open the link on your phone), then enter the code it shows.</p>
          <code>{setup.secret.match(/.{1,4}/g)?.join(' ')}</code>
          <a className="small" href={setup.otpAuthUri}>Open in authenticator app</a>
          <div className="field">
            <label htmlFor="totp-confirm-code">Code</label>
            <input id="totp-confirm-code" name="code" className="input" inputMode="numeric" pattern="[0-9]{6}" required maxLength={6} autoComplete="one-time-code" />
          </div>
          <div className="row wrap">
            <Button type="submit" variant="primary" busy={busy === 'confirm'}>Turn on</Button>
            <Button onClick={() => setSetup(null)}>Cancel</Button>
          </div>
        </form>
      )}
      {status.available && status.enabled && (
        <form className="stack" onSubmit={event => event.preventDefault()}>
          <p className="muted small">On. {status.recoveryCodesRemaining} recovery code{status.recoveryCodesRemaining === 1 ? '' : 's'} left.
            Changing this needs your password and an app code (or a recovery code).</p>
          <div className="field">
            <label htmlFor="totp-password">Password</label>
            <input id="totp-password" name="password" type="password" className="input" autoComplete="current-password" required maxLength={256} />
          </div>
          <div className="row wrap">
            <input name="code" className="input" inputMode="numeric" pattern="[0-9]{6}" maxLength={6} placeholder="App code" aria-label="App code" autoComplete="one-time-code" />
            <input name="recovery" className="input" maxLength={32} placeholder="or recovery code" aria-label="Recovery code" autoComplete="off" />
          </div>
          <div className="row wrap">
            <Button busy={busy === 'renew'} onClick={event => change('renew', event.currentTarget.form)}>New recovery codes</Button>
            <Button variant="danger" busy={busy === 'disable'} onClick={event => change('disable', event.currentTarget.form)}>Turn off</Button>
          </div>
        </form>
      )}
    </details>
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
