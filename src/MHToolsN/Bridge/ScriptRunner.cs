using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Autodesk.Navisworks.Api;
using MHToolsN.Services;

namespace MHToolsN.Bridge
{
    /// <summary>
    /// Trusted C#: runs a script the bridge server compiled (MHToolsN.Generated.Script.Run
    /// (Document doc, string inputsJson, CancellationToken)) on Navisworks' UI thread.
    ///
    /// - Through ChangeGate like every change: Off refuses; Ask shows the user the
    ///   code and runs it only on Yes; Auto logs it.
    /// - wrap: inside one Navisworks transaction, so the run is one Ctrl+Z step.
    ///   Without wrap the script manages its own (e.g. a Quantification database
    ///   transaction).
    /// - dryRun: undone right after (Document.TryUndo), and the answer says whether the
    ///   undo took - how an agent finds out what Ctrl+Z really covers.
    ///
    /// A loaded assembly cannot be unloaded on .NET Framework, so the loads per
    /// Navisworks session are capped (as MHTools-R does for Revit).
    /// </summary>
    internal static class ScriptRunner
    {
        private const int MaxLoads = 300;
        private const int MaxOutput = 200000;
        private static int _loads;

        public static object Execute(BridgeHost host, Dictionary<string, object> args)
        {
            Document doc = ModelMethods.OpenDocument(host.Items);
            byte[] assembly;
            try { assembly = Convert.FromBase64String(BridgeWire.Text(args, "assemblyBase64")); }
            catch (FormatException) { throw new BridgeException("INVALID_ARGUMENT", "The compiled script did not arrive whole. Compile it again."); }
            string source = BridgeWire.Text(args, "source");
            string typeName = BridgeWire.Text(args, "typeName", "MHToolsN.Generated.Script");
            string methodName = BridgeWire.Text(args, "methodName", "Run");
            string inputs = BridgeWire.Text(args, "inputsJson", "{}");
            bool wrap = BridgeWire.Bool(args, "wrap", true);
            bool dryRun = BridgeWire.Bool(args, "dryRun", false);

            int lines = string.IsNullOrEmpty(source) ? 0 : source.Split('\n').Length;
            ChangeGate.Allow(host, (dryRun ? "TRY OUT (run, then undo) " : "Run ") + "C# written by the AI - " + lines + " line(s)" +
                                   (wrap ? ", as one undo step" : ", managing its own transactions") + ":\n\n" + Preview(source));

            if (Interlocked.Increment(ref _loads) > MaxLoads)
                throw new BridgeException("CAPACITY", MaxLoads + " scripts have run in this Navisworks session; restart Navisworks to run more.");

            MethodInfo run;
            try
            {
                Type type = Assembly.Load(assembly).GetType(typeName, true);
                run = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
                if (run == null) throw new MissingMethodException(typeName, methodName);
            }
            catch (Exception ex)
            {
                throw new BridgeException("INVALID_SCRIPT", "The compiled script could not be loaded: " + ex.Message);
            }

            string undoBefore = Safe(() => doc.NextUndo);
            var clock = Stopwatch.StartNew();
            string output = null;
            Exception error = null;
            Transaction tx = null;
            try
            {
                if (wrap) tx = doc.BeginTransaction("MHTools-N AI: C# script");
                output = run.Invoke(null, new object[] { doc, inputs, CancellationToken.None }) as string;
                if (tx != null) tx.Commit();
            }
            catch (TargetInvocationException ex)
            {
                error = ex.InnerException ?? ex;
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                if (tx != null) tx.Dispose();
            }
            clock.Stop();

            string undoAfter = Safe(() => doc.NextUndo);
            bool undoable = !string.Equals(undoBefore, undoAfter, StringComparison.Ordinal);
            Watchdog.Log("Bridge script " + (error == null ? "ran" : "FAILED") + " in " + clock.ElapsedMilliseconds + " ms" +
                         (undoable ? ", undo step '" + undoAfter + "'" : ", no new undo step") + (error != null ? ": " + error.GetType().Name + ": " + error.Message : ""));

            if (error != null)
                throw new BridgeException("SCRIPT_FAILED", error.GetType().Name + ": " + error.Message +
                    (undoable ? "\nThe model DID change (a new undo step '" + undoAfter + "' exists)." : "\nNo new undo step appeared.") +
                    "\n" + Short(error.StackTrace, 3000));

            var result = new Dictionary<string, object>
            {
                { "output", Short(output, MaxOutput) },
                { "milliseconds", clock.ElapsedMilliseconds },
                { "newUndoStep", undoable ? undoAfter : null }
            };
            if (dryRun)
            {
                if (!undoable) result["dryRun"] = "Nothing to undo: no new undo step appeared (the run changed nothing undoable - or something Ctrl+Z does not cover).";
                else
                {
                    bool undone = Safe(() => doc.TryUndo());
                    result["dryRun"] = undone ? "Undone with TryUndo." : "TryUndo FAILED - the change is still there.";
                    result["undoStepNow"] = Safe(() => doc.NextUndo);
                }
            }
            return result;
        }

        private static string Preview(string source)
        {
            if (string.IsNullOrEmpty(source)) return "(no source text)";
            string[] lines = source.Replace("\r", "").Split('\n');
            string shown = string.Join("\n", lines.Take(25));
            if (shown.Length > 1800) shown = shown.Substring(0, 1800);
            return shown + (lines.Length > 25 || shown.Length >= 1800 ? "\n... (" + lines.Length + " lines in all)" : "");
        }

        private static string Short(string text, int max)
        {
            if (text == null) return null;
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }

        private static T Safe<T>(Func<T> read)
        {
            try { return read(); }
            catch (Exception) { return default(T); }
        }
    }
}
