using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace VoiceAgend.App.Services;

/// <summary>
/// System-Backdrop, der das Fenster pro Pixel durchsichtig macht: überall, wo XAML nichts
/// (oder etwas Halbtransparentes) malt, scheint der Desktop durch. Damit kann das HUD nur
/// seinen Hintergrund transparent machen, während Punkt/Waveform/Text voll deckend bleiben —
/// mit SetLayeredWindowAttributes ginge nur ein einheitliches Alpha fürs ganze Fenster.
/// </summary>
public sealed class TransparentBackdrop : SystemBackdrop
{
    private static Windows.UI.Composition.Compositor? s_compositor;
    private static object? s_dispatcherQueueController;
    private Windows.UI.Composition.CompositionColorBrush? _brush;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        try
        {
            // Der System-Compositor (Windows.UI.Composition) braucht eine
            // Windows.System.DispatcherQueue auf dem Thread — WinUI 3 hat nur die
            // Microsoft.UI-Variante, daher ggf. selbst anlegen.
            EnsureWindowsSystemDispatcherQueue();
            s_compositor ??= new Windows.UI.Composition.Compositor();
            _brush = s_compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            connectedTarget.SystemBackdrop = _brush;
            Logger.Info("TransparentBackdrop connected");
        }
        catch (Exception ex)
        {
            // Ohne Backdrop bleibt das Fenster nur undurchsichtig — kein Grund zu crashen.
            Logger.Error("TransparentBackdrop connect failed", ex);
        }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        disconnectedTarget.SystemBackdrop = null;
        _brush?.Dispose();
        _brush = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;     // 2 = DQTYPE_THREAD_CURRENT
        public int apartmentType;  // 2 = DQTAT_COM_STA
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(
        DispatcherQueueOptions options,
        [MarshalAs(UnmanagedType.IUnknown)] out object dispatcherQueueController);

    private static void EnsureWindowsSystemDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() != null) return;
        if (s_dispatcherQueueController != null) return;
        var options = new DispatcherQueueOptions
        {
            dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
            threadType = 2,
            apartmentType = 2,
        };
        // Controller statisch halten, sonst stirbt die Queue mit dem GC.
        CreateDispatcherQueueController(options, out s_dispatcherQueueController);
    }
}
