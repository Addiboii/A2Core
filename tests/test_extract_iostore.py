import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from tools.extract_iostore import extract


class ExtractTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.toc = Path(self.temp.name) / 'sample.utoc'
        self.toc.with_suffix('.ucas').write_bytes(b'abcdefgh')
        self.toc.with_name('sample_s1.ucas').write_bytes(b'ijklmnop')
        self.meta = {'compression_block_size': 8, 'encrypted': False,
                     'compression_methods': ['Oodle']}
        self.blocks = [(0, 0, 0, 8, 8, 0, 8), (1, 1, 0, 8, 8, 0, 8)]

    def run_extract(self, **kwargs):
        tables = (self.meta, 'digest', [('aa', 1, 3, 10, 0, 1)], self.blocks)
        with patch('tools.extract_iostore.read_tables', return_value=tables):
            return extract(self.toc, 'aa', **kwargs)

    def test_partial_blocks_across_partitions(self):
        data, report = self.run_extract(expected_sha256='digest')
        self.assertEqual(data, b'defghijklm')
        self.assertEqual(report['length'], 10)

    def test_stale_toc_and_size_limit(self):
        with self.assertRaisesRegex(ValueError, 'hash differs'):
            self.run_extract(expected_sha256='old')
        with self.assertRaisesRegex(ValueError, 'limit'):
            self.run_extract(limit=9)

    def test_truncated_partition(self):
        self.toc.with_name('sample_s1.ucas').write_bytes(b'i')
        with self.assertRaisesRegex(ValueError, 'Truncated'):
            self.run_extract()

    def test_no_compressed_bytes_masquerade_as_decoded(self):
        self.blocks[1] = (1, 1, 0, 8, 8, 1, 8)
        with self.assertRaisesRegex(ValueError, 'Decoder required for: Oodle'):
            self.run_extract()

    def test_aes_padding_removed_before_logical_slice(self):
        self.meta['encrypted'] = True
        self.blocks = [(0, 0, 0, 8, 8, 0, 16), (1, 1, 0, 8, 8, 0, 16)]
        self.toc.with_suffix('.ucas').write_bytes(b'abcdefgh' + bytes(8))
        self.toc.with_name('sample_s1.ucas').write_bytes(b'ijklmnop' + bytes(8))
        with self.assertRaisesRegex(ValueError, 'archive key'):
            self.run_extract()
        data, _ = self.run_extract(decrypt=lambda b: b)
        self.assertEqual(data, b'defghijklm')

    def test_compression_receives_only_unpadded_ciphertext_result(self):
        self.meta['encrypted'] = True
        self.blocks = [(0, 0, 0, 3, 8, 1, 16), (1, 1, 0, 3, 8, 1, 16)]
        self.toc.with_suffix('.ucas').write_bytes(b'one' + bytes(13))
        self.toc.with_name('sample_s1.ucas').write_bytes(b'two' + bytes(13))
        def decode(data, size):
            self.assertEqual(size, 8)
            return {b'one': b'abcdefgh', b'two': b'ijklmnop'}[data]
        data, _ = self.run_extract(decrypt=lambda b: b, decoders={'Oodle': decode})
        self.assertEqual(data, b'defghijklm')

    def test_wrong_decompressed_size_rejected(self):
        self.blocks[1] = (1, 1, 0, 8, 8, 1, 8)
        with self.assertRaisesRegex(ValueError, 'length mismatch'):
            self.run_extract(decoders={'Oodle': lambda b, n: b'x'})


if __name__ == '__main__':
    unittest.main()
