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
        private readonly CastBackendClient _backendClient;
        private readonly FFmpegService _ffmpegService;
        private Process? _backendProcess;
        private DispatcherTimer _telemetryTimer;

        private List<CastDevice> _devices = new();
        private bool _isStreaming = false;

        public MainWindow()
        {
            InitializeComponent();

            _backendClient = new CastBackendClient();
            _ffmpegService = new FFmpegService();

            _ffmpegService.LogReceived += OnFFmpegLogReceived;
            _ffmpegService.StatsUpdated += OnFFmpegStatsUpdated;
            _ffmpegService.ProcessExited += OnFFmpegProcessExited;

            _telemetryTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _telemetryTimer.Tick += TelemetryTimer_Tick;

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            AppendLog("Iniciando CastDesktop HD...");
            EnsureBackendRunning();
            await RefreshDevicesAsync();
            await CheckBandwidthAsync();
            _telemetryTimer.Start();
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            _telemetryTimer.Stop();
            _ffmpegService.StopStreaming();
            StopBackendProcess();
        }

        private void EnsureBackendRunning()
        {
            Task.Run(async () =>
            {
                var status = await _backendClient.GetStatusAsync();
                if (status == null)
                {
                    Dispatcher.Invoke(() => AppendLog("Servicio backend no detectado en puerto 5000. Intentando iniciar python app.py..."));
                    StartPythonBackend();
                }
                else
                {
                    Dispatcher.Invoke(() => TxtBackendState.Text = $"Backend: Activo (IP {status.LocalIp})");
                }
            });
        }

        private void StartPythonBackend()
        {
            try
            {
                string scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "cast_backend", "app.py");
                if (!File.Exists(scriptPath))
                {
                    scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cast_backend", "app.py");
                }

                if (File.Exists(scriptPath))
                {
                    string pythonExe = GetPythonExecutable();
                    ProcessStartInfo startInfo = new ProcessStartInfo
                    {
                        FileName = pythonExe,
                        Arguments = $"\"{scriptPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    _backendProcess = Process.Start(startInfo);
                    AppendLog($"Servidor Python ({pythonExe}) iniciado en background: PID {_backendProcess?.Id}");
                }
                else
                {
                    AppendLog("AVISO: No se encontró script 'app.py' para auto-iniciar backend local.");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Error iniciando script backend: {ex.Message}");
            }
        }

        private string GetPythonExecutable()
        {
            if (OperatingSystem.IsWindows())
            {
                // Check if python.exe exists in PATH
                string? path = Environment.GetEnvironmentVariable("PATH");
                if (path != null)
                {
                    foreach (var p in path.Split(Path.PathSeparator))
                    {
                        string fullPath = Path.Combine(p.Trim(), "python.exe");
                        if (File.Exists(fullPath)) return fullPath;
                    }
                }
                return "python";
            }
            return "python3";
        }

        private void StopBackendProcess()
        {
            if (_backendProcess != null && !_backendProcess.HasExited)
            {
                try
                {
                    _backendProcess.Kill();
                }
                catch { }
            }
        }

        private async Task RefreshDevicesAsync()
        {
            BtnRefreshDevices.IsEnabled = false;
            AppendLog("Buscando dispositivos Chromecast en la red local vía mDNS...");

            _devices = await _backendClient.GetDevicesAsync();
            CmbDevices.ItemsSource = null;
            CmbDevices.ItemsSource = _devices;

            if (_devices.Count > 0)
            {
                CmbDevices.SelectedIndex = 0;
                AppendLog($"Se encontraron {_devices.Count} dispositivo(s) Chromecast.");
            }
            else
            {
                AppendLog("⚠️ No se encontraron dispositivos Chromecast en la red local.");
            }

            BtnRefreshDevices.IsEnabled = true;
        }

        private async Task CheckBandwidthAsync()
        {
            var (lanConnected, warning) = await _backendClient.CheckBandwidthAsync();
            if (!string.IsNullOrEmpty(warning))
            {
                BorderWarning.Visibility = Visibility.Visible;
                TxtWarningMessage.Text = warning;
                AppendLog($"Advertencia de red: {warning}");
            }
            else
            {
                BorderWarning.Visibility = Visibility.Collapsed;
            }
        }

        private async void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            await RefreshDevicesAsync();
            await CheckBandwidthAsync();
        }

        private void CmbSourceType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TxtWindowTitle == null || LblWindowTitle == null) return;

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
            if (GridCustomQuality == null) return;

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
        }

        private void CustomSetting_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Custom settings slider listener
        }

        private QualityProfile SelectedQualityProfile
        {
            get
            {
                switch (CmbQualityPreset.SelectedIndex)
                {
                    case 0: return QualityProfile.HighQuality;
                    case 1: return QualityProfile.MediumQuality;
                    case 2: return QualityProfile.LowQuality;
                    case 3:
                        return new QualityProfile
                        {
                            Name = "Personalizada",
                            Resolution = "Native",
                            Framerate = (int)SldFps.Value,
                            BitrateKbps = (int)SldBitrate.Value * 1000,
                            Preset = "medium",
                            Codec = CmbCodec.SelectedIndex == 1 ? "libx265" : "libx264",
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
            var selectedDevice = CmbDevices.SelectedItem as CastDevice;
            if (selectedDevice == null)
            {
                MessageBox.Show("Por favor selecciona un dispositivo Chromecast de la lista.", "Dispositivo no seleccionado", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            QualityProfile profile = SelectedQualityProfile;
            AppendLog($"Iniciando captura con perfil: {profile.Name} ({profile.Framerate} FPS, {profile.BitrateKbps / 1000} Mbps)");

            string source = "desktop";
            if (CmbSourceType.SelectedIndex == 1)
            {
                string title = TxtWindowTitle.Text.Trim();
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

            var (success, message) = await _backendClient.StartCastAsync(selectedDevice.Name, _ffmpegService.StreamUrl);
            if (success)
            {
                _isStreaming = true;
                BtnStartStop.Content = "⏹ DETENER TRANSMISIÓN";
                BtnStartStop.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D32F2F"));
                BtnReconnect.IsEnabled = true;

                TxtStatusState.Text = "Transmitiendo";
                TxtStatusState.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#388E3C"));

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
            _ffmpegService.StopStreaming();
            await _backendClient.StopCastAsync();

            _isStreaming = false;
            BtnStartStop.Content = "▶ INICIAR TRANSMISIÓN";
            BtnStartStop.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CAF50"));
            BtnReconnect.IsEnabled = false;

            TxtStatusState.Text = "Detenido";
            TxtStatusState.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C62828"));
            TxtStatusFps.Text = "0.0 FPS";
            TxtStatusBitrate.Text = "0 Kbps";

            AppendLog("Transmisión detenida.");
        }

        private async void BtnReconnect_Click(object sender, RoutedEventArgs e)
        {
            var selectedDevice = CmbDevices.SelectedItem as CastDevice;
            if (selectedDevice == null || !_isStreaming) return;

            AppendLog($"Reconectando sesión de transmisión con {selectedDevice.Name}...");
            var (success, message) = await _backendClient.StartCastAsync(selectedDevice.Name, _ffmpegService.StreamUrl);
            if (success)
            {
                AppendLog("Reconexión exitosa.");
            }
            else
            {
                AppendLog($"Fallo en reconexión: {message}");
            }
        }

        private async void TelemetryTimer_Tick(object? sender, EventArgs e)
        {
            var status = await _backendClient.GetStatusAsync();
            if (status != null)
            {
                TxtBackendState.Text = $"Backend: Conectado ({status.LocalIp})";
                if (!string.IsNullOrEmpty(status.LastError))
                {
                    AppendLog($"Aviso de Backend: {status.LastError}");
                }
            }
            else
            {
                TxtBackendState.Text = "Backend: Desconectado";
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
                TxtStatusFps.Text = $"{fps:F1} FPS";
                TxtStatusBitrate.Text = $"{bitrateKbps:N0} Kbps";
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
            string time = DateTime.Now.ToString("HH:mm:ss");
            TxtLogs.AppendText($"[{time}] {message}\n");
            ScrollLogs.ScrollToEnd();
        }
    }
}
