using System.Windows.Forms;

namespace NDIJobConfigurator.Core;

/// <summary>Routes Windows maintenance/session shutdown into the host's normal stop path.</summary>
internal sealed class WindowsShutdownWindow : NativeWindow, IDisposable
{
    private const int WmClose = 0x0010;
    private const int WmQueryEndSession = 0x0011;
    private const int WmEndSession = 0x0016;
    private readonly Action _stop;
    private bool _stopRequested;

    public WindowsShutdownWindow(Action stop)
    {
        _stop = stop;
        // A hidden top-level window receives Restart Manager and session messages.
        // A message-only window does not receive these broadcasts.
        CreateHandle(new CreateParams { Caption = "NDI Job Configurator lifecycle" });
    }

    protected override void WndProc(ref Message message)
    {
        switch (message.Msg)
        {
            case WmQueryEndSession:
                // A query may be cancelled by another application. Do not stop yet.
                message.Result = (IntPtr)1;
                return;
            case WmEndSession:
                if (message.WParam != IntPtr.Zero) RequestStop();
                message.Result = IntPtr.Zero;
                return;
            case WmClose:
                RequestStop();
                message.Result = IntPtr.Zero;
                return;
        }
        base.WndProc(ref message);
    }

    private void RequestStop()
    {
        if (_stopRequested) return;
        _stopRequested = true;
        _stop();
    }

    public void Dispose() => DestroyHandle();
}
