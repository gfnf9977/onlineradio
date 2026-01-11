using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace OnlineRadioStation.Domain
{
    public class LiveStreamService : IDisposable
    {
        private Process? _ffmpegProcess;
        private Stream? _ffmpegInputStream;
        private bool _isBroadcasting;
        private readonly string _outputFolder;
        private readonly string _webRootPath;
        private readonly string _ffmpegPath; // Окреме поле для шляху

        public LiveStreamService(string webRootPath)
        {
            _webRootPath = webRootPath;
            _outputFolder = Path.Combine(webRootPath, "live");
            
            // Будуємо шлях до FFmpeg один раз
            _ffmpegPath = Path.Combine(webRootPath, "ffmpeg", "ffmpeg.exe");

            if (!Directory.Exists(_outputFolder)) 
                Directory.CreateDirectory(_outputFolder);
                
            Console.WriteLine($"[LiveService] Init. Output: {_outputFolder}");
            Console.WriteLine($"[LiveService] FFmpeg path: {_ffmpegPath}");
        }

        public bool IsBroadcasting => _isBroadcasting;

        public void StartBroadcast()
        {
            if (_isBroadcasting) return;

            // Перевірка існування файлу перед запуску
            if (!File.Exists(_ffmpegPath))
            {
                Console.WriteLine($"[LiveService ERROR] FFmpeg не знайдено за шляхом: {_ffmpegPath}");
                return;
            }

            try
            {
                // Очищення папки
                var files = Directory.GetFiles(_outputFolder);
                foreach (var file in files) 
                {
                    try { File.Delete(file); } catch { }
                }
            }
            catch (Exception ex) 
            {
                Console.WriteLine($"[LiveService] Помилка очищення папки: {ex.Message}");
            }

            var playlistPath = Path.Combine(_outputFolder, "index.m3u8");
            
            // Додали логування помилок (-loglevel error)
            var args = $"-y -f webm -i pipe:0 -c:a aac -b:a 192k -ac 2 " +
                       $"-f hls -hls_time 4 -hls_list_size 5 -hls_flags delete_segments " +
                       $"\"{playlistPath}\"";

            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = args,
                RedirectStandardInput = true,
                RedirectStandardOutput = true, // Читаємо вивід
                RedirectStandardError = true,  // Читаємо помилки
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Console.WriteLine("[LiveService] Starting FFmpeg...");
            
            try 
            {
                _ffmpegProcess = new Process { StartInfo = psi };
                
                // Підписка на логи
                _ffmpegProcess.ErrorDataReceived += (sender, e) => 
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Console.WriteLine($"[FFMPEG ERROR] {e.Data}");
                };
                _ffmpegProcess.OutputDataReceived += (sender, e) => 
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Console.WriteLine($"[FFMPEG INFO] {e.Data}");
                };

                _ffmpegProcess.Start();
                _ffmpegProcess.BeginErrorReadLine();
                _ffmpegProcess.BeginOutputReadLine();

                _ffmpegInputStream = _ffmpegProcess.StandardInput.BaseStream;
                _isBroadcasting = true;
                Console.WriteLine("[LiveService] FFmpeg started successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LiveService FATAL] Не вдалося запустити процес: {ex.Message}");
                _isBroadcasting = false;
            }
        }

        public async Task WriteAudioDataAsync(byte[] buffer, int count)
        {
            if (!_isBroadcasting || _ffmpegInputStream == null) return;

            try
            {
                // Перевіряємо, чи процес ще живий
                if (_ffmpegProcess == null || _ffmpegProcess.HasExited)
                {
                    Console.WriteLine("[LiveService] FFmpeg процес помер несподівано!");
                    StopBroadcast();
                    return;
                }

                await _ffmpegInputStream.WriteAsync(buffer, 0, count);
                await _ffmpegInputStream.FlushAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LiveService] Помилка запису даних: {ex.Message}");
                StopBroadcast();
            }
        }

        public void StopBroadcast()
        {
            if (!_isBroadcasting) return;
            
            Console.WriteLine("[LiveService] Stopping broadcast...");
            _isBroadcasting = false;
            try
            {
                _ffmpegInputStream?.Close();
                _ffmpegProcess?.WaitForExit(2000); // Чекаємо трохи довше
                if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                {
                    _ffmpegProcess.Kill();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LiveService] Помилка при зупинці: {ex.Message}");
            }
        }

        public void Dispose() => StopBroadcast();
    }
}