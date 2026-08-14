using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NDIJobConfigurator.Bootstrapper;

internal static class BootstrapperProgram
{
    private const string PayloadResource = "NDIJobConfigurator.Payload.zip";
    private const string BrandIconResource = "NDIJobConfigurator.BrandIcon.ico";
    private const string LicenseResource = "NDIJobConfigurator.License.md";
    private const string ProductName = "NDI Job Configurator";
    private const string InstallerAppUserModelId = "JohnLightfoot.NDIJobConfigurator.Installer";
    private static readonly string InstallerLogPath = Path.Combine(Path.GetTempPath(), "NDIJobConfigurator-Installer.log");

    [STAThread]
    private static int Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        _ = SetCurrentProcessExplicitAppUserModelID(InstallerAppUserModelId);

        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var extractRoot = Path.Combine(temporaryRoot, $"NDIJobConfigurator-{Guid.NewGuid():N}");

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
            var archivePath = Path.Combine(extractRoot, "NDIJobConfigurator-Payload.zip");
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

            var installerScript = Path.Combine(extractRoot, "Install-NDIJobConfigurator.ps1");
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
            AutoScaleDimensions = new SizeF(96F, 96F),
            AutoScaleMode = AutoScaleMode.Dpi,
            ClientSize = new Size(820, 700),
            MinimumSize = new Size(640, 520),
            MinimizeBox = false,
            MaximizeBox = true,
            FormBorderStyle = FormBorderStyle.Sizable,
            ShowIcon = true,
            ShowInTaskbar = true,
            Icon = (Icon)applicationIcon.Clone(),
            BackColor = Color.FromArgb(245, 248, 246)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
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
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
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
            Font = new Font("Consolas", 9, FontStyle.Bold),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
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

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = new Padding(22, 14, 22, 12)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        var heading = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Text = "License agreement\nPlease review the terms below before continuing.",
            ForeColor = Color.FromArgb(25, 35, 32),
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            Margin = Padding.Empty
        };

        var license = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            DetectUrls = true,
            Text = licenseText,
            Font = new Font("Segoe UI", 9),
            BackColor = SystemColors.Window,
            BorderStyle = BorderStyle.FixedSingle,
            TabStop = true,
            Margin = Padding.Empty
        };

        var acceptance = new CheckBox
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Text = "I have read and accept the license agreement.",
            AutoEllipsis = true,
            Margin = new Padding(2, 7, 2, 5)
        };

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = new Padding(0, 7, 0, 0)
        };

        var cancel = new Button
        {
            Text = "Decline",
            Width = 108,
            Height = 34,
            DialogResult = DialogResult.Cancel,
            Margin = new Padding(8, 0, 0, 0)
        };

        var install = new Button
        {
            Text = "Accept && Install",
            Width = 122,
            Height = 34,
            Enabled = false,
            DialogResult = DialogResult.OK,
            Margin = Padding.Empty
        };

        acceptance.CheckedChanged += (_, _) => install.Enabled = acceptance.Checked;
        foregroundReleaseTimer.Tick += (_, _) =>
        {
            foregroundReleaseTimer.Stop();
            dialog.TopMost = false;
        };
        dialog.Load += (_, _) => FitToWorkingArea(dialog);
        dialog.Shown += (_, _) =>
        {
            BringInstallerToForeground(dialog);
            foregroundReleaseTimer.Start();
        };
        dialog.FormClosed += (_, _) => logoBox.Image?.Dispose();
        actions.Controls.AddRange([install, cancel]);
        content.Controls.Add(heading, 0, 0);
        content.Controls.Add(license, 0, 1);
        content.Controls.Add(acceptance, 0, 2);
        content.Controls.Add(actions, 0, 3);
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(content, 0, 1);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = install;
        dialog.CancelButton = cancel;

        return dialog.ShowDialog() == DialogResult.OK && acceptance.Checked;
    }

    private static void FitToWorkingArea(Form dialog)
    {
        var workingArea = Screen.FromControl(dialog).WorkingArea;
        const int margin = 12;
        var availableWidth = Math.Max(1, workingArea.Width - margin * 2);
        var availableHeight = Math.Max(1, workingArea.Height - margin * 2);
        var width = Math.Min(dialog.Width, availableWidth);
        var height = Math.Min(dialog.Height, availableHeight);

        dialog.MinimumSize = new Size(
            Math.Min(dialog.MinimumSize.Width, width),
            Math.Min(dialog.MinimumSize.Height, height));
        dialog.Size = new Size(width, height);
        dialog.Location = new Point(
            workingArea.Left + Math.Max(margin, (workingArea.Width - width) / 2),
            workingArea.Top + Math.Max(margin, (workingArea.Height - height) / 2));
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
