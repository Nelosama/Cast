using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CastDesktop.Models;

namespace CastDesktop.Services
{
    public class FFmpegService
    {
        private Process? _ffmpegProcess;
        public bool IsRunning => _ffmpegProcess != null && !_ffmpegProcess.HasExited;

        public event Action<string>? LogReceived;
        public event Action<double, double>? StatsUpdated; // (fps, bitrateKbps)
        public event Action<int>? ProcessExited;
        public event Action<string>? PermissionErrorDetected;

        public string StreamUrl { get; private set; } = string.Empty;

        public bool StartStreaming(QualityProfile profile, string targetSource = "desktop", int ingestPort = 8088, int backendHttpPort = 5000)
        {
            if (IsRunning)
            {
                StopStreaming();
            }

            string ffmpegExe = FindFFmpegExecutable();
            if (string.IsNullOrEmpty(ffmpegExe))
            {
                LogReceived?.Invoke("ERROR: FFmpeg executable not found. Please install FFmpeg and add it to PATH or app folder.");
                return false;
            }

            // Stream URL hosted on embedded C# HTTP server for Chromecast playback
            StreamUrl = $"http://{GetLocalIPAddress()}:{backendHttpPort}/stream/live.mp4";

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = ffmpegExe,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            // Safely build ArgumentList to avoid unescaped window title quote breaking the process
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("gdigrab");
            startInfo.ArgumentList.Add("-framerate");
            startInfo.ArgumentList.Add(profile.Framerate.ToString());

            if (targetSource.StartsWith("window="))
            {
                string windowTitle = targetSource.Substring("window=".Length);
                startInfo.ArgumentList.Add("-i");
                startInfo.ArgumentList.Add($"title={windowTitle}");
            }
            else
            {
                startInfo.ArgumentList.Add("-draw_mouse");
                startInfo.ArgumentList.Add("1");
                startInfo.ArgumentList.Add("-i");
                startInfo.ArgumentList.Add("desktop");
            }

            int bitrateKbps = profile.BitrateKbps;
            int maxRateKbps = (int)(bitrateKbps * 1.2);
            int bufSizeKbps = bitrateKbps * 2;
            string codecProfile = profile.Codec == "libx265" ? "main" : profile.Profile;

            startInfo.ArgumentList.Add("-c:v");
            startInfo.ArgumentList.Add(profile.Codec);
            startInfo.ArgumentList.Add("-preset");
            startInfo.ArgumentList.Add(profile.Preset);
            startInfo.ArgumentList.Add("-profile:v");
            startInfo.ArgumentList.Add(codecProfile);
            startInfo.ArgumentList.Add("-pix_fmt");
            startInfo.ArgumentList.Add("yuv420p");
            startInfo.ArgumentList.Add("-b:v");
            startInfo.ArgumentList.Add($"{bitrateKbps}k");
            startInfo.ArgumentList.Add("-maxrate");
            startInfo.ArgumentList.Add($"{maxRateKbps}k");
            startInfo.ArgumentList.Add("-bufsize");
            startInfo.ArgumentList.Add($"{bufSizeKbps}k");
            startInfo.ArgumentList.Add("-g");
            startInfo.ArgumentList.Add((profile.Framerate * 2).ToString());

            if (profile.Resolution == "1920x1080")
            {
                startInfo.ArgumentList.Add("-vf");
                startInfo.ArgumentList.Add("scale=1920:1080:flags=lanczos");
            }
            else if (profile.Resolution == "3840x2160")
            {
                startInfo.ArgumentList.Add("-vf");
                startInfo.ArgumentList.Add("scale=3840:2160:flags=lanczos");
            }
            else if (profile.Resolution == "1280x720")
            {
                startInfo.ArgumentList.Add("-vf");
                startInfo.ArgumentList.Add("scale=1280:720:flags=lanczos");
            }

            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("mp4");
            startInfo.ArgumentList.Add("-movflags");
            startInfo.ArgumentList.Add("frag_keyframe+empty_moov+default_base_moof");
            startInfo.ArgumentList.Add($"tcp://127.0.0.1:{ingestPort}");

            LogReceived?.Invoke($"Launching FFmpeg with {startInfo.ArgumentList.Count} arguments targeting tcp://127.0.0.1:{ingestPort}");

            try
            {
                _ffmpegProcess = new Process { StartInfo = startInfo };
                _ffmpegProcess.EnableRaisingEvents = true;

                _ffmpegProcess.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        ParseFFmpegLog(e.Data);
                        LogReceived?.Invoke(e.Data);
                    }
                };

                _ffmpegProcess.Exited += (sender, e) =>
                {
                    int exitCode = _ffmpegProcess?.ExitCode ?? -1;
                    LogReceived?.Invoke($"FFmpeg process exited with code {exitCode}");
                    ProcessExited?.Invoke(exitCode);
                    _ffmpegProcess = null;
                };

                _ffmpegProcess.Start();
                _ffmpegProcess.BeginErrorReadLine();

                return true;
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"Exception starting FFmpeg: {ex.Message}");
                return false;
            }
        }

        public void StopStreaming()
        {
            if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
            {
                try
                {
                    _ffmpegProcess.Kill();
                    _ffmpegProcess.WaitForExit(1000);
                }
                catch (Exception ex)
                {
                    LogReceived?.Invoke($"Error stopping FFmpeg process: {ex.Message}");
                }
                finally
                {
                    _ffmpegProcess = null;
                }
            }
        }

        private void ParseFFmpegLog(string line)
        {
            try
            {
                // Check for screen capture permission denied logs in Windows / gdigrab
                string lower = line.ToLower();
                if (lower.Contains("permission denied") || lower.Contains("access is denied") ||
                    lower.Contains("cannot open display") || lower.Contains("gdigrab: graphics device interface error"))
                {
                    PermissionErrorDetected?.Invoke("Permiso de captura de pantalla denegado por Windows o dispositivo gráfico restringido. Verifica la configuración de Privacidad y Seguridad en Windows (Privacidad > Captura de pantalla/Grabación) y ejecuta la aplicación como Administrador.");
                }

                Match fpsMatch = Regex.Match(line, @"fps=\s*([\d\.]+)");
                Match bitrateMatch = Regex.Match(line, @"bitrate=\s*([\d\.]+)kbits/s");

                if (fpsMatch.Success && bitrateMatch.Success)
                {
                    double fps = double.Parse(fpsMatch.Groups[1].Value);
                    double bitrate = double.Parse(bitrateMatch.Groups[1].Value);
                    StatsUpdated?.Invoke(fps, bitrate);
                }
            }
            catch
            {
                // Ignore parse errors for standard logs
            }
        }

        private string FindFFmpegExecutable()
        {
            string exeName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

            if (File.Exists(exeName)) return Path.GetFullPath(exeName);
            if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, exeName)))
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, exeName);

            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (pathEnv != null)
            {
                foreach (string path in pathEnv.Split(Path.PathSeparator))
                {
                    string fullPath = Path.Combine(path.Trim(), exeName);
                    if (File.Exists(fullPath)) return fullPath;
                }
            }

            return "ffmpeg";
        }

        private string GetLocalIPAddress()
        {
            try
            {
                using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, 0);
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
