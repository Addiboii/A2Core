import { useEffect, useState } from 'react';
import QRCode from 'qrcode';

/** Renders a TOTP otpauth URI as an inline SVG (bundled qrcode lib, no CDN). */
export function TotpQrCode({ uri, label = 'Scan in your authenticator app' }: { uri: string; label?: string }) {
  const [svg, setSvg] = useState('');

  useEffect(() => {
    let cancelled = false;
    QRCode.toString(uri, { type: 'svg', margin: 1, width: 200, errorCorrectionLevel: 'M' })
      .then(markup => { if (!cancelled) setSvg(markup); })
      .catch(() => { if (!cancelled) setSvg(''); });
    return () => { cancelled = true; };
  }, [uri]);

  if (!svg) return null;
  return (
    <figure className="totp-qr stack-sm">
      <span className="small muted">{label}</span>
      <div className="totp-qr-frame" dangerouslySetInnerHTML={{ __html: svg }} aria-hidden="true" />
    </figure>
  );
}
