import unittest
from tools.read_captured_image import assemble


def report(pid, start, data, status='anchors_matched'):
    return {'pid': pid, 'followup': {'status': status, 'windows': [
        {'status': 'read', 'rva': start, 'hex': data.hex()}]}}


class CapturedImageTests(unittest.TestCase):
    def test_contiguous_and_identical_overlap(self):
        result = assemble([('a', report(1, 10, b'abc')), ('b', report(1, 12, b'cde'))], 1, 10, 5)
        self.assertEqual(bytes.fromhex(result['hex']), b'abcde')
        self.assertEqual(result['status'], 'complete')

    def test_gap_is_not_filled_with_invented_bytes(self):
        result = assemble([('a', report(1, 10, b'a')), ('b', report(1, 12, b'c'))], 1, 10, 3)
        self.assertEqual(result['missing_ranges'], [{'rva': 11, 'size': 1}])
        self.assertNotIn('hex', result)

    def test_conflicting_snapshots_do_not_emit_code(self):
        result = assemble([('a', report(1, 10, b'abc')), ('b', report(1, 11, b'X'))], 1, 10, 3)
        self.assertEqual(result['status'], 'conflict')
        self.assertEqual(result['conflicting_rvas'], [11])
        self.assertNotIn('hex', result)

    def test_other_process_and_failed_anchors_are_excluded(self):
        result = assemble([('a', report(2, 10, b'abc')), ('b', report(1, 10, b'abc', 'anchor_mismatch'))], 1, 10, 3)
        self.assertEqual(result['covered_bytes'], 0)
        self.assertEqual(result['status'], 'incomplete')

    def test_requested_range_is_clipped_and_bounded(self):
        result = assemble([('a', report(1, 0, b'012345'))], 1, 2, 2)
        self.assertEqual(bytes.fromhex(result['hex']), b'23')
        with self.assertRaises(ValueError):
            assemble([], 1, 0, 65537)
