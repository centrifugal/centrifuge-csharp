using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// The event loop of a client: every step that reads or changes the state of the client and its
    /// subscriptions runs on it, one at a time and in order — an application call, a frame received, a
    /// timer, the result of an application callback (GetToken, GetState) or of a write. Event handlers run
    /// inside the step that raises them, so the application observes the events in the order of the steps.
    /// <para>
    /// The loop isn't a thread. A step runs on the thread that brings it when the loop is idle and nothing is
    /// queued, otherwise it is queued and run on the thread pool after the steps before it. Nothing waits for
    /// the loop: a call made while a step runs on another thread takes effect after that step.
    /// </para>
    /// <para>
    /// An application call made inside a step — from an event handler — runs at once, nested in it, as in
    /// centrifuge-js: the handler sees its effect when the call returns. A step that raises events checks
    /// afterwards whether a handler changed the state, as centrifuge-js does.
    /// </para>
    /// </summary>
    internal sealed class EventLoop
    {
        private readonly object _gate = new object();
        private readonly Queue<Action> _queue = new Queue<Action>();
        /// <summary>Actions to run when the current step ends (see <see cref="Defer"/>); touched in steps only.</summary>
        private readonly Queue<Action> _deferred = new Queue<Action>();
        /// <summary>Completions of tasks the application awaits (see <see cref="Complete"/>); touched in steps only.</summary>
        private List<Action> _completions = new List<Action>();
        private readonly ILogger? _logger;
        private readonly Action? _afterStep;
        private bool _running;
        private bool _drainScheduled;
        private int _ownerThread;

        /// <param name="logger">Logs an exception a step throws (a bug): the loop goes on.</param>
        /// <param name="afterStep">Runs at the end of each step, still on the loop (e.g. to write the commands
        /// the step queued as one frame).</param>
        public EventLoop(ILogger? logger, Action? afterStep)
        {
            _logger = logger;
            _afterStep = afterStep;
        }

        /// <summary>Whether the current thread runs a step of this loop.</summary>
        public bool InStep => Volatile.Read(ref _ownerThread) == Environment.CurrentManagedThreadId;

        /// <summary>
        /// Runs an application call: nested at once inside a step of this loop, else on this thread when the
        /// loop is idle, else after the steps queued before it. A nested call is completed as a step: the
        /// commands it queued are written when it returns, not when the handler that made it returns.
        /// </summary>
        public void Invoke(Action step)
        {
            if (InStep)
            {
                step();
                if (_afterStep != null) Guarded(_afterStep);
                return;
            }
            Execute(step);
        }

        /// <summary>
        /// Runs a step of an outside event (a frame, a transport close, a timer, a callback's result): on this
        /// thread when the loop is idle, else after the steps queued before it — never nested in a step, also
        /// when the event comes from inside one (e.g. a cancellation callback run by a step).
        /// </summary>
        public void Execute(Action step)
        {
            lock (_gate)
            {
                if (_running || _queue.Count > 0)
                {
                    _queue.Enqueue(step);
                    ScheduleDrainLocked();
                    return;
                }
                Enter();
            }
            Run(step);
            var completions = TakeCompletions();
            lock (_gate)
            {
                Leave();
                if (_queue.Count > 0) ScheduleDrainLocked();
            }
            RunCompletions(completions);
        }

        /// <summary>
        /// Completes a task the application awaits once the current step has ended and the loop is released
        /// — as a promise resolves in JS after the code that settled it: the awaiting code observes the whole
        /// step, and a call it makes doesn't wait behind it. Call in a step.
        /// </summary>
        public void Complete(Action completion)
        {
            Debug.Assert(InStep);
            _completions.Add(completion);
        }

        /// <summary>
        /// Runs <paramref name="action"/> when the current step ends, before the next one — as a promise
        /// continuation runs in JS: after the code that settled it, e.g. the errbacks of the calls a teardown
        /// fails run after the teardown and its events. Call in a step.
        /// </summary>
        public void Defer(Action action)
        {
            Debug.Assert(InStep);
            _deferred.Enqueue(action);
        }

        private void Enter()
        {
            _running = true;
            Volatile.Write(ref _ownerThread, Environment.CurrentManagedThreadId);
        }

        private void Leave()
        {
            Volatile.Write(ref _ownerThread, 0);
            _running = false;
        }

        private void ScheduleDrainLocked()
        {
            if (_running || _drainScheduled) return;
            _drainScheduled = true;
            ThreadPool.QueueUserWorkItem(_ => Drain());
        }

        private void Drain()
        {
            while (true)
            {
                Action step;
                lock (_gate)
                {
                    // A thread running a step inline schedules the drain again when it leaves.
                    if (_running || _queue.Count == 0)
                    {
                        _drainScheduled = false;
                        return;
                    }
                    step = _queue.Dequeue();
                    Enter();
                }
                Run(step);
                var completions = TakeCompletions();
                lock (_gate) Leave();
                RunCompletions(completions);
            }
        }

        private List<Action>? TakeCompletions()
        {
            if (_completions.Count == 0) return null;
            var completions = _completions;
            _completions = new List<Action>();
            return completions;
        }

        private void RunCompletions(List<Action>? completions)
        {
            if (completions == null) return;
            foreach (var completion in completions) Guarded(completion);
        }

        private void Run(Action step)
        {
            Guarded(step);
            while (_deferred.Count > 0) Guarded(_deferred.Dequeue());
            if (_afterStep != null) Guarded(_afterStep);
        }

        private void Guarded(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Centrifuge event loop step failed");
            }
        }
    }

    /// <summary>
    /// A one-shot timer whose callback is a step of an <see cref="EventLoop"/>. Cancelled in a step, its
    /// callback doesn't run, also when the timer already fired and its step waits in the queue (as
    /// clearTimeout in JS).
    /// </summary>
    internal sealed class LoopTimer
    {
        private readonly Timer _timer;
        /// <summary>Fired or cancelled; touched in steps only.</summary>
        private bool _done;

        private LoopTimer(EventLoop loop, int delayMs, Action onFire)
        {
            _timer = new Timer(_ => loop.Execute(() =>
            {
                if (_done) return;
                _done = true;
                _timer!.Dispose();
                onFire();
            }), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(Math.Max(0, delayMs), Timeout.Infinite);
        }

        /// <summary>Starts a timer running <paramref name="onFire"/> as a step after <paramref name="delayMs"/>.</summary>
        public static LoopTimer Start(EventLoop loop, int delayMs, Action onFire) => new LoopTimer(loop, delayMs, onFire);

        /// <summary>Cancels the timer. Call in a step.</summary>
        public void Cancel()
        {
            _done = true;
            _timer.Dispose();
        }
    }

    /// <summary>Monotonic milliseconds, for timers that check how late they run.</summary>
    internal static class Clock
    {
        public static long NowMs => (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency));
    }
}
