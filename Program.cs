using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.ComponentModel;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ChromiumLauncher
{
    internal static class Program
    {
        private static Dictionary<string, string> Config;
        private static string IniPath;
        private static string LogPath;
        private static bool IsDebugMode = false;

        private static void Log(string message)
        {
            if (!IsDebugMode) return;

            try
            {
                string logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                File.AppendAllText(LogPath, logMessage);
            }
            catch { /* Silently ignore logging failures */ }
        }

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                // Initialize paths and config early
                Config = new Dictionary<string, string>();
                IniPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "chrlauncher.ini");
                LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log");
                
                LoadConfig();

                // Check INI for debug mode instead of args
                IsDebugMode = Config.GetValueOrDefault("Debug", "false").Equals("true", StringComparison.OrdinalIgnoreCase);

                if (IsDebugMode)
                {
                    Log("===============================================================");
                    Log("Application Started. Debug mode enabled via INI.");
                }

                string binDir = Path.GetFullPath(Config.GetValueOrDefault("ChromiumDirectory", ".\\bin"));
                string exeName = Config.GetValueOrDefault("ChromiumBinary", "chrome.exe");
                string exePath = Path.Combine(binDir, exeName);
                
                // If updateUrl is empty, it will fall back to the built-in GitHub fetcher
                string updateUrl = Config.GetValueOrDefault("ChromiumUpdateUrl", "");
                string cmdLine = Config.GetValueOrDefault("ChromiumCommandLine", "");
                
                long lastCheck = long.Parse(Config.GetValueOrDefault("ChromiumLastCheck", "0"));
                int checkPeriodDays = int.Parse(Config.GetValueOrDefault("ChromiumCheckPeriod", "2"));

                Log($"Resolved paths -> BinDir: '{binDir}', ExePath: '{exePath}'");

                bool isExeMissing = !File.Exists(exePath);
                bool shouldCheckUpdate = checkPeriodDays == -1 || 
                    (checkPeriodDays > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - lastCheck > (checkPeriodDays * 86400));
                bool isExeInUse = IsChromiumRunning(binDir, exeName);

                Log($"Update conditions -> Missing: {isExeMissing}, ShouldCheck: {shouldCheckUpdate}, IsExeInUse: {isExeInUse}");

                // Trigger update check if needed (Notice we no longer check !string.IsNullOrEmpty(updateUrl) here)
                if ((shouldCheckUpdate || isExeMissing) && !isExeInUse)
                {
                    Log($"Initiating async update check... (Force Download: {isExeMissing})");
                    CheckAndUpdateAsync(updateUrl, binDir, isExeMissing).GetAwaiter().GetResult();
                }

                // Final safety check: abort if we still don't have an executable
                if (!File.Exists(exePath))
                {
                    Log("Executable is missing after update phase. Aborting launch.");
                    return; 
                }

                Log("Preparing to launch Chromium...");
                LaunchChromium(exePath, cmdLine, args);
            }
            catch (Exception ex)
            {
                Log($"FATAL ERROR in Main: {ex.Message}\n{ex.StackTrace}");
                MessageBox.Show($"A fatal error occurred. Check debug.log for details.\n\n{ex.Message}", "Launcher Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            
            Log("Application Exiting.");
        }

        static void LoadConfig()
        {
            if (!File.Exists(IniPath)) return;

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
                string downloadUrl = null;
                long newTimestamp = 0;
                string version = "Unknown";

                using var client = new HttpClient();
                // GitHub API requires a User-Agent header
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ChromiumLauncher/1.0");

                if (!string.IsNullOrEmpty(updateUrl))
                {
                    Log($"Using custom Update URL: {updateUrl}");
                    string response = await client.GetStringAsync(updateUrl);
                    
                    var apiData = response.Split(';')
                        .Select(p => p.Split('=', 2))
                        .Where(p => p.Length == 2)
                        .ToDictionary(p => p[0].Trim(), p => p[1].Trim());

                    if (apiData.TryGetValue("download", out downloadUrl) && 
                        apiData.TryGetValue("timestamp", out string newTimestampStr))
                    {
                        newTimestamp = long.Parse(newTimestampStr);
                        version = apiData.GetValueOrDefault("version", "Unknown");
                    }
                }
                else
                {
                    // Built-in GitHub API fetcher
                    string targetArch = Config.GetValueOrDefault("ChromiumArchitecture", "x64");
                    string githubApi = "https://api.github.com/repos/ungoogled-software/ungoogled-chromium-windows/releases";
                    Log($"Using built-in GitHub fetcher for architecture: {targetArch}");

                    string response = await client.GetStringAsync(githubApi);
                    using var doc = JsonDocument.Parse(response);
                    
                    foreach (var release in doc.RootElement.EnumerateArray())
                    {
                        var assets = release.GetProperty("assets");
                        JsonElement? targetAsset = null;
                        
                        foreach (var asset in assets.EnumerateArray())
                        {
                            string assetName = asset.GetProperty("name").GetString();
                            if (assetName != null && assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && assetName.Contains(targetArch, StringComparison.OrdinalIgnoreCase))
                            {
                                targetAsset = asset;
                                break;
                            }
                        }

                        if (targetAsset.HasValue)
                        {
                            downloadUrl = targetAsset.Value.GetProperty("browser_download_url").GetString();
                            
                            if (release.TryGetProperty("published_at", out JsonElement pubAt))
                            {
                                if (DateTimeOffset.TryParse(pubAt.GetString(), out DateTimeOffset publishedDate))
                                {
                                    newTimestamp = publishedDate.ToUnixTimeSeconds();
                                }
                            }

                            string releaseName = release.GetProperty("name").GetString() ?? "";
                            var match = Regex.Match(releaseName, @"(\d+\.\d+\.\d+\.\d+)");
                            if (match.Success) version = match.Groups[1].Value;
                            
                            Log($"Found GitHub Release: Version {version}, Timestamp {newTimestamp}");
                            break; // Stop looking, we found the newest compatible release
                        }
                    }
                }

                if (!string.IsNullOrEmpty(downloadUrl) && newTimestamp > 0)
                {
                    long currentLastCheck = long.Parse(Config.GetValueOrDefault("ChromiumLastCheck", "0"));

                    if (forceDownload || newTimestamp > currentLastCheck)
                    {
                        Log($"Downloading version ({version})...");
                        await ShowDownloadUiAndInstall(downloadUrl, version, binDir);
                        UpdateLastCheckTime();
                    }
                    else
                    {
                        Log("No new update available based on timestamp.");
                    }
                }
                else
                {
                    Log("Failed to parse valid download URL or timestamp from the update source.");
                }
            }
            catch (Exception ex) 
            { 
                Log($"Network/Update API check failed: {ex.Message}");
                
                if (forceDownload)
                {
                    MessageBox.Show($"Failed to connect to the update server to download the browser.\n\nPlease check your internet connection.\n\nError: {ex.Message}", "Network Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
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
                    
                    using var client = new HttpClient();
                    using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    
                    response.EnsureSuccessStatusCode();
                    long? totalBytes = response.Content.Headers.ContentLength;

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

                    if (!skipped)
                    {
                        lbl.Invoke((Action)(() => lbl.Text = "Extracting and installing..."));
                        pbar.Invoke((Action)(() => pbar.Style = ProgressBarStyle.Marquee));
                        
                        await Task.Run(() => InstallUpdate(tempZipFile, binDir));
                        File.Delete(tempZipFile);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) 
                { 
                    Log($"Download/Install failed: {ex.Message}");
                    MessageBox.Show("Update failed: " + ex.Message, "Download Error", MessageBoxButtons.OK, MessageBoxIcon.Error); 
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
            ZipFile.ExtractToDirectory(zipPath, tempExtractDir);

            var extractedDirs = Directory.GetDirectories(tempExtractDir);
            string sourceDir = extractedDirs.Length == 1 ? extractedDirs[0] : tempExtractDir;

            if (Directory.Exists(binDir)) Directory.Delete(binDir, true);
            Directory.CreateDirectory(binDir);

            foreach (string dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dirPath.Replace(sourceDir, binDir));
            }

            foreach (string newPath in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
            {
                File.Move(newPath, newPath.Replace(sourceDir, binDir));
            }

            Directory.Delete(tempExtractDir, true);
        }

        static void LaunchChromium(string exePath, string cmdLine, string[] args)
        {
            var formattedArgs = args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a);
            string passedArgs = string.Join(" ", formattedArgs);
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
            }
            catch (Exception ex)
            {
                Log($"Failed to start process: {ex.Message}");
                MessageBox.Show($"Failed to launch browser:\n{ex.Message}", "Launch Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        static bool IsChromiumRunning(string binDirectory, string exeName)
        {
            // 1. Normalize the directory path
            string normalizedBinDir = Path.GetFullPath(binDirectory).TrimEnd('\\') + "\\";
            
            // 2. Strip the extension (e.g., "chrome.exe" becomes "chrome")
            string processName = Path.GetFileNameWithoutExtension(exeName);
            
            // 3. Search only for processes matching the INI configuration
            Process[] targetProcesses = Process.GetProcessesByName(processName);

            foreach (Process p in targetProcesses)
            {
                try
                {
                    string processPath = p.MainModule.FileName;
                    
                    if (processPath.StartsWith(normalizedBinDir, StringComparison.OrdinalIgnoreCase))
                    {
                        return true; 
                    }
                }
                catch (Win32Exception)
                {
                    // Ignore processes we don't have permission to inspect
                }
                catch (InvalidOperationException)
                {
                    // Ignore processes that closed while we were looking at them
                }
            }

            return false;
        }
    }
}