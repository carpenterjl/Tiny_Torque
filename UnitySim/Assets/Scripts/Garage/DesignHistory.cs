using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Garage
{
    /// <summary>
    /// Undo/redo for a working design object (e.g. <see cref="VehicleDesign"/> or
    /// a track design), stored as JSON snapshots (cheap, immutable). Callers push
    /// the <em>pre-mutation</em> state before each change; rapid edits sharing a
    /// change key (e.g. a slider drag) within a short window coalesce into a
    /// single snapshot. T must be a JsonUtility-serializable class.
    /// </summary>
    public sealed class DesignHistory<T> where T : class
    {
        private const int Capacity = 50;
        private const float CoalesceWindow = 0.7f;

        private readonly List<string> _undo = new List<string>();
        private readonly List<string> _redo = new List<string>();
        private string _lastKey;
        private float _lastPushTime = -999f;

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        /// <summary>Record the current (pre-change) design under a change key.</summary>
        public void Push(T current, string changeKey)
        {
            if (current == null) return;
            float now = Time.unscaledTime;
            if (changeKey != null && changeKey == _lastKey && now - _lastPushTime < CoalesceWindow)
            {
                _lastPushTime = now; // extend the coalesce window, don't add a snapshot
                return;
            }
            _lastKey = changeKey;
            _lastPushTime = now;
            _undo.Add(JsonUtility.ToJson(current));
            if (_undo.Count > Capacity) _undo.RemoveAt(0);
            _redo.Clear();
        }

        /// <summary>Return the state to restore (or null), pushing current onto the redo stack.</summary>
        public T Undo(T current)
        {
            if (_undo.Count == 0) return null;
            string snap = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(JsonUtility.ToJson(current));
            _lastKey = null;
            return JsonUtility.FromJson<T>(snap);
        }

        public T Redo(T current)
        {
            if (_redo.Count == 0) return null;
            string snap = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(JsonUtility.ToJson(current));
            _lastKey = null;
            return JsonUtility.FromJson<T>(snap);
        }

        /// <summary>End the current coalesce window so the next push snapshots anew.</summary>
        public void BreakCoalescing() => _lastKey = null;

        public void Clear()
        {
            _undo.Clear();
            _redo.Clear();
            _lastKey = null;
        }
    }
}
