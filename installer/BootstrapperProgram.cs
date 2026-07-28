using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KiloviewSetup.Bootstrapper;

internal static class BootstrapperProgram
{
    private const string PayloadResource = "KiloviewSetup.Payload.zip";
    private const string BrandIconResource = "KiloviewSetup.BrandIcon.ico";
    private const string LicenseResource = "KiloviewSetup.License.md";
    private const string ProductName = "Kiloview Job Configurator";
    private const string InstallerAppUserModelId = "JohnLightfoot.KiloviewJobConfigurator.Installer";
    private static readonly string InstallerLogPath = Path.Combine(Path.GetTempPath(), "KiloviewSetup-Installer.log");

    [STAThread]
    private static int Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        _ = SetCurrentProcessExplicitAppUserModelID(InstallerAppUserModelId);

        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var extractRoot = Path.Combine(temporaryRoot, $"KiloviewSetup-{Guid.NewGuid():N}");

        try
        {
            Log($"Installer {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} started from {Environment.ProcessPath}.");
            if (!ShowLicenseAgreement(ReadEmbeddedText(LicenseResource)))
            {
                Log("License agreement declined; installation cancelled.");
                return 2;
            }
            Log("License agreement accepted; extracting the installation payload.");

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

            Log("Payload ready; starting the elevated installation script.");

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

    private static string ReadEmbeddedText(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Installer resource '{resourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static bool ShowLicenseAgreement(string licenseText)
    {
        using var applicationIcon = LoadBrandIcon();
        using var logo = applicationIcon.ToBitmap();
        using var foregroundReleaseTimer = new System.Windows.Forms.Timer { Interval = 1250 };

        using var dialog = new Form
        {
            Text = $"{ProductName} Installer — License Agreement",
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(820, 700),
            AutoScaleMode = AutoScaleMode.Dpi,
            MinimizeBox = false,
            MaximizeBox = false,
            FormBorderStyle = FormBorderStyle.FixedSingle,
            ShowIcon = true,
            ShowInTaskbar = true,
            Icon = (Icon)applicationIcon.Clone(),
            BackColor = Color.FromArgb(245, 248, 246)
        };

        var header = new Panel
        {
            Left = 0,
            Top = 0,
            Width = 820,
            Height = 108,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.FromArgb(8, 13, 12)
        };

        var logoBox = new PictureBox
        {
            Left = 22,
            Top = 20,
            Width = 66,
            Height = 66,
            Image = (Bitmap)logo.Clone(),
            SizeMode = PictureBoxSizeMode.Zoom,
            TabStop = false
        };

        var title = new Label
        {
            AutoSize = false,
            Left = 105,
            Top = 24,
            Width = 680,
            Height = 33,
            Text = ProductName,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 18, FontStyle.Bold)
        };

        var subtitle = new Label
        {
            AutoSize = false,
            Left = 108,
            Top = 61,
            Width = 670,
            Height = 23,
            Text = $"WINDOWS INSTALLER  ·  VERSION {ResolveDisplayVersion()}",
            ForeColor = Color.FromArgb(184, 243, 74),
            Font = new Font("Consolas", 9, FontStyle.Bold)
        };

        var accent = new Panel
        {
            Left = 0,
            Top = 104,
            Width = 820,
            Height = 4,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.FromArgb(184, 243, 74)
        };
        header.Controls.AddRange([logoBox, title, subtitle, accent]);

        var heading = new Label
        {
            AutoSize = false,
            Left = 22,
            Top = 126,
            Width = 776,
            Height = 47,
            Text = "License agreement\nPlease review the terms below before continuing.",
            ForeColor = Color.FromArgb(25, 35, 32),
            Font = new Font("Segoe UI", 11, FontStyle.Bold)
        };

        var license = new RichTextBox
        {
            Left = 22,
            Top = 180,
            Width = 776,
            Height = 420,
            ReadOnly = true,
            DetectUrls = true,
            Text = licenseText,
            Font = new Font("Segoe UI", 9),
            BackColor = SystemColors.Window,
            BorderStyle = BorderStyle.FixedSingle,
            TabStop = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };

        var acceptance = new CheckBox
        {
            Left = 24,
            Top = 618,
            Width = 520,
            Height = 30,
            Text = "I have read and accept the license agreement.",
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        };

        var cancel = new Button
        {
            Text = "Decline",
            Left = 590,
            Top = 650,
            Width = 98,
            Height = 34,
            DialogResult = DialogResult.Cancel,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right
        };

        var install = new Button
        {
            Text = "Accept && Install",
            Left = 696,
            Top = 650,
            Width = 102,
            Height = 34,
            Enabled = false,
            DialogResult = DialogResult.OK,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right
        };

        acceptance.CheckedChanged += (_, _) => install.Enabled = acceptance.Checked;
        foregroundReleaseTimer.Tick += (_, _) =>
        {
            foregroundReleaseTimer.Stop();
            dialog.TopMost = false;
        };
        dialog.Shown += (_, _) =>
        {
            BringInstallerToForeground(dialog);
            foregroundReleaseTimer.Start();
        };
        dialog.FormClosed += (_, _) => logoBox.Image?.Dispose();
        dialog.Controls.AddRange([header, heading, license, acceptance, cancel, install]);
        dialog.AcceptButton = install;
        dialog.CancelButton = cancel;

        return dialog.ShowDialog() == DialogResult.OK && acceptance.Checked;
    }

    private static Icon LoadBrandIcon()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(BrandIconResource);
        if (stream is not null) return new Icon(stream, 64, 64);
        var executableIcon = Environment.ProcessPath is { } executable
            ? Icon.ExtractAssociatedIcon(executable)
            : null;
        return executableIcon ?? (Icon)SystemIcons.Application.Clone();
    }

    private static string ResolveDisplayVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return string.IsNullOrWhiteSpace(informational)
            ? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "UNKNOWN"
            : informational.Split('+', 2)[0].ToUpperInvariant();
    }

    private static void BringInstallerToForeground(Form dialog)
    {
        dialog.TopMost = true;
        dialog.BringToFront();
        dialog.Activate();
        _ = ShowWindow(dialog.Handle, 9);
        _ = BringWindowToTop(dialog.Handle);
        _ = SetForegroundWindow(dialog.Handle);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
}
