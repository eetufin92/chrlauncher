using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ChromiumLauncher
{
    internal static class Program
    {
        private static Dictionary<string, string> Config = new();
        private static string IniPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "chrlauncher.ini");
        private static string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log");
        private static bool IsDebugMode = false;

        // Helper method for verbose logging
        private static void Log(string message)
        {
            if (!IsDebugMode) return;

            try
            {
                string logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                File.AppendAllText(LogPath, logMessage);
            }
            catch 
            { 
                // Silently ignore logging failures to prevent crashing the app over a locked log file
            }
        }

        [STAThread]
        static void Main(string[] args)
        {
            // 1. Intercept and remove the --debug flag
            var argsList = args.ToList();
            if (argsList.Contains("--debug"))
            {
                IsDebugMode = true;
                argsList.Remove("--debug");
                
                // Add a clear separator for a new run
                Log("===============================================================");
                Log("Application Started. Debug mode enabled.");
            }
            
            // Reassign args without the --debug flag so Chromium doesn't receive it
            args = argsList.ToArray(); 

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                Log("Loading config...");
                LoadConfig();

                string binDir = Path.GetFullPath(Config.GetValueOrDefault("ChromiumDirectory", ".\\bin"));
                string exeName = Config.GetValueOrDefault("ChromiumBinary", "chrome.exe");
                string exePath = Path.Combine(binDir, exeName);
                string updateUrl = Config.GetValueOrDefault("ChromiumUpdateUrl", "");
                string cmdLine = Config.GetValueOrDefault("ChromiumCommandLine", "");
                
                long lastCheck = long.Parse(Config.GetValueOrDefault("ChromiumLastCheck", "0"));
                int checkPeriodDays = int.Parse(Config.GetValueOrDefault("ChromiumCheckPeriod", "2"));

                Log($"Resolved paths -> BinDir: '{binDir}', ExePath: '{exePath}'");
                Log($"Config values -> UpdateUrl: '{updateUrl}', LastCheck: {lastCheck}, CheckPeriodDays: {checkPeriodDays}");

                bool isExeMissing = !File.Exists(exePath); // NEW: Check if it actually exists
                bool shouldCheckUpdate = checkPeriodDays == -1 || 
                    (checkPeriodDays > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - lastCheck > (checkPeriodDays * 86400));

                bool isExeInUse = IsFileLocked(exePath);

                // NEW: Pass 'isExeMissing' to the async method to force a download
                if ((shouldCheckUpdate || isExeMissing) && !string.IsNullOrEmpty(updateUrl) && !isExeInUse)
                {
                    Log($"Initiating async update check... (Force Download: {isExeMissing})");
                    CheckAndUpdateAsync(updateUrl, binDir, isExeMissing).GetAwaiter().GetResult();
                }
                else
                {
                    Log("Skipping update check.");
                }

                Log("Preparing to launch Chromium...");
                LaunchChromium(exePath, cmdLine, args);
            }
            catch (Exception ex)
            {
                // Catch any unhandled exceptions that might be causing a silent crash
                Log($"FATAL ERROR in Main: {ex.Message}\n{ex.StackTrace}");
                MessageBox.Show($"A fatal error occurred. Check debug.log for details.\n\n{ex.Message}", "Launcher Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            
            Log("Application Exiting.");
        }

        static void LoadConfig()
        {
            if (!File.Exists(IniPath))
            {
                Log($"Config file not found at: {IniPath}. Using defaults.");
                return;
            }

            var lines = File.ReadAllLines(IniPath);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#")) continue;
                
                var parts = line.Split('=', 2);
                if (parts.Length == 2)
                {
                    Config[parts[0].Trim()] = parts[1].Trim();
                }
            }
            Log($"Config loaded successfully. Found {Config.Count} entries.");
        }

        static void UpdateLastCheckTime()
        {
            if (!File.Exists(IniPath)) return;

            long currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var lines = File.ReadAllLines(IniPath).ToList();
            
            bool found = false;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].StartsWith("ChromiumLastCheck="))
                {
                    lines[i] = $"ChromiumLastCheck={currentTimestamp}";
                    found = true;
                    break;
                }
            }

            if (!found) lines.Add($"ChromiumLastCheck={currentTimestamp}");
            
            File.WriteAllLines(IniPath, lines);
            Log($"Updated ChromiumLastCheck to {currentTimestamp}");
        }

        static async Task CheckAndUpdateAsync(string updateUrl, string binDir, bool forceDownload) 
        {
            try
            {
                Log($"Fetching update data from: {updateUrl}");
                using var client = new HttpClient();
                string response = await client.GetStringAsync(updateUrl);
                
                var apiData = response.Split(';')
                    .Select(p => p.Split('=', 2))
                    .Where(p => p.Length == 2)
                    .ToDictionary(p => p[0].Trim(), p => p[1].Trim());

                if (apiData.TryGetValue("download", out string downloadUrl) && 
                    apiData.TryGetValue("timestamp", out string newTimestampStr))
                {
                    long newTimestamp = long.Parse(newTimestampStr);
                    long currentLastCheck = long.Parse(Config.GetValueOrDefault("ChromiumLastCheck", "0"));

                    // NEW: If forceDownload is true, ignore the timestamp check
                    if (forceDownload || newTimestamp > currentLastCheck)
                    {
                        string version = apiData.GetValueOrDefault("version", "Unknown");
                        Log($"Downloading version ({version})...");
                        await ShowDownloadUiAndInstall(downloadUrl, version, binDir);
                        UpdateLastCheckTime();
                    }
                    else
                    {
                        Log("No new update available based on timestamp.");
                    }
                }
            }
            catch (Exception ex) 
            { 
                Log($"Network/Update check silently failed: {ex.Message}");
            }
        }

        static async Task ShowDownloadUiAndInstall(string downloadUrl, string version, string binDir)
        {
            using var cts = new CancellationTokenSource();
            bool skipped = false;

            var form = new Form
            {
                Text = "chrlauncher updater",
                Size = new Size(400, 160),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };

            var lbl = new Label { Text = $"Downloading version {version}...", AutoSize = true, Location = new Point(20, 20) };
            var pbar = new ProgressBar { Location = new Point(20, 50), Size = new Size(340, 25), Style = ProgressBarStyle.Continuous };
            var btnSkip = new Button { Text = "Skip", Location = new Point(285, 85) };

            btnSkip.Click += (s, e) =>
            {
                Log("User clicked Skip.");
                skipped = true;
                cts.Cancel();
                form.Close();
            };

            form.Controls.Add(lbl);
            form.Controls.Add(pbar);
            form.Controls.Add(btnSkip);

            form.Shown += async (s, e) =>
            {
                try
                {
                    string tempZipFile = Path.Combine(Path.GetTempPath(), "chromium_update.zip");
                    Log($"Starting download to temp file: {tempZipFile}");
                    
                    using var client = new HttpClient();
                    using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    
                    response.EnsureSuccessStatusCode();
                    long? totalBytes = response.Content.Headers.ContentLength;
                    Log($"Download size: {totalBytes} bytes");

                    using var contentStream = await response.Content.ReadAsStreamAsync(cts.Token);
                    using var fileStream = new FileStream(tempZipFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                    var buffer = new byte[8192];
                    bool isMoreToRead = true;
                    long totalRead = 0;

                    while (isMoreToRead)
                    {
                        if (cts.Token.IsCancellationRequested) break;

                        int read = await contentStream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                        if (read == 0)
                        {
                            isMoreToRead = false;
                        }
                        else
                        {
                            await fileStream.WriteAsync(buffer, 0, read, cts.Token);
                            totalRead += read;

                            if (totalBytes.HasValue)
                            {
                                int percentage = (int)((totalRead * 100) / totalBytes.Value);
                                pbar.Invoke((Action)(() => pbar.Value = percentage));
                            }
                        }
                    }

                    fileStream.Close();
                    Log("Download completed successfully.");

                    if (!skipped)
                    {
                        lbl.Invoke((Action)(() => lbl.Text = "Extracting and installing..."));
                        pbar.Invoke((Action)(() => pbar.Style = ProgressBarStyle.Marquee));
                        
                        Log("Calling InstallUpdate...");
                        await Task.Run(() => InstallUpdate(tempZipFile, binDir));
                        File.Delete(tempZipFile);
                        Log("Installation complete. Temp zip deleted.");
                    }
                }
                catch (OperationCanceledException) 
                { 
                    Log("Download was cancelled by user."); 
                }
                catch (Exception ex) 
                { 
                    Log($"Download/Install failed: {ex.Message}\n{ex.StackTrace}");
                    MessageBox.Show("Update failed: " + ex.Message); 
                }
                finally 
                { 
                    form.Close(); 
                }
            };

            Application.Run(form);
        }

        static void InstallUpdate(string zipPath, string binDir)
        {
            string tempExtractDir = Path.Combine(Path.GetTempPath(), "chromium_extract_" + Guid.NewGuid().ToString());
            Log($"Extracting to temporary directory: {tempExtractDir}");
            ZipFile.ExtractToDirectory(zipPath, tempExtractDir);

            // Chromium zips usually have a root folder (e.g., ungoogled-chromium_148..._windows)
            var extractedDirs = Directory.GetDirectories(tempExtractDir);
            string sourceDir = extractedDirs.Length == 1 ? extractedDirs[0] : tempExtractDir;
            Log($"Determined source directory for files: {sourceDir}");

            if (Directory.Exists(binDir)) 
            {
                Log($"Deleting old bin directory: {binDir}");
                Directory.Delete(binDir, true);
            }
            Directory.CreateDirectory(binDir);

            Log($"Moving files to {binDir}...");
            // Move all files to the bin directory
            foreach (string dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dirPath.Replace(sourceDir, binDir));
            }

            foreach (string newPath in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
            {
                File.Move(newPath, newPath.Replace(sourceDir, binDir));
            }

            Directory.Delete(tempExtractDir, true);
            Log("File move complete and temporary extraction folder cleaned up.");
        }

        static void LaunchChromium(string exePath, string cmdLine, string[] args)
        {
            if (!File.Exists(exePath))
            {
                Log($"CRITICAL: Cannot launch Chromium. Executable not found at path: {exePath}");
                MessageBox.Show($"Chromium executable not found at:\n{exePath}\n\nPlease check your ini configuration or download the binaries.", "Launch Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Format all incoming arguments: wrap in quotes if they contain spaces
            var formattedArgs = args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a);
            string passedArgs = string.Join(" ", formattedArgs);

            // Combine the INI command line with any passed arguments
            string finalArguments = $"{cmdLine} {passedArgs}".Trim();
            
            Log($"Starting Process: {exePath}");
            Log($"Arguments: {finalArguments}");

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = finalArguments,
                    UseShellExecute = false
                });
                Log("Process launched successfully.");
            }
            catch (Exception ex)
            {
                Log($"Failed to start process: {ex.Message}\n{ex.StackTrace}");
                MessageBox.Show($"Failed to launch browser:\n{ex.Message}", "Launch Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        static bool IsFileLocked(string filePath)
        {
            if (!File.Exists(filePath)) return false;

            try
            {
                using (FileStream stream = new FileInfo(filePath).Open(FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    stream.Close();
                }
            }
            catch (IOException)
            {
                Log($"File {filePath} is currently locked (in use).");
                return true; 
            }
            return false;
        }
    }
}