using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CastDesktop.Models;
using CastDesktop.Services;

namespace CastDesktop
{
    public partial class MainWindow : Window
    {
        private readonly ChromecastService _chromecastService;
        private readonly HttpStreamServer _httpStreamServer;
        private readonly FFmpegService _ffmpegService;
        private DispatcherTimer _telemetryTimer;

        private List<CastDevice> _devices = new();
        private bool _isStreaming = false;
        private bool _isInitialized = false;

        public MainWindow()
        {
            try
            {
                InitializeComponent();
            }
            catch (Exception ex)
            {
                Exception actualEx = ex.InnerException ?? ex;
                string errorLog = $"[XAML Init Exception] Type: {actualEx.GetType().FullName}\nMessage: {actualEx.Message}\nStackTrace:\n{actualEx.StackTrace}";
                Debug.WriteLine(errorLog);
                try
                {
                    File.WriteAllText("xaml_init_error.log", errorLog);
                }
                catch { }
                MessageBox.Show($"Error durante la inicialización de XAML:\n\n{actualEx.GetType().Name}: {actualEx.Message}\n\nUbicación:\n{actualEx.StackTrace}",
                                "Error de Inicialización XAML", MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }

            _chromecastService = new ChromecastService();
            _httpStreamServer = new HttpStreamServer();
            _ffmpegService = new FFmpegService();

            _chromecastService.LogReceived += AppendLog;
            _chromecastService.DevicesDiscovered += OnDevicesDiscovered;
            _chromecastService.StatusChanged += OnChromecastStatusChanged;

            _httpStreamServer.LogReceived += AppendLog;

            _ffmpegService.LogReceived += OnFFmpegLogReceived;
            _ffmpegService.StatsUpdated += OnFFmpegStatsUpdated;
            _ffmpegService.ProcessExited += OnFFmpegProcessExited;
            _ffmpegService.PermissionErrorDetected += OnFFmpegPermissionErrorDetected;

            _telemetryTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _telemetryTimer.Tick += TelemetryTimer_Tick;

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _isInitialized = true;

            AppendLog("Iniciando CastDesktop HD (Modo Nativo C# / Sin Python)...");

            _httpStreamServer.Start(5000, 8088);
            if (TxtBackendState != null)
            {
                TxtBackendState.Text = $"Backend C#: Activo ({_chromecastService.GetLocalIPAddress()}:5000)";
            }

            _chromecastService.StartDiscovery();
            CheckBandwidthAsync();
            _telemetryTimer.Start();
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            _telemetryTimer?.Stop();
            _ffmpegService?.StopStreaming();
            _chromecastService?.StopDiscovery();
            _httpStreamServer?.Stop();
        }

        private void OnDevicesDiscovered(List<CastDevice> devices)
        {
            Dispatcher.Invoke(() =>
            {
                if (CmbDevices == null) return;

                var previouslySelected = CmbDevices.SelectedItem as CastDevice;

                _devices = devices ?? new List<CastDevice>();
                CmbDevices.ItemsSource = null;
                CmbDevices.ItemsSource = _devices;

                if (_devices.Count > 0)
                {
                    CastDevice? matchedDevice = null;
                    if (previouslySelected != null)
                    {
                        matchedDevice = _devices.FirstOrDefault(d =>
                            (!string.IsNullOrEmpty(d.Uuid) && d.Uuid == previouslySelected.Uuid) ||
                            (!string.IsNullOrEmpty(d.Name) && d.Name == previouslySelected.Name));
                    }

                    if (matchedDevice != null)
                    {
                        CmbDevices.SelectedItem = matchedDevice;
                    }
                    else
                    {
                        CmbDevices.SelectedIndex = 0;
                    }
                }
            });
        }

        private void OnChromecastStatusChanged(bool isCasting, string? statusMessage)
        {
            Dispatcher.Invoke(() =>
            {
                if (TxtStatusState == null || BtnStartStop == null || BtnReconnect == null || TxtStatusFps == null || TxtStatusBitrate == null)
                    return;

                if (isCasting)
                {
                    string msg = !string.IsNullOrEmpty(statusMessage) ? statusMessage : "Transmitiendo";
                    TxtStatusState.Text = msg;

                    if (msg.Contains("Reconectando"))
                    {
                        TxtStatusState.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F57C00"));
                    }
                    else
                    {
                        TxtStatusState.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#388E3C"));
                    }
                }
                else
                {
                    _isStreaming = false;
                    _ffmpegService?.StopStreaming();

                    BtnStartStop.Content = "▶ INICIAR TRANSMISIÓN";
                    BtnStartStop.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CAF50"));
                    BtnReconnect.IsEnabled = false;

                    TxtStatusState.Text = !string.IsNullOrEmpty(statusMessage) ? statusMessage : "Detenido";
                    TxtStatusState.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C62828"));
                    TxtStatusFps.Text = "0.0 FPS";
                    TxtStatusBitrate.Text = "0 Kbps";
                }
            });
        }

        private Task CheckBandwidthAsync()
        {
            if (!_isInitialized || BorderWarning == null || TxtWarningMessage == null)
                return Task.CompletedTask;

            QualityProfile profile = SelectedQualityProfile;
            if (profile == null) return Task.CompletedTask;

            var status = NetworkService.CheckNetworkSpeed(profile.BitrateKbps);
            if (status != null && !string.IsNullOrEmpty(status.WarningMessage))
            {
                BorderWarning.Visibility = Visibility.Visible;
                TxtWarningMessage.Text = status.WarningMessage;
                AppendLog(status.WarningMessage);
            }
            else
            {
                BorderWarning.Visibility = Visibility.Collapsed;
            }
            return Task.CompletedTask;
        }

        private void OnFFmpegPermissionErrorDetected(string errorMessage)
        {
            Dispatcher.Invoke(() =>
            {
                AppendLog($"⛔ ERROR DE PERMISOS: {errorMessage}");
                MessageBox.Show(errorMessage, "Permiso de Captura Denegado", MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }

        private void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            AppendLog("Buscando dispositivos Chromecast en la red local vía mDNS + SSDP...");
            _chromecastService?.ForceDiscoveryNow();
            CheckBandwidthAsync();
        }

        private void CmbSourceType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbSourceType == null || TxtWindowTitle == null || LblWindowTitle == null) return;

            if (CmbSourceType.SelectedIndex == 1) // Window
            {
                LblWindowTitle.Visibility = Visibility.Visible;
                TxtWindowTitle.Visibility = Visibility.Visible;
            }
            else
            {
                LblWindowTitle.Visibility = Visibility.Collapsed;
                TxtWindowTitle.Visibility = Visibility.Collapsed;
            }
        }

        private void CmbQualityPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GridCustomQuality == null || CmbQualityPreset == null) return;

            if (CmbQualityPreset.SelectedIndex == 3) // Custom
            {
                GridCustomQuality.IsEnabled = true;
                GridCustomQuality.Opacity = 1.0;
            }
            else
            {
                GridCustomQuality.IsEnabled = false;
                GridCustomQuality.Opacity = 0.5;
            }

            if (_isInitialized)
            {
                CheckBandwidthAsync();
            }
        }

        private void CustomSetting_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isInitialized || SldFps == null || SldBitrate == null || CmbQualityPreset == null) return;
            CheckBandwidthAsync();
        }

        private QualityProfile SelectedQualityProfile
        {
            get
            {
                if (CmbQualityPreset == null) return QualityProfile.HighQuality;

                switch (CmbQualityPreset.SelectedIndex)
                {
                    case 0: return QualityProfile.HighQuality;
                    case 1: return QualityProfile.MediumQuality;
                    case 2: return QualityProfile.LowQuality;
                    case 3:
                        int fps = SldFps != null ? (int)SldFps.Value : 60;
                        int bitrate = SldBitrate != null ? (int)SldBitrate.Value * 1000 : 35000;
                        string codec = (CmbCodec != null && CmbCodec.SelectedIndex == 1) ? "libx265" : "libx264";
                        return new QualityProfile
                        {
                            Name = "Personalizada",
                            Resolution = "Native",
                            Framerate = fps,
                            BitrateKbps = bitrate,
                            Preset = "medium",
                            Codec = codec,
                            Profile = "high"
                        };
                    default: return QualityProfile.HighQuality;
                }
            }
        }

        private async void BtnStartStop_Click(object sender, RoutedEventArgs e)
        {
            if (_isStreaming)
            {
                await StopTransmissionAsync();
            }
            else
            {
                await StartTransmissionAsync();
            }
        }

        private async Task StartTransmissionAsync()
        {
            if (CmbDevices == null) return;

            var selectedDevice = CmbDevices.SelectedItem as CastDevice;
            if (selectedDevice == null)
            {
                MessageBox.Show("Por favor selecciona un dispositivo Chromecast de la lista.", "Dispositivo no seleccionado", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            QualityProfile profile = SelectedQualityProfile;
            AppendLog($"Iniciando captura con perfil: {profile.Name} ({profile.Framerate} FPS, {profile.BitrateKbps / 1000} Mbps)");

            string source = "desktop";
            if (CmbSourceType != null && CmbSourceType.SelectedIndex == 1)
            {
                string title = TxtWindowTitle != null ? TxtWindowTitle.Text.Trim() : "";
                if (string.IsNullOrEmpty(title))
                {
                    MessageBox.Show("Por favor ingresa el título de la ventana a capturar.", "Ventana no especificada", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                source = $"window={title}";
            }

            bool ffmpegStarted = _ffmpegService.StartStreaming(profile, source);
            if (!ffmpegStarted)
            {
                MessageBox.Show("No se pudo iniciar el codificador FFmpeg. Revisa el registro de diagnósticos.", "Error FFmpeg", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            AppendLog($"Stream HTTP de FFmpeg listo en: {_ffmpegService.StreamUrl}");
            AppendLog($"Enviando orden de reproducción a Chromecast '{selectedDevice.Name}'...");

            var (success, message) = await _chromecastService.StartCastAsync(selectedDevice, _ffmpegService.StreamUrl);
            if (success)
            {
                _isStreaming = true;
                if (BtnStartStop != null)
                {
                    BtnStartStop.Content = "⏹ DETENER TRANSMISIÓN";
                    BtnStartStop.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D32F2F"));
                }
                if (BtnReconnect != null) BtnReconnect.IsEnabled = true;

                if (TxtStatusState != null)
                {
                    TxtStatusState.Text = "Transmitiendo";
                    TxtStatusState.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#388E3C"));
                }

                AppendLog($"Transmisión iniciada correctamente hacia {selectedDevice.Name}");
            }
            else
            {
                AppendLog($"Error al transmitir hacia Chromecast: {message}");
                _ffmpegService.StopStreaming();
                MessageBox.Show($"Error al transmitir hacia Chromecast:\n{message}", "Error de Conexión Cast", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task StopTransmissionAsync()
        {
            AppendLog("Deteniendo transmisión...");
            _ffmpegService?.StopStreaming();
            if (_chromecastService != null)
            {
                await _chromecastService.StopCastAsync();
            }

            _isStreaming = false;
            if (BtnStartStop != null)
            {
                BtnStartStop.Content = "▶ INICIAR TRANSMISIÓN";
                BtnStartStop.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CAF50"));
            }
            if (BtnReconnect != null) BtnReconnect.IsEnabled = false;

            if (TxtStatusState != null)
            {
                TxtStatusState.Text = "Detenido";
                TxtStatusState.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C62828"));
            }
            if (TxtStatusFps != null) TxtStatusFps.Text = "0.0 FPS";
            if (TxtStatusBitrate != null) TxtStatusBitrate.Text = "0 Kbps";

            AppendLog("Transmisión detenida.");
        }

        private async void BtnReconnect_Click(object sender, RoutedEventArgs e)
        {
            if (CmbDevices == null) return;
            var selectedDevice = CmbDevices.SelectedItem as CastDevice;
            if (selectedDevice == null || !_isStreaming) return;

            AppendLog($"Reconectando sesión de transmisión con {selectedDevice.Name}...");
            var (success, message) = await _chromecastService.StartCastAsync(selectedDevice, _ffmpegService.StreamUrl);
            if (success)
            {
                AppendLog("Reconexión exitosa.");
            }
            else
            {
                AppendLog($"Fallo en reconexión: {message}");
            }
        }

        private void TelemetryTimer_Tick(object? sender, EventArgs e)
        {
            if (TxtBackendState != null && _chromecastService != null)
            {
                TxtBackendState.Text = $"Backend C#: Activo ({_chromecastService.GetLocalIPAddress()}:5000)";
            }
        }

        private void OnFFmpegLogReceived(string log)
        {
            Dispatcher.Invoke(() => AppendLog($"[FFmpeg] {log}"));
        }

        private void OnFFmpegStatsUpdated(double fps, double bitrateKbps)
        {
            Dispatcher.Invoke(() =>
            {
                if (TxtStatusFps != null) TxtStatusFps.Text = $"{fps:F1} FPS";
                if (TxtStatusBitrate != null) TxtStatusBitrate.Text = $"{bitrateKbps:N0} Kbps";
            });
        }

        private void OnFFmpegProcessExited(int exitCode)
        {
            Dispatcher.Invoke(async () =>
            {
                if (_isStreaming)
                {
                    AppendLog($"🔴 FFmpeg se detuvo inesperadamente (Exit code {exitCode}).");
                    await StopTransmissionAsync();
                }
            });
        }

        private void AppendLog(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => AppendLog(message));
                return;
            }
            if (TxtLogs == null) return;
            string time = DateTime.Now.ToString("HH:mm:ss");
            TxtLogs.AppendText($"[{time}] {message}\n");
            if (ScrollLogs != null)
            {
                ScrollLogs.ScrollToEnd();
            }
        }
    }
}
