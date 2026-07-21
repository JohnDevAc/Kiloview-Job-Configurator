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

            var licensePath = Path.Combine(extractRoot, "LICENSE.md");
            if (!File.Exists(licensePath))
            {
                throw new FileNotFoundException("The license agreement is missing from the installer payload.", licensePath);
            }

            if (!ShowLicenseAgreement(File.ReadAllText(licensePath)))
            {
                return 2;
            }

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
