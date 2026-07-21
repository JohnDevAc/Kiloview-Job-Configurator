using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace KiloviewSetup.Core;

/// <summary>Publishes one full-frame NDI identity card per decoder using the locally installed NDI Tools runtime.</summary>
public sealed class NdiTitleCardService(ILogger<NdiTitleCardService> logger) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TitleCardSender> _senders = new(StringComparer.OrdinalIgnoreCase);
    private NdiRuntime? _runtime;

    public async Task<TitleCardSource> StartOrUpdateAsync(ManagedDevice device, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("NDI identity cards require Windows and NDI Tools.");
        var normalizedHostname = NormalizeSourceSegment(device.Hostname);
        var addressSuffix = device.IpAddress.Replace('.', '-');
        var identity = $"{normalizedHostname}-{addressSuffix}";
        var sourceName = "KV-ID-" + identity[..Math.Min(56, identity.Length)];
        var group = string.IsNullOrWhiteSpace(device.NdiGroup) ? "public" : device.NdiGroup.Trim();
        var publishedGroups = device.Family == DeviceFamily.Simulated && !string.Equals(group, "public", StringComparison.OrdinalIgnoreCase)
            ? $"public,{group}"
            : group;
        var localAddress = LocalAddressFor(device.IpAddress);
        var created = false;
        lock (_gate)
        {
            _runtime ??= new NdiRuntime();
            if (!_senders.TryGetValue(device.Id, out var sender)
                || !string.Equals(sender.Name, sourceName, StringComparison.Ordinal)
                || !string.Equals(sender.Groups, publishedGroups, StringComparison.Ordinal))
            {
                sender?.Dispose();
                sender = new TitleCardSender(_runtime, sourceName, publishedGroups, device, logger);
                _senders[device.Id] = sender;
                created = true;
                logger.LogInformation("Started NDI identity source {Source} for {Device} in groups {Groups}", sourceName, device.Id, publishedGroups);
            }
            else sender.Update(device);
        }
        // NDI discovery is asynchronous. Do not tell the UI that a newly-created
        // card is active until it has sent frames long enough to be advertised.
        if (created) await Task.Delay(TimeSpan.FromSeconds(2), ct);
        return new TitleCardSource(sourceName, group, localAddress);
    }

    public void StopAll()
    {
        lock (_gate)
        {
            foreach (var sender in _senders.Values) sender.Dispose();
            _senders.Clear();
            _runtime?.Dispose();
            _runtime = null;
        }
    }

    public void Dispose() => StopAll();

    private static string LocalAddressFor(string remoteAddress)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(IPAddress.Parse(remoteAddress), 5960);
            return ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
        }
        catch (Exception ex) when (ex is SocketException or FormatException)
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Select(address => address.Address)
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                ?.ToString() ?? "127.0.0.1";
        }
    }

    private static string NormalizeSourceSegment(string value)
    {
        var normalized = new string(value
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            .Select(char.ToUpperInvariant)
            .ToArray());
        return normalized.Length == 0 ? "DEVICE" : normalized;
    }

    private sealed class TitleCardSender : IDisposable
    {
        private readonly NdiRuntime _runtime;
        private readonly ILogger _logger;
        private readonly object _frameGate = new();
        private readonly IntPtr _sender;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private GCHandle _pixelsHandle;
        private byte[] _pixels = [];
        private bool _disposed;

        public string Name { get; }
        public string Groups { get; }

        public TitleCardSender(NdiRuntime runtime, string sourceName, string group, ManagedDevice device, ILogger logger)
        {
            _runtime = runtime;
            _logger = logger;
            Name = sourceName;
            Groups = group;
            _sender = runtime.CreateSender(sourceName, group);
            _pixels = TitleCardRenderer.Render(device);
            _pixelsHandle = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
            _loop = Task.Run(SendLoopAsync);
        }

        public void Update(ManagedDevice device)
        {
            var replacement = TitleCardRenderer.Render(device);
            lock (_frameGate)
            {
                if (_pixelsHandle.IsAllocated) _pixelsHandle.Free();
                _pixels = replacement;
                _pixelsHandle = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
            }
        }

        private Task SendLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    lock (_frameGate)
                    {
                        if (_pixelsHandle.IsAllocated) _runtime.SendVideo(_sender, _pixelsHandle.AddrOfPinnedObject());
                    }
                }
            }
            catch (Exception ex) when (!_stop.IsCancellationRequested)
            {
                _logger.LogError(ex, "NDI identity source stopped sending unexpectedly");
            }
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            lock (_frameGate)
            {
                if (_pixelsHandle.IsAllocated) _pixelsHandle.Free();
                _runtime.DestroySender(_sender);
            }
            _stop.Dispose();
        }
    }

    private sealed class NdiRuntime : IDisposable
    {
        private readonly IntPtr _library;
        private readonly InitializeDelegate _initialize;
        private readonly SendCreateDelegate _sendCreate;
        private readonly SendDestroyDelegate _sendDestroy;
        private readonly SendVideoDelegate _sendVideo;
        private bool _disposed;

        public NdiRuntime()
        {
            var path = FindRuntime();
            _library = NativeLibrary.Load(path);
            _initialize = Export<InitializeDelegate>("NDIlib_initialize");
            _sendCreate = Export<SendCreateDelegate>("NDIlib_send_create");
            _sendDestroy = Export<SendDestroyDelegate>("NDIlib_send_destroy");
            _sendVideo = Export<SendVideoDelegate>("NDIlib_send_send_video_v2");
            if (_initialize() == 0) throw new InvalidOperationException("The installed NDI runtime could not be initialized.");
        }

        public IntPtr CreateSender(string name, string group)
        {
            var namePtr = Marshal.StringToCoTaskMemUTF8(name);
            var groupPtr = Marshal.StringToCoTaskMemUTF8(group);
            try
            {
                var settings = new SendCreate { Name = namePtr, Groups = groupPtr, ClockVideo = true, ClockAudio = false };
                var sender = _sendCreate(ref settings);
                if (sender == IntPtr.Zero) throw new InvalidOperationException($"NDI could not create identity source '{name}'.");
                return sender;
            }
            finally
            {
                Marshal.FreeCoTaskMem(namePtr);
                Marshal.FreeCoTaskMem(groupPtr);
            }
        }

        public void SendVideo(IntPtr sender, IntPtr pixels)
        {
            var frame = new VideoFrame
            {
                Xres = TitleCardRenderer.Width,
                Yres = TitleCardRenderer.Height,
                // GDI's 32-bit DIB buffer is BGRX: its fourth byte is padding and is
                // normally zero. Advertising it as BGRA makes receivers treat the
                // entire identity card as fully transparent (black in Studio Monitor).
                FourCC = 0x58524742, // BGRX
                FrameRateN = 10_000,
                FrameRateD = 1_000,
                PictureAspectRatio = 16f / 9f,
                FrameFormatType = 1, // progressive
                Timecode = long.MaxValue,
                Data = pixels,
                LineStride = TitleCardRenderer.Width * 4,
                Metadata = IntPtr.Zero,
                Timestamp = 0
            };
            _sendVideo(sender, ref frame);
        }

        public void DestroySender(IntPtr sender) => _sendDestroy(sender);

        private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            NativeLibrary.Free(_library);
        }

        private static string FindRuntime()
        {
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V6") is { Length: > 0 } root ? Path.Combine(root, "Processing.NDI.Lib.x64.dll") : "",
                Path.Combine(AppContext.BaseDirectory, "Processing.NDI.Lib.x64.dll"),
                @"C:\Program Files\NDI\NDI 6 Tools\Runtime\Processing.NDI.Lib.x64.dll",
                @"C:\Program Files\NDI\NDI 6 Runtime\v6\Processing.NDI.Lib.x64.dll",
                @"C:\Program Files\NewTek\NewTek NDI 3.8 Runtime\v3\Processing.NDI.Lib.x64.dll"
            };
            return candidates.FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("NDI Tools Runtime was not found. Install NDI Tools 6 on this PC before using display identity cards.");
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte InitializeDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr SendCreateDelegate(ref SendCreate settings);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SendDestroyDelegate(IntPtr sender);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SendVideoDelegate(IntPtr sender, ref VideoFrame frame);

        [StructLayout(LayoutKind.Sequential)]
        private struct SendCreate
        {
            public IntPtr Name;
            public IntPtr Groups;
            [MarshalAs(UnmanagedType.I1)] public bool ClockVideo;
            [MarshalAs(UnmanagedType.I1)] public bool ClockAudio;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct VideoFrame
        {
            public int Xres;
            public int Yres;
            public int FourCC;
            public int FrameRateN;
            public int FrameRateD;
            public float PictureAspectRatio;
            public int FrameFormatType;
            public long Timecode;
            public IntPtr Data;
            public int LineStride;
            public IntPtr Metadata;
            public long Timestamp;
        }
    }

    private static class TitleCardRenderer
    {
        public const int Width = 1920;
        public const int Height = 1080;

        public static byte[] Render(ManagedDevice device)
        {
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = Width,
                    Height = -Height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                    SizeImage = Width * Height * 4
                }
            };
            var dc = CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) throw new InvalidOperationException("Windows could not create a title-card drawing context.");
            var bitmap = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                DeleteDC(dc);
                throw new InvalidOperationException("Windows could not render the NDI identity card.");
            }
            var previousBitmap = SelectObject(dc, bitmap);
            try
            {
                Fill(dc, new Rect(0, 0, Width, Height), 0x00100D08);
                Fill(dc, new Rect(0, 0, Width, 24), 0x006DD337);
                Fill(dc, new Rect(118, 160, 138, 900), 0x006DD337);
                SetBkMode(dc, 1);
                Draw(dc, "KILOVIEW DISPLAY IDENTIFICATION", 54, new Rect(180, 105, 1760, 205), 0x00B8AFA4, 400);
                var hostSize = device.Hostname.Length > 25 ? 88 : 116;
                Draw(dc, device.Hostname.ToUpperInvariant(), hostSize, new Rect(180, 235, 1760, 465), 0x00FFFFFF, 700);
                Draw(dc, device.IpAddress, 92, new Rect(180, 490, 1760, 660), 0x006DD337, 600);
                Draw(dc, $"JOB NAME / NDI GROUP   {device.NdiGroup}", 42, new Rect(185, 715, 1760, 805), 0x00D6CEC5, 400);
                Draw(dc, $"CHANNEL     {device.NdiChannelName}", 42, new Rect(185, 810, 1760, 900), 0x00D6CEC5, 400);
                Draw(dc, "MATCH THIS SCREEN TO THE CARD IN KILOVIEW SETUP", 30, new Rect(180, 960, 1760, 1025), 0x008D857E, 400);
                var pixels = new byte[Width * Height * 4];
                Marshal.Copy(bits, pixels, 0, pixels.Length);
                return pixels;
            }
            finally
            {
                SelectObject(dc, previousBitmap);
                DeleteObject(bitmap);
                DeleteDC(dc);
            }
        }

        private static void Fill(IntPtr dc, Rect rect, uint color)
        {
            var brush = CreateSolidBrush(color);
            try { FillRect(dc, ref rect, brush); }
            finally { DeleteObject(brush); }
        }

        private static void Draw(IntPtr dc, string text, int size, Rect rect, uint color, int weight)
        {
            var font = CreateFontW(-size, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
            var previous = SelectObject(dc, font);
            try
            {
                SetTextColor(dc, color);
                DrawTextW(dc, text, -1, ref rect, 0x00000004 | 0x00000020 | 0x00000040);
            }
            finally
            {
                SelectObject(dc, previous);
                DeleteObject(font);
            }
        }

        [StructLayout(LayoutKind.Sequential)] private struct Rect(int left, int top, int right, int bottom) { public int Left = left; public int Top = top; public int Right = right; public int Bottom = bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
        [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
        {
            public uint Size; public int Width; public int Height; public ushort Planes; public ushort BitCount; public uint Compression;
            public int SizeImage; public int XPelsPerMeter; public int YPelsPerMeter; public uint ClrUsed; public uint ClrImportant;
        }

        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo pbmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
        [DllImport("user32.dll")] private static extern int FillRect(IntPtr hdc, ref Rect rect, IntPtr brush);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outputPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
        [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr hdc, uint color);
        [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawTextW(IntPtr hdc, string text, int count, ref Rect rect, uint format);
    }
}
