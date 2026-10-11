using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using MHToolsN.Services;

namespace MHToolsN.Bridge
{
    /// <summary>
    /// The named pipe the server connects to: one connection, one request, one
    /// reply. Only the current Windows user can open it. Runs on a pool thread;
    /// the handler it calls must not touch the Navisworks API (it hands the work
    /// to the <see cref="UiDispatcher"/>). Ported from MHTools-R's BridgePipe.
    /// </summary>
    internal sealed class BridgePipe : IDisposable
    {
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly string _pipeName;
        private readonly Func<Dictionary<string, object>, Dictionary<string, object>> _handler;
        private readonly object _gate = new object();
        private NamedPipeServerStream _active;

        public BridgePipe(string pipeName, Func<Dictionary<string, object>, Dictionary<string, object>> handler)
        {
            _pipeName = pipeName;
            _handler = handler;
        }

        public void Start()
        {
            Task.Run(ServeAsync);
        }

        private NamedPipeServerStream CreatePipe()
        {
            // .NET Framework has no CurrentUserOnly: an ACL with the current user
            // alone, nothing inherited.
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            var sid = WindowsIdentity.GetCurrent().User;
            if (sid == null) throw new InvalidOperationException("No Windows user.");
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
            return new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 65536, 65536, security);
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await ServeOneAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Whatever one connection does, the loop goes on.
                    if (_stop.IsCancellationRequested) return;
                    Watchdog.Log("Bridge pipe: a connection failed and was dropped: " + ex.GetType().Name + ": " + ex.Message);
                    try { await Task.Delay(500, _stop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
                finally
                {
                    lock (_gate) _active = null;
                }
            }
        }

        private async Task ServeOneAsync()
        {
            using (var pipe = CreatePipe())
            {
                lock (_gate)
                {
                    if (_stop.IsCancellationRequested) return;
                    _active = pipe;
                }

                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);

                // The request must arrive within 10 s; the reply may take as long as
                // the handler allows (it bounds itself), then 10 s more to send.
                Dictionary<string, object> request;
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    using (deadline.Token.Register(() => pipe.Dispose()))
                        request = await BridgeWire.ReadAsync(pipe, deadline.Token).ConfigureAwait(false);
                }

                Dictionary<string, object> response;
                try { response = _handler(request); }
                catch (Exception ex) { response = BridgeWire.Error("INVALID_REQUEST", ex.Message); }

                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    using (deadline.Token.Register(() => pipe.Dispose()))
                        await BridgeWire.WriteAsync(pipe, response, deadline.Token).ConfigureAwait(false);
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            lock (_gate)
            {
                if (_active != null) _active.Dispose();
            }
        }
    }
}
