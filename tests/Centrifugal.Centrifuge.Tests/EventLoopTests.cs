using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Centrifugal.Centrifuge.Tests
{
    /// <summary>
    /// The client's event loop on its own: steps run one at a time and in order; an application call nests
    /// inside a step, an outside event never does; deferred actions, the after-step hook and task completions
    /// run when they should; a throwing step doesn't stop the loop; a cancelled timer never fires.
    /// </summary>
    public class EventLoopTests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

        private sealed class CountingLogger : ILogger
        {
            public int Errors;

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Error) Interlocked.Increment(ref Errors);
            }
        }

        private static void Until(Func<bool> condition) =>
            Assert.True(SpinWait.SpinUntil(condition, Wait), "condition not met in time");

        [Fact]
        public void IdleLoopRunsStepInlineOnCallingThread()
        {
            var loop = new EventLoop(null, null);
            var thread = Environment.CurrentManagedThreadId;
            int? ranOn = null;
            var inStep = false;
            loop.Execute(() =>
            {
                ranOn = Environment.CurrentManagedThreadId;
                inStep = loop.InStep;
            });
            Assert.Equal(thread, ranOn);
            Assert.True(inStep);
            Assert.False(loop.InStep);
        }

        [Fact]
        public void InvokeInsideStepRunsNestedAtOnce()
        {
            var order = new List<string>();
            var loop = new EventLoop(null, () => order.Add("after"));
            loop.Execute(() =>
            {
                order.Add("outer-start");
                loop.Invoke(() =>
                {
                    order.Add("nested");
                    loop.Invoke(() => order.Add("nested-2"));
                });
                order.Add("outer-end");
            });
            // A nested call is completed as a step (its after-step hook runs when it returns).
            Assert.Equal(new[] { "outer-start", "nested", "nested-2", "after", "after", "outer-end", "after" }, order);
        }

        [Fact]
        public void ExecuteInsideStepIsQueuedNotNested()
        {
            var order = new ConcurrentQueue<string>();
            var loop = new EventLoop(null, null);
            var done = new ManualResetEventSlim();
            loop.Execute(() =>
            {
                order.Enqueue("outer-start");
                loop.Execute(() =>
                {
                    order.Enqueue("event");
                    done.Set();
                });
                order.Enqueue("outer-end");
            });
            Assert.True(done.Wait(Wait));
            Assert.Equal(new[] { "outer-start", "outer-end", "event" }, order);
        }

        [Fact]
        public async Task CallFromAnotherThreadWhileBusyIsQueuedInOrderAndDoesNotWait()
        {
            var loop = new EventLoop(null, null);
            var order = new ConcurrentQueue<int>();
            var inStep = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var busy = Task.Run(() => loop.Execute(() =>
            {
                inStep.Set();
                release.Wait(Wait);
                order.Enqueue(0);
            }));
            Assert.True(inStep.Wait(Wait));

            // Another thread: neither call waits for the step in progress.
            Assert.False(loop.InStep);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            loop.Invoke(() => order.Enqueue(1));
            loop.Execute(() => order.Enqueue(2));
            loop.Invoke(() => order.Enqueue(3));
            Assert.True(sw.ElapsedMilliseconds < 1000, "a call waited for the step in progress");
            Assert.Empty(order);

            release.Set();
            await busy.WaitAsync(Wait);
            Until(() => order.Count == 4);
            Assert.Equal(new[] { 0, 1, 2, 3 }, order);
        }

        [Fact]
        public async Task IdleLoopWithQueuedStepsRunsThemFirst()
        {
            // A call while steps are queued (loop idle, drain not yet run) doesn't jump ahead of them.
            var loop = new EventLoop(null, null);
            var order = new ConcurrentQueue<int>();
            var gate = new ManualResetEventSlim();
            var t = Task.Run(() => loop.Execute(() =>
            {
                gate.Wait(Wait);
                // Queued while running: drained after this step.
                loop.Execute(() => order.Enqueue(1));
                loop.Execute(() => order.Enqueue(2));
            }));
            gate.Set();
            await t.WaitAsync(Wait);
            loop.Execute(() => order.Enqueue(3));
            Until(() => order.Count == 3);
            Assert.Equal(new[] { 1, 2, 3 }, order);
        }

        [Fact]
        public async Task StepsNeverOverlapAndKeepPerThreadOrderUnderContention()
        {
            var loop = new EventLoop(null, null);
            const int threads = 8;
            const int perThread = 2000;
            var running = 0;
            var overlaps = 0;
            var counter = 0; // not atomic: only correct if steps never overlap
            var seen = new List<int>[threads];
            for (var i = 0; i < threads; i++) seen[i] = new List<int>();

            var tasks = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
            {
                for (var n = 0; n < perThread; n++)
                {
                    var k = n;
                    Action step = () =>
                    {
                        if (Interlocked.Increment(ref running) != 1) Interlocked.Increment(ref overlaps);
                        counter++;
                        seen[t].Add(k);
                        if (k % 97 == 0) Thread.SpinWait(200);
                        Interlocked.Decrement(ref running);
                    };
                    if (n % 3 == 0)
                    {
                        // An outside event raised inside a step: queued, so drains run alongside inline steps.
                        loop.Execute(() => loop.Execute(step));
                    }
                    else if (n % 3 == 1) loop.Execute(step);
                    else loop.Invoke(step);
                }
            })).ToArray();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
            Until(() => Volatile.Read(ref counter) == threads * perThread);

            Assert.Equal(0, overlaps);
            Assert.Equal(threads * perThread, counter);
            // Direct calls of one thread run in call order (a step queued from inside a step runs later).
            for (var t = 0; t < threads; t++)
                Assert.Equal(Enumerable.Range(0, perThread).Where(n => n % 3 != 0), seen[t].Where(n => n % 3 != 0));
        }

        /// <summary>
        /// The drain thread and inline callers race at the moment the loop frees up: inline callers keep
        /// entering an idle loop while events queued from inside steps keep a drain alive (its completions
        /// widen the moment it frees the loop). Steps must never overlap: the drain must not take a step
        /// while an inline caller holds the loop.
        /// </summary>
        [Fact]
        public void DrainNeverOverlapsInlineCallers()
        {
            var loop = new EventLoop(null, null);
            var running = 0;
            var overlaps = 0;
            var stop = DateTime.UtcNow.AddSeconds(2);
            void Step()
            {
                if (Interlocked.Increment(ref running) != 1) Interlocked.Increment(ref overlaps);
                Thread.SpinWait(100);
                Interlocked.Decrement(ref running);
            }
            // Dedicated threads: producers that wait for the loop must not starve the pool the drain runs on.
            var producers = Enumerable.Range(0, Environment.ProcessorCount * 2).Select(i => new Thread(() =>
            {
                var n = 0;
                while (DateTime.UtcNow < stop && Volatile.Read(ref overlaps) == 0)
                {
                    if (++n % 32 == 0)
                    {
                        // Bounds the backlog: wait until what this producer queued so far has run.
                        var caughtUp = new ManualResetEventSlim();
                        loop.Execute(caughtUp.Set);
                        caughtUp.Wait();
                    }
                    if (i % 2 == 0)
                    {
                        loop.Execute(Step);
                        continue;
                    }
                    loop.Execute(() =>
                    {
                        Step();
                        loop.Execute(() =>
                        {
                            Step();
                            loop.Complete(() => Thread.SpinWait(2000));
                        });
                    });
                }
            }) { IsBackground = true }).ToList();
            producers.ForEach(t => t.Start());
            foreach (var t in producers) Assert.True(t.Join(TimeSpan.FromSeconds(30)), "a producer didn't finish");

            var drained = new ManualResetEventSlim();
            loop.Execute(() => loop.Execute(drained.Set));
            Assert.True(drained.Wait(Wait));
            Assert.Equal(0, overlaps);
        }

        [Fact]
        public void DeferredRunsAfterStepBeforeAfterStepHookAndNextStep()
        {
            var order = new List<string>();
            var loop = new EventLoop(null, () => order.Add("after"));
            loop.Execute(() =>
            {
                loop.Defer(() => order.Add("deferred-1"));
                loop.Invoke(() => loop.Defer(() => order.Add("deferred-nested")));
                order.Add("step");
                loop.Defer(() =>
                {
                    order.Add("deferred-2");
                    loop.Defer(() => order.Add("deferred-3"));
                });
            });
            Assert.Equal(new[] { "after", "step", "deferred-1", "deferred-nested", "deferred-2", "deferred-3", "after" }, order);
        }

        [Fact]
        public void CompletionRunsAfterTheLoopIsReleased()
        {
            var loop = new EventLoop(null, null);
            var order = new List<string>();
            bool? inStepDuringCompletion = null;
            loop.Execute(() =>
            {
                loop.Complete(() =>
                {
                    inStepDuringCompletion = loop.InStep;
                    order.Add("completion");
                    // The loop is free: a call from the completion runs as its own step, inline.
                    loop.Execute(() => order.Add("call-from-completion"));
                });
                loop.Invoke(() => loop.Complete(() => order.Add("nested-completion")));
                order.Add("step");
            });
            Assert.False(inStepDuringCompletion);
            Assert.Equal(new[] { "step", "completion", "call-from-completion", "nested-completion" }, order);
        }

        [Fact]
        public void ThrowingStepIsLoggedAndTheLoopGoesOn()
        {
            var logger = new CountingLogger();
            var throwAfter = true;
            var loop = new EventLoop(logger, () => { if (throwAfter) throw new InvalidOperationException("after"); });
            var ran = new List<string>();
            loop.Execute(() =>
            {
                loop.Defer(() => throw new InvalidOperationException("deferred"));
                loop.Defer(() => ran.Add("deferred-after-throw"));
                loop.Complete(() => throw new InvalidOperationException("completion"));
                throw new InvalidOperationException("step");
            });
            throwAfter = false;
            loop.Execute(() => ran.Add("next"));

            Assert.Equal(new[] { "deferred-after-throw", "next" }, ran);
            Assert.Equal(4, logger.Errors);
            Assert.False(loop.InStep);
        }

        [Fact]
        public void ThrowingStepWithoutLoggerDoesNotStopTheLoop()
        {
            var loop = new EventLoop(null, null);
            var ran = false;
            loop.Execute(() => throw new InvalidOperationException("step"));
            loop.Execute(() => ran = true);
            Assert.True(ran);
        }

        [Fact]
        public void QueuedStepsAreDrainedAfterAnInlineStepLeaves()
        {
            var loop = new EventLoop(null, null);
            var drained = new ManualResetEventSlim();
            int? drainThread = null;
            var caller = Environment.CurrentManagedThreadId;
            loop.Execute(() => loop.Execute(() =>
            {
                drainThread = Environment.CurrentManagedThreadId;
                drained.Set();
            }));
            Assert.True(drained.Wait(Wait));
            Assert.NotEqual(caller, drainThread);
        }

        [Fact]
        public void LoopTimerFiresAsStep()
        {
            var loop = new EventLoop(null, null);
            var fired = new ManualResetEventSlim();
            var inStep = false;
            loop.Execute(() => LoopTimer.Start(loop, 10, () =>
            {
                inStep = loop.InStep;
                fired.Set();
            }));
            Assert.True(fired.Wait(Wait));
            Assert.True(inStep);
        }

        [Fact]
        public void CancelledLoopTimerNeverFires()
        {
            var loop = new EventLoop(null, null);
            var fired = 0;
            loop.Execute(() =>
            {
                var timer = LoopTimer.Start(loop, 50, () => Interlocked.Increment(ref fired));
                timer.Cancel();
            });
            Thread.Sleep(200);
            Assert.Equal(0, Volatile.Read(ref fired));
        }

        [Fact]
        public void LoopTimerCancelledAfterItFiredButBeforeItsStepRanDoesNotRun()
        {
            // The timer fires while the loop is busy: its step is queued; the busy step cancels the timer.
            // The timer is started by the step that holds the loop, so it can't run before the loop is busy.
            var loop = new EventLoop(null, null);
            var fired = 0;
            var cancelled = false;
            var done = new ManualResetEventSlim();
            Task.Run(() => loop.Execute(() =>
            {
                var timer = LoopTimer.Start(loop, 20, () => Interlocked.Increment(ref fired));
                // Hold the loop well past the timer's due time, so its step is queued behind this one.
                Thread.Sleep(300);
                timer.Cancel();
                cancelled = true;
                done.Set();
            }));
            Assert.True(done.Wait(Wait));
            Thread.Sleep(200);
            Assert.True(cancelled);
            Assert.Equal(0, Volatile.Read(ref fired));
        }

        [Fact]
        public async Task InStepIsPerThread()
        {
            var loop = new EventLoop(null, null);
            var inStep = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var t = Task.Run(() => loop.Execute(() =>
            {
                inStep.Set();
                release.Wait(Wait);
            }));
            Assert.True(inStep.Wait(Wait));
            Assert.False(loop.InStep);
            release.Set();
            await t.WaitAsync(Wait);
        }
    }
}
