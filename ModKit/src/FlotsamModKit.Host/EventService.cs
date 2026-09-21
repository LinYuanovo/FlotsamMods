using System;
using System.Collections.Generic;
using FlotsamModKit.Abstractions;
using UnityEngine.Events;

namespace FlotsamModKit.Host
{
    /// <summary>
    /// Wraps the game's GameEventDispatcher. Subscriptions are tracked so that disabling
    /// a mod cannot leave dangling listeners (the classic mod leak).
    /// </summary>
    public sealed class EventService : IGameEvents
    {
        private readonly List<Sub> _subs = new List<Sub>();
        private readonly ILog _log;

        public EventService(ILog log)
        {
            _log = log;
        }

        private sealed class Sub
        {
            public GameEventType Type;
            public UnityAction<GameEvent> Callback;
        }

        public IDisposable On(string gameEventType, Action<object> handler)
        {
            if (handler == null) return new NoopDisposable();
            if (!Enum.TryParse(gameEventType, true, out GameEventType type))
            {
                _log?.Warn($"unknown game event '{gameEventType}'");
                return new NoopDisposable();
            }

            UnityAction<GameEvent> callback = ge =>
            {
                try { handler(ge); }
                catch (Exception e) { _log?.Error($"event {type} handler threw: {e}"); }
            };

            try
            {
                GameEventDispatcher.AddListener(type, callback);
            }
            catch (Exception e)
            {
                _log?.Warn($"could not subscribe to {type}: {e.Message}");
                return new NoopDisposable();
            }

            var sub = new Sub { Type = type, Callback = callback };
            _subs.Add(sub);
            return new Disposable(() => Remove(sub));
        }

        public void Signal(string gameEventType)
        {
            if (Enum.TryParse(gameEventType, true, out GameEventType type))
            {
                try { GameEventDispatcher.Dispatch(type); }
                catch (Exception e) { _log?.Warn($"dispatch {type} failed: {e.Message}"); }
            }
        }

        private void Remove(Sub sub)
        {
            if (sub == null || !_subs.Remove(sub)) return;
            try { GameEventDispatcher.RemoveListener(sub.Type, sub.Callback); } catch { }
        }

        /// <summary>
        /// Re-registers every tracked subscription and returns how many were restored.
        /// The game wipes ALL GameEventDispatcher listeners at the start of every scene load
        /// (LoadingScreen.LoadSceneCoroutine → RemoveAllGameEventListeners, decompile 105273),
        /// so anything subscribed at boot (mod OnEnable, main menu) is dead once a save loads.
        /// ModKitRuntime calls this on the IsPlaying rising edge. Remove-then-add makes it
        /// idempotent whether or not the listener actually got wiped.
        /// </summary>
        public int RestoreAll()
        {
            int n = 0;
            foreach (var sub in _subs.ToArray())
            {
                try
                {
                    GameEventDispatcher.RemoveListener(sub.Type, sub.Callback);
                    GameEventDispatcher.AddListener(sub.Type, sub.Callback);
                    n++;
                }
                catch { }
            }
            return n;
        }

        /// <summary>Called when the owning mod is disabled.</summary>
        public void ClearAll()
        {
            foreach (var sub in _subs.ToArray())
            {
                try { GameEventDispatcher.RemoveListener(sub.Type, sub.Callback); } catch { }
            }
            _subs.Clear();
        }

        private sealed class Disposable : IDisposable
        {
            private Action _dispose;
            public Disposable(Action dispose) { _dispose = dispose; }
            public void Dispose()
            {
                var d = _dispose;
                _dispose = null;
                try { d?.Invoke(); } catch { }
            }
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}