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

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            LoadConfig();

            string binDir = Path.GetFullPath(Config.GetValueOrDefault("ChromiumDirectory", ".\\bin"));
            string exeName = Config.GetValueOrDefault("ChromiumBinary", "chrome.exe");
            string exePath = Path.Combine(binDir, exeName);
            string updateUrl = Config.GetValueOrDefault("ChromiumUpdateUrl", "");
            string cmdLine = Config.GetValueOrDefault("ChromiumCommandLine", "");
            
            long lastCheck = long.Parse(Config.GetValueOrDefault("ChromiumLastCheck", "0"));
            int checkPeriodDays = int.Parse(Config.GetValueOrDefault("ChromiumCheckPeriod", "2"));

            bool shouldCheckUpdate = checkPeriodDays == -1 || 
                (checkPeriodDays > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - lastCheck > (checkPeriodDays * 86400));

            bool isExeInUse = IsFileLocked(exePath);

            if (shouldCheckUpdate && !string.IsNullOrEmpty(updateUrl) && !isExeInUse)
            {
                CheckAndUpdateAsync(updateUrl, binDir).GetAwaiter().GetResult();
            }

            LaunchChromium(exePath, cmdLine, args);
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
        }

        static async Task CheckAndUpdateAsync(string updateUrl, string binDir)
        {
            try
            {
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

                    if (newTimestamp > currentLastCheck)
                    {
                        string version = apiData.GetValueOrDefault("version", "Unknown");
                        await ShowDownloadUiAndInstall(downloadUrl, version, binDir);
                        UpdateLastCheckTime();
                    }
                }
            }
            catch { /* Silently fail on network issues and proceed to launch */ }
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
                catch (OperationCanceledException) { /* User skipped */ }
                catch (Exception ex) { MessageBox.Show("Update failed: " + ex.Message); }
                finally { form.Close(); }
            };

            Application.Run(form);
        }

        static void InstallUpdate(string zipPath, string binDir)
        {
            string tempExtractDir = Path.Combine(Path.GetTempPath(), "chromium_extract_" + Guid.NewGuid().ToString());
            ZipFile.ExtractToDirectory(zipPath, tempExtractDir);

            // Chromium zips usually have a root folder (e.g., ungoogled-chromium_148..._windows)
            var extractedDirs = Directory.GetDirectories(tempExtractDir);
            string sourceDir = extractedDirs.Length == 1 ? extractedDirs[0] : tempExtractDir;

            if (Directory.Exists(binDir)) Directory.Delete(binDir, true);
            Directory.CreateDirectory(binDir);

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
        }

        static void LaunchChromium(string exePath, string cmdLine, string[] args)
        {
            if (!File.Exists(exePath)) return;

            // Format all incoming arguments: wrap in quotes if they contain spaces
            var formattedArgs = args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a);
            string passedArgs = string.Join(" ", formattedArgs);

            // Combine the INI command line with any passed arguments
            string finalArguments = $"{cmdLine} {passedArgs}".Trim();

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = finalArguments,
                UseShellExecute = false
            });
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
                return true; // File is in use
            }
            return false;
        }
    }
}