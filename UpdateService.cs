using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace TodoList;

public sealed record UpdateCheckResult(
    bool HasUpdate,
    string CurrentVersion,
    string? LatestVersion,
    string? Message,
    string? AssetUrl = null);

public static class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/Hoone02/Ticky/releases/latest";
    private const string AssetName = "Ticky-win-x64.zip";

    public static async Task<UpdateCheckResult> CheckLatestAsync()
    {
        var currentVersion = GetCurrentVersion();
        using var http = CreateHttpClient();

        using var response = await http.GetAsync(LatestReleaseUrl);
        if (!response.IsSuccessStatusCode)
        {
            return new UpdateCheckResult(
                false,
                currentVersion,
                null,
                "업데이트를 확인할 수 없습니다. 저장소가 private이면 TICKY_GITHUB_TOKEN 환경 변수가 필요합니다.");
        }

        await using var releaseStream = await response.Content.ReadAsStreamAsync();
        using var releaseJson = await JsonDocument.ParseAsync(releaseStream);
        var root = releaseJson.RootElement;
        var latestVersion = NormalizeVersion(root.GetProperty("tag_name").GetString());

        if (!IsNewerVersion(currentVersion, latestVersion))
        {
            return new UpdateCheckResult(false, currentVersion, latestVersion, "이미 최신 버전입니다.");
        }

        var assetUrl = FindAssetUrl(root);
        if (assetUrl is null)
        {
            return new UpdateCheckResult(false, currentVersion, latestVersion, $"릴리스에 {AssetName} 파일이 없습니다.");
        }

        return new UpdateCheckResult(true, currentVersion, latestVersion, "업데이트 확인됨!", assetUrl);
    }

    public static async Task<UpdateCheckResult> InstallAsync(UpdateCheckResult update)
    {
        if (!update.HasUpdate || string.IsNullOrWhiteSpace(update.AssetUrl) || string.IsNullOrWhiteSpace(update.LatestVersion))
        {
            return update with { Message = "설치할 업데이트가 없습니다." };
        }

        using var http = CreateHttpClient();
        var updateDirectory = Path.Combine(Path.GetTempPath(), "TickyUpdate", update.LatestVersion);
        var zipPath = Path.Combine(updateDirectory, AssetName);
        var extractDirectory = Path.Combine(updateDirectory, "extract");
        Directory.CreateDirectory(updateDirectory);

        using (var assetResponse = await http.GetAsync(update.AssetUrl))
        {
            assetResponse.EnsureSuccessStatusCode();
            await using var file = File.Create(zipPath);
            await assetResponse.Content.CopyToAsync(file);
        }

        if (Directory.Exists(extractDirectory))
        {
            Directory.Delete(extractDirectory, recursive: true);
        }

        ZipFile.ExtractToDirectory(zipPath, extractDirectory);
        StartApplyScript(extractDirectory);

        return update with { Message = "업데이트를 설치합니다. 앱이 곧 다시 시작됩니다." };
    }

    public static async Task<UpdateCheckResult> CheckAndInstallLatestAsync()
    {
        var update = await CheckLatestAsync();
        return update.HasUpdate ? await InstallAsync(update) : update;
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Ticky-Updater");

        var token = Environment.GetEnvironmentVariable("TICKY_GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return http;
    }

    private static string GetCurrentVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static string NormalizeVersion(string? version)
    {
        return string.IsNullOrWhiteSpace(version)
            ? "0.0.0"
            : version.Trim().TrimStart('v', 'V');
    }

    private static bool IsNewerVersion(string currentVersion, string latestVersion)
    {
        return Version.TryParse(currentVersion, out var current) &&
               Version.TryParse(latestVersion, out var latest) &&
               latest > current;
    }

    private static string? FindAssetUrl(JsonElement releaseRoot)
    {
        foreach (var asset in releaseRoot.GetProperty("assets").EnumerateArray())
        {
            if (string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
            {
                return asset.GetProperty("browser_download_url").GetString();
            }
        }

        return null;
    }

    private static void StartApplyScript(string extractedDirectory)
    {
        var installDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var exePath = Environment.ProcessPath ?? Path.Combine(installDirectory, "Ticky.exe");
        var scriptPath = Path.Combine(Path.GetTempPath(), "TickyUpdate", "Apply-TickyUpdate.ps1");
        var processId = Environment.ProcessId;

        var script = $$"""
        $ErrorActionPreference = 'Stop'
        $source = '{{extractedDirectory}}'
        $target = '{{installDirectory}}'
        $exe = '{{exePath}}'
        $pidToWait = {{processId}}

        Wait-Process -Id $pidToWait -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 400
        Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
        Start-Process -FilePath $exe
        """;

        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.WriteAllText(scriptPath, script);

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });

        System.Windows.Application.Current.Shutdown();
    }
}
