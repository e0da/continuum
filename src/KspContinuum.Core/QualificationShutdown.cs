using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace KspContinuum
{
    // Optional assemblies register on the main thread; the plugin never references Mission's assembly.
    public static class QualificationShutdown
    {
        public static readonly ShutdownRegistry Requests = new ShutdownRegistry();
        static bool captureReleased;
        static QualificationShutdown()
        {
            Requests.Register("qualification-capture", reason => captureReleased ? "inactive" : "error");
        }
        public static void RecordCaptureCleanup(bool succeeded) { captureReleased = succeeded; }
    }
    public sealed class ShutdownReceipt
    {
        readonly string[] handlers;
        readonly int errors;
        internal ShutdownReceipt(List<string> handlers, int errors) { this.handlers = handlers.ToArray(); this.errors = errors; }
        public bool HasErrors { get { return errors != 0; } }
        public int HandlerCount { get { return handlers.Length; } }
        public string Text(string captureStatus)
        {
            if (captureStatus != "complete" && captureStatus != "timeout" && captureStatus != "error" && captureStatus != "interrupted")
                throw new ArgumentException("Invalid capture status.", "captureStatus");
            var text = new StringBuilder("schema=ksp-continuum-shutdown/v1\nstatus=");
            text.Append(HasErrors ? "error" : "complete").Append("\nrequestedBy=qualification\ncaptureStatus=").Append(captureStatus)
                .Append("\nhandlers=").Append(handlers.Length).Append('\n');
            foreach (string handler in handlers) text.Append("handler=").Append(handler).Append('\n');
            return text.Append("errors=").Append(errors).Append('\n').ToString();
        }
    }
    public sealed class ShutdownRegistry
    {
        sealed class Registration : IDisposable
        {
            public readonly string Name;
            public readonly Func<string, string> Handler;
            readonly ShutdownRegistry owner;
            public Registration(ShutdownRegistry owner, string name, Func<string, string> handler)
            { this.owner = owner; Name = name; Handler = handler; }
            public void Dispose() { owner.handlers.Remove(this); }
        }
        readonly List<Registration> handlers = new List<Registration>();
        bool requested;
        ShutdownReceipt receipt;
        public IDisposable Register(string name, Func<string, string> handler)
        {
            if (requested) throw new InvalidOperationException("Shutdown has already been requested.");
            if (handlers.Count == 32) throw new InvalidOperationException("Shutdown handler limit reached.");
            if (name == null || !Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z") || handler == null)
                throw new ArgumentException("A safe owner name and callback are required.");
            if (handlers.Exists(item => item.Name == name)) throw new InvalidOperationException("Shutdown owner already registered: " + name);
            var registration = new Registration(this, name, handler); handlers.Add(registration); return registration;
        }
        public ShutdownReceipt Request(string reason)
        {
            if (receipt != null) return receipt;
            if (requested) throw new InvalidOperationException("Shutdown dispatch is already in progress.");
            if (string.IsNullOrEmpty(reason)) throw new ArgumentException("Shutdown reason is required.", "reason");
            requested = true;
            var results = new List<string>(); int errors = 0;
            var snapshot = handlers.ToArray(); handlers.Clear();
            foreach (Registration entry in snapshot)
            {
                string status;
                try
                {
                    status = entry.Handler(reason);
                    if (status != "interrupted" && status != "already-terminal" && status != "inactive" && status != "error") status = "error";
                }
                catch (Exception) { status = "error"; }
                if (status == "error") errors++;
                results.Add(entry.Name + ":" + status);
            }
            receipt = new ShutdownReceipt(results, errors);
            return receipt;
        }
    }
}
