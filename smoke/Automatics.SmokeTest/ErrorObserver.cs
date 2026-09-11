using System;
using System.Collections.Generic;
using UnityEngine;

namespace Automatics.SmokeTest
{
    // Everything Unity logs as an error, an exception or a failed assertion while a check runs,
    // unfiltered. The probe it logs as it starts shows that it receives what Unity logs; without
    // it, an empty log would prove nothing. Each command that uses it has its own probe marker,
    // and the probe names the command, so the deliberate line in LogOutput.log says whose it is.
    internal sealed class ErrorObserver
    {
        private readonly string _probeMarker;
        private readonly string _command;
        private readonly List<string> _unexpected = new List<string>();
        private bool _listening;

        public ErrorObserver(string probeMarker, string command)
        {
            _probeMarker = probeMarker;
            _command = command;
        }

        public bool Started { get; private set; }
        public int Probes { get; private set; }
        public IReadOnlyList<string> Unexpected => _unexpected;

        public void Start()
        {
            if (Started) return;
            Started = true;
            _listening = true;
            Application.logMessageReceived += OnLog;
            UnityEngine.Debug.LogException(new InvalidOperationException(
                $"{_probeMarker}: a deliberate test entry of {_command}, logged to show the check sees what Unity logs; it is not an error"));
        }

        public void Stop()
        {
            if (!_listening) return;
            _listening = false;
            Application.logMessageReceived -= OnLog;
        }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            if (condition != null && condition.Contains(_probeMarker))
            {
                Probes++;
                return;
            }

            _unexpected.Add(string.IsNullOrEmpty(stackTrace)
                ? $"[{type}] {condition}"
                : $"[{type}] {condition}\n{stackTrace.Trim()}");
        }
    }
}
