using System.Diagnostics;
using Microsoft.Win32;

namespace NDIJobConfigurator.Core;

public static class WindowsInstallationRegistration
{
    private const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public static void Ensure(string contentRootPath, ILogger logger)
    {
        if (!OperatingSystem.IsWindows()) return;

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData)) return;
        var expectedRoot = Path.GetFullPath(Path.Combine(localApplicationData, "Programs", "NDI Job Configurator"));
        var actualRoot = Path.GetFullPath(contentRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        expectedRoot = expectedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!actualRoot.Equals(expectedRoot, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var executable = Path.Combine(actualRoot, "NDIJobConfigurator.exe");
            var icon = Path.Combine(actualRoot, "NDIJobConfigurator.ico");
            var uninstaller = Path.Combine(actualRoot, "Uninstall-NDIJobConfigurator.ps1");
            if (!File.Exists(executable) || !File.Exists(uninstaller)) return;

            using var uninstallRoot = Registry.CurrentUser.CreateSubKey(UninstallPath, true)
                ?? throw new InvalidOperationException("Windows did not open the current user's Installed Apps registry key.");
            using var productKey = uninstallRoot.CreateSubKey("NDIJobConfigurator", true)
                ?? throw new InvalidOperationException("Windows did not create the NDI Job Configurator Installed Apps entry.");
            var version = FileVersionInfo.GetVersionInfo(executable).ProductVersion ?? BuildIdentity.Version;
            productKey.SetValue("DisplayName", "NDI Job Configurator", RegistryValueKind.String);
            productKey.SetValue("DisplayVersion", version, RegistryValueKind.String);
            productKey.SetValue("Publisher", "John Lightfoot", RegistryValueKind.String);
            productKey.SetValue("InstallLocation", actualRoot, RegistryValueKind.String);
            productKey.SetValue("DisplayIcon", icon, RegistryValueKind.String);
            productKey.SetValue(
                "UninstallString",
                $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{uninstaller}\"",
                RegistryValueKind.String);
            productKey.SetValue("NoModify", 1, RegistryValueKind.DWord);
            productKey.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            uninstallRoot.DeleteSubKeyTree("KiloviewSetup", false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "The current user's Installed Apps registration could not be refreshed.");
        }
    }
}
