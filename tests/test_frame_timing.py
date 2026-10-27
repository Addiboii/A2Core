import unittest

from tools.inspect_capture import frame_capture_times


class FrameTimingTests(unittest.TestCase):
    def test_reordering_and_retransmissions_do_not_change_earliest_availability(self):
        segments = [(104, b"DEF", 1.0), (101, b"ABC", 2.0), (101, b"ABCDEF", 9.0)]
        frames = [{"offset": 0, "wire_length": 3}, {"offset": 3, "wire_length": 3}]
        times = frame_capture_times(segments, 100, frames)
        self.assertEqual(times, ["1970-01-01T00:00:02+00:00"] * 2)

    def test_segmented_frame_and_sequence_wrap(self):
        segments = [(0xFFFFFFFE, b"AB", 1.0), (0, b"CDEF", 2.0)]
        frames = [{"offset": 0, "wire_length": 3}, {"offset": 3, "wire_length": 3}]
        self.assertEqual(frame_capture_times(segments, 0xFFFFFFFD, frames),
                         ["1970-01-01T00:00:02+00:00"] * 2)

    def test_missing_prefix_cannot_be_timed(self):
        with self.assertRaisesRegex(ValueError, "missing bytes"):
            frame_capture_times([(104, b"DEF", 1.0)], 100, [{"offset": 0, "wire_length": 6}])


if __name__ == "__main__":
    unittest.main()
