using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KiloviewSetup.Bootstrapper;

internal static class BootstrapperProgram
{
    private const string PayloadResource = "KiloviewSetup.Payload.zip";
    private const string ProductName = "Kiloview Job Configurator";
    private static readonly string InstallerLogPath = Path.Combine(Path.GetTempPath(), "KiloviewSetup-Installer.log");

    [STAThread]
    private static int Main()
    {
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var extractRoot = Path.Combine(temporaryRoot, $"KiloviewSetup-{Guid.NewGuid():N}");

        try
        {
            Log($"Installer {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} started from {Environment.ProcessPath}.");
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
            Log($"Payload extracted to {extractRoot}.");

            var licensePath = Path.Combine(extractRoot, "LICENSE.md");
            if (!File.Exists(licensePath))
            {
                throw new FileNotFoundException("The license agreement is missing from the installer payload.", licensePath);
            }

            if (!ShowLicenseAgreement(File.ReadAllText(licensePath)))
            {
                Log("License agreement declined; installation cancelled.");
                return 2;
            }
            Log("License agreement accepted; starting the elevated installation script.");

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
                RedirectStandardOutput = true,
                RedirectStandardError = true,
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
            var standardOutput = installer.StandardOutput.ReadToEndAsync();
            var standardError = installer.StandardError.ReadToEndAsync();
            installer.WaitForExit();
            var output = standardOutput.GetAwaiter().GetResult();
            var error = standardError.GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(output)) Log($"Installer output:{Environment.NewLine}{output.Trim()}");
            if (!string.IsNullOrWhiteSpace(error)) Log($"Installer error:{Environment.NewLine}{error.Trim()}");
            if (installer.ExitCode != 0)
            {
                throw new InvalidOperationException($"Installation failed with exit code {installer.ExitCode}. See {InstallerLogPath} for details.");
            }

            Log("Installation completed successfully.");
            return 0;
        }
        catch (Exception exception)
        {
            Log($"Installation failed:{Environment.NewLine}{exception}");
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

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(InstallerLogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never prevent the installer from running or displaying the real error.
        }
    }

    private static bool ShowLicenseAgreement(string licenseText)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var dialog = new Form
        {
            Text = $"{ProductName} — License Agreement",
            StartPosition = FormStartPosition.CenterScreen,
            Width = 780,
            Height = 680,
            MinimizeBox = false,
            MaximizeBox = false,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ShowIcon = true
        };

        var heading = new Label
        {
            AutoSize = false,
            Left = 18,
            Top = 16,
            Width = 730,
            Height = 44,
            Text = "Please read and accept the license agreement to install Kiloview Job Configurator.",
            Font = new Font("Segoe UI", 11, FontStyle.Bold)
        };

        var license = new RichTextBox
        {
            Left = 18,
            Top = 66,
            Width = 730,
            Height = 490,
            ReadOnly = true,
            DetectUrls = true,
            Text = licenseText,
            Font = new Font("Segoe UI", 9),
            BackColor = SystemColors.Window,
            TabStop = true
        };

        var acceptance = new CheckBox
        {
            Left = 20,
            Top = 570,
            Width = 520,
            Height = 28,
            Text = "I have read and accept the license agreement."
        };

        var cancel = new Button
        {
            Text = "Decline",
            Left = 548,
            Top = 606,
            Width = 95,
            Height = 32,
            DialogResult = DialogResult.Cancel
        };

        var install = new Button
        {
            Text = "Accept && Install",
            Left = 650,
            Top = 606,
            Width = 98,
            Height = 32,
            Enabled = false,
            DialogResult = DialogResult.OK
        };

        acceptance.CheckedChanged += (_, _) => install.Enabled = acceptance.Checked;
        dialog.Controls.AddRange([heading, license, acceptance, cancel, install]);
        dialog.AcceptButton = install;
        dialog.CancelButton = cancel;

        return dialog.ShowDialog() == DialogResult.OK && acceptance.Checked;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
