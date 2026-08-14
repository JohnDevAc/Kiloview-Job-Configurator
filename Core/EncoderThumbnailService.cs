using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace NDIJobConfigurator.Core;

public sealed record EncoderThumbnail(
    byte[] Bytes,
    bool Live,
    DateTimeOffset CapturedUtc,
    bool Warning = false,
    int ConsecutiveFailures = 0);

/// <summary>Captures a low-bandwidth NDI preview frame for each encoder and caches a browser-ready 320x240 bitmap.</summary>
public sealed class EncoderThumbnailService(AppStateStore store, ILogger<EncoderThumbnailService> logger) : IDisposable
{
    // The browser requests a new preview every five seconds. Keep the cache just
    // below that interval so each scheduled request can capture a newer frame.
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(4);
    private readonly ConcurrentDictionary<string, EncoderThumbnail> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _deviceLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _captureLimit = new(4, 4);
    private readonly SemaphoreSlim _runtimeAccess = new(4, 4);
    private readonly object _runtimeGate = new();
    private NdiReceiveRuntime? _runtime;

    public async Task<EncoderThumbnail> GetAsync(string id, CancellationToken ct)
    {
        var device = (await store.ReadAsync()).Devices.FirstOrDefault(d => d.Id == id)
            ?? throw new KeyNotFoundException($"Device '{id}' was not found.");
        if (!device.IsOnboarded || device.Role != DeviceRole.Encoder)
            throw new InvalidOperationException("HDMI input previews are available only for onboarded encoders.");

        var activeTeleToolStream = device.IsTeleTool() && device.StreamRunning == true;
        if (!activeTeleToolStream)
        {
            _failures.TryRemove(id, out _);
            if (_cache.TryGetValue(id, out var stoppedStreamPreview) && stoppedStreamPreview.Warning)
                _cache.TryRemove(id, out _);
        }
        else if (_cache.TryGetValue(id, out var stoppedStreamPreview) &&
                 !stoppedStreamPreview.Live &&
                 stoppedStreamPreview.ConsecutiveFailures == 0)
        {
            _cache.TryRemove(id, out _);
        }

        if (_cache.TryGetValue(id, out var cached) && DateTimeOffset.UtcNow - cached.CapturedUtc < CacheLifetime) return cached;
        var deviceLock = _deviceLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await deviceLock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(id, out cached) && DateTimeOffset.UtcNow - cached.CapturedUtc < CacheLifetime) return cached;
            await _captureLimit.WaitAsync(ct);
            try
            {
                EncoderThumbnail thumbnail;
                if (device.IsSimulation())
                {
                    _failures.TryRemove(id, out _);
                    thumbnail = new(ThumbnailBitmap.Pattern(device.Id), true, DateTimeOffset.UtcNow);
                }
                else
                {
                    byte[]? frame = null;
                    await _runtimeAccess.WaitAsync(ct);
                    try { frame = await Runtime().CaptureAsync(device, ct); }
                    catch (Exception ex) { logger.LogWarning(ex, "Could not capture NDI preview for encoder {Device}", device.Id); }
                    finally { _runtimeAccess.Release(); }
                    if (frame is not null)
                    {
                        _failures.TryRemove(id, out _);
                        thumbnail = new(frame, true, DateTimeOffset.UtcNow);
                    }
                    else
                    {
                        var failures = activeTeleToolStream
                            ? _failures.AddOrUpdate(id, 1, static (_, current) => current + 1)
                            : 0;
                        var warning = activeTeleToolStream && failures >= 2;
                        thumbnail = new(
                            warning ? ThumbnailBitmap.Warning(device.Id) : ThumbnailBitmap.Unavailable(device.Id),
                            false,
                            DateTimeOffset.UtcNow,
                            warning,
                            failures);
                    }
                }
                _cache[id] = thumbnail;
                return thumbnail;
            }
            finally { _captureLimit.Release(); }
        }
        finally { deviceLock.Release(); }
    }

    public void Forget(string id)
    {
        _cache.TryRemove(id, out _);
        _failures.TryRemove(id, out _);
        _deviceLocks.TryRemove(id, out _);
    }

    public void ForgetAll()
    {
        _cache.Clear();
        _failures.Clear();
        _deviceLocks.Clear();
    }

    /// <summary>
    /// Recreates the embedded NDI receiver after Access Manager changes. NDI reads
    /// its shared configuration when the runtime starts, so existing preview
    /// receivers otherwise continue using the previous transport policy.
    /// </summary>
    public async Task ReloadNdiConfigurationAsync(CancellationToken ct)
    {
        var acquired = 0;
        try
        {
            for (; acquired < 4; acquired++) await _runtimeAccess.WaitAsync(ct);
            lock (_runtimeGate)
            {
                _runtime?.Dispose();
                _runtime = null;
            }
            _cache.Clear();
            _failures.Clear();
            logger.LogInformation("Reloaded the NDI encoder preview runtime after Access Manager configuration changed");
        }
        finally
        {
            for (var index = 0; index < acquired; index++) _runtimeAccess.Release();
        }
    }

    private NdiReceiveRuntime Runtime()
    {
        lock (_runtimeGate) return _runtime ??= new NdiReceiveRuntime();
    }

    public void Dispose()
    {
        lock (_runtimeGate)
        {
            _runtime?.Dispose();
            _runtime = null;
        }
        foreach (var gate in _deviceLocks.Values) gate.Dispose();
        _runtimeAccess.Dispose();
        _captureLimit.Dispose();
    }

    private sealed class NdiReceiveRuntime : IDisposable
    {
        private const int FrameTypeVideo = 1;
        private const int ColorFormatBgrxBgra = 0;
        private const int BandwidthLowest = 0;
        private const int FourCcBgrx = 0x58524742;
        private const int FourCcBgra = 0x41524742;

        private readonly IntPtr _library;
        private readonly FindCreateDelegate _findCreate;
        private readonly FindDestroyDelegate _findDestroy;
        private readonly FindWaitDelegate _findWait;
        private readonly FindSourcesDelegate _findSources;
        private readonly RecvCreateDelegate _recvCreate;
        private readonly RecvDestroyDelegate _recvDestroy;
        private readonly RecvCaptureDelegate _recvCapture;
        private readonly RecvFreeVideoDelegate _recvFreeVideo;
        private bool _disposed;

        public NdiReceiveRuntime()
        {
            _library = NativeLibrary.Load(FindRuntime());
            var initialize = Export<InitializeDelegate>("NDIlib_initialize");
            _findCreate = Export<FindCreateDelegate>("NDIlib_find_create_v2");
            _findDestroy = Export<FindDestroyDelegate>("NDIlib_find_destroy");
            _findWait = Export<FindWaitDelegate>("NDIlib_find_wait_for_sources");
            _findSources = Export<FindSourcesDelegate>("NDIlib_find_get_current_sources");
            _recvCreate = Export<RecvCreateDelegate>("NDIlib_recv_create_v3");
            _recvDestroy = Export<RecvDestroyDelegate>("NDIlib_recv_destroy");
            _recvCapture = Export<RecvCaptureDelegate>("NDIlib_recv_capture_v3");
            _recvFreeVideo = Export<RecvFreeVideoDelegate>("NDIlib_recv_free_video_v2");
            if (initialize() == 0) throw new InvalidOperationException("The installed NDI runtime could not be initialized.");
        }

        public Task<byte[]?> CaptureAsync(ManagedDevice device, CancellationToken ct) => Task.Run(() => Capture(device, ct), ct);

        private byte[]? Capture(ManagedDevice device, CancellationToken ct)
        {
            var groupPtr = Marshal.StringToCoTaskMemUTF8(string.IsNullOrWhiteSpace(device.NdiGroup) ? "public" : device.NdiGroup);
            var ipPtr = Marshal.StringToCoTaskMemUTF8(device.IpAddress);
            IntPtr finder = IntPtr.Zero;
            try
            {
                var findSettings = new FindCreate { ShowLocalSources = false, Groups = groupPtr, ExtraIps = ipPtr };
                finder = _findCreate(ref findSettings);
                if (finder == IntPtr.Zero) throw new InvalidOperationException("NDI could not create an encoder preview finder.");

                var discoveryEnd = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
                while (DateTimeOffset.UtcNow < discoveryEnd)
                {
                    ct.ThrowIfCancellationRequested();
                    _findWait(finder, 500);
                    var sourcesPtr = _findSources(finder, out var count);
                    var source = FindSource(sourcesPtr, count, device);
                    if (source is not null) return Receive(source.Value, device, ct);
                }
                return null;
            }
            finally
            {
                if (finder != IntPtr.Zero) _findDestroy(finder);
                Marshal.FreeCoTaskMem(groupPtr);
                Marshal.FreeCoTaskMem(ipPtr);
            }
        }

        private static (string Name, string Url)? FindSource(IntPtr sources, uint count, ManagedDevice device)
        {
            (string Name, string Url)? hostnameMatch = null;
            var size = Marshal.SizeOf<NdiSource>();
            for (var index = 0; index < count; index++)
            {
                var source = Marshal.PtrToStructure<NdiSource>(IntPtr.Add(sources, checked((int)index * size)));
                var name = Marshal.PtrToStringUTF8(source.Name) ?? "";
                var url = Marshal.PtrToStringUTF8(source.Url) ?? "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                var host = name.Contains(device.Hostname, StringComparison.OrdinalIgnoreCase);
                var channel = name.Contains(device.NdiChannelName, StringComparison.OrdinalIgnoreCase);
                if (host && channel) return (name, url);
                if (host) hostnameMatch = (name, url);
            }
            return hostnameMatch;
        }

        private byte[]? Receive((string Name, string Url) source, ManagedDevice device, CancellationToken ct)
        {
            var namePtr = Marshal.StringToCoTaskMemUTF8(source.Name);
            var urlPtr = string.IsNullOrWhiteSpace(source.Url) ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(source.Url);
            var receiverNamePtr = Marshal.StringToCoTaskMemUTF8($"NDI Job Configurator Preview {device.Id}");
            IntPtr receiver = IntPtr.Zero;
            try
            {
                var settings = new RecvCreate
                {
                    Source = new NdiSource { Name = namePtr, Url = urlPtr },
                    ColorFormat = ColorFormatBgrxBgra,
                    Bandwidth = BandwidthLowest,
                    AllowVideoFields = false,
                    ReceiverName = receiverNamePtr
                };
                receiver = _recvCreate(ref settings);
                if (receiver == IntPtr.Zero) throw new InvalidOperationException($"NDI could not connect to encoder source '{source.Name}'.");
                var end = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6);
                while (DateTimeOffset.UtcNow < end)
                {
                    ct.ThrowIfCancellationRequested();
                    var type = _recvCapture(receiver, out var frame, IntPtr.Zero, IntPtr.Zero, 500);
                    if (type != FrameTypeVideo) continue;
                    try
                    {
                        if (frame.Data != IntPtr.Zero && frame.Xres > 0 && frame.Yres > 0 && frame.FourCC is FourCcBgrx or FourCcBgra)
                            return ThumbnailBitmap.FromBgrx(frame);
                    }
                    finally { _recvFreeVideo(receiver, ref frame); }
                }
                return null;
            }
            finally
            {
                if (receiver != IntPtr.Zero) _recvDestroy(receiver);
                Marshal.FreeCoTaskMem(namePtr);
                if (urlPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(urlPtr);
                Marshal.FreeCoTaskMem(receiverNamePtr);
            }
        }

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
                ?? throw new FileNotFoundException("NDI Tools Runtime was not found. Install NDI Tools 6 to use encoder thumbnails.");
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte InitializeDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FindCreateDelegate(ref FindCreate settings);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FindDestroyDelegate(IntPtr finder);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte FindWaitDelegate(IntPtr finder, uint timeoutMs);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FindSourcesDelegate(IntPtr finder, out uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr RecvCreateDelegate(ref RecvCreate settings);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RecvDestroyDelegate(IntPtr receiver);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RecvCaptureDelegate(IntPtr receiver, out NdiVideoFrame frame, IntPtr audio, IntPtr metadata, uint timeoutMs);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RecvFreeVideoDelegate(IntPtr receiver, ref NdiVideoFrame frame);

        [StructLayout(LayoutKind.Sequential)]
        private struct FindCreate
        {
            [MarshalAs(UnmanagedType.I1)] public bool ShowLocalSources;
            public IntPtr Groups;
            public IntPtr ExtraIps;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NdiSource { public IntPtr Name; public IntPtr Url; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RecvCreate
        {
            public NdiSource Source;
            public int ColorFormat;
            public int Bandwidth;
            [MarshalAs(UnmanagedType.I1)] public bool AllowVideoFields;
            public IntPtr ReceiverName;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NdiVideoFrame
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

    private static class ThumbnailBitmap
    {
        private const int Width = 320;
        private const int Height = 240;
        private const int HeaderSize = 54;
        private const int RowStride = Width * 3;

        public static byte[] FromBgrx(NdiReceiveRuntime.NdiVideoFrame frame)
        {
            var bitmap = CreateEmpty();
            var scale = Math.Min((double)Width / frame.Xres, (double)Height / frame.Yres);
            var displayWidth = Math.Max(1, (int)Math.Round(frame.Xres * scale));
            var displayHeight = Math.Max(1, (int)Math.Round(frame.Yres * scale));
            var offsetX = (Width - displayWidth) / 2;
            var offsetY = (Height - displayHeight) / 2;
            var sourceRow = new byte[checked(frame.Xres * 4)];
            var previousSourceY = -1;
            for (var y = 0; y < displayHeight; y++)
            {
                var sourceY = Math.Min(frame.Yres - 1, y * frame.Yres / displayHeight);
                if (sourceY != previousSourceY)
                {
                    Marshal.Copy(IntPtr.Add(frame.Data, checked(sourceY * frame.LineStride)), sourceRow, 0, sourceRow.Length);
                    previousSourceY = sourceY;
                }
                var destination = HeaderSize + (Height - 1 - (offsetY + y)) * RowStride + offsetX * 3;
                for (var x = 0; x < displayWidth; x++)
                {
                    var sourceX = Math.Min(frame.Xres - 1, x * frame.Xres / displayWidth) * 4;
                    bitmap[destination++] = sourceRow[sourceX];
                    bitmap[destination++] = sourceRow[sourceX + 1];
                    bitmap[destination++] = sourceRow[sourceX + 2];
                }
            }
            return bitmap;
        }

        public static byte[] Pattern(string seed)
        {
            var bitmap = CreateEmpty();
            var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(seed);
            var colors = new[]
            {
                ((byte)(48 + (hash & 63)), (byte)198, (byte)74),
                ((byte)43, (byte)(100 + ((hash >> 6) & 95)), (byte)210),
                ((byte)205, (byte)(80 + ((hash >> 12) & 95)), (byte)45),
                ((byte)145, (byte)60, (byte)(120 + ((hash >> 18) & 95)))
            };
            for (var y = 0; y < Height; y++)
            {
                var row = HeaderSize + (Height - 1 - y) * RowStride;
                for (var x = 0; x < Width; x++)
                {
                    var color = colors[(x / 80 + y / 60) % colors.Length];
                    bitmap[row++] = color.Item3;
                    bitmap[row++] = color.Item2;
                    bitmap[row++] = color.Item1;
                }
            }
            return bitmap;
        }

        public static byte[] Unavailable(string seed)
        {
            var bitmap = CreateEmpty();
            var shift = Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(seed) % 40);
            for (var y = 0; y < Height; y++)
            {
                var row = HeaderSize + (Height - 1 - y) * RowStride;
                for (var x = 0; x < Width; x++)
                {
                    var stripe = ((x + y + shift) / 18) % 2 == 0;
                    var value = stripe ? (byte)34 : (byte)24;
                    bitmap[row++] = value;
                    bitmap[row++] = value;
                    bitmap[row++] = value;
                }
            }
            return bitmap;
        }

        public static byte[] Warning(string seed)
        {
            var bitmap = CreateEmpty();
            var shift = Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(seed) % 24);
            for (var y = 0; y < Height; y++)
            {
                var row = HeaderSize + (Height - 1 - y) * RowStride;
                for (var x = 0; x < Width; x++)
                {
                    var stripe = ((x + y + shift) / 16) % 2 == 0;
                    var red = stripe ? (byte)52 : (byte)38;
                    var green = stripe ? (byte)35 : (byte)27;
                    var blue = stripe ? (byte)24 : (byte)20;

                    var triangleY = y - 26;
                    var triangleHalfWidth = triangleY is >= 0 and <= 158 ? triangleY * 112 / 158 : -1;
                    var distanceFromCentre = Math.Abs(x - Width / 2);
                    if (triangleHalfWidth >= 0 && distanceFromCentre <= triangleHalfWidth)
                    {
                        var onBorder = triangleY >= 148 || distanceFromCentre >= triangleHalfWidth - 7;
                        red = onBorder ? (byte)243 : (byte)38;
                        green = onBorder ? (byte)189 : (byte)29;
                        blue = onBorder ? (byte)74 : (byte)24;
                    }

                    var exclamationStem = y is >= 78 and <= 137 && x is >= 153 and <= 166;
                    var exclamationDot = (x - 160) * (x - 160) + (y - 158) * (y - 158) <= 64;
                    if (exclamationStem || exclamationDot)
                    {
                        red = 243;
                        green = 189;
                        blue = 74;
                    }

                    bitmap[row++] = blue;
                    bitmap[row++] = green;
                    bitmap[row++] = red;
                }
            }
            return bitmap;
        }

        private static byte[] CreateEmpty()
        {
            var imageBytes = RowStride * Height;
            var bytes = new byte[HeaderSize + imageBytes];
            bytes[0] = (byte)'B';
            bytes[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), HeaderSize);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), Width);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), Height);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), 24);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34), imageBytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(38), 2835);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(42), 2835);
            return bytes;
        }
    }
}
