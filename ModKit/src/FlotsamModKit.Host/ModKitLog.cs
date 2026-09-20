using System;
using System.Collections.Generic;
using FlotsamModKit.Abstractions;

namespace FlotsamModKit.Host
{
    /// <summary>One captured log line, shown in the manager window's Log tab.</summary>
    public sealed class LogLine
    {
        public DateTime Time;
        public string Source;
        public string Level;
        public string Message;

        public string Format()
        {
            return $"{Time:HH:mm:ss} [{Level}] {Source}: {Message}";
        }
    }

    /// <summary>
    /// Central log: writes through BepInEx's logger and keeps a bounded ring buffer for
    /// the in-game Log tab. Each mod gets a prefixed view via ModLogger.
    /// </summary>
    public sealed class ModKitLog
    {
        private const int MaxLines = 400;

        private readonly BepInEx.Logging.ManualLogSource _sink;
        private readonly List<LogLine> _lines = new List<LogLine>();
        private readonly object _gate = new object();

        public ModKitLog(BepInEx.Logging.ManualLogSource sink)
        {
            _sink = sink;
        }

        public event Action Changed;

        public void Write(string source, string level, string message)
        {
            var line = new LogLine
            {
                Time = DateTime.Now,
                Source = string.IsNullOrEmpty(source) ? "modkit" : source,
                Level = level,
                Message = message
            };

            lock (_gate)
            {
                _lines.Add(line);
                while (_lines.Count > MaxLines) _lines.RemoveAt(0);
            }

            try
            {
                switch (level)
                {
                    case "ERROR": _sink.LogError($"[{line.Source}] {message}"); break;
                    case "WARN": _sink.LogWarning($"[{line.Source}] {message}"); break;
                    default: _sink.LogInfo($"[{line.Source}] {message}"); break;
                }
            }
            catch { }

            try { Changed?.Invoke(); } catch { }
        }

        public void Info(string message) => Write(null, "INFO", message);
        public void Warn(string message) => Write(null, "WARN", message);
        public void Error(string message) => Write(null, "ERROR", message);

        public List<LogLine> Snapshot()
        {
            lock (_gate) return new List<LogLine>(_lines);
        }

        public void Clear()
        {
            lock (_gate) _lines.Clear();
            try { Changed?.Invoke(); } catch { }
        }
    }

    /// <summary>Per-mod logger that prefixes every line with the mod id.</summary>
    public sealed class ModLogger : ILog
    {
        private readonly ModKitLog _log;
        private readonly string _source;

        public ModLogger(ModKitLog log, string source)
        {
            _log = log;
            _source = source;
        }

        public void Info(string message) => _log.Write(_source, "INFO", message);
        public void Warn(string message) => _log.Write(_source, "WARN", message);
        public void Error(string message) => _log.Write(_source, "ERROR", message);
        public void Error(string message, Exception exception)
            => _log.Write(_source, "ERROR", $"{message} :: {exception?.GetType().Name}: {exception?.Message}");
    }
}