using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace KiloviewSetup.Bootstrapper;

internal static class BootstrapperProgram
{
    private const string PayloadResource = "KiloviewSetup.Payload.zip";
    private const string ProductName = "Kiloview Job Setup Manager";

    [STAThread]
    private static int Main()
    {
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var extractRoot = Path.Combine(temporaryRoot, $"KiloviewSetup-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(extractRoot);
            var archivePath = Path.Combine(extractRoot, "KiloviewSetup-Payload.zip");
            using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource)
                ?? throw new InvalidOperationException("The embedded installer payload is missing."))
            using (var archive = File.Create(archivePath))
            {
                payload.CopyTo(archive);
            }

            ZipFile.ExtractToDirectory(archivePath, extractRoot, overwriteFiles: true);
            File.Delete(archivePath);

            var installerScript = Path.Combine(extractRoot, "Install-KiloviewSetup.ps1");
            if (!File.Exists(installerScript))
            {
                throw new FileNotFoundException("The installation script is missing from the payload.", installerScript);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = extractRoot
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(installerScript);
            startInfo.ArgumentList.Add("-Source");
            startInfo.ArgumentList.Add(extractRoot);

            using var installer = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows PowerShell could not be started.");
            installer.WaitForExit();
            if (installer.ExitCode != 0)
            {
                throw new InvalidOperationException($"Installation failed with exit code {installer.ExitCode}. See the installer log in your temporary folder.");
            }

            return 0;
        }
        catch (Exception exception)
        {
            MessageBox(IntPtr.Zero, exception.Message, $"{ProductName} installation failed", 0x00000010);
            return 1;
        }
        finally
        {
            var resolved = Path.GetFullPath(extractRoot);
            if (resolved.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
            {
                try
                {
                    Directory.Delete(resolved, recursive: true);
                }
                catch
                {
                    // Installation is already complete; Windows can clean up a locked temp folder later.
                }
            }
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
