import io
import struct
import unittest
import zipfile
from compare_water_snapshots import compare, read


class SnapshotTests(unittest.TestCase):
    def test_exact_contamination_bits_and_coordinates(self):
        streams = [io.BytesIO(), io.BytesIO()]
        for i, stream in enumerate(streams):
            counts = bytes([0, 0, 0, 0, 1, 0, 0, 0, 0])
            records = b''.join(struct.pack('<BBffff', 0, 32, .5, .5, .25 + i*.125 if cell == 4 else 0, 0)
                               for cell in range(9))
            with zipfile.ZipFile(stream, 'w') as archive:
                archive.writestr('tick-3-0.bin', struct.pack('<6i', 0x42575731, 3, 3, 9, 9, 9) + counts + records)
        output = '\n'.join(compare(*streams))
        self.assertIn('cell=(0,0) layer=0 active=True/True', output)
        self.assertIn('contamination: 0.25 [0x3E800000] -> 0.375 [0x3EC00000]', output)
        self.assertIn('1 different column records', output)
        self.assertIn('0 different column records', '\n'.join(compare(streams[0], streams[0])))

    def test_truncated_records_are_rejected(self):
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, 'w') as archive:
            archive.writestr('tick-3-0.bin', struct.pack('<6i', 0x42575731, 3, 3, 9, 9, 9))
        with self.assertRaises(ValueError):
            read(stream)


if __name__ == '__main__':
    unittest.main()
