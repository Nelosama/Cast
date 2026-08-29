using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CastDesktop.Models;

namespace CastDesktop.Services
{
    public class CastBackendClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;

        public CastBackendClient(string baseUrl = "http://127.0.0.1:5000")
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(5)
            };
        }

        public async Task<List<CastDevice>> GetDevicesAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/devices");
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var devices = new List<CastDevice>();

                    if (root.TryGetProperty("devices", out var devicesArray) && devicesArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var elem in devicesArray.EnumerateArray())
                        {
                            devices.Add(new CastDevice
                            {
                                Name = elem.GetProperty("name").GetString() ?? "Dispositivo Desconocido",
                                ModelName = elem.TryGetProperty("model_name", out var m) ? m.GetString() ?? "Chromecast" : "Chromecast",
                                Uuid = elem.TryGetProperty("uuid", out var u) ? u.GetString() ?? "" : "",
                                Host = elem.TryGetProperty("host", out var h) ? h.GetString() ?? "" : "",
                                Port = elem.TryGetProperty("port", out var p) ? p.GetInt32() : 8009,
                                Is4k = elem.TryGetProperty("is_4k", out var k) && k.GetBoolean()
                            });
                        }
                    }
                    return devices;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fetching devices: {ex.Message}");
            }

            return new List<CastDevice>();
        }

        public async Task<(bool success, string message)> StartCastAsync(string deviceName, string streamUrl, string contentType = "video/mp4")
        {
            try
            {
                var payload = new
                {
                    device_name = deviceName,
                    stream_url = streamUrl,
                    content_type = contentType
                };

                string jsonPayload = JsonSerializer.Serialize(payload);
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync($"{_baseUrl}/api/cast/start", content);
                string jsonResp = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(jsonResp);
                var root = doc.RootElement;

                string status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "error" : "error";
                string message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";

                return (status == "ok", message);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public async Task<(bool success, string message)> StopCastAsync()
        {
            try
            {
                var content = new StringContent("{}", Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync($"{_baseUrl}/api/cast/stop", content);
                string jsonResp = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(jsonResp);
                var root = doc.RootElement;

                string status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "error" : "error";
                string message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";

                return (status == "ok", message);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public async Task<BackendStatus?> GetStatusAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/status");
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    return new BackendStatus
                    {
                        IsCasting = root.TryGetProperty("is_casting", out var c) && c.GetBoolean(),
                        ActiveDevice = root.TryGetProperty("active_device", out var d) ? d.GetString() : null,
                        StreamUrl = root.TryGetProperty("stream_url", out var u) ? u.GetString() : null,
                        PlayerState = root.TryGetProperty("player_state", out var p) ? p.GetString() ?? "UNKNOWN" : "UNKNOWN",
                        LocalIp = root.TryGetProperty("local_ip", out var ip) ? ip.GetString() ?? "127.0.0.1" : "127.0.0.1",
                        LastError = root.TryGetProperty("last_error", out var err) ? err.GetString() : null,
                        DeviceCount = root.TryGetProperty("device_count", out var cnt) ? cnt.GetInt32() : 0
                    };
                }
            }
            catch
            {
                // Backend unreachable
            }
            return null;
        }

        public async Task<(bool lanConnected, string? warning)> CheckBandwidthAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/bandwidth-check");
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    bool lanConnected = root.TryGetProperty("lan_connected", out var lan) && lan.GetBoolean();
                    string? warning = root.TryGetProperty("warning", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;

                    return (lanConnected, warning);
                }
            }
            catch
            {
                return (false, "No se pudo conectar con el servicio backend.");
            }
            return (false, "Respuesta inválida del backend.");
        }
    }

    public class BackendStatus
    {
        public bool IsCasting { get; set; }
        public string? ActiveDevice { get; set; }
        public string? StreamUrl { get; set; }
        public string PlayerState { get; set; } = "UNKNOWN";
        public string LocalIp { get; set; } = "127.0.0.1";
        public string? LastError { get; set; }
        public int DeviceCount { get; set; }
    }
}
