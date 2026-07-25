using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace AIHWSim.Telemetry
{
    /// <summary>
    /// Records a drive session's telemetry to a single <b>temporary</b> CSV that is
    /// overwritten at the start of each new session. Nothing is written to the
    /// permanent <c>UnitySim/TelemetryLogs</c> folder unless the user explicitly
    /// calls <see cref="Save"/> — at which point the temp file is copied there with
    /// a timestamped name and a JSON metadata sidecar. The temp file is discarded
    /// on <see cref="End"/>, so an unsaved session leaves nothing behind.
    ///
    /// Columns are fixed at <see cref="Begin"/> from the hub's registered channels,
    /// so all channels must be registered first.
    /// </summary>
    public sealed class CsvLogger : IDisposable
    {
        private const string TempFileName = "current_drive";

        private readonly TelemetryHub _hub;
        private StreamWriter _writer;
        private string[] _columns;
        private readonly StringBuilder _sb = new StringBuilder(256);

        private string _controllerName;
        private Dictionary<string, string> _metadata;
        private string _stamp;
        private string _label = "";
        private bool _dirtySinceSave;

        /// <summary>Path of the temporary session CSV (recreated each session).</summary>
        public string TempPath { get; private set; }

        /// <summary>Path of the last explicit save into TelemetryLogs, or null.</summary>
        public string SavedPath { get; private set; }

        /// <summary>True when the session has rows not yet persisted via <see cref="Save"/>.</summary>
        public bool HasUnsavedData => _writer != null && _dirtySinceSave;

        public CsvLogger(TelemetryHub hub)
        {
            _hub = hub;
        }

        public void Begin(string controllerName, Dictionary<string, string> metadata, string label = "")
        {
            _controllerName = string.IsNullOrEmpty(controllerName) ? "run" : controllerName;
            _metadata = metadata;
            _label = string.IsNullOrEmpty(label) ? "" : "_" + label;
            _stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            SavedPath = null;
            _dirtySinceSave = false;

            // A single scratch file per label, overwritten (append:false) each session.
            string tdir = Application.temporaryCachePath;
            Directory.CreateDirectory(tdir);
            TempPath = Path.Combine(tdir, TempFileName + _label + ".csv");
            _writer = new StreamWriter(TempPath, append: false, Encoding.UTF8);

            // Fix column order now.
            var cols = new List<string> { "time" };
            foreach (var ch in _hub.Channels) cols.Add(ch.Name);
            _columns = cols.ToArray();
            _writer.WriteLine(string.Join(",", _columns));

            _hub.FrameCommitted += OnFrame;
        }

        /// <summary>Add/update one sidecar metadata entry (e.g. step metrics at save time).</summary>
        public void SetMetadata(string key, string value)
        {
            _metadata ??= new Dictionary<string, string>();
            _metadata[key] = value;
        }

        private void OnFrame(float time)
        {
            if (_writer == null) return;
            _dirtySinceSave = true;

            _sb.Clear();
            _sb.Append(time.ToString("R", CultureInfo.InvariantCulture));
            foreach (var ch in _hub.Channels)
            {
                _sb.Append(',');
                _sb.Append(ch.Latest.ToString("R", CultureInfo.InvariantCulture));
            }
            _writer.WriteLine(_sb.ToString());
        }

        /// <summary>
        /// Persist the session so far into UnitySim/TelemetryLogs (CSV + sidecar).
        /// Returns the saved CSV path, or null if there is nothing to save.
        /// </summary>
        public string Save()
        {
            if (_writer == null) return null;
            _writer.Flush();

            try
            {
                string dir = Path.Combine(AIHWSim.Persistence.AppPaths.BaseDir, "TelemetryLogs");
                Directory.CreateDirectory(dir);
                string dest = Path.Combine(dir, $"{_stamp}_{_controllerName}{_label}.csv");
                File.Copy(TempPath, dest, overwrite: true);
                WriteSidecar(dir, _stamp, _controllerName + _label, _metadata, Path.GetFileName(dest));
                SavedPath = dest;
                _dirtySinceSave = false;
                Debug.Log($"[CsvLogger] Saved telemetry to {dest}");
                return dest;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CsvLogger] Save failed: {e.Message}");
                return null;
            }
        }

        private void WriteSidecar(string dir, string stamp, string name,
            Dictionary<string, string> metadata, string csvFileName)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append($"  \"controller\": \"{Escape(name)}\",\n");
                sb.Append($"  \"timestamp\": \"{stamp}\",\n");
                if (metadata != null)
                {
                    foreach (var kv in metadata)
                        sb.Append($"  \"{Escape(kv.Key)}\": \"{Escape(kv.Value)}\",\n");
                }
                sb.Append($"  \"csv\": \"{Escape(csvFileName)}\"\n");
                sb.Append("}\n");
                File.WriteAllText(Path.Combine(dir, $"{stamp}_{name}.json"), sb.ToString());
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CsvLogger] Failed to write sidecar: {e.Message}");
            }
        }

        private static string Escape(string s) =>
            (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

        /// <summary>Force-write buffered rows to the temp file without ending the session.</summary>
        public void Flush() => _writer?.Flush();

        /// <summary>End the session and discard the temporary file (only Save persists data).</summary>
        public void End()
        {
            _hub.FrameCommitted -= OnFrame;
            if (_writer != null)
            {
                _writer.Flush();
                _writer.Dispose();
                _writer = null;
            }
            try
            {
                if (!string.IsNullOrEmpty(TempPath) && File.Exists(TempPath))
                    File.Delete(TempPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CsvLogger] Could not remove temp file: {e.Message}");
            }
        }

        public void Dispose() => End();
    }
}
