"""Sign in to the local account service when launching, instead of a downloaded file.

The launcher asks for the account's username and password (a small window, or a
terminal prompt without a display), signs in through the same public API as the
account page, and requests the five-minute single-use launch authorization that
the page would otherwise download. The password is never stored or logged; the
session token stays in memory and is signed out when the run ends (which also
revokes the game's world-session credentials, as a browser logout does).
"""
import getpass
import json
from urllib.error import HTTPError, URLError
from urllib.request import ProxyHandler, Request, build_opener


class AccountLoginError(ValueError):
    pass


class AccountSession:
    def __init__(self, port=8780):
        if not isinstance(port, int) or not 1024 <= port <= 65535:
            raise ValueError('Invalid account service port')
        self.base = f'http://127.0.0.1:{port}'
        self.token = None
        self._opener = build_opener(ProxyHandler({}))

    def __repr__(self):
        return 'AccountSession [credentials redacted]'

    def _post(self, path, body=None):
        headers = {'Content-Type': 'application/json'}
        if self.token:
            headers['Authorization'] = 'Bearer ' + self.token
        request = Request(self.base + path, data=json.dumps(body or {}).encode(), method='POST', headers=headers)
        try:
            with self._opener.open(request, timeout=10) as response:
                raw = response.read(65536)
                return json.loads(raw) if raw else None
        except HTTPError as error:
            if error.code == 401:
                raise AccountLoginError('Wrong username or password.') from None
            if error.code == 429:
                raise AccountLoginError('Too many attempts; wait a minute and try again.') from None
            raise AccountLoginError(f'Account service error (HTTP {error.code}).') from None
        except (URLError, OSError, ValueError):
            raise AccountLoginError('Account service is not running (python tools/run_account_server.py).') from None

    def sign_in(self, username, password):
        session = self._post('/api/accounts/login', {'username': username, 'password': password})
        token = (session or {}).get('token')
        if not isinstance(token, str) or not token:
            raise AccountLoginError('Account service returned no session.')
        self.token = token

    def launch_authorization(self):
        """The same {authorization, worldId} the account page downloads as AionLaunch.json."""
        if not self.token:
            raise AccountLoginError('Not signed in.')
        authorization = self._post('/api/client-launches')
        if not isinstance(authorization, dict) or 'authorization' not in authorization:
            raise AccountLoginError('Account service returned no launch authorization.')
        return authorization

    def sign_out(self):
        if not self.token:
            return
        try:
            self._post('/api/accounts/logout')
        except AccountLoginError:
            pass  # The session still expires on its own.
        finally:
            self.token = None


def login_window(attempt, username=''):
    """Show a sign-in window until `attempt(username, password)` returns None
    (success) or the user cancels. `attempt` returns an error message to show.
    Returns True when signed in. Raises RuntimeError when no display exists."""
    import tkinter
    from tkinter import ttk

    root = tkinter.Tk()
    root.title('AION2 dev sign-in')
    root.resizable(False, False)
    frame = ttk.Frame(root, padding=16)
    frame.grid()
    ttk.Label(frame, text='Username').grid(row=0, column=0, sticky='w', pady=4)
    user = ttk.Entry(frame, width=28)
    user.insert(0, username)
    user.grid(row=0, column=1, pady=4)
    ttk.Label(frame, text='Password').grid(row=1, column=0, sticky='w', pady=4)
    secret = ttk.Entry(frame, width=28, show='•')
    secret.grid(row=1, column=1, pady=4)
    status = ttk.Label(frame, text='Sign in to launch the game.', foreground='#555', wraplength=260)
    status.grid(row=2, column=0, columnspan=2, sticky='w', pady=(8, 4))
    result = {'ok': False}

    def submit(_event=None):
        status.configure(text='Signing in…', foreground='#555')
        root.update_idletasks()
        error = attempt(user.get().strip(), secret.get())
        secret.delete(0, 'end')
        if error is None:
            result['ok'] = True
            root.destroy()
        else:
            status.configure(text=error, foreground='#b00020')
            secret.focus_set()

    buttons = ttk.Frame(frame)
    buttons.grid(row=3, column=0, columnspan=2, sticky='e', pady=(8, 0))
    ttk.Button(buttons, text='Cancel', command=root.destroy).grid(row=0, column=0, padx=4)
    ttk.Button(buttons, text='Sign in and launch', command=submit).grid(row=0, column=1)
    root.bind('<Return>', submit)
    root.bind('<Escape>', lambda _event: root.destroy())
    (secret if username else user).focus_set()
    root.attributes('-topmost', True)
    root.after(500, lambda: root.attributes('-topmost', False))
    root.mainloop()
    return result['ok']


def sign_in_for_launch(session, username=''):
    """Interactive sign-in; returns the launch authorization dict, or None if cancelled."""
    authorization = {}

    def attempt(name, password):
        if not name or not password:
            return 'Enter your username and password.'
        try:
            session.sign_in(name, password)
            authorization.update(session.launch_authorization())
            return None
        except AccountLoginError as error:
            return str(error)

    signed_in = None
    try:
        import tkinter
        try:
            signed_in = login_window(attempt, username)
        except tkinter.TclError:
            pass  # no display
    except ImportError:
        pass
    if signed_in is None:  # terminal prompt
        signed_in = False
        for _ in range(3):
            name = username or input('Username: ').strip()
            error_text = attempt(name, getpass.getpass('Password: '))
            if error_text is None:
                signed_in = True
                break
            print(error_text)
    return authorization if signed_in else None
