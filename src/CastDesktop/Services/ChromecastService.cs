using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CastDesktop.Models;
using Sharpcaster;
using Sharpcaster.Interfaces;
using Sharpcaster.Models;
using Sharpcaster.Models.Media;
using Zeroconf;

namespace CastDesktop.Services
{
    public class ChromecastService
    {
        private ChromecastClient? _client;
        private CancellationTokenSource? _discoveryCts;
        private CancellationTokenSource? _monitorCts;
        private readonly SemaphoreSlim _reconnectSemaphore = new(1, 1);
        private readonly List<CastDevice> _discoveredDevices = new();
        private readonly object _devicesLock = new();
        private volatile bool _isUserStopping = false;

        public event Action<List<CastDevice>>? DevicesDiscovered;
        public event Action<string>? LogReceived;
        public event Action<bool, string?>? StatusChanged;

        public bool IsCasting { get; private set; }
        public CastDevice? CurrentDevice { get; private set; }
        public string? ActiveStreamUrl { get; private set; }

        public void StartDiscovery()
        {
            if (_discoveryCts != null) return;

            _discoveryCts = new CancellationTokenSource();
            Task.Run(() => BackgroundDiscoveryLoopAsync(_discoveryCts.Token));
            LogReceived?.Invoke("[ChromecastService] Buscador de dispositivos Chromecast iniciado en background.");
        }

        public void StopDiscovery()
        {
            _discoveryCts?.Cancel();
            _discoveryCts = null;
        }

        private async Task BackgroundDiscoveryLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var results = await ZeroconfResolver.ResolveAsync("_googlecast._tcp.local.", cancellationToken: ct);
                    var newDevices = new List<CastDevice>();

                    foreach (var result in results)
                    {
                        string host = result.IPAddress;
                        int port = 8009;

                        string friendlyName = result.DisplayName ?? "Chromecast";
                        string model = "Chromecast";

                        if (result.Services.TryGetValue("_googlecast._tcp.local.", out var service))
                        {
                            port = service.Port;
                            foreach (var txt in service.Properties)
                            {
                                if (txt.ContainsKey("fn")) friendlyName = txt["fn"];
                                if (txt.ContainsKey("md")) model = txt["md"];
                            }
                        }

                        bool is4k = (model + " " + friendlyName).ToLower().Contains("ultra") ||
                                    (model + " " + friendlyName).ToLower().Contains("4k") ||
                                    (model + " " + friendlyName).ToLower().Contains("shield");

                        newDevices.Add(new CastDevice
                        {
                            Name = friendlyName,
                            ModelName = model,
                            Host = host,
                            Port = port,
                            Is4k = is4k,
                            Uuid = result.Id ?? Guid.NewGuid().ToString()
                        });
                    }

                    lock (_devicesLock)
                    {
                        _discoveredDevices.Clear();
                        _discoveredDevices.AddRange(newDevices);
                    }

                    DevicesDiscovered?.Invoke(newDevices);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogReceived?.Invoke($"[ChromecastService] Error en búsqueda mDNS: {ex.Message}");
                }

                try
                {
                    await Task.Delay(10000, ct);
                }
                catch
                {
                    break;
                }
            }
        }

        public async Task<(bool success, string message)> StartCastAsync(CastDevice device, string streamUrl)
        {
            _isUserStopping = false;
            try
            {
                LogReceived?.Invoke($"[ChromecastService] Conectando a {device.Name} en {device.Host}:{device.Port}...");

                if (_client != null)
                {
                    try { await _client.DisconnectAsync(); } catch { }
                    _client = null;
                }

                _client = new ChromecastClient();
                AttachClientEvents(_client);

                var receiver = new ChromecastReceiver
                {
                    DeviceUri = new Uri($"https://{device.Host}:{device.Port}")
                };

                await _client.ConnectChromecast(receiver);

                LogReceived?.Invoke("[ChromecastService] Conexión establecida. Iniciando reproductor por defecto...");
                await _client.LaunchApplicationAsync("CC1AD845"); // Default Media Receiver

                var media = new Media
                {
                    ContentUrl = streamUrl,
                    ContentType = "video/mp4",
                    StreamType = StreamType.Live
                };

                await _client.MediaChannel.LoadAsync(media);

                IsCasting = true;
                CurrentDevice = device;
                ActiveStreamUrl = streamUrl;

                StatusChanged?.Invoke(true, $"Transmitiendo hacia {device.Name}");
                LogReceived?.Invoke($"[ChromecastService] Transmisión iniciada exitosamente hacia {device.Name} ({streamUrl})");

                StartReconnectionMonitor();

                return (true, $"Transmisión iniciada hacia {device.Name}");
            }
            catch (Exception ex)
            {
                IsCasting = false;
                StatusChanged?.Invoke(false, ex.Message);
                LogReceived?.Invoke($"[ChromecastService] Error al iniciar transmisión: {ex.Message}");
                return (false, ex.Message);
            }
        }

        private void AttachClientEvents(ChromecastClient client)
        {
            client.Disconnected += OnClientDisconnected;
            if (client.MediaChannel != null)
            {
                client.MediaChannel.StatusChanged += OnMediaStatusChanged;
            }
        }

        private void OnClientDisconnected(object? sender, EventArgs e)
        {
            if (IsCasting && !_isUserStopping)
            {
                LogReceived?.Invoke("[ChromecastService] Desconexión detectada vía evento Disconnected.");
                Task.Run(() => TryAutoReconnectAsync());
            }
        }

        private void OnMediaStatusChanged(object? sender, MediaStatus status)
        {
            if (IsCasting && !_isUserStopping && status != null && status.PlayerState == PlayerStateType.Idle)
            {
                LogReceived?.Invoke("[ChromecastService] Estado de reproductor pasó a IDLE inesperadamente.");
                Task.Run(() => TryAutoReconnectAsync());
            }
        }

        private void StartReconnectionMonitor()
        {
            StopReconnectionMonitor();
            _monitorCts = new CancellationTokenSource();
            Task.Run(() => BackgroundMonitorLoopAsync(_monitorCts.Token));
            LogReceived?.Invoke("[ChromecastService] Monitor de reconexión automática iniciado en background.");
        }

        private void StopReconnectionMonitor()
        {
            _monitorCts?.Cancel();
            _monitorCts = null;
        }

        private async Task BackgroundMonitorLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(4000, ct);

                    if (!IsCasting || _isUserStopping) continue;

                    bool shouldReconnect = false;
                    if (_client == null)
                    {
                        shouldReconnect = true;
                    }
                    else
                    {
                        try
                        {
                            var status = await _client.MediaChannel.GetMediaStatusAsync();
                            if (status == null || status.PlayerState == PlayerStateType.Idle)
                            {
                                shouldReconnect = true;
                            }
                        }
                        catch
                        {
                            shouldReconnect = true;
                        }
                    }

                    if (shouldReconnect && IsCasting && !_isUserStopping)
                    {
                        LogReceived?.Invoke("[ChromecastService] Monitor detectó pérdida de sesión/canal de media.");
                        await TryAutoReconnectAsync();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogReceived?.Invoke($"[ChromecastService] Error en monitor de reconexión: {ex.Message}");
                }
            }
        }

        private async Task TryAutoReconnectAsync()
        {
            if (!_reconnectSemaphore.Wait(0)) return;

            try
            {
                if (!IsCasting || _isUserStopping || CurrentDevice == null || string.IsNullOrEmpty(ActiveStreamUrl))
                    return;

                const int maxAttempts = 3;
                bool reconnected = false;

                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    if (_isUserStopping) break;

                    LogReceived?.Invoke($"[ChromecastService] Intentando reconexión automática ({attempt}/{maxAttempts}) a {CurrentDevice.Name}...");
                    StatusChanged?.Invoke(true, $"Reconectando... ({attempt}/{maxAttempts})");

                    try
                    {
                        if (_client != null)
                        {
                            try { _client.Disconnected -= OnClientDisconnected; } catch { }
                            try { await _client.DisconnectAsync(); } catch { }
                            _client = null;
                        }

                        _client = new ChromecastClient();
                        AttachClientEvents(_client);

                        var receiver = new ChromecastReceiver
                        {
                            DeviceUri = new Uri($"https://{CurrentDevice.Host}:{CurrentDevice.Port}")
                        };

                        await _client.ConnectChromecast(receiver);
                        await _client.LaunchApplicationAsync("CC1AD845");

                        var media = new Media
                        {
                            ContentUrl = ActiveStreamUrl,
                            ContentType = "video/mp4",
                            StreamType = StreamType.Live
                        };

                        await _client.MediaChannel.LoadAsync(media);

                        reconnected = true;
                        IsCasting = true;
                        StatusChanged?.Invoke(true, $"Transmitiendo hacia {CurrentDevice.Name}");
                        LogReceived?.Invoke($"[ChromecastService] Reconexión automática exitosa en el intento {attempt}.");
                        break;
                    }
                    catch (Exception ex)
                    {
                        LogReceived?.Invoke($"[ChromecastService] Fallo en intento {attempt}/{maxAttempts} de reconexión: {ex.Message}");
                        if (attempt < maxAttempts)
                        {
                            await Task.Delay(2000);
                        }
                    }
                }

                if (!reconnected && !_isUserStopping)
                {
                    IsCasting = false;
                    StatusChanged?.Invoke(false, "Conexión perdida. Se agotaron los reintentos.");
                    LogReceived?.Invoke("[ChromecastService] No se pudo restablecer la conexión después de 3 intentos.");
                }
            }
            finally
            {
                _reconnectSemaphore.Release();
            }
        }

        public async Task StopCastAsync()
        {
            _isUserStopping = true;
            StopReconnectionMonitor();

            try
            {
                if (_client != null)
                {
                    try { _client.Disconnected -= OnClientDisconnected; } catch { }
                    await _client.MediaChannel.StopAsync();
                    await _client.DisconnectAsync();
                    _client = null;
                }
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"[ChromecastService] Error al detener transmisión: {ex.Message}");
            }
            finally
            {
                IsCasting = false;
                CurrentDevice = null;
                ActiveStreamUrl = null;
                StatusChanged?.Invoke(false, "Transmisión detenida");
            }
        }

        public string GetLocalIPAddress()
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect("8.8.8.8", 65530);
                var endPoint = socket.LocalEndPoint as System.Net.IPEndPoint;
                return endPoint?.Address.ToString() ?? "127.0.0.1";
            }
            catch
            {
                return "127.0.0.1";
            }
        }
    }
}
