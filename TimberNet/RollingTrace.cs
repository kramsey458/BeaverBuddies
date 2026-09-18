using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TimberNet
{
    // Bounded, main-thread recorder. Immutable strings can safely be handed to an IO worker.
    public sealed class RollingTrace
    {
        public const int SnapshotLimit = 120, EventLimit = 256, SnapshotCharacters = 65536, EventCharacters = 2048;
        readonly Queue<string> snapshots = new Queue<string>(), events = new Queue<string>();
        public void Snapshot(JObject value) => Add(snapshots, value, SnapshotLimit, SnapshotCharacters);
        public void Event(JObject value) => Add(events, value, EventLimit, EventCharacters);
        static void Add(Queue<string> queue, JObject value, int count, int characters)
        {
            string text = value.ToString(Formatting.None);
            if (text.Length > characters) throw new IOException("Rolling diagnostic record exceeded its size budget.");
            if (queue.Count == count) queue.Dequeue();
            queue.Enqueue(text);
        }
        public RollingReport Freeze(JObject metadata) => new RollingReport(metadata.ToString(Formatting.None), snapshots.ToArray(), events.ToArray());
        public void Clear() { snapshots.Clear(); events.Clear(); }
    }
    public sealed class RollingReport
    {
        readonly string metadata;
        readonly string[] snapshots, events;
        internal RollingReport(string metadata, string[] snapshots, string[] events)
        { this.metadata = metadata; this.snapshots = snapshots; this.events = events; }
        // Caller serializes writes to this directory. Retention only removes our reports.
        public string Write(string directory, string fileName)
        {
            if (Path.GetFileName(fileName) != fileName || !fileName.StartsWith("rolling-", StringComparison.Ordinal) || !fileName.EndsWith(".zip", StringComparison.Ordinal))
                throw new ArgumentException("Invalid diagnostic report filename.");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, fileName), temp = path + ".tmp";
            try
            {
                using (var output = File.Create(temp))
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
                using (var entry = zip.CreateEntry("report.json", CompressionLevel.Optimal).Open())
                using (var writer = new StreamWriter(entry, new UTF8Encoding(false)))
                using (var json = new JsonTextWriter(writer))
                {
                    json.WriteStartObject();
                    json.WritePropertyName("schema"); json.WriteValue(1);
                    json.WritePropertyName("metadata"); json.WriteRawValue(metadata);
                    json.WritePropertyName("snapshots"); json.WriteStartArray(); foreach (string item in snapshots) json.WriteRawValue(item); json.WriteEndArray();
                    json.WritePropertyName("events"); json.WriteStartArray(); foreach (string item in events) json.WriteRawValue(item); json.WriteEndArray();
                    json.WriteEndObject();
                }
                File.Move(temp, path);
                foreach (var old in new DirectoryInfo(directory).GetFiles("rolling-*.zip").OrderByDescending(f => f.LastWriteTimeUtc).Skip(10)) old.Delete();
                return path;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
