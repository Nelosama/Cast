using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CastDesktop.Services
{
    public class HttpStreamServer
    {
        private TcpListener? _httpTcpListener;
        private TcpListener? _ingestTcpListener;

        private readonly List<StreamClientQueue> _clients = new();
        private readonly object _clientLock = new();

        private byte[] _initSegment = Array.Empty<byte>();
        private readonly ConcurrentQueue<byte[]> _ringBuffer = new();
        private const int MaxRingBufferChunks = 200;

        public bool IsRunning { get; private set; }
        public int HttpPort { get; private set; } = 5000;
        public int TcpPort { get; private set; } = 8088;

        public event Action<string>? LogReceived;

        public void Start(int httpPort = 5000, int tcpPort = 8088)
        {
            if (IsRunning) return;

            HttpPort = httpPort;
            TcpPort = tcpPort;
            IsRunning = true;

            StartTcpIngestServer(tcpPort);
            StartHttpTcpServer(httpPort);

            LogReceived?.Invoke($"[HttpStreamServer] Servidor HTTP listo en http://0.0.0.0:{httpPort}/stream/live.mp4 (Ingest TCP :{tcpPort})");
        }

        public void Stop()
        {
            IsRunning = false;
            try
            {
                _httpTcpListener?.Stop();
                _httpTcpListener = null;
            }
            catch { }

            try
            {
                _ingestTcpListener?.Stop();
                _ingestTcpListener = null;
            }
            catch { }

            lock (_clientLock)
            {
                _clients.Clear();
            }
        }

        private void StartTcpIngestServer(int port)
        {
            Task.Run(async () =>
            {
                try
                {
                    _ingestTcpListener = new TcpListener(IPAddress.Any, port);
                    _ingestTcpListener.Start();

                    while (IsRunning)
                    {
                        var client = await _ingestTcpListener.AcceptTcpClientAsync();
                        LogReceived?.Invoke("[HttpStreamServer] FFmpeg conectado al puerto TCP de ingest.");
                        _ = HandleTcpIngestClientAsync(client);
                    }
                }
                catch (Exception ex)
                {
                    if (IsRunning)
                    {
                        LogReceived?.Invoke($"[HttpStreamServer] Error en TCP Ingest: {ex.Message}");
                    }
                }
            });
        }

        private async Task HandleTcpIngestClientAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                byte[] buffer = new byte[32768];
                var headerAccumulator = new List<byte>();
                bool headerCaptured = false;

                lock (_clientLock)
                {
                    _initSegment = Array.Empty<byte>();
                    _ringBuffer.Clear();
                }

                while (IsRunning)
                {
                    int bytesRead = 0;
                    try
                    {
                        bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    }
                    catch
                    {
                        break;
                    }

                    if (bytesRead <= 0) break;

                    byte[] chunk = new byte[bytesRead];
                    Array.Copy(buffer, 0, chunk, 0, bytesRead);

                    if (!headerCaptured)
                    {
                        headerAccumulator.AddRange(chunk);
                        byte[] parsedHeader = ExtractMp4InitSegment(headerAccumulator.ToArray());
                        if (parsedHeader.Length > 0)
                        {
                            lock (_clientLock)
                            {
                                _initSegment = parsedHeader;
                            }
                            headerCaptured = true;
                        }
                    }

                    lock (_clientLock)
                    {
                        _ringBuffer.Enqueue(chunk);
                        while (_ringBuffer.Count > MaxRingBufferChunks)
                        {
                            _ringBuffer.TryDequeue(out _);
                        }

                        // Broadcast to HTTP clients
                        for (int i = _clients.Count - 1; i >= 0; i--)
                        {
                            var c = _clients[i];
                            if (!c.Enqueue(chunk))
                            {
                                _clients.RemoveAt(i);
                            }
                        }
                    }
                }
                LogReceived?.Invoke("[HttpStreamServer] FFmpeg desconectado del puerto TCP Ingest.");
            }
        }

        private byte[] ExtractMp4InitSegment(byte[] data)
        {
            // Parses ftyp + moov boxes from fMP4 stream
            int offset = 0;
            int ftypEnd = -1;
            int moovEnd = -1;

            while (offset + 8 <= data.Length)
            {
                uint boxSize = ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
                string boxType = Encoding.ASCII.GetString(data, offset + 4, 4);

                if (boxSize == 0 || boxSize > data.Length - offset) break;

                if (boxType == "ftyp")
                {
                    ftypEnd = offset + (int)boxSize;
                }
                else if (boxType == "moov")
                {
                    moovEnd = offset + (int)boxSize;
                    break;
                }

                offset += (int)boxSize;
            }

            if (ftypEnd > 0 && moovEnd > ftypEnd && moovEnd <= data.Length)
            {
                byte[] initSeg = new byte[moovEnd];
                Array.Copy(data, 0, initSeg, 0, moovEnd);
                return initSeg;
            }

            return Array.Empty<byte>();
        }

        private void StartHttpTcpServer(int port)
        {
            Task.Run(async () =>
            {
                try
                {
                    _httpTcpListener = new TcpListener(IPAddress.Any, port);
                    _httpTcpListener.Start();

                    while (IsRunning)
                    {
                        var client = await _httpTcpListener.AcceptTcpClientAsync();
                        _ = ProcessHttpSocketClientAsync(client);
                    }
                }
                catch (Exception ex)
                {
                    if (IsRunning)
                    {
                        LogReceived?.Invoke($"[HttpStreamServer] Error HTTP listener TCP: {ex.Message}");
                    }
                }
            });
        }

        private async Task ProcessHttpSocketClientAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var reader = new StreamReader(stream, Encoding.ASCII);
                string? requestLine = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(requestLine)) return;

                // Read headers until empty line
                string? headerLine;
                while (!string.IsNullOrEmpty(headerLine = await reader.ReadLineAsync())) { }

                string[] parts = requestLine.Split(' ');
                string method = parts.Length > 0 ? parts[0] : "GET";
                string path = parts.Length > 1 ? parts[1] : "/";

                if (path == "/stream/live.mp4")
                {
                    if (method == "HEAD")
                    {
                        string headResponse = "HTTP/1.1 200 OK\r\n" +
                                              "Content-Type: video/mp4\r\n" +
                                              "Accept-Ranges: none\r\n" +
                                              "Access-Control-Allow-Origin: *\r\n" +
                                              "Connection: close\r\n\r\n";
                        byte[] headBytes = Encoding.ASCII.GetBytes(headResponse);
                        await stream.WriteAsync(headBytes, 0, headBytes.Length);
                        return;
                    }

                    string httpHeader = "HTTP/1.1 200 OK\r\n" +
                                        "Content-Type: video/mp4\r\n" +
                                        "Accept-Ranges: none\r\n" +
                                        "Access-Control-Allow-Origin: *\r\n" +
                                        "Connection: close\r\n\r\n";
                    byte[] headerBytes = Encoding.ASCII.GetBytes(httpHeader);
                    await stream.WriteAsync(headerBytes, 0, headerBytes.Length);

                    var clientQueue = new StreamClientQueue(150);
                    lock (_clientLock)
                    {
                        if (_initSegment.Length > 0)
                        {
                            clientQueue.Enqueue(_initSegment);
                        }
                        foreach (var chunk in _ringBuffer)
                        {
                            clientQueue.Enqueue(chunk);
                        }
                        _clients.Add(clientQueue);
                    }

                    try
                    {
                        while (IsRunning && client.Connected)
                        {
                            if (clientQueue.TryDequeue(out var data))
                            {
                                await stream.WriteAsync(data, 0, data.Length);
                                await stream.FlushAsync();
                            }
                            else
                            {
                                await Task.Delay(10);
                            }
                        }
                    }
                    catch
                    {
                        // Client disconnected
                    }
                    finally
                    {
                        lock (_clientLock)
                        {
                            _clients.Remove(clientQueue);
                        }
                    }
                }
                else
                {
                    string notFound = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    byte[] notFoundBytes = Encoding.ASCII.GetBytes(notFound);
                    await stream.WriteAsync(notFoundBytes, 0, notFoundBytes.Length);
                }
            }
        }

        private class StreamClientQueue
        {
            private readonly ConcurrentQueue<byte[]> _queue = new();
            private readonly int _maxCount;

            public StreamClientQueue(int maxCount = 150)
            {
                _maxCount = maxCount;
            }

            public bool Enqueue(byte[] data)
            {
                _queue.Enqueue(data);
                while (_queue.Count > _maxCount)
                {
                    _queue.TryDequeue(out _);
                }
                return true;
            }

            public bool TryDequeue(out byte[] data)
            {
                return _queue.TryDequeue(out data!);
            }
        }
    }
}
