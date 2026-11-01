import { useState } from 'react';
import { Button } from '../ui/components';

/** Monospace secret/URI block that wraps on narrow viewports, with a copy action. */
export function CopyableValue({ label, value, id }: { label: string; value: string; id?: string }) {
  const [copied, setCopied] = useState(false);
  async function copy() {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2000);
    } catch {
      const area = document.createElement('textarea');
      area.value = value;
      area.setAttribute('readonly', '');
      area.style.position = 'fixed';
      area.style.left = '-9999px';
      document.body.appendChild(area);
      area.select();
      document.execCommand('copy');
      document.body.removeChild(area);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2000);
    }
  }
  return (
    <div className="copy-block stack-sm">
      <span className="small">{label}</span>
      <div className="copy-block-row">
        <code className="copy-block-value break" id={id}>{value}</code>
        <Button type="button" size="sm" variant="secondary" className="copy-block-btn" onClick={copy}>
          {copied ? 'Copied' : 'Copy'}
        </Button>
      </div>
    </div>
  );
}
