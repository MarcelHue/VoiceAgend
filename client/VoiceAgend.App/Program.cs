using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;
using VoiceAgend.App.Services;
using WinRT;

namespace VoiceAgend.App;

/// <summary>
/// Eigener Entry-Point, weil DISABLE_XAML_GENERATED_MAIN gesetzt ist.
/// Velopack braucht den ersten Aufruf in Main(), bevor irgendeine UI startet —
/// nur so kann es Install/Update-/Uninstall-Hooks abarbeiten.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            VelopackApp.Build().Run();
        }
        catch (Exception ex)
        {
            Logger.Error("Velopack init", ex);
        }

        ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(ctx);
            new App();
        });
    }
}
