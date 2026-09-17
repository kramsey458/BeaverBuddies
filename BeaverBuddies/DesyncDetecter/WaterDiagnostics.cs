using BeaverBuddies.IO;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Timberborn.WaterSystem;
using UnityEngine;

namespace BeaverBuddies.DesyncDetecter
{
    // Main-thread only. Keep a bounded window in memory; write only on desync.
    // No saves, player identifiers or network uploads are included.
    public static class WaterDiagnostics
    {
        private sealed class Snapshot
        {
            public int Tick, Stride, VerticalStride;
            public ReadOnlyWaterColumn[] Columns;
            public byte[] Counts;
            public long Bytes => (long)Columns.Length * 24 + Counts.Length;
        }

        private static readonly Queue<Snapshot> snapshots = new Queue<Snapshot>();
        private const long MaxBytes = 64L * 1024 * 1024;
        private static long bytes;
        private static bool written, failed;

        internal static void Reset()
        {
            snapshots.Clear();
            bytes = 0;
            written = failed = false;
        }

        internal static void Capture(ThreadSafeWaterMap map, int tick)
        {
            if (tick < 0 || EventIO.IsNull || written || failed) return;
            try
            {
                CaptureData(map._threadSafeWaterColumns, map._threadSafeColumnCounts,
                    tick, map._mapIndexService.Stride, map._verticalStride);
            }
            catch (Exception e) { Fail(e); }
        }

        internal static void CaptureData(ReadOnlyWaterColumn[] columns, byte[] counts,
            int tick, int stride, int verticalStride)
        {
                long size = (long)columns.Length * 24 + counts.Length;
                if (size > MaxBytes) return;
                while (snapshots.Count > 0 && (snapshots.Count >= 4 || bytes + size > MaxBytes))
                    bytes -= snapshots.Dequeue().Bytes;
                var snapshot = new Snapshot
                {
                    Tick = tick,
                    Stride = stride,
                    VerticalStride = verticalStride,
                    Columns = (ReadOnlyWaterColumn[])columns.Clone(),
                    Counts = (byte[])counts.Clone()
                };
                snapshots.Enqueue(snapshot);
                bytes += snapshot.Bytes;
        }

        internal static string Describe(ReadOnlyWaterColumn[] columns, byte[] counts, int verticalStride)
        {
            int depth = 13, contamination = 13, overflow = 13, geometry = 13, inactive = 13;
            int activeCount = 0;
            unchecked
            {
                for (int i = 0; i < columns.Length; i++)
                {
                    var c = columns[i];
                    bool active = verticalStride > 0 && i % verticalStride < counts.Length &&
                        i / verticalStride < counts[i % verticalStride];
                    if (active)
                    {
                        activeCount++;
                        depth = depth * 7 + BitConverter.SingleToInt32Bits(c.WaterDepth);
                        contamination = contamination * 7 + BitConverter.SingleToInt32Bits(c.Contamination);
                        overflow = overflow * 7 + BitConverter.SingleToInt32Bits(c.Overflow);
                        geometry = (geometry * 7 + c.Floor) * 7 + c.Ceiling;
                    }
                    else
                    {
                        inactive = inactive * 7 + BitConverter.SingleToInt32Bits(c.WaterDepth);
                        inactive = inactive * 7 + BitConverter.SingleToInt32Bits(c.Contamination);
                        inactive = inactive * 7 + BitConverter.SingleToInt32Bits(c.Overflow);
                        inactive = (inactive * 7 + c.Floor) * 7 + c.Ceiling;
                    }
                }
            }
            return $"Water fields active={activeCount} depth={depth:X8} contamination={contamination:X8} " +
                $"overflow={overflow:X8} geometry={geometry:X8} inactive={inactive:X8}";
        }

        public static void WriteOnDesync()
        {
            if (written || failed || snapshots.Count == 0) return;
            try
            {
                string directory = Path.Combine(Application.persistentDataPath, "BeaverBuddiesDiagnostics");
                string path = WriteArchive(directory);
                Plugin.Log($"Water desync diagnostics saved locally: {path}");
            }
            catch (Exception e) { Fail(e); }
        }

        internal static string WriteArchive(string directory)
        {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, $"water-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.zip");
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    int ordinal = 0;
                    foreach (var s in snapshots)
                    {
                        var entry = zip.CreateEntry($"tick-{s.Tick}-{ordinal++}.bin", System.IO.Compression.CompressionLevel.Fastest);
                        using (var writer = new BinaryWriter(entry.Open()))
                        {
                            writer.Write(0x42575731); // BWW1, little-endian
                            writer.Write(s.Tick);
                            writer.Write(s.Stride);
                            writer.Write(s.VerticalStride);
                            writer.Write(s.Counts.Length);
                            writer.Write(s.Columns.Length);
                            writer.Write(s.Counts);
                            foreach (var c in s.Columns)
                            {
                                writer.Write(c.Floor);
                                writer.Write(c.Ceiling);
                                writer.Write(c.WaterDepth);
                                writer.Write(c.OldWaterDepth);
                                writer.Write(c.Contamination);
                                writer.Write(c.Overflow);
                            }
                        }
                    }
                }
                written = true;
                snapshots.Clear();
                bytes = 0;
                return path;
        }

        private static void Fail(Exception e)
        {
            failed = true;
            snapshots.Clear();
            bytes = 0;
            Plugin.LogWarning($"Water diagnostics unavailable: {e.Message}");
        }
    }
}
