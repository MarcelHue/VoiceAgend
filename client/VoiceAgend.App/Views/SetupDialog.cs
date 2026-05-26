using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VoiceAgend.App.Services;

namespace VoiceAgend.App.Views;

/// <summary>
/// First-Run-Dialog: fragt Server-URL und API-Key ab, validiert über /api/health
/// und gibt dem User Bescheid, wenn schon Settings auf dem Server vorhanden sind.
/// </summary>
public static class SetupDialog
{
    public sealed record Result(bool Confirmed, string ServerUrl, string ApiKey, bool RemoteSettingsAvailable);

    public static async Task<Result?> ShowAsync(XamlRoot root)
    {
        var L = App.Current.Loc;

        var urlBox = new TextBox
        {
            PlaceholderText = "wss://va.example.com/ws/transcribe",
            Header = L.T("Setup.ServerUrlLabel"),
            FontFamily = (FontFamily)Application.Current.Resources["VAFontMono"],
        };
        var keyBox = new PasswordBox
        {
            PlaceholderText = "va_…",
            Header = L.T("Setup.ApiKeyLabel"),
            FontFamily = (FontFamily)Application.Current.Resources["VAFontMono"],
        };
        var status = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["VATextMuteBrush"],
        };

        var stack = new StackPanel { Spacing = 10, MinWidth = 420 };
        stack.Children.Add(new TextBlock
        {
            Text = L.T("Setup.Intro"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["VATextDimBrush"],
        });
        stack.Children.Add(urlBox);
        stack.Children.Add(keyBox);
        stack.Children.Add(status);

        var dlg = new ContentDialog
        {
            XamlRoot = root,
            Title = L.T("Setup.Title"),
            Content = stack,
            PrimaryButtonText = L.T("Setup.Connect"),
            CloseButtonText = L.T("Setup.Skip"),
            DefaultButton = ContentDialogButton.Primary,
        };

        // Wir validieren im Click-Handler und schließen den Dialog manuell —
        // sonst kommt der User nicht zurück, wenn URL/Key falsch sind.
        string finalUrl = "";
        string finalKey = "";
        bool remoteAvailable = false;
        var confirmed = false;

        dlg.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var url = urlBox.Text.Trim();
                var key = keyBox.Password.Trim();
                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
                {
                    status.Text = L.T("Setup.ErrorEmpty");
                    args.Cancel = true;
                    return;
                }
                status.Text = L.T("Setup.Validating");
                try
                {
                    var baseUrl = ServerApiClient.ToHttpBase(url);
                    var profile = await App.Current.ServerApi.GetProfileAsync(baseUrl, key);
                    finalUrl = url;
                    finalKey = key;
                    remoteAvailable = profile.ClientSettings is { ValueKind: System.Text.Json.JsonValueKind.Object };
                    confirmed = true;
                }
                catch (Exception ex)
                {
                    status.Text = string.Format(L.T("Setup.ErrorFmt"), ex.Message);
                    args.Cancel = true;
                }
            }
            finally { deferral.Complete(); }
        };

        await dlg.ShowAsync();
        return confirmed ? new Result(true, finalUrl, finalKey, remoteAvailable) : null;
    }
}
