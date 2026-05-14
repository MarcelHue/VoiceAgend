using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace VoiceAgend.App.Services;

public sealed class UpdateService
{
    // Wird vom Build via <UpdateRepoUrl>-MSBuild-Property gesetzt. Fallback nur
    // für Dev-Builds — Releases werden vom Workflow mit der echten Repo-URL injiziert.
    private static readonly string RepoUrl =
        typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepoUrl")?.Value
        ?? "";

    public sealed record Status(string CurrentVersion, string? AvailableVersion, bool IsUpdateAvailable, string? Message);

    public Status Last { get; private set; } = new(GetCurrentVersion(), null, false, null);

    public event Action<Status>? StatusChanged;

    private UpdateManager? _manager;

    private UpdateManager Manager => _manager ??= new UpdateManager(new GithubSource(RepoUrl, null, false));

    public bool IsConfigured => !string.IsNullOrWhiteSpace(RepoUrl);

    public async Task<Status> CheckAsync()
    {
        var L = App.Current.Loc;
        try
        {
            if (!IsConfigured)
                return Set(new Status(GetCurrentVersion(), null, false, L.T("Update.NotConfigured")));
            if (!Manager.IsInstalled)
                return Set(new Status(GetCurrentVersion(), null, false, L.T("Update.NotInstalled")));
            var info = await Manager.CheckForUpdatesAsync();
            if (info is null)
                return Set(new Status(GetCurrentVersion(), null, false, L.T("Update.UpToDate")));
            return Set(new Status(GetCurrentVersion(), info.TargetFullRelease.Version.ToString(), true,
                string.Format(L.T("Update.AvailableFmt"), info.TargetFullRelease.Version)));
        }
        catch (Exception ex)
        {
            Logger.Warn("Update check failed: " + ex.Message);
            return Set(new Status(GetCurrentVersion(), null, false, string.Format(L.T("Update.CheckFailedFmt"), ex.Message)));
        }
    }

    public async Task ApplyAndRestartAsync()
    {
        var L = App.Current.Loc;
        try
        {
            if (!Manager.IsInstalled) return;
            var info = await Manager.CheckForUpdatesAsync();
            if (info is null) return;
            Set(new Status(GetCurrentVersion(), info.TargetFullRelease.Version.ToString(), true, L.T("Update.Downloading")));
            await Manager.DownloadUpdatesAsync(info);
            Set(new Status(GetCurrentVersion(), info.TargetFullRelease.Version.ToString(), true, L.T("Update.Applying")));
            Manager.ApplyUpdatesAndRestart(info);
        }
        catch (Exception ex)
        {
            Logger.Error("Update apply", ex);
            Set(new Status(GetCurrentVersion(), null, false, string.Format(L.T("Update.FailedFmt"), ex.Message)));
        }
    }

    private Status Set(Status s)
    {
        Last = s;
        StatusChanged?.Invoke(s);
        return s;
    }

    private static string GetCurrentVersion()
    {
        var v = typeof(UpdateService).Assembly.GetName().Version;
        return v?.ToString(3) ?? "0.0.0";
    }
}
