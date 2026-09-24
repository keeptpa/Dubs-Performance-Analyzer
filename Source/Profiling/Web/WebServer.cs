using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Analyzer.Profiling.Web
{
    /// <summary>
    /// A hand-rolled HTTP/1.1 server on a loopback socket.
    ///
    /// We deliberately avoid <c>System.Net.HttpListener</c>: under Unity's Mono runtime it has
    /// platform-specific behaviour, while a raw <see cref="TcpListener"/> behaves identically
    /// everywhere and gives us chunked SSE streaming for free.
    ///
    /// Nothing in this file may touch Unity or Verse types - it runs on its own threads.
    /// </summary>
    public static class WebServer
    {
        private const int MaxSseClients = 8;
        private const int MaxHeaderBytes = 16384;
        private static readonly byte[] Crlf = { 13, 10 };
        private static readonly string[] HeaderSeparator = { "\r\n" };

        private static TcpListener listener;
        private static Thread acceptThread;
        private static volatile bool running;
        private static readonly object clientsLock = new object();
        private static readonly List<TcpClient> sseClients = new List<TcpClient>();

        public static int Port { get; private set; }
        public static bool IsRunning => running;

        /// <summary>Starts listening. Returns null on success, or a human readable error.</summary>
        public static string Start(int preferredPort)
        {
            if (running) return null;

            int port = Math.Max(1, preferredPort);
            Exception last = null;
            TcpListener bound = null;

            for (int attempt = 0; attempt <= 20; attempt++)
            {
                try
                {
                    var probe = new TcpListener(IPAddress.Loopback, port + attempt);
                    probe.Start();
                    bound = probe;
                    Port = ((IPEndPoint)probe.LocalEndpoint).Port;
                    last = null;
                    break;
                }
                catch (Exception e)
                {
                    last = e;
                }
            }

            if (bound == null)
                return last?.Message ?? "no free port";

            listener = bound;
            running = true;
            // The listener is captured by the thread rather than read from the field, so a
            // restart cannot leave the old accept loop servicing the new socket.
            acceptThread = new Thread(() => AcceptLoop(bound)) { IsBackground = true, Name = "AnalyzerWebServer" };
            acceptThread.Start();
            WebTelemetry.ServerPort = Port;
            return null;
        }

        public static void Stop()
        {
            running = false;

            var current = listener;
            listener = null;
            try { current?.Stop(); } catch { /* shutting down */ }

            lock (clientsLock)
            {
                foreach (var client in sseClients)
                {
                    try { client.Close(); } catch { /* shutting down */ }
                }
                sseClients.Clear();
            }
        }

        private static void AcceptLoop(TcpListener bound)
        {
            while (running)
            {
                TcpClient client;
                try
                {
                    client = bound.AcceptTcpClient();
                }
                catch (Exception)
                {
                    if (!running) return;
                    Thread.Sleep(50);
                    continue;
                }

                ThreadPool.QueueUserWorkItem(HandleClient, client);
            }
        }

        private static void HandleClient(object state)
        {
            var client = (TcpClient)state;
            NetworkStream stream = null;
            bool handedOff = false;

            try
            {
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 15000;
                client.NoDelay = true;
                stream = client.GetStream();

                var request = ReadRequest(stream);
                if (request == null) return;

                if (request.Path == "/api/stream")
                {
                    handedOff = true;
                    StartSse(client, stream);
                    return;
                }

                Route(stream, request);
            }
            catch (Exception)
            {
                // A browser navigating away mid-response is normal; nothing to report.
            }
            finally
            {
                if (!handedOff)
                {
                    try { stream?.Close(); } catch { /* already gone */ }
                    try { client.Close(); } catch { /* already gone */ }
                }
            }
        }

        // ---- routing ---------------------------------------------------------

        private static void Route(NetworkStream stream, HttpRequest request)
        {
            switch (request.Path)
            {
                case "/":
                case "/index.html":
                    WriteBytes(stream, 200, "OK", "text/html; charset=utf-8", WebAssets.Get("index.html"));
                    return;

                case "/app.js":
                    WriteBytes(stream, 200, "OK", "application/javascript; charset=utf-8", WebAssets.Get("app.js"));
                    return;

                case "/app.css":
                    WriteBytes(stream, 200, "OK", "text/css; charset=utf-8", WebAssets.Get("app.css"));
                    return;

                case "/favicon.ico":
                    WriteBytes(stream, 204, "No Content", "image/x-icon", Array.Empty<byte>());
                    return;

                case "/api/snapshot":
                    WriteText(stream, 200, "OK", "application/json; charset=utf-8", WebTelemetry.LatestPayload ?? "{}");
                    return;

                case "/api/stack":
                    {
                        string idText = request.Get("id");
                        string stack = int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                            ? WebTelemetry.GetStack(id)
                            : null;

                        if (stack == null) WriteText(stream, 404, "Not Found", "text/plain; charset=utf-8", "no stack trace retained for that spike");
                        else WriteText(stream, 200, "OK", "text/plain; charset=utf-8", stack);
                        return;
                    }

                case "/api/control":
                    HandleControl(stream, request);
                    return;

                default:
                    WriteText(stream, 404, "Not Found", "text/plain; charset=utf-8", "not found");
                    return;
            }
        }

        private static void HandleControl(NetworkStream stream, HttpRequest request)
        {
            string result = "ok";

            try
            {
                string action = request.Get("action");
                string value = request.Get("value");

                switch (action)
                {
                    case "monitor":
                        WebEntry.Enqueue(() =>
                        {
                            bool on = ParseBool(value);
                            Settings.webMonitorEnabled = on;
                            WebTelemetry.Enabled = on;
                            if (on) WebEntry.StartServer();
                            else WebEntry.StopServer();
                            Modbase.Settings?.Write();
                        });
                        break;

                    case "deep":
                        WebEntry.Enqueue(() => WebProfilerControl.SetDeep(ParseBool(value)));
                        break;

                    case "tickMode":
                        WebEntry.Enqueue(() => WebProfilerControl.UseTickCategory(ParseBool(value)));
                        break;

                    case "pause":
                        WebEntry.Enqueue(() =>
                        {
                            global::Analyzer.Profiling.Analyzer.CurrentlyPaused = ParseBool(value);
                        });
                        break;

                    case "reset":
                        WebEntry.Enqueue(() => WebProfilerControl.ResetProfilers());
                        break;

                    case "clearSpikes":
                        WebEntry.Enqueue(WebTelemetry.ClearSpikes);
                        break;

                    case "threshold":
                        WebEntry.Enqueue(() =>
                        {
                            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float ms))
                            {
                                ms = Math.Max(5f, Math.Min(5000f, ms));
                                Settings.webSpikeThresholdMs = ms;
                                WebTelemetry.SpikeThresholdMs = ms;
                                Modbase.Settings?.Write();
                            }
                        });
                        break;

                    case "captureStacks":
                        WebEntry.Enqueue(() =>
                        {
                            bool on = ParseBool(value);
                            Settings.webCaptureStacks = on;
                            WebTelemetry.CaptureStacks = on;
                            Modbase.Settings?.Write();
                        });
                        break;

                    case "sort":
                        WebEntry.Enqueue(() =>
                        {
                            if (Enum.TryParse(value, true, out SortBy parsed))
                                global::Analyzer.Profiling.Analyzer.SortBy = parsed;
                        });
                        break;

                    case "port":
                        WebEntry.Enqueue(() =>
                        {
                            if (int.TryParse(value, out int p) && p >= 1024 && p <= 65535)
                            {
                                Settings.webMonitorPort = p;
                                Modbase.Settings?.Write();
                                WebEntry.RestartServer();
                            }
                        });
                        break;

                    default:
                        result = "unknown action";
                        break;
                }
            }
            catch (Exception e)
            {
                result = "error: " + e.Message;
            }

            WriteText(stream, 200, "OK", "application/json; charset=utf-8", "{\"result\":\"" + result.Replace("\"", "'") + "\"}");
        }

        private static bool ParseBool(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            value = value.Trim().ToLowerInvariant();
            return value == "1" || value == "true" || value == "on" || value == "yes";
        }

        // ---- server sent events ---------------------------------------------

        private static void StartSse(TcpClient client, NetworkStream stream)
        {
            lock (clientsLock)
            {
                if (sseClients.Count >= MaxSseClients)
                {
                    WriteText(stream, 503, "Service Unavailable", "text/plain; charset=utf-8", "too many live streams");
                    try { stream.Close(); } catch { /* already gone */ }
                    try { client.Close(); } catch { /* already gone */ }
                    return;
                }
                sseClients.Add(client);
            }

            var thread = new Thread(() => SseLoop(client, stream)) { IsBackground = true, Name = "AnalyzerWebSse" };
            thread.Start();
        }

        private static void SseLoop(TcpClient client, NetworkStream stream)
        {
            try
            {
                var headers = new StringBuilder();
                headers.Append("HTTP/1.1 200 OK\r\n");
                headers.Append("Content-Type: text/event-stream; charset=utf-8\r\n");
                headers.Append("Cache-Control: no-cache, no-store\r\n");
                headers.Append("Connection: keep-alive\r\n");
                headers.Append("Transfer-Encoding: chunked\r\n");
                headers.Append("Access-Control-Allow-Origin: *\r\n");
                headers.Append("\r\n");

                stream.Write(Encoding.ASCII.GetBytes(headers.ToString()), 0, headers.Length);

                // Tell the browser to reconnect fast, and give it something to render immediately.
                WriteChunk(stream, "retry: 1000\n\n");

                string lastSent = null;
                float lastPing = 0f;
                var clock = System.Diagnostics.Stopwatch.StartNew();

                while (running && IsConnected(client))
                {
                    string payload = WebTelemetry.LatestPayload;

                    if (!ReferenceEquals(payload, lastSent) && payload != null)
                    {
                        lastSent = payload;
                        WriteChunk(stream, "data: " + payload + "\n\n");
                    }
                    else if (clock.Elapsed.TotalSeconds - lastPing > 10d)
                    {
                        lastPing = (float)clock.Elapsed.TotalSeconds;
                        WriteChunk(stream, ": ping\n\n");
                    }

                    Thread.Sleep(150);
                }
            }
            catch (Exception)
            {
                // Broken pipe when the tab closes.
            }
            finally
            {
                lock (clientsLock)
                {
                    sseClients.Remove(client);
                }

                try { stream.Close(); } catch { /* already gone */ }
                try { client.Close(); } catch { /* already gone */ }
            }
        }

        private static bool IsConnected(TcpClient client)
        {
            try
            {
                var socket = client.Client;
                return socket != null
                    && socket.Connected
                    && !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
            }
            catch
            {
                return false;
            }
        }

        private static void WriteChunk(NetworkStream stream, string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            byte[] header = Encoding.ASCII.GetBytes(data.Length.ToString("x", CultureInfo.InvariantCulture) + "\r\n");

            stream.Write(header, 0, header.Length);
            stream.Write(data, 0, data.Length);
            stream.Write(Crlf, 0, Crlf.Length);
        }

        // ---- response helpers ------------------------------------------------

        private static void WriteText(NetworkStream stream, int code, string status, string contentType, string body)
        {
            WriteBytes(stream, code, status, contentType, Encoding.UTF8.GetBytes(body ?? string.Empty));
        }

        private static void WriteBytes(NetworkStream stream, int code, string status, string contentType, byte[] body)
        {
            body ??= Array.Empty<byte>();

            var header = new StringBuilder(160);
            header.Append("HTTP/1.1 ").Append(code).Append(' ').Append(status).Append("\r\n");
            header.Append("Content-Type: ").Append(contentType).Append("\r\n");
            header.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            header.Append("Cache-Control: no-store\r\n");
            header.Append("Access-Control-Allow-Origin: *\r\n");
            header.Append("Connection: close\r\n");
            header.Append("\r\n");

            byte[] headerBytes = Encoding.ASCII.GetBytes(header.ToString());
            stream.Write(headerBytes, 0, headerBytes.Length);
            if (body.Length > 0) stream.Write(body, 0, body.Length);
        }

        // ---- request parsing -------------------------------------------------

        private class HttpRequest
        {
            public string Method;
            public string Path;
            private Dictionary<string, string> query;

            public string Get(string key)
            {
                return query != null && query.TryGetValue(key, out var value) ? value : null;
            }

            public static HttpRequest Parse(string requestLine, string queryString)
            {
                var parts = requestLine.Split(' ');
                if (parts.Length < 3) return null;

                var request = new HttpRequest { Method = parts[0], Path = parts[1] };
                int question = request.Path.IndexOf('?');
                if (question >= 0)
                {
                    queryString = request.Path.Substring(question + 1);
                    request.Path = request.Path.Substring(0, question);
                }

                request.query = ParseQuery(queryString);
                return request;
            }

            private static Dictionary<string, string> ParseQuery(string queryString)
            {
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (string.IsNullOrEmpty(queryString)) return result;

                foreach (var pair in queryString.Split('&'))
                {
                    if (pair.Length == 0) continue;
                    int eq = pair.IndexOf('=');
                    if (eq < 0) result[Uri.UnescapeDataString(pair)] = string.Empty;
                    else result[Uri.UnescapeDataString(pair.Substring(0, eq))] = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                }

                return result;
            }
        }

        private static HttpRequest ReadRequest(NetworkStream stream)
        {
            var buffer = new MemoryStream();
            var chunk = new byte[1024];
            int headerEnd = -1;

            while (headerEnd < 0)
            {
                int read;
                try
                {
                    read = stream.Read(chunk, 0, chunk.Length);
                }
                catch (IOException)
                {
                    return null;
                }

                if (read <= 0) return null;
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxHeaderBytes) return null;

                headerEnd = FindHeaderEnd(buffer);
            }

            string text = Encoding.UTF8.GetString(buffer.ToArray(), 0, (int)headerEnd);
            var lines = text.Split(HeaderSeparator, StringSplitOptions.None);
            if (lines.Length == 0) return null;

            int contentLength = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(lines[i].Substring(15).Trim(), out int len))
                {
                    contentLength = len;
                }
            }

            // Drain the body so the socket does not stay half-read (we only use the query string).
            int bodyAlreadyRead = buffer.Length > 0 ? (int)(buffer.Length - headerEnd - 4) : 0;
            int remaining = contentLength - bodyAlreadyRead;
            while (remaining > 0)
            {
                int read = stream.Read(chunk, 0, Math.Min(chunk.Length, remaining));
                if (read <= 0) break;
                remaining -= read;
            }

            return HttpRequest.Parse(lines[0], null);
        }

        private static int FindHeaderEnd(MemoryStream buffer)
        {
            var data = buffer.GetBuffer();
            int length = (int)buffer.Length;

            for (int i = 0; i < length - 3; i++)
            {
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                    return i;
            }

            return -1;
        }
    }
}
