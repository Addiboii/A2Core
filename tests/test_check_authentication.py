import unittest
from pathlib import Path

from tools import check_authentication

ROOT = Path(__file__).resolve().parents[1]


class AccountPageTitleTests(unittest.TestCase):
    def test_expected_title_is_the_account_page_title(self):
        # The end-to-end check looked for "' Account</title>'" after the page became "Sign in".
        self.assertIn(check_authentication.ACCOUNT_PAGE_TITLE, (ROOT / 'web/index.html').read_bytes())


if __name__ == '__main__':
    unittest.main()
