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

            // Stream URL hosted on Python Flask persistent server for Chromecast playback
            StreamUrl = $"http://{GetLocalIPAddress()}:{backendHttpPort}/stream/live.mp4";

            // Build FFmpeg command for High Image Quality
            string inputArgs;
            if (targetSource.StartsWith("window="))
            {
                string windowTitle = targetSource.Substring("window=".Length);
                inputArgs = $"-f gdigrab -framerate {profile.Framerate} -i title=\"{windowTitle}\"";
            }
            else
            {
                // Full Desktop Screen Capture
                inputArgs = $"-f gdigrab -framerate {profile.Framerate} -draw_mouse 1 -i desktop";
            }

            string scaleFilter = "";
            if (profile.Resolution == "1920x1080")
            {
                scaleFilter = "-vf \"scale=1920:1080:flags=lanczos\"";
            }
            else if (profile.Resolution == "3840x2160")
            {
                scaleFilter = "-vf \"scale=3840:2160:flags=lanczos\"";
            }
            else if (profile.Resolution == "1280x720")
            {
                scaleFilter = "-vf \"scale=1280:720:flags=lanczos\"";
            }

            int bitrateKbps = profile.BitrateKbps;
            int maxRateKbps = (int)(bitrateKbps * 1.2);
            int bufSizeKbps = bitrateKbps * 2;

            // Ensure valid codec profile (x265 uses main profile, x264 uses high profile)
            string codecProfile = profile.Codec == "libx265" ? "main" : profile.Profile;

            string videoCodecArgs = $"-c:v {profile.Codec} -preset {profile.Preset} -profile:v {codecProfile} " +
                                   $"-pix_fmt yuv420p -b:v {bitrateKbps}k -maxrate {maxRateKbps}k -bufsize {bufSizeKbps}k " +
                                   $"-g {profile.Framerate * 2} {scaleFilter}";

            // Send Fragmented MP4 stream to local Python backend TCP ingest socket
            string outputArgs = $"-f mp4 -movflags frag_keyframe+empty_moov+default_base_moof tcp://127.0.0.1:{ingestPort}";

            string arguments = $"{inputArgs} {videoCodecArgs} {outputArgs}";

            LogReceived?.Invoke($"Launching FFmpeg: {ffmpegExe} {arguments}");

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = ffmpegExe,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

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
            // Parse line for stats: fps= 60 bitrate=34500.2kbits/s
            try
            {
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
            // Check PATH or current directory
            string exeName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

            if (File.Exists(exeName)) return Path.GetFullPath(exeName);
            if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, exeName)))
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, exeName);

            // Search in PATH
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (pathEnv != null)
            {
                foreach (string path in pathEnv.Split(Path.PathSeparator))
                {
                    string fullPath = Path.Combine(path.Trim(), exeName);
                    if (File.Exists(fullPath)) return fullPath;
                }
            }

            return "ffmpeg"; // Fallback to executable name
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
