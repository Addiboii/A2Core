"""Layout checks for the account site (registration / sign-in on port 8780)."""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ACCOUNT_CSS = ROOT / 'web/src/account/account.css'
ACCOUNT_APP = ROOT / 'web/src/account/AccountApp.tsx'
INDEX_HTML = ROOT / 'web/index.html'


class AccountWebLayoutTests(unittest.TestCase):
    def test_viewport_meta_on_account_index(self):
        html = INDEX_HTML.read_text(encoding='utf-8')
        self.assertIn('name="viewport"', html)
        self.assertIn('width=device-width', html)

    def test_account_css_prevents_horizontal_overflow(self):
        css = ACCOUNT_CSS.read_text(encoding='utf-8')
        self.assertIn('overflow-x: clip', css)
        self.assertIn('max-width: 100%', css)
        self.assertIn('box-sizing', Path(ROOT / 'web/src/ui/ui.css').read_text(encoding='utf-8'))
        self.assertIn('overflow-wrap: anywhere', css)
        self.assertIn('@media (max-width: 480px)', css)

    def test_totp_enroll_uses_qr_and_copy_blocks(self):
        tsx = ACCOUNT_APP.read_text(encoding='utf-8')
        self.assertIn('TotpQrCode', tsx)
        self.assertIn('CopyableValue', tsx)
        self.assertNotRegex(tsx, r'<code>\{enroll\.otpAuthUri\}</code>')


if __name__ == '__main__':
    unittest.main()
