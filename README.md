Here is the updated `README.md` reflecting the new Widevine auto-updater features, updated architecture support, and the new INI configurations.

---

# Chromium Launcher

A lightweight, configurable, and self-updating launcher for Chromium-based browsers. Built in C#, this launcher is designed to keep your browser up-to-date while providing flexible configuration for portable setups.

By default, it includes built-in support for fetching the latest [ungoogled-chromium](https://github.com/ungoogled-software/ungoogled-chromium-windows) releases directly from GitHub.

## Features

* **Portable by Design:** Uses relative paths tied strictly to the executable directory, making it perfect for USB drives or portable setups.
* **Auto-Updater:** Automatically checks for, downloads, and installs new browser updates.
* **Widevine CDM Support:** Automatically fetches, extracts, and updates the Widevine decryption module directly from official edge servers to enable DRM video streaming (Netflix, Spotify, Hulu, etc.). Safely preserves the module across browser updates.
* **Built-in UI:** Displays a clean, native Windows progress bar when a new version is being downloaded and extracted, with an option to skip.
* **Custom Arguments:** Pass default command-line flags to the browser automatically on every launch.
* **GitHub Integration:** Natively parses GitHub Releases to find the correct architecture zip file if a custom update server is not provided.
* **Concurrency Safe:** Detects if the browser is currently running before attempting to overwrite files with an update.

## Configuration (`chrlauncher.ini`)

The launcher reads configuration from a `chrlauncher.ini` file located in the same directory as the executable. If the file is missing, the application uses built-in defaults.

### Available Settings

| Setting | Default Value | Description |
| --- | --- | --- |
| `ChromiumDirectory` | `.\bin` | The folder where the browser executable is stored. Supports both relative (anchored to the launcher) and absolute paths. |
| `ChromiumBinary` | `chrome.exe` | The name of the browser executable to launch. |
| `ChromiumCommandLine` | *(Empty)* | Command-line arguments to always append when launching the browser (e.g., `--incognito`). |
| `ChromiumArchitecture` | `x64` | The target architecture for pulling updates from GitHub and fetching Widevine (`x64`, `arm64`, or `x86`). |
| `ChromiumEnableWidevine` | `false` | Set to `true` to automatically download and maintain the latest Widevine CDM payload. |
| `ChromiumCheckPeriod` | `2` | Number of days to wait between update checks. Set to `-1` to force an update check on every launch. |
| `ChromiumUpdateUrl` | *(Empty)* | Custom API endpoint for updates. If left empty, falls back to the ungoogled-chromium GitHub repository. |
| `Debug` | `false` | Set to `true` to enable verbose logging to `debug.log`. |

*(Note: The launcher will automatically manage the `ChromiumLastCheck` key to keep track of update schedules).*

## Usage

1. Place `ChromiumLauncher.exe` in your desired root folder.
2. Create a `chrlauncher.ini` file next to it (optional, defaults will be used otherwise).
3. Run `ChromiumLauncher.exe`.
* If the browser is missing or an update is available, the launcher will download and extract it to the configured `ChromiumDirectory`.
* If `ChromiumEnableWidevine=true`, it will seamlessly check for and securely download the latest Widevine component.
* The launcher will then start the browser, passing along any configured command-line arguments.



Any arguments passed directly to `ChromiumLauncher.exe` via the command line or shortcuts (e.g., file paths or URLs) will be forwarded directly to the Chromium instance.

## Logs & Troubleshooting

If you encounter issues launching or updating the browser:

1. Open `chrlauncher.ini`.
2. Add or modify the line `Debug=true`.
3. Run the launcher again.
4. Check the generated `debug.log` file in the launcher's directory for detailed execution steps, path resolution information, and API error messages.
