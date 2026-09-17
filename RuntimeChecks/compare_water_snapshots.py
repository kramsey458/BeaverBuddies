"""Compare two Preview 3 water diagnostic ZIPs without extracting them.

Usage: python compare_water_snapshots.py host-water.zip client-water.zip
Snapshots contain map storage slots, not terrain-height indices. The layer is
the column's ordinal; floor and ceiling give its actual vertical boundaries.
"""
import argparse
import collections
import struct
import zipfile

HEADER = struct.Struct('<6i')
RECORD = struct.Struct('<BB4I')
FIELDS = ('floor', 'ceiling', 'depth', 'old_depth', 'contamination', 'overflow')


def read(path):
    result = collections.defaultdict(list)
    with zipfile.ZipFile(path) as archive:
        for entry in archive.infolist():
            if not entry.filename.endswith('.bin'):
                continue
            if entry.file_size > 64 * 1024 * 1024:
                raise ValueError('Snapshot exceeds diagnostic memory limit')
            data = archive.read(entry)
            magic, tick, stride, vertical, count_len, column_len = HEADER.unpack_from(data)
            if magic != 0x42575731 or stride <= 0 or vertical <= 0 or count_len < 0 or column_len < 0:
                raise ValueError('Invalid water snapshot header')
            if len(data) != HEADER.size + count_len + column_len * RECORD.size:
                raise ValueError('Invalid water snapshot size')
            counts = data[HEADER.size:HEADER.size + count_len]
            result[tick].append((stride, vertical, counts, data[HEADER.size + count_len:]))
    return result


def compare(left, right, limit=20):
    a, b = read(left), read(right)
    shared = sorted(a.keys() & b.keys())
    if not shared:
        return ['No shared ticks retained; keep both Player.log files as well.']
    output = []
    for tick in shared:
        if len(a[tick]) != len(b[tick]):
            output.append(f'Tick {tick}: different number of water-map updates; cannot pair safely.')
            continue
        for ordinal, (x, y) in enumerate(zip(a[tick], b[tick])):
            sx, vx, cx, dx = x
            sy, vy, cy, dy = y
            if (sx, vx, len(cx), len(dx)) != (sy, vy, len(cy), len(dy)):
                output.append(f'Tick {tick} update {ordinal}: different storage dimensions.')
                continue
            count_changes = sum(i != j for i, j in zip(cx, cy))
            output.append(f'Tick {tick} update {ordinal}: {count_changes} changed column counts.')
            differences, shown = 0, 0
            for i, (rx, ry) in enumerate(zip(RECORD.iter_unpack(dx), RECORD.iter_unpack(dy))):
                if rx == ry:
                    continue
                differences += 1
                if shown >= limit:
                    continue
                shown += 1
                cell, layer = i % vx, i // vx
                active_a = cell < len(cx) and layer < cx[cell]
                active_b = cell < len(cy) and layer < cy[cell]
                details = []
                for field, first, second in zip(FIELDS, rx, ry):
                    if first == second:
                        continue
                    if field in ('floor', 'ceiling'):
                        details.append(f'{field}: {first} -> {second}')
                    else:
                        f = struct.unpack('<f', struct.pack('<I', first))[0]
                        g = struct.unpack('<f', struct.pack('<I', second))[0]
                        details.append(f'{field}: {f:.9g} [0x{first:08X}] -> {g:.9g} [0x{second:08X}]')
                output.append(f'  cell=({cell % sx - 1},{cell // sx - 1}) layer={layer} '
                              f'active={active_a}/{active_b}: ' + '; '.join(details))
            output.append(f'  {differences} different column records (showing {shown}).')
    return output


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('host')
    parser.add_argument('client')
    parser.add_argument('--limit', type=int, default=20)
    args = parser.parse_args()
    print('\n'.join(compare(args.host, args.client, max(0, args.limit))))
