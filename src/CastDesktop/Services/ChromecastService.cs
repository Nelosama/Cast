using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
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
        private DateTime _lastLoadTime = DateTime.MinValue;
        private DateTime _lastReconnectAttemptTime = DateTime.MinValue;

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

        /// <summary>
        /// Forces an immediate discovery scan using both mDNS and SSDP in parallel.
        /// Called when the user clicks the "Buscar" button.
        /// </summary>
        public void ForceDiscoveryNow()
        {
            // Restart background loop
            StopDiscovery();
            _discoveryCts = new CancellationTokenSource();
            var ct = _discoveryCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    LogReceived?.Invoke("[ChromecastService] Búsqueda forzada: ejecutando mDNS + SSDP en paralelo...");

                    // Run mDNS and SSDP simultaneously
                    var mDnsTask = ZeroconfResolver.ResolveAsync("_googlecast._tcp.local.", scanTime: TimeSpan.FromSeconds(3), retries: 2, retryDelayMilliseconds: 500, callback: null, cancellationToken: ct);
                    var ssdpTask = DiscoverSsdpDevicesAsync(ct);

                    await Task.WhenAll(mDnsTask, ssdpTask);

                    var newDevices = new List<CastDevice>();

                    // Process mDNS results
                    var mDnsResults = await mDnsTask;
                    foreach (var result in mDnsResults)
                    {
                        string host = result.IPAddress;
                        int port = 8009;
                        string friendlyName = result.DisplayName ?? "Chromecast";
                        string model = "Chromecast";

                        foreach (var s in result.Services)
                        {
                            if (s.Value.Port > 0) port = s.Value.Port;
                            foreach (var dict in s.Value.Properties)
                            {
                                foreach (var kvp in dict)
                                {
                                    if (kvp.Key.Equals("fn", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kvp.Value))
                                        friendlyName = kvp.Value;
                                    if (kvp.Key.Equals("md", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kvp.Value))
                                        model = kvp.Value;
                                }
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
                        LogReceived?.Invoke($"[mDNS] Encontrado: '{friendlyName}' ({model}) en {host}:{port}");
                    }

                    // Add SSDP results (avoid duplicates by IP)
                    var ssdpResults = await ssdpTask;
                    foreach (var dev in ssdpResults)
                    {
                        if (!newDevices.Any(d => d.Host == dev.Host))
                        {
                            newDevices.Add(dev);
                        }
                    }

                    LogReceived?.Invoke($"[ChromecastService] Búsqueda forzada completada: {newDevices.Count} dispositivo(s) encontrado(s).");

                    lock (_devicesLock)
                    {
                        _discoveredDevices.Clear();
                        _discoveredDevices.AddRange(newDevices);
                    }

                    DevicesDiscovered?.Invoke(newDevices);

                    // Continue with normal background loop after this forced scan
                    _ = BackgroundDiscoveryLoopAsync(ct);
                }
                catch (Exception ex)
                {
                    LogReceived?.Invoke($"[ChromecastService] Error en búsqueda forzada: {ex.Message}");
                }
            });
        }

        private async Task BackgroundDiscoveryLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var results = await ZeroconfResolver.ResolveAsync("_googlecast._tcp.local.", scanTime: TimeSpan.FromSeconds(3), retries: 3, retryDelayMilliseconds: 1000, callback: null, cancellationToken: ct);
                    var newDevices = new List<CastDevice>();

                    foreach (var result in results)
                    {
                        string host = result.IPAddress;
                        int port = 8009;

                        string friendlyName = result.DisplayName ?? "Chromecast";
                        string model = "Chromecast";

                        var serviceKeys = string.Join(", ", result.Services.Keys);
                        var txtDetails = new List<string>();

                        foreach (var s in result.Services)
                        {
                            if (s.Value.Port > 0)
                            {
                                port = s.Value.Port;
                            }

                            foreach (var dict in s.Value.Properties)
                            {
                                foreach (var kvp in dict)
                                {
                                    txtDetails.Add($"{s.Key}->{kvp.Key}={kvp.Value}");
                                    if (kvp.Key.Equals("fn", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kvp.Value))
                                    {
                                        friendlyName = kvp.Value;
                                    }
                                    if (kvp.Key.Equals("md", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kvp.Value))
                                    {
                                        model = kvp.Value;
                                    }
                                }
                            }
                        }

                        LogReceived?.Invoke($"[mDNS Diagnostics] Discovered Host: {host}, DisplayName: '{result.DisplayName}', Id: '{result.Id}', ServiceKeys: [{serviceKeys}], TXT Properties: [{string.Join("; ", txtDetails)}]");

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

                    if (newDevices.Count == 0)
                    {
                        LogReceived?.Invoke("[ChromecastService] mDNS no encontró dispositivos. Intentando fallback rápido con SSDP (DIAL)...");
                        var ssdpDevices = await DiscoverSsdpDevicesAsync(ct);
                        if (ssdpDevices.Count > 0)
                        {
                            newDevices.AddRange(ssdpDevices);
                        }
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

        private async Task<List<CastDevice>> DiscoverSsdpDevicesAsync(CancellationToken ct)
        {
            var devices = new List<CastDevice>();
            try
            {
                // Get all local IPv4 addresses from active network interfaces
                var localIps = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address)
                    .ToList();

                LogReceived?.Invoke($"[ChromecastService] SSDP: Buscando en {localIps.Count} interfaces de red: {string.Join(", ", localIps)}");

                var request = "M-SEARCH * HTTP/1.1\r\n" +
                              "HOST: 239.255.255.250:1900\r\n" +
                              "MAN: \"ssdp:discover\"\r\n" +
                              "MX: 3\r\n" +
                              "ST: urn:dial-multiscreen-org:service:dial:1\r\n" +
                              "\r\n";
                var requestBytes = Encoding.ASCII.GetBytes(request);
                var multicastEndpoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

                // Search on each interface in parallel (key fix: bind to specific IP)
                var tasks = localIps.Select(ip => SearchSsdpOnInterfaceAsync(ip, requestBytes, multicastEndpoint, ct)).ToList();
                var results = await Task.WhenAll(tasks);

                foreach (var list in results)
                {
                    foreach (var dev in list)
                    {
                        if (!devices.Any(d => d.Host == dev.Host))
                        {
                            devices.Add(dev);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"[ChromecastService] Error en fallback SSDP: {ex.Message}");
            }
            return devices;
        }

        private async Task<List<CastDevice>> SearchSsdpOnInterfaceAsync(IPAddress localIp, byte[] requestBytes, IPEndPoint multicastEndpoint, CancellationToken ct)
        {
            var devices = new List<CastDevice>();
            try
            {
                using var client = new UdpClient();
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                client.Client.Bind(new IPEndPoint(localIp, 0));

                await client.SendAsync(requestBytes, requestBytes.Length, multicastEndpoint);

                var timeoutTask = Task.Delay(4000, ct);

                while (!ct.IsCancellationRequested)
                {
                    var receiveTask = client.ReceiveAsync();
                    var completedTask = await Task.WhenAny(receiveTask, timeoutTask);

                    if (completedTask == timeoutTask)
                        break;

                    var result = await receiveTask;
                    var response = Encoding.ASCII.GetString(result.Buffer);

                    if (response.Contains("LOCATION:", StringComparison.OrdinalIgnoreCase))
                    {
                        var ip = result.RemoteEndPoint.Address.ToString();
                        if (devices.Any(d => d.Host == ip))
                            continue;

                        // Extract LOCATION URL to fetch device info
                        string locationUrl = "";
                        foreach (var line in response.Split('\n'))
                        {
                            if (line.TrimStart().StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                            {
                                locationUrl = line.Substring(line.IndexOf(':') + 1).Trim().TrimEnd('\r');
                                break;
                            }
                        }

                        // Fetch device-desc.xml to get friendly name
                        string friendlyName = "Google TV (SSDP)";
                        string modelName = "Google TV";
                        string uuid = Guid.NewGuid().ToString();
                        if (!string.IsNullOrEmpty(locationUrl))
                        {
                            try
                            {
                                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                                var xml = await http.GetStringAsync(locationUrl, ct);

                                var fnMatch = System.Text.RegularExpressions.Regex.Match(xml, @"<friendlyName>(.+?)</friendlyName>");
                                if (fnMatch.Success) friendlyName = fnMatch.Groups[1].Value;

                                var mdMatch = System.Text.RegularExpressions.Regex.Match(xml, @"<modelName>(.+?)</modelName>");
                                if (mdMatch.Success) modelName = mdMatch.Groups[1].Value;

                                var udnMatch = System.Text.RegularExpressions.Regex.Match(xml, @"<UDN>uuid:(.+?)</UDN>");
                                if (udnMatch.Success) uuid = udnMatch.Groups[1].Value;

                                LogReceived?.Invoke($"[SSDP] Dispositivo encontrado: '{friendlyName}' ({modelName}) en {ip} vía interfaz {localIp}");
                            }
                            catch (Exception ex)
                            {
                                LogReceived?.Invoke($"[SSDP] No se pudo leer descripción de {ip}: {ex.Message}");
                            }
                        }

                        devices.Add(new CastDevice
                        {
                            Name = friendlyName,
                            ModelName = modelName,
                            Host = ip,
                            Port = 8009,
                            Is4k = true,
                            Uuid = uuid
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"[SSDP] Error en interfaz {localIp}: {ex.Message}");
            }
            return devices;
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

                LogLocalNetworkInterfaces();

                var receiver = new ChromecastReceiver
                {
                    DeviceUri = new Uri($"https://{device.Host}:{device.Port}"),
                    Port = device.Port
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
                _lastLoadTime = DateTime.UtcNow;

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
                string details = FormatExceptionDetails(ex);
                LogReceived?.Invoke($"[ChromecastService] Error al iniciar transmisión:\n{details}");
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
                string idleReason = status.IdleReason ?? "NULO";
                LogReceived?.Invoke($"[ChromecastService] Estado de reproductor pasó a IDLE (Motivo: {idleReason}).");

                if ((DateTime.UtcNow - _lastLoadTime).TotalSeconds < 5)
                {
                    LogReceived?.Invoke("[ChromecastService] Estado IDLE ignorado por periodo de gracia tras la carga inicial/reconexión.");
                    return;
                }

                if (string.Equals(status.IdleReason, "ERROR", StringComparison.OrdinalIgnoreCase))
                {
                    LogReceived?.Invoke("[ChromecastService] Estado IDLE con motivo ERROR detectado. Iniciando reconexión...");
                    Task.Run(() => TryAutoReconnectAsync());
                }
                else
                {
                    LogReceived?.Invoke($"[ChromecastService] Estado IDLE con motivo '{idleReason}' es normal/no crítico. No se reconecta.");
                }
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
                            bool inGracePeriod = (DateTime.UtcNow - _lastLoadTime).TotalSeconds < 5;

                            if (status == null)
                            {
                                if (!inGracePeriod)
                                {
                                    shouldReconnect = true;
                                }
                            }
                            else if (status.PlayerState == PlayerStateType.Idle)
                            {
                                if (!inGracePeriod && string.Equals(status.IdleReason, "ERROR", StringComparison.OrdinalIgnoreCase))
                                {
                                    shouldReconnect = true;
                                }
                            }
                        }
                        catch
                        {
                            if ((DateTime.UtcNow - _lastLoadTime).TotalSeconds >= 5)
                            {
                                shouldReconnect = true;
                            }
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
            if ((DateTime.UtcNow - _lastReconnectAttemptTime).TotalSeconds < 5)
            {
                LogReceived?.Invoke("[ChromecastService] Intento de reconexión ignorado por debounce/cooldown (< 5s).");
                return;
            }

            if (!_reconnectSemaphore.Wait(0)) return;

            try
            {
                if (!IsCasting || _isUserStopping || CurrentDevice == null || string.IsNullOrEmpty(ActiveStreamUrl))
                    return;

                _lastReconnectAttemptTime = DateTime.UtcNow;

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

                        LogLocalNetworkInterfaces();

                        var receiver = new ChromecastReceiver
                        {
                            DeviceUri = new Uri($"https://{CurrentDevice.Host}:{CurrentDevice.Port}"),
                            Port = CurrentDevice.Port
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
                        _lastLoadTime = DateTime.UtcNow;

                        reconnected = true;
                        IsCasting = true;
                        StatusChanged?.Invoke(true, $"Transmitiendo hacia {CurrentDevice.Name}");
                        LogReceived?.Invoke($"[ChromecastService] Reconexión automática exitosa en el intento {attempt}.");
                        break;
                    }
                    catch (Exception ex)
                    {
                        string details = FormatExceptionDetails(ex);
                        LogReceived?.Invoke($"[ChromecastService] Fallo en intento {attempt}/{maxAttempts} de reconexión:\n{details}");
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
                if (ex.Message.Contains("Client disconnected before receiving response", StringComparison.OrdinalIgnoreCase) ||
                    (ex.InnerException?.Message.Contains("Client disconnected before receiving response", StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    LogReceived?.Invoke("[ChromecastService] Transmisión detenida correctamente (desconexión del cliente antes de respuesta tratada como cierre exitoso).");
                }
                else
                {
                    LogReceived?.Invoke($"[ChromecastService] Error al detener transmisión: {ex.Message}");
                }
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

        private void LogLocalNetworkInterfaces()
        {
            try
            {
                var localIps = new List<string>();
                var hostName = Dns.GetHostName();
                var addresses = Dns.GetHostAddresses(hostName);
                foreach (var ip in addresses)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        localIps.Add(ip.ToString());
                    }
                }

                var activeInterfaces = new List<string>();
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus == OperationalStatus.Up &&
                        ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    {
                        var ipProps = ni.GetIPProperties();
                        var unicast = ipProps.UnicastAddresses
                            .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                            .Select(u => u.Address.ToString());
                        activeInterfaces.Add($"{ni.Name} ({ni.NetworkInterfaceType}): [{string.Join(", ", unicast)}]");
                    }
                }

                LogReceived?.Invoke($"[ChromecastService] Diagnóstico Interfaces Locales -> IPs DNS: {string.Join(", ", localIps)} | Adaptadores Activos: {string.Join("; ", activeInterfaces)}");
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"[ChromecastService] No se pudo obtener diagnóstico de red: {ex.Message}");
            }
        }

        private string FormatExceptionDetails(Exception ex)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Tipo: {ex.GetType().FullName}");
            sb.AppendLine($"Mensaje: {ex.Message}");
            sb.AppendLine($"StackTrace:\n{ex.StackTrace}");
            if (ex.InnerException != null)
            {
                sb.AppendLine($"InnerException Tipo: {ex.InnerException.GetType().FullName}");
                sb.AppendLine($"InnerException Mensaje: {ex.InnerException.Message}");
                sb.AppendLine($"InnerException StackTrace:\n{ex.InnerException.StackTrace}");
            }
            return sb.ToString().TrimEnd();
        }
    }
}
