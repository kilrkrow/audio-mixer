using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace audio_mixer
{
    /// <summary>
    /// Fixed-capacity least-recently-used cache. The icon cache used to be an unbounded dictionary keyed by
    /// exe path, so every updater folder (Discord app-1.0.x, Teams, Chrome ...) added an entry forever.
    /// </summary>
    public sealed class BoundedCache<TKey, TValue> where TKey : notnull
    {
        private readonly int _capacity;
        private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _map = new();
        private readonly LinkedList<KeyValuePair<TKey, TValue>> _order = new();

        public BoundedCache(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
        }

        public int Count => _map.Count;

        public bool TryGetValue(TKey key, out TValue value)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
            value = default!;
            return false;
        }

        public void Set(TKey key, TValue value)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _map.Remove(key);
            }

            var node = new LinkedListNode<KeyValuePair<TKey, TValue>>(new KeyValuePair<TKey, TValue>(key, value));
            _order.AddFirst(node);
            _map[key] = node;

            while (_map.Count > _capacity)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    /// <summary>
    /// Runs a call that may block inside the Windows audio service / a driver on a worker thread and gives up
    /// after a timeout, so the UI thread never waits indefinitely on it. A call that times out keeps running on
    /// its (background) worker; once <c>maxStuck</c> such calls are outstanding new calls fail fast instead of
    /// piling up more blocked threads.
    /// </summary>
    public sealed class TimeLimitedRunner
    {
        public static readonly TimeLimitedRunner Shared = new(maxStuck: 4);

        private readonly int _maxStuck;
        private int _stuck;

        public TimeLimitedRunner(int maxStuck) => _maxStuck = maxStuck;

        public int Stuck => Volatile.Read(ref _stuck);

        public bool TryRun<T>(Func<T> func, TimeSpan timeout, out T? result)
        {
            result = default;
            if (Stuck >= _maxStuck) return false;

            var task = Task.Run(func);
            bool finished;
            try
            {
                finished = task.Wait(timeout);
            }
            catch (AggregateException)
            {
                return false; // the call itself threw; callers treat that like a failed read
            }

            if (!finished)
            {
                Interlocked.Increment(ref _stuck);
                task.ContinueWith(
                    t => { _ = t.Exception; Interlocked.Decrement(ref _stuck); },
                    TaskScheduler.Default);
                return false;
            }

            result = task.Result;
            return true;
        }

        public bool TryRun(Action action, TimeSpan timeout) =>
            TryRun<bool>(() => { action(); return true; }, timeout, out _);
    }

    /// <summary>
    /// Force-exits the process if graceful shutdown does not finish. After <c>grace</c> it calls the exit action
    /// (Environment.Exit) on its own thread; if that does not end the process within <c>exitWait</c> it calls the
    /// kill action (TerminateProcess on self). Runs on a background thread, so a normal quick exit is unaffected.
    /// </summary>
    public sealed class ShutdownWatchdog
    {
        private readonly TimeSpan _grace;
        private readonly TimeSpan _exitWait;
        private readonly Action<int> _exit;
        private readonly Action _kill;
        private int _armed;

        public ShutdownWatchdog(TimeSpan grace, TimeSpan exitWait, Action<int> exit, Action kill)
        {
            _grace = grace;
            _exitWait = exitWait;
            _exit = exit;
            _kill = kill;
        }

        public bool IsArmed => Volatile.Read(ref _armed) == 1;

        /// <summary>Starts the countdown once; later calls are ignored. Returns true if this call armed it.</summary>
        public bool Arm()
        {
            if (Interlocked.Exchange(ref _armed, 1) == 1) return false;
            new Thread(Run) { IsBackground = true, Name = "ShutdownWatchdog" }.Start();
            return true;
        }

        private void Run()
        {
            Thread.Sleep(_grace);

            var exitThread = new Thread(() =>
            {
                try { _exit(0); }
                catch { /* fall through to kill */ }
            })
            { IsBackground = true, Name = "ShutdownWatchdogExit" };
            exitThread.Start();

            // If _exit really ends the process we never get past this point. If it returned or is stuck, kill.
            exitThread.Join(_exitWait);
            _kill();
        }
    }
}