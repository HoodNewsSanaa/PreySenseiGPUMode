using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace PreySense.Frontend.Services
{
    /// <summary>A published iGPU Only build: tag igpu-&lt;version&gt;-&lt;build&gt; with a PreySense.exe asset.</summary>
    public sealed record ForkRelease(string Tag, Version Version, int Build, string PageUrl, string AssetUrl, string Sha256, long Size);

    /// <summary>
    /// iGPU Only fork: checks this fork's releases once at startup (and on request) and can install an update:
    /// it downloads PreySense.exe from the release, refuses it unless its SHA-256 matches the digest GitHub
    /// lists for that asset, keeps the current exe as PreySense.previous.exe, swaps the new one in and restarts.
    /// Builds not made by the fork's release workflow (no build number) never update themselves.
    /// </summary>
    public static class ForkUpdateService
    {
        public const string Repository = "HoodNewsSanaa/PreySenseiGPUMode";
        private const string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
        private const string ReleasesPage = "https://github.com/" + Repository + "/releases";
        private const string AssetName = "PreySense.exe";
        private const string BackupName = "PreySense.previous.exe";
        private const long MinAssetBytes = 512 * 1024;
        private const long MaxAssetBytes = 200L * 1024 * 1024;
        private static readonly Regex TagPattern = new(@"^igpu-(\d+\.\d+\.\d+)-(\d+)$", RegexOptions.CultureInvariant);

        private static readonly HttpClient Api = CreateClient(TimeSpan.FromSeconds(8));
        private static readonly HttpClient Downloads = CreateClient(TimeSpan.FromMinutes(5));
        private static ForkRelease? _pending;
        private static bool _prompted;
        private static int _busy;

        private static HttpClient CreateClient(TimeSpan timeout)
        {
            var client = new HttpClient { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PreySense-iGPU-Only");
            return client;
        }

        public static Version CurrentVersion =>
            Version.TryParse(UpdateCheckService.CurrentVersion, out var v) ? v : new Version(0, 0, 0);

        /// <summary>The release-workflow build number baked into this exe; 0 for local builds.</summary>
        public static int CurrentBuild
        {
            get
            {
                string? value = typeof(ForkUpdateService).Assembly
                    .GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(a => a.Key == "ForkBuild")?.Value;
                return int.TryParse(value, out int build) ? build : 0;
            }
        }

        internal static (Version Version, int Build)? ParseTag(string? tag)
        {
            if (tag is null) return null;
            var m = TagPattern.Match(tag.Trim());
            if (!m.Success || !Version.TryParse(m.Groups[1].Value, out var version)) return null;
            return int.TryParse(m.Groups[2].Value, out int build) ? (version, build) : null;
        }

        internal static bool IsNewer(ForkRelease release, Version currentVersion, int currentBuild) =>
            release.Version > currentVersion || (release.Version == currentVersion && release.Build > currentBuild);

        /// <summary>Reads GitHub's "latest release" JSON. Null unless it is a fork build with a verifiable exe.</summary>
        internal static ForkRelease? ParseRelease(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (ParseTag(tag) is not { } parsed) return null;
            string page = root.TryGetProperty("html_url", out var h) && IsGitHubHttps(h.GetString()) ? h.GetString()! : ReleasesPage;
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
            foreach (var asset in assets.EnumerateArray())
            {
                if (!string.Equals(asset.TryGetProperty("name", out var n) ? n.GetString() : null, AssetName, StringComparison.OrdinalIgnoreCase))
                    continue;
                string? url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                string? digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                long size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out long bytes) ? bytes : 0;
                if (!IsGitHubHttps(url) || digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    return null;
                string sha = digest["sha256:".Length..].Trim().ToLowerInvariant();
                if (sha.Length != 64 || !sha.All(Uri.IsHexDigit)) return null;
                return new ForkRelease(tag!, parsed.Version, parsed.Build, page, url!, sha, size);
            }
            return null;
        }

        private static bool IsGitHubHttps(string? url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
            (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase));

        /// <summary>Called once at startup. Prompts now if the window is open, otherwise the next time it opens.</summary>
        public static async Task CheckOnStartupAsync()
        {
            DeleteStaleDownloads();
            if (CurrentBuild == 0) return; // local build: nothing to compare against
            try
            {
                var release = await FetchLatestAsync();
                if (release is null || !IsNewer(release, CurrentVersion, CurrentBuild)) return;
                _pending = release;
                AppLogger.Log($"[Update] {release.Tag} is available (running build {CurrentBuild}).");
                await OnUiAsync(() => { if (MainWindowVisible()) _ = PromptAsync(release, interactive: false); });
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Update] Check skipped: {ex.Message}");
            }
        }

        /// <summary>Called when the main window is shown: offers a pending update once per run.</summary>
        public static void OnMainWindowShown()
        {
            if (_pending is { } release && !_prompted) _ = PromptAsync(release, interactive: false);
        }

        /// <summary>Settings → Updates → Check now.</summary>
        public static async Task CheckNowAsync()
        {
            if (CurrentBuild == 0)
            {
                await InformAsync(LocalizationService.Translate("This copy of PreySense wasn't built by the fork's release workflow, so it can't update itself."));
                return;
            }
            try
            {
                var release = await FetchLatestAsync();
                if (release is null || !IsNewer(release, CurrentVersion, CurrentBuild))
                {
                    await InformAsync(LocalizationService.Format("PreySense is up to date (build {0}).", CurrentBuild));
                    return;
                }
                _pending = release;
                await PromptAsync(release, interactive: true);
            }
            catch (Exception ex)
            {
                await InformAsync(LocalizationService.Format("Could not check for updates: {0}", ex.Message));
            }
        }

        private static async Task<ForkRelease?> FetchLatestAsync()
        {
            string json = await Api.GetStringAsync(LatestReleaseApi).ConfigureAwait(false);
            return ParseRelease(json);
        }

        private static async Task PromptAsync(ForkRelease release, bool interactive)
        {
            if (System.Threading.Interlocked.Exchange(ref _busy, 1) != 0) return;
            try
            {
                _prompted = true;
                bool install = await OnUiAsync(() => Ask(
                    LocalizationService.Format("PreySense iGPU Only build {0} is available (you have build {1}). Update now? PreySense will restart.",
                        release.Build, CurrentBuild),
                    LocalizationService.Translate("Update"),
                    LocalizationService.Translate("Later")));
                if (!install) return;

                string? error = await Task.Run(() => DownloadAndSwapAsync(release));
                if (error is null)
                {
                    await OnUiAsync(Restart);
                    return;
                }
                AppLogger.Error("[Update] " + error);
                bool openPage = await OnUiAsync(() => Ask(
                    LocalizationService.Format("The update could not be installed: {0}", error),
                    LocalizationService.Translate("Download"),
                    LocalizationService.Translate("Later")));
                if (openPage) Process.Start(new ProcessStartInfo { FileName = release.PageUrl, UseShellExecute = true });
            }
            finally
            {
                System.Threading.Volatile.Write(ref _busy, 0);
            }
        }

        /// <summary>Downloads, verifies and swaps the exe. Returns null on success, else a reason.</summary>
        private static async Task<string?> DownloadAndSwapAsync(ForkRelease release)
        {
            string? current = Environment.ProcessPath;
            if (string.IsNullOrEmpty(current) || !File.Exists(current)) return "PreySense couldn't find its own exe.";
            string folder = Path.GetDirectoryName(current)!;
            string staged = Path.Combine(folder, "PreySense.update.tmp");
            string backup = Path.Combine(folder, BackupName);
            try
            {
                using (var response = await Downloads.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    long? length = response.Content.Headers.ContentLength;
                    if (length is > MaxAssetBytes) return "The download is unexpectedly large.";
                    await using var input = await response.Content.ReadAsStreamAsync();
                    await using var output = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None);
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer)) > 0)
                    {
                        total += read;
                        if (total > MaxAssetBytes) return "The download is unexpectedly large.";
                        await output.WriteAsync(buffer.AsMemory(0, read));
                    }
                }

                var info = new FileInfo(staged);
                if (info.Length < MinAssetBytes || (release.Size > 0 && info.Length != release.Size))
                    return "The download is incomplete.";
                string actual;
                using (var stream = File.OpenRead(staged))
                    actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (!string.Equals(actual, release.Sha256, StringComparison.Ordinal))
                    return LocalizationService.Translate("The downloaded file didn't match GitHub's checksum, so it wasn't installed.");
                using (var stream = File.OpenRead(staged))
                {
                    if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') return "The download isn't a Windows program.";
                }

                // A running exe can be renamed but not overwritten: move it aside, then put the new one in place.
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(current, backup);
                try
                {
                    File.Move(staged, current);
                }
                catch
                {
                    File.Move(backup, current); // put the running version back
                    throw;
                }
                AppLogger.Log($"[Update] Installed {release.Tag}; previous version kept as {BackupName}.");
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            finally
            {
                try { if (File.Exists(staged)) File.Delete(staged); } catch { }
            }
        }

        /// <summary>Starts the new exe once this process has exited, then quits PreySense normally.</summary>
        private static void Restart()
        {
            string? exe = Environment.ProcessPath;
            if (exe is null) return;
            bool hidden = !MainWindowVisible();
            string quoted = exe.Replace("'", "''");
            string args = hidden ? " -ArgumentList '--tray'" : string.Empty;
            string script = $"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue; Start-Process -FilePath '{quoted}'{args}";
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                ArgumentList = { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", script },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (Application.Current?.MainWindow is MainWindow main) main.QuitApplication();
            else Application.Current?.Shutdown();
        }

        private static void DeleteStaleDownloads()
        {
            try
            {
                string? folder = Path.GetDirectoryName(Environment.ProcessPath);
                if (folder is null) return;
                string staged = Path.Combine(folder, "PreySense.update.tmp");
                if (File.Exists(staged)) File.Delete(staged);
            }
            catch { }
        }

        private static bool MainWindowVisible() =>
            Application.Current?.MainWindow is { IsVisible: true } w && w.WindowState != WindowState.Minimized;

        private static bool Ask(string message, string confirm, string cancel)
        {
            if (Application.Current?.MainWindow is not { IsVisible: true } owner) return false;
            var dialog = new Views.CustomModalDialog
            {
                Owner = owner,
                Title = "PreySense — Update Available",
                HeaderSubtitle = " — Update Available",
                Message = message,
                ConfirmText = confirm,
                CancelText = cancel,
                ShowCancelButton = true
            };
            return dialog.ShowDialog() == true;
        }

        private static Task InformAsync(string message) => OnUiAsync(() =>
        {
            if (Application.Current?.MainWindow is not { IsVisible: true } owner) return;
            new Views.CustomModalDialog
            {
                Owner = owner,
                Title = "PreySense — Update",
                HeaderSubtitle = " — Update",
                Message = message,
                ConfirmText = "OK",
                ShowCancelButton = false
            }.ShowDialog();
        });

        private static async Task OnUiAsync(Action action)
        {
            var app = Application.Current;
            if (app?.Dispatcher is null || app.Dispatcher.HasShutdownStarted) return;
            await app.Dispatcher.InvokeAsync(action);
        }

        private static async Task<T> OnUiAsync<T>(Func<T> func)
        {
            var app = Application.Current;
            if (app?.Dispatcher is null || app.Dispatcher.HasShutdownStarted) return default!;
            return await app.Dispatcher.InvokeAsync(func);
        }
    }
}
