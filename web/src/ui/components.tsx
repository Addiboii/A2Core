import { useEffect, useRef, type ButtonHTMLAttributes, type ReactNode } from 'react';

type Variant = 'primary' | 'secondary' | 'ghost' | 'danger';

export function Button({ variant = 'secondary', size, block, busy, className = '', children, ...rest }:
  ButtonHTMLAttributes<HTMLButtonElement> & { variant?: Variant; size?: 'sm'; block?: boolean; busy?: boolean }) {
  const classes = ['btn', `btn-${variant}`, size === 'sm' && 'btn-sm', block && 'btn-block', className].filter(Boolean).join(' ');
  return (
    <button type="button" className={classes} {...rest} disabled={rest.disabled || busy} aria-busy={busy || undefined}>
      {busy && <span className="spinner" aria-hidden="true" />}
      {children}
    </button>
  );
}

export type StatusKind = '' | 'error' | 'ok';
export interface StatusMessage { text: string; kind: StatusKind }

export function Status({ message }: { message: StatusMessage | null }) {
  return <p className={`status ${message?.kind ? 'status-' + message.kind : ''}`} role="status" aria-live="polite">{message?.text ?? ''}</p>;
}

export function Badge({ tone, children }: { tone?: 'accent' | 'danger' | 'ok'; children: ReactNode }) {
  return <span className={`badge ${tone ? 'badge-' + tone : ''}`}>{children}</span>;
}

export function Tabs<T extends string>({ tabs, value, onChange, label }:
  { tabs: { id: T; label: string }[]; value: T; onChange: (id: T) => void; label: string }) {
  return (
    <div className="tabs" role="tablist" aria-label={label}>
      {tabs.map(tab => (
        <button key={tab.id} type="button" role="tab" className="tab" aria-selected={tab.id === value} onClick={() => onChange(tab.id)}>
          {tab.label}
        </button>
      ))}
    </div>
  );
}

/** A confirmation dialog (native &lt;dialog&gt;: focus trap and Escape for free). */
export interface ConfirmRequest {
  title: string;
  body: ReactNode;
  note?: string;
  confirmLabel: string;
  danger?: boolean;
  onConfirm: () => void;
}

export function ConfirmDialog({ request, onClose }: { request: ConfirmRequest | null; onClose: () => void }) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    if (request && !dialog.open) dialog.showModal();
    if (!request && dialog.open) dialog.close();
  }, [request]);
  return (
    <dialog ref={ref} className="dialog" onClose={onClose} aria-labelledby="confirm-title">
      {request && (
        <form method="dialog" onSubmit={event => { event.preventDefault(); const act = request.onConfirm; onClose(); act(); }}>
          <div className="dialog-body">
            <h2 id="confirm-title">{request.title}</h2>
            <div>{request.body}</div>
            {request.note && <p className="muted small">{request.note}</p>}
          </div>
          <div className="dialog-actions">
            <Button variant="ghost" onClick={onClose}>Cancel</Button>
            <Button type="submit" variant={request.danger ? 'danger' : 'primary'} autoFocus>{request.confirmLabel}</Button>
          </div>
        </form>
      )}
    </dialog>
  );
}

/** A small form dialog (message, kick reason, teleport destination). Values are strings keyed by field name. */
export interface PromptField { name: string; label: string; placeholder?: string; type?: 'text' | 'number' | 'textarea'; maxLength?: number; required?: boolean; value?: string }
export interface PromptRequest {
  title: string;
  body?: ReactNode;
  fields: PromptField[];
  confirmLabel: string;
  danger?: boolean;
  onSubmit: (values: Record<string, string>) => void;
}

export function PromptDialog({ request, onClose }: { request: PromptRequest | null; onClose: () => void }) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    if (request && !dialog.open) dialog.showModal();
    if (!request && dialog.open) dialog.close();
  }, [request]);
  return (
    <dialog ref={ref} className="dialog" onClose={onClose} aria-labelledby="prompt-title">
      {request && (
        <form method="dialog" onSubmit={event => {
          event.preventDefault();
          const data = new FormData(event.currentTarget);
          const values = Object.fromEntries(request.fields.map(f => [f.name, String(data.get(f.name) ?? '').trim()]));
          const act = request.onSubmit;
          onClose();
          act(values);
        }}>
          <div className="dialog-body">
            <h2 id="prompt-title">{request.title}</h2>
            {request.body && <div className="muted">{request.body}</div>}
            {request.fields.map((f, i) => (
              <div className="field" key={f.name}>
                <label htmlFor={'prompt-' + f.name}>{f.label}</label>
                {f.type === 'textarea'
                  ? <textarea id={'prompt-' + f.name} name={f.name} className="input textarea" placeholder={f.placeholder} maxLength={f.maxLength}
                      required={f.required} defaultValue={f.value} autoFocus={i === 0} rows={3} />
                  : <input id={'prompt-' + f.name} name={f.name} className="input" type={f.type === 'number' ? 'number' : 'text'} step="any"
                      placeholder={f.placeholder} maxLength={f.maxLength} required={f.required} defaultValue={f.value} autoFocus={i === 0} />}
              </div>
            ))}
          </div>
          <div className="dialog-actions">
            <Button variant="ghost" onClick={onClose}>Cancel</Button>
            <Button type="submit" variant={request.danger ? 'danger' : 'primary'}>{request.confirmLabel}</Button>
          </div>
        </form>
      )}
    </dialog>
  );
}

/** Inline SVG icons (stroke style, 20px grid). */
const ICONS: Record<string, string> = {
  overview: 'M3 10.5 10 4l7 6.5V17a1 1 0 0 1-1 1h-4v-5H8v5H4a1 1 0 0 1-1-1z',
  map: 'M2 5l5-2 6 2 5-2v12l-5 2-6-2-5 2zM7 3v12M13 5v12',
  players: 'M7 9a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM1.5 17c.6-3 2.8-5 5.5-5s4.9 2 5.5 5M14 9a2.5 2.5 0 1 0 0-5M15 12c2 .4 3.3 2.2 3.6 5',
  broadcast: 'M3 8v4l9 4V4zM12 7.5a3 3 0 0 1 0 5M15 5a6.5 6.5 0 0 1 0 10M5 12.5 6 17h2l-1-4',
  activity: 'M2 10h3l2-6 4 12 2-6h5',
  server: 'M3 3h14v5H3zM3 12h14v5H3zM6 5.5h.01M6 14.5h.01M10 5.5h4M10 14.5h4',
  message: 'M3 4h14v10H8l-4 3v-3H3z',
  heart: 'M10 17s-7-4.4-7-9a3.8 3.8 0 0 1 7-2 3.8 3.8 0 0 1 7 2c0 4.6-7 9-7 9z',
  pin: 'M10 18s6-5.3 6-10a6 6 0 0 0-12 0c0 4.7 6 10 6 10zM10 10a2 2 0 1 0 0-4 2 2 0 0 0 0 4z',
  teleport: 'M4 16 16 4M9 4h7v7',
  kick: 'M8 3H4v14h4M13 6l4 4-4 4M17 10H8',
  open: 'M8 4H4v12h12v-4M11 3h6v6M17 3l-8 8',
  refresh: 'M16 6a7 7 0 1 0 1.5 5M16 2v4h-4',
  logout: 'M8 3H4v14h4M13 6l4 4-4 4M17 10H8',
  plus: 'M10 4v12M4 10h12',
  restart: 'M4 10a6 6 0 1 0 2-4.5M4 3v3.5h3.5',
  follow: 'M10 2v3M10 15v3M2 10h3M15 10h3M10 13a3 3 0 1 0 0-6 3 3 0 0 0 0 6z',
  fit: 'M3 7V3h4M13 3h4v4M17 13v4h-4M7 17H3v-4',
  shield: 'M10 2l6 2.5V9c0 4-2.7 7-6 9-3.3-2-6-5-6-9V4.5zM7.5 10l2 2 3.5-4',
  gift: 'M3 8h14v3H3zM4 11h12v6H4zM10 8v9M10 8S8.5 3 6.5 4 7 8 10 8zM10 8s1.5-5 3.5-4S13 8 10 8z',
  coins: 'M10 7c3.3 0 6-1.1 6-2.5S13.3 2 10 2 4 3.1 4 4.5 6.7 7 10 7zM4 4.5v4C4 9.9 6.7 11 10 11s6-1.1 6-2.5v-4M4 8.5v4c0 1.4 2.7 2.5 6 2.5s6-1.1 6-2.5v-4M4 12.5v3C4 16.9 6.7 18 10 18s6-1.1 6-2.5v-3',
};

export function Icon({ name, size = 18 }: { name: keyof typeof ICONS | string; size?: number }) {
  return (
    <svg className="icon-svg" width={size} height={size} viewBox="0 0 20 20" fill="none" stroke="currentColor" strokeWidth="1.6"
      strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d={ICONS[name] ?? ''} />
    </svg>
  );
}
