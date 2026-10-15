using System;
using System.IO;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace Project_Phantom;

/// <summary>Uses Windows' per-user MSIX startup registration, never a build-path Run entry.</summary>
internal static class StartupService
{
    private const string TaskId = "PhantomStartup";
    internal sealed record Status(bool? Enabled, bool CanChange, string? Warning);

    public static async Task<Status> ReadAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            var enabled = task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            var installed = IsInstalledPackage();
            var warning = task.State switch
            {
                StartupTaskState.DisabledByUser => "Windows has disabled Phantom startup. Enable it in Settings > Apps > Startup to allow it again.",
                StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy => "Startup is controlled by your Windows administrator's policy.",
                _ => installed ? null : "Launch with Windows requires an installed MSIX package. Development/build-folder registrations are not enabled."
            };
            var canChange = task.State is StartupTaskState.Enabled ||
                (installed && task.State is StartupTaskState.Disabled);
            return new Status(enabled, canChange, warning);
        }
        catch (Exception ex)
        {
            return new Status(null, false,
                "Windows startup registration is unavailable. Install the updated MSIX package containing Phantom's startup task. " + ex.Message);
        }
    }

    public static async Task<Status> SetEnabledAsync(bool enabled)
    {
        string? error = null;
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (enabled)
            {
                if (!IsInstalledPackage())
                    error = "Install Phantom's MSIX package before enabling startup; a development build path is not a stable installation.";
                else
                {
                    var state = await task.RequestEnableAsync();
                    if (state is not (StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy))
                        error = "Windows did not enable startup. Check Settings > Apps > Startup or your administrator's policy.";
                }
            }
            else task.Disable();
        }
        catch (Exception ex) { error = "Could not change Windows startup registration: " + ex.Message; }

        var actual = await ReadAsync();
        if (error is null && actual.Enabled is bool current && current != enabled)
            error = "Windows did not apply the requested startup change.";
        return actual with { Warning = error ?? actual.Warning };
    }

    private static bool IsInstalledPackage()
    {
        // Loose Visual Studio/developer packages can point directly into bin/obj.
        // Only an installed package may be opted into startup from this UI.
        var package = Package.Current;
        if (package.IsDevelopmentMode) return false;
        var executable = Environment.ProcessPath;
        var root = Path.GetFullPath(package.InstalledLocation.Path).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return executable is not null && Path.GetFullPath(executable).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
