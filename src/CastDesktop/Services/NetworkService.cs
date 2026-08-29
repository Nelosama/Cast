using System;
using System.Linq;
using System.Net.NetworkInformation;

namespace CastDesktop.Services
{
    public class NetworkService
    {
        public class NetworkStatus
        {
            public bool IsConnected { get; set; }
            public string InterfaceName { get; set; } = string.Empty;
            public NetworkInterfaceType InterfaceType { get; set; }
            public long SpeedBps { get; set; } // Bits per second reported by OS
            public double SpeedMbps => SpeedBps / 1_000_000.0;
            public string? WarningMessage { get; set; }
        }

        public static NetworkStatus CheckNetworkSpeed(int requiredBitrateKbps)
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                                 ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                 ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .OrderByDescending(ni => ni.Speed)
                    .ToList();

                if (interfaces.Count == 0)
                {
                    return new NetworkStatus
                    {
                        IsConnected = false,
                        WarningMessage = "⚠️ No se detectó ninguna interfaz de red activa (Ethernet o Wi-Fi)."
                    };
                }

                var activeNi = interfaces.First();
                long speedBps = activeNi.Speed;
                double speedMbps = speedBps / 1_000_000.0;
                double requiredMbps = requiredBitrateKbps / 1000.0;

                string? warning = null;

                // If network link speed is lower than required bitrate (plus overhead)
                if (speedMbps > 0 && speedMbps < (requiredMbps * 1.5))
                {
                    warning = $"⚠️ La velocidad del adaptador de red '{activeNi.Name}' ({speedMbps:F0} Mbps) puede ser insuficiente para el perfil seleccionado ({requiredMbps:F0} Mbps). Considera seleccionar la calidad Media o Baja.";
                }
                else if (activeNi.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && speedMbps < 50 && requiredMbps >= 30)
                {
                    warning = $"⚠️ La conexión Wi-Fi actual ({speedMbps:F0} Mbps) podría presentar fluctuaciones con la calidad Alta ({requiredMbps:F0} Mbps).";
                }

                return new NetworkStatus
                {
                    IsConnected = true,
                    InterfaceName = activeNi.Name,
                    InterfaceType = activeNi.NetworkInterfaceType,
                    SpeedBps = speedBps,
                    WarningMessage = warning
                };
            }
            catch (Exception ex)
            {
                return new NetworkStatus
                {
                    IsConnected = true,
                    WarningMessage = $"Error al verificar interfaz de red: {ex.Message}"
                };
            }
        }
    }
}
