using System;
using System.Threading;
using System.Windows.Forms;

namespace MHToolsN.Bridge
{
    /// <summary>
    /// Runs bridge work on Navisworks' UI thread - the only thread the Navisworks
    /// API may be used from; touching a Document from the pipe's thread corrupts it
    /// or crashes Navisworks, with no exception to catch.
    ///
    /// The idea is NavisCoord's (HorizunGroup, MIT): work is posted with
    /// BeginInvoke and waited for with a real deadline. When the deadline passes
    /// before the work has STARTED - Navisworks busy with a dialog or a long
    /// command - the work is abandoned and never runs: a request nobody waits for
    /// any more must not change the model minutes later. When it passes after the
    /// work started, the caller is told it is still running, never that nothing
    /// happened.
    ///
    /// Created on the UI thread (the Agent Bridge button runs there): the hidden
    /// control's window handle binds it to that thread.
    /// </summary>
    internal sealed class UiDispatcher : IDisposable
    {
        private const int Capacity = 16;

        private readonly Control _marshal;
        private int _outstanding;

        public UiDispatcher()
        {
            _marshal = new Control();
            _marshal.CreateControl();
            IntPtr handle = _marshal.Handle;
            if (handle == IntPtr.Zero) throw new InvalidOperationException("Could not create the UI-thread marshal window.");
        }

        /// <summary>One piece of work and what became of it.</summary>
        internal sealed class Work
        {
            private const int Pending = 0, Started = 1, Abandoned = 2;

            private int _state;
            private readonly ManualResetEventSlim _done = new ManualResetEventSlim(false);

            public Func<object> Body;
            public object Result;
            public Exception Error;
            public DateTime StartedUtc;
            public DateTime FinishedUtc;

            public bool IsDone { get { return _done.IsSet; } }
            public bool HasStarted { get { return Volatile.Read(ref _state) == Started; } }

            /// <summary>On the UI thread: runs the body unless the caller gave up first.</summary>
            internal void RunOnUiThread()
            {
                if (Interlocked.CompareExchange(ref _state, Started, Pending) != Pending) return;
                StartedUtc = DateTime.UtcNow;
                try { Result = Body(); }
                catch (Exception ex) { Error = ex; }
                finally
                {
                    FinishedUtc = DateTime.UtcNow;
                    _done.Set();
                }
            }

            /// <summary>True when the work had not started and now never will.</summary>
            internal bool TryAbandon()
            {
                return Interlocked.CompareExchange(ref _state, Abandoned, Pending) == Pending;
            }

            internal bool Wait(TimeSpan timeout)
            {
                return _done.Wait(timeout);
            }
        }

        /// <summary>
        /// Posts <paramref name="body"/> to the UI thread and waits up to <paramref name="wait"/>.
        /// Returns the work: done (Result or Error set), still running (HasStarted, not done),
        /// or throws BUSY when it was abandoned unstarted or the queue is full.
        /// </summary>
        public Work Run(Func<object> body, TimeSpan wait)
        {
            if (Interlocked.Increment(ref _outstanding) > Capacity)
            {
                Interlocked.Decrement(ref _outstanding);
                throw new BridgeException("BUSY", "Navisworks already has " + Capacity + " bridge requests waiting. Wait for them, then try again.");
            }

            var work = new Work { Body = body };
            try
            {
                _marshal.BeginInvoke((Action)(() =>
                {
                    try { work.RunOnUiThread(); }
                    finally { Interlocked.Decrement(ref _outstanding); }
                }));
            }
            catch (Exception)
            {
                Interlocked.Decrement(ref _outstanding);
                throw new BridgeException("UNAVAILABLE", "Navisworks' UI thread is not accepting work (it may be closing).");
            }

            if (work.Wait(wait)) return work;
            if (work.TryAbandon())
                throw new BridgeException("BUSY",
                    "Navisworks did not get to the request within " + (int)wait.TotalSeconds + " s - a dialog is open or a command is running. " +
                    "Nothing was done; ask the user to finish what is open in Navisworks, then try again.");
            return work; // started, still running
        }

        public void Dispose()
        {
            try { _marshal.Dispose(); }
            catch (Exception) { }
        }
    }
}
