using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace Analyzer.Profiling
{
    public class PendingMessage
    {
        public string message;
        public StackTrace stackTrace;
        public LogMessageType severity;

        public PendingMessage(string messsage, StackTrace trace, LogMessageType severity)
        {
            this.message = messsage;
            this.stackTrace = trace;
            this.severity = severity;
        }
    }

    public static class ThreadSafeLogger
    {
        private static readonly ConcurrentQueue<PendingMessage> messages = new ConcurrentQueue<PendingMessage>();
        // ignore the value, no ConcurrentHashSet in the standard, this just avoids using a mutex-locked hashset
        private static readonly ConcurrentDictionary<int, byte> keys = new ConcurrentDictionary<int, byte>();

        private const string MOD_TAG = "[Analyzer]";

        private static int mainThreadId = -1;

        /// <summary>
        /// Must be called once from the mod constructor, which runs on the Unity main thread.
        /// </summary>
        public static void CaptureMainThread()
        {
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        // If we have not captured the main thread yet, assume we are on it. Deferring a message
        // forever is worse than the rare race it protects against.
        private static bool OnMainThread =>
            mainThreadId == -1 || Thread.CurrentThread.ManagedThreadId == mainThreadId;

        public static string PrependTag(string message)
        {
            if (message.StartsWith(MOD_TAG)) return message;

            var toInsert = MOD_TAG;
            if (message.First() != ' ') toInsert += ' ';
                
            message = message.Insert(0, toInsert);

            return message;
        }

        public static void Message(string message)
        {
            if (message == null) return;
            Publish(LogMessageType.Message, PrependTag(message));
        }

        public static void Warning(string message)
        {
            if (message == null) return;
            Publish(LogMessageType.Warning, PrependTag(message));
        }

        public static void Error(string message)
        {
            if (message == null) return;
            Publish(LogMessageType.Error, PrependTag(message));
        }

        /// <summary>
        /// Verse.Log writes into a queue that the log window enumerates on the main thread.
        /// Calling it from a patch worker thread corrupts that enumeration, so off-thread
        /// messages are parked here and flushed by <see cref="DisplayLogs"/>.
        /// </summary>
        private static void Publish(LogMessageType severity, string message)
        {
            if (OnMainThread)
            {
                Emit(severity, message);
                return;
            }

            messages.Enqueue(new PendingMessage(message, null, severity));
        }

        /// <summary>Drains messages that arrived from background threads. Main thread only.</summary>
        public static void DisplayLogs()
        {
            while (messages.TryDequeue(out var pending))
            {
                Emit(pending.severity, pending.message);
            }
        }

        private static void Emit(LogMessageType severity, string message)
        {
            switch (severity)
            {
                case LogMessageType.Message:
                    Log.Message(message);
                    break;
                case LogMessageType.Warning:
                    Log.Warning(message);
                    break;
                default:
                    Log.Error(message);
                    break;
            }
        }

        public static void ErrorOnce(string message, int key)
        {
            if (keys.ContainsKey(key)) return;
            keys.TryAdd(key, 0);

            Error(message);
        }

        public static void ReportException(Exception e, string message)
        {
            var finalMessage = $"{message}, exception: {e.Message}, occured at \n{ExtractTrace(new StackTrace(e, false))}";
            Error(finalMessage);
        }

        public static void ReportExceptionOnce(Exception e, string message, int key)
        {
            if (keys.ContainsKey(key)) return;
            keys.TryAdd(key, 0);

            ReportException(e, message);
        }

        // The old commented-out DisplayLogs() pushed straight into Log.messageQueue, which is
        // what the log window enumerates. It is replaced by the Emit/DisplayLogs pair above,
        // which goes through the supported Log.Message/Warning/Error entry points instead.

        internal static string ExtractTrace(StackTrace stackTrace)
        {
            return StackTraceUtility.GetStackTraceString(stackTrace, out _);
        }
    }
}
