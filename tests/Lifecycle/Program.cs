using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.FileProviders;
using NDIJobConfigurator.Core;

if (args is ["--child", var directory, var restartIntent])
{
    // This fixture never starts the server, discovers devices, loads NDI or installs software.
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    builder.Services.AddSingleton<IWebHostEnvironment>(new FixtureEnvironment(directory));
    builder.Services.AddHostedService<LockedResource>();
    builder.Services.AddSingleton<SystemTrayService>();
    builder.Services.AddHostedService(services => services.GetRequiredService<SystemTrayService>());
    var host = builder.Build();
    await host.StartAsync();
    var tray = host.Services.GetRequiredService<SystemTrayService>();
    if (restartIntent == "pending-restart")
    {
        typeof(SystemTrayService).GetField("_restartRequested", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(tray, 1);
        Check(tray.RestartRequested, "The pending Restart fixture was not established.");
    }
    File.WriteAllText(Path.Combine(directory, "ready"), Environment.ProcessId.ToString());
    await host.WaitForShutdownAsync();
    host.Dispose();
    Check(!tray.RestartRequested, "Maintenance shutdown retained a pending tray Restart.");
    File.WriteAllText(Path.Combine(directory, "complete"), "graceful");
    return;
}

var root = Path.Combine(Path.GetTempPath(), "ndi-lifecycle-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Console.WriteLine($"Isolated lifecycle fixtures: {root}");
if (!args.Contains("--restart-manager-only"))
{
    await Run("cancelled-query", async (process, window, _) =>
    {
        Check(Send(window, 0x11, 0, 1) == (IntPtr)1, "Shutdown query was not accepted.");
        Send(window, 0x16, 0, 1);
        Check(!process.HasExited, "A cancelled query stopped the host.");
        // A second query must still be answered, proving the message loop remains alive.
        Check(Send(window, 0x11, 0, 1) == (IntPtr)1, "Cancelled shutdown damaged the message loop.");
        Send(window, 0x16, 1, 1);
        await ExpectExit(process);
    });
    await Run("windows-close", async (process, window, _) =>
    {
        Send(window, 0x10, 0, 0);
        await ExpectExit(process);
    });
}
await Run("restart-manager", async (process, _, path) =>
{
    var key = new StringBuilder(33);
    Check(Native.RmStartSession(out var session, 0, key) == 0, "Restart Manager session failed.");
    try
    {
        // Only this newly created fixture resource is registered. Never register a live DLL/PID.
        Check(Native.RmRegisterResources(session, 1, [path], 0, null, 0, null) == 0, "Resource registration failed.");
        uint count = 0, reasons = 0;
        var status = Native.RmGetList(session, out var needed, ref count, null, ref reasons);
        Check(status == 234 && needed > 0, "Restart Manager did not identify the fixture lock.");
        var holders = new Native.ProcessInfo[needed];
        count = needed;
        Check(Native.RmGetList(session, out needed, ref count, holders, ref reasons) == 0, "Resource query failed.");
        Check(count == 1 && holders[0].Process.ProcessId == process.Id, "Refusing to shut down anything except the owned fixture.");
        var shutdown = await Task.Run(() => Native.RmShutdown(session, 0, IntPtr.Zero)).WaitAsync(TimeSpan.FromSeconds(30));
        Check(shutdown == 0, $"Restart Manager graceful shutdown failed: {shutdown}; application type {holders[0].ApplicationType}.");
        await ExpectExit(process);
    }
    finally { Native.RmEndSession(session); }
    // A session retains stopped application records. A fresh session verifies current holders.
    Check(Native.RmStartSession(out var verification, 0, new StringBuilder(33)) == 0, "Verification session failed.");
    try
    {
        Check(Native.RmRegisterResources(verification, 1, [path], 0, null, 0, null) == 0, "Verification registration failed.");
        uint count = 0, reasons = 0;
        Check(Native.RmGetList(verification, out var needed, ref count, null, ref reasons) == 0 && needed == 0,
            "Restart Manager still reports a file holder after shutdown.");
    }
    finally { Native.RmEndSession(verification); }
});
Console.WriteLine($"PASS native lifecycle checks. Isolated data: {root}");

async Task Run(string name, Func<Process, IntPtr, string, Task> exercise)
{
    var path = Path.Combine(root, name);
    Directory.CreateDirectory(path);
    var resource = Path.Combine(path, "fixture-codec.dll");
    File.WriteAllText(resource, "test resource, not a vendor DLL");
    File.WriteAllText(Path.Combine(path, "state.json"), "{\"job\":\"preserve\"}");
    File.WriteAllText(Path.Combine(path, "ndi.json"), "{\"ndi\":\"unchanged\"}");
    var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Lifecycle.exe"))
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden,
        WorkingDirectory = path
    };
    start.ArgumentList.Add("--child");
    start.ArgumentList.Add(path);
    start.ArgumentList.Add(name == "windows-close" ? "pending-restart" : "none");
    start.Environment["NDI_JOB_CONFIGURATOR_DATA_DIR"] = path;
    start.Environment["NDI_JOB_CONFIGURATOR_NDI_CONFIG_PATH"] = Path.Combine(path, "ndi.json");
    using var process = Process.Start(start)!;
    try
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!File.Exists(Path.Combine(path, "ready")) && !process.HasExited && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Check(!process.HasExited && File.Exists(Path.Combine(path, "ready")), "Fixture failed to start.");
        var window = IntPtr.Zero;
        Native.EnumWindows((handle, _) =>
        {
            Native.GetWindowThreadProcessId(handle, out var owner);
            if (owner != process.Id) return true;
            var caption = new StringBuilder(128);
            Native.GetWindowText(handle, caption, caption.Capacity);
            if (caption.ToString() == "NDI Job Configurator lifecycle") window = handle;
            return true;
        }, IntPtr.Zero);
        if (name != "restart-manager") Check(window != IntPtr.Zero, "The host has no shutdown message window.");
        await exercise(process, window, resource);
        Check(File.ReadAllText(Path.Combine(path, "complete")) == "graceful", "Host shutdown/disposal did not complete.");
        Check(File.ReadAllText(Path.Combine(path, "state.json")) == "{\"job\":\"preserve\"}", "State changed.");
        Check(File.ReadAllText(Path.Combine(path, "ndi.json")) == "{\"ndi\":\"unchanged\"}", "NDI configuration changed.");
        File.Move(resource, resource + ".released");
        Console.WriteLine($"PASS {name}: clean exit, resource released, state retained");
    }
    finally
    {
        // Failure cleanup is restricted to this exact process created by this test.
        if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
    }
}

static async Task ExpectExit(Process process)
{
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Check(process.ExitCode == 0, $"Fixture exited with {process.ExitCode}.");
}

static IntPtr Send(IntPtr window, uint message, int wParam, int lParam)
{
    Check(Native.SendMessageTimeout(window, message, (IntPtr)wParam, (IntPtr)lParam, 2, 3000, out var result) != IntPtr.Zero,
        $"Window did not answer {message:X}.");
    return result;
}

static void Check(bool condition, string error)
{
    if (!condition) throw new InvalidOperationException(error);
}

sealed class LockedResource : IHostedService, IDisposable
{
    private readonly FileStream _stream = new("fixture-codec.dll", FileMode.Open, FileAccess.Read, FileShare.Read);
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.Delay(150, ct); // Prove the host drains work before releasing the lock.
    public void Dispose() => _stream.Dispose();
}

sealed class FixtureEnvironment(string root) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "Lifecycle fixture";
    public string EnvironmentName { get; set; } = "Test";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = root;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}

static class Native
{
    internal delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] internal static extern int RmStartSession(out uint session, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] internal static extern int RmRegisterResources(uint session, uint files, string[] names, uint applications, UniqueProcess[]? processes, uint services, string[]? serviceNames);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] internal static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[]? processes, ref uint reasons);
    [DllImport("rstrtmgr.dll")] internal static extern int RmShutdown(uint session, uint flags, IntPtr callback);
    [DllImport("rstrtmgr.dll")] internal static extern int RmEndSession(uint session);
    [StructLayout(LayoutKind.Sequential)] internal struct UniqueProcess { public int ProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME Started; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Service;
        public int ApplicationType;
        public uint Status;
        public uint Session;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }
}
