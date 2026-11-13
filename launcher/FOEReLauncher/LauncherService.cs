using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FOEReLauncher
{
    /// <summary>
    /// Everything the window needs that is not drawing: settings, the download site, update runs,
    /// starting the game, noticing it runs, and updating the launcher itself.
    /// </summary>
    public sealed class LauncherService
    {
        /// <summary>
        /// The download site (GitHub Pages of the launcher repository);
        /// settings.json "baseUrl" or --base-url override it. Must end with a slash.
        /// </summary>
        public const string DefaultBaseUrl = "https://bigtester8498-dev.github.io/FOEReborn/";

        public const string UserAgent = "FOEReLauncher";

        public const string FallbackVersion = "1.0.0";

        private readonly HttpClient _http;

        // JSON files ask for gzip (Pages compresses them; the manifest shrinks about 6 times). Packs go
        // through _http without it, so Range offsets and hashes stay on the raw bytes.
        private readonly HttpClient _jsonHttp;

        public LauncherService(string? baseDir = null, string? baseUrlOverride = null, HttpClient? http = null, string? gameSettings = null)
        {
            BaseDir = baseDir ?? DefaultBaseDir();
            _http = http ?? CreateHttpClient(decompress: false);
            _jsonHttp = http ?? CreateHttpClient(decompress: true);
            Downloader = new Downloader(_http);
            Updater = new Updater(BaseDir, Downloader);
            GameSettingsOverride = gameSettings;
            Mods = new ModService(BaseDir, Updater.GameDir, Downloader, new GameSettingsFile(gameSettings ?? GameSettingsFile.DefaultPath));
            Mods.GameRunning = IsGameRunning;
            LoadSettings();
            if (!string.IsNullOrWhiteSpace(baseUrlOverride))
            {
                BaseUrlOverride = WithSlash(baseUrlOverride!);
            }
        }

        /// <summary>Mod list and catalogue mods of this install (the game's settings.json; --game-settings for tests).</summary>
        public ModService Mods { get; }

        /// <summary>--game-settings: the game is started with -settings on the same file.</summary>
        public string? GameSettingsOverride { get; }

        public string BaseDir { get; }

        public Downloader Downloader { get; }

        public Updater Updater { get; }

        public string GameDir => Updater.GameDir;

        public string SettingsFile => Path.Combine(BaseDir, "settings.json");

        public UpdateChannel Channel { get; set; }

        /// <summary>"ru" or "en"; empty means "follow the system UI language".</summary>
        public string Language { get; set; } = string.Empty;

        /// <summary>settings.json "baseUrl"; empty when not set.</summary>
        public string SettingsBaseUrl { get; private set; } = string.Empty;

        /// <summary>--base-url of this run; never saved.</summary>
        public string? BaseUrlOverride { get; }

        public string BaseUrl => BaseUrlOverride ?? (SettingsBaseUrl.Length > 0 ? SettingsBaseUrl : DefaultBaseUrl);

        public string DownloadsUrl => BaseUrl + "downloads/";

        public string ChannelUrl => DownloadsUrl + (Channel == UpdateChannel.Test ? "test/" : string.Empty);

        public string ChannelName => Channel == UpdateChannel.Test ? "test" : "prod";

        /// <summary>The cache buster: Pages caches for about ten minutes.</summary>
        private static string Fresh(string url) => url + "?t=" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);

        private static string WithSlash(string url) => url.EndsWith("/", StringComparison.Ordinal) ? url : url + "/";

        // -------------------------------------------------------------- settings

        public void LoadSettings()
        {
            Channel = UpdateChannel.Prod;
            try
            {
                if (!File.Exists(SettingsFile))
                {
                    return;
                }

                var o = Json.Obj(Json.Parse(File.ReadAllText(SettingsFile)));
                Channel = string.Equals(Json.Str(o, "channel"), "test", StringComparison.OrdinalIgnoreCase) ? UpdateChannel.Test : UpdateChannel.Prod;
                var language = Json.Str(o, "language") ?? string.Empty;
                Language = language == "ru" || language == "en" ? language : string.Empty;
                var baseUrl = Json.Str(o, "baseUrl") ?? string.Empty;
                SettingsBaseUrl = baseUrl.Length > 0 ? WithSlash(baseUrl) : string.Empty;
            }
            catch (Exception ex) when (ex is IOException || ex is FormatException || ex is UnauthorizedAccessException)
            {
                // A broken settings file means defaults.
            }
        }

        public void SaveSettings()
        {
            var o = new Dictionary<string, object?> { ["channel"] = ChannelName };
            if (Language.Length > 0)
            {
                o["language"] = Language;
            }

            if (SettingsBaseUrl.Length > 0)
            {
                o["baseUrl"] = SettingsBaseUrl;
            }

            try
            {
                File.WriteAllText(SettingsFile, Json.Write(o));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Read-only folder: the choice lasts for this run only.
            }
        }

        // ---------------------------------------------------------------- remote

        private async Task<string> GetTextAsync(string url, CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, Fresh(url)))
            {
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                using (var response = await _jsonHttp.SendAsync(request, ct).ConfigureAwait(false))
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        throw new FileNotFoundException("not published: " + url, url);
                    response.EnsureSuccessStatusCode();
                    var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    return Encoding.UTF8.GetString(bytes).TrimStart('﻿');
                }
            }
        }

        public async Task<Manifest> FetchManifestAsync(CancellationToken ct = default)
        {
            var text = await GetTextAsync(ChannelUrl + "manifest.json", ct).ConfigureAwait(false);
            var manifest = Manifest.Parse(text);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachedManifestFile)!);
                File.WriteAllText(CachedManifestFile, text);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // The cache only saves bytes on the next check.
            }

            return manifest;
        }

        /// <summary>The channel's last downloaded manifest, kept for the cheap update check.</summary>
        public string CachedManifestFile => Path.Combine(BaseDir, ".foere", "manifest-" + ChannelName + ".json");

        /// <summary>The channel's version.json: the published version only (about 100 bytes).</summary>
        public async Task<string> FetchVersionAsync(CancellationToken ct = default)
        {
            var o = Json.Obj(Json.Parse(await GetTextAsync(ChannelUrl + "version.json", ct).ConfigureAwait(false)));
            return Json.Str(o, "version") ?? throw new FormatException("version.json has no version");
        }

        /// <summary>
        /// The update check: version.json first; when it names the installed version and the manifest of that version
        /// is on disk, that manifest is used and nothing else is downloaded. Otherwise (another version, no install, no
        /// cache, or <paramref name="forceManifest"/> for Verify) the manifest is downloaded. A site without version.json
        /// falls back to the manifest.
        /// </summary>
        public async Task<Manifest> CheckAsync(bool forceManifest, CancellationToken ct = default)
        {
            if (!forceManifest)
            {
                string? remote = null;
                try
                {
                    remote = await FetchVersionAsync(ct).ConfigureAwait(false);
                }
                catch (FileNotFoundException)
                {
                    remote = null;
                }

                var installed = GetInstalled();
                if (remote != null && installed.Version.Length > 0 && string.Equals(remote, installed.Version, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(installed.Channel, ChannelName, StringComparison.OrdinalIgnoreCase) && File.Exists(CachedManifestFile))
                {
                    try
                    {
                        var cached = Manifest.Parse(File.ReadAllText(CachedManifestFile));
                        if (string.Equals(cached.Version, remote, StringComparison.OrdinalIgnoreCase))
                        {
                            LastCheckUsedCache = true;
                            return cached;
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is FormatException)
                    {
                        // A damaged cache: download it again.
                    }
                }
            }

            LastCheckUsedCache = false;
            return await FetchManifestAsync(ct).ConfigureAwait(false);
        }

        /// <summary>True when the last <see cref="CheckAsync"/> answered from the cached manifest.</summary>
        public bool LastCheckUsedCache { get; private set; }

        /// <summary>The mod catalogue (downloads/mods/catalog.json, the same for both channels).</summary>
        public async Task<ModCatalog> FetchCatalogAsync(CancellationToken ct = default) =>
            ModCatalog.Parse(await GetTextAsync(DownloadsUrl + "mods/catalog.json", ct).ConfigureAwait(false));

        public async Task<IList<PatchNote>> FetchNotesAsync(CancellationToken ct = default) =>
            PatchNote.ParseList(await GetTextAsync(ChannelUrl + "notes.json", ct).ConfigureAwait(false));

        public async Task<LauncherVersionInfo> FetchLauncherVersionAsync(CancellationToken ct = default) =>
            LauncherVersionInfo.Parse(await GetTextAsync(DownloadsUrl + "launcher-version.json", ct).ConfigureAwait(false));

        // ----------------------------------------------------------------- local

        /// <summary>The install record for reading only (cached); the updater reads its own copy to change.</summary>
        public InstalledState GetInstalled() => InstalledState.LoadCached(Updater.StateFile);

        /// <summary>The game exe recorded by the last install; FOEReborn.exe when unknown.</summary>
        public string GameExecutable => Path.Combine(GameDir, "FOEReborn.exe");

        public bool IsGameInstalled()
        {
            var state = GetInstalled();
            return state.Version.Length > 0 && File.Exists(GameExecutable);
        }

        public Task<UpdateResult> InstallOrUpdateAsync(Manifest manifest, bool verify, IProgress<UpdateProgress>? progress, CancellationToken ct) =>
            Updater.RunAsync(manifest, ChannelName, verify, progress, ct);

        /// <summary>Starts the game in its own folder; with --game-settings it gets -settings on that file.</summary>
        public Process Play()
        {
            var exe = GameExecutable;
            if (!File.Exists(exe))
            {
                throw new FileNotFoundException("Game executable not found: " + exe);
            }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = GameDir,
                UseShellExecute = false
            };
            if (GameSettingsOverride != null)
            {
                psi.Arguments = "-settings \"" + Path.GetFullPath(GameSettingsOverride) + "\"";
            }

            return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the game process.");
        }

        /// <summary>
        /// True when this install's game runs, also when started outside the launcher. Matching is by
        /// executable path, so another FOE Reborn copy elsewhere does not lock this launcher.
        /// </summary>
        public bool IsGameRunning()
        {
            var exe = GameExecutable;
            if (!File.Exists(exe))
            {
                return false;
            }

            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
            }
            catch (Exception)
            {
                return false;
            }

            var unmatched = false;
            try
            {
                var target = Path.GetFullPath(exe);
                foreach (var process in processes)
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path) && string.Equals(Path.GetFullPath(path!), target, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    catch (Exception)
                    {
                        // MainModule is denied across a bitness or privilege boundary.
                    }

                    unmatched = true;
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }

            // A process of that name exists but no path matched: a running image cannot be opened for writing.
            return unmatched && IsExecutableInUse(exe);
        }

        private static bool IsExecutableInUse(string path)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    return false;
                }
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        // ----------------------------------------------------------- self-update

        public static string ProcessPath
        {
            get
            {
                try
                {
                    var location = Assembly.GetEntryAssembly()?.Location;
                    if (!string.IsNullOrEmpty(location))
                    {
                        return location!;
                    }
                }
                catch (Exception)
                {
                    // Fall through.
                }

                return Path.Combine(DefaultBaseDir(), "FOEReLauncher.exe");
            }
        }

        private static string DefaultBaseDir()
        {
            string? dir = null;
            try
            {
                var location = Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrEmpty(location))
                {
                    dir = Path.GetDirectoryName(location);
                }
            }
            catch (Exception)
            {
                dir = null;
            }

            if (string.IsNullOrEmpty(dir))
            {
                dir = AppDomain.CurrentDomain.BaseDirectory;
            }

            return dir!.TrimEnd(Path.DirectorySeparatorChar);
        }

        private static HttpClient CreateHttpClient(bool decompress)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch (NotSupportedException)
            {
                // Older platform: OS defaults.
            }

            var handler = new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 };
            if (decompress)
                handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return client;
        }

        public static string CurrentLauncherVersion
        {
            get
            {
                var asm = Assembly.GetExecutingAssembly();
                var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(informational))
                {
                    var plus = informational!.IndexOf('+');
                    var value = plus >= 0 ? informational.Substring(0, plus) : informational;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return Normalize(value);
                    }
                }

                var version = asm.GetName().Version;
                return version == null ? FallbackVersion : Normalize(version.ToString());
            }
        }

        private static string Normalize(string version)
        {
            var v = version.Trim();
            if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                v = v.Substring(1);
            }

            var parts = v.Split('.');
            return parts.Length == 4 && parts[3] == "0" ? string.Join(".", parts[0], parts[1], parts[2]) : v;
        }

        /// <summary>True when <paramref name="candidate"/> is strictly newer than <paramref name="current"/>.</summary>
        public static bool IsNewer(string? candidate, string? current)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !TryParseVersion(candidate!, out var a))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(current) || !TryParseVersion(current!, out var b))
            {
                return true;
            }

            return a > b;
        }

        public static bool TryParseVersion(string text, out Version version)
        {
            version = new Version(0, 0);
            var value = text.Trim();
            if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(1);
            }

            var parts = new List<int>();
            foreach (var part in value.Split('.'))
            {
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                {
                    return false;
                }

                parts.Add(number);
            }

            if (parts.Count == 0 || parts.Count > 4)
            {
                return false;
            }

            while (parts.Count < 4)
            {
                parts.Add(0);
            }

            version = new Version(parts[0], parts[1], parts[2], parts[3]);
            return true;
        }

        /// <summary>
        /// Downloads the newer launcher exe next to a temp folder, checks its hash and writes the swap
        /// script. Returns the script path; <see cref="StartSwap"/> runs it.
        /// </summary>
        public async Task<string> PrepareSelfUpdateAsync(LauncherVersionInfo info, string targetExe, string arguments, IProgress<double>? progress, CancellationToken ct)
        {
            var work = Path.Combine(Path.GetTempPath(), "foere-launcher-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var newExe = Path.Combine(work, info.File);
            long done = 0;
            await Downloader.DownloadVerifiedAsync(
                DownloadsUrl + info.File + "?v=" + Uri.EscapeDataString(info.Version),
                newExe,
                info.Size,
                info.Sha256,
                bytes =>
                {
                    done += bytes;
                    if (info.Size > 0)
                    {
                        progress?.Report((double)done / info.Size);
                    }
                },
                ct).ConfigureAwait(false);
            return WriteSwapScript(work, newExe, targetExe, arguments);
        }

        /// <summary>
        /// The swap script: waits until the process with the given id is gone, copies the new exe over
        /// the old one (retrying while it is still locked), starts it and deletes its temp folder.
        /// </summary>
        public static string WriteSwapScript(string workDir, string newExe, string targetExe, string arguments)
        {
            var path = Path.Combine(workDir, "apply-update.bat");
            // Full System32 paths: a PATH with Git's Unix find.exe first would make the wait loop end at once.
            // No parenthesised blocks: %TRIES% inside one would be expanded only once.
            var script = new StringBuilder()
                .AppendLine("@echo off")
                .AppendLine("setlocal")
                .AppendLine("set PID=%1")
                .AppendLine("set TRIES=0")
                .AppendLine(":waitloop")
                .AppendLine(@"""%SystemRoot%\System32\tasklist.exe"" /FI ""PID eq %PID%"" 2>nul | ""%SystemRoot%\System32\find.exe"" ""%PID%"" >nul")
                .AppendLine("if errorlevel 1 goto copyloop")
                .AppendLine(@"""%SystemRoot%\System32\ping.exe"" -n 2 127.0.0.1 >nul")
                .AppendLine("goto waitloop")
                .AppendLine(":copyloop")
                .AppendLine("copy /Y \"" + newExe + "\" \"" + targetExe + "\" >nul 2>&1")
                .AppendLine("if not errorlevel 1 goto start")
                .AppendLine("set /a TRIES+=1")
                .AppendLine("if %TRIES% GEQ 30 goto start")
                .AppendLine(@"""%SystemRoot%\System32\ping.exe"" -n 2 127.0.0.1 >nul")
                .AppendLine("goto copyloop")
                .AppendLine(":start")
                .AppendLine("start \"\" \"" + targetExe + "\" " + arguments)
                .AppendLine("del /Q \"" + newExe + "\" >nul 2>&1")
                .ToString();
            File.WriteAllText(path, script);
            return path;
        }

        public static Process StartSwap(string script, int waitForPid)
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c \"" + script + "\" " + waitForPid.ToString(CultureInfo.InvariantCulture))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(script)
            };
            return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the update script.");
        }

        public static string FormatSize(long bytes, string language)
        {
            var ru = language == "ru";
            var culture = ru ? CultureInfo.GetCultureInfo("ru-RU") : CultureInfo.InvariantCulture;
            const double Kb = 1024d;
            const double Mb = Kb * 1024d;
            const double Gb = Mb * 1024d;
            if (bytes < Kb)
            {
                return bytes.ToString("0", culture) + (ru ? " Б" : " B");
            }

            if (bytes < Mb)
            {
                return (bytes / Kb).ToString("0", culture) + (ru ? " КБ" : " KB");
            }

            if (bytes < Gb)
            {
                var mb = bytes / Mb;
                return mb.ToString(mb < 10 ? "0.0" : "0", culture) + (ru ? " МБ" : " MB");
            }

            return (bytes / Gb).ToString("0.00", culture) + (ru ? " ГБ" : " GB");
        }
    }
}
