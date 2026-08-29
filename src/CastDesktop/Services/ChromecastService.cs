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
        private readonly List<CastDevice> _discoveredDevices = new();
        private readonly object _devicesLock = new();

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
            try
            {
                LogReceived?.Invoke($"[ChromecastService] Conectando a {device.Name} en {device.Host}:{device.Port}...");

                _client = new ChromecastClient();
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

        public async Task StopCastAsync()
        {
            try
            {
                if (_client != null)
                {
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
