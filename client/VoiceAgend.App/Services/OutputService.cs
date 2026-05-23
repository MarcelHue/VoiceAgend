using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using VoiceAgend.App.Models;

namespace VoiceAgend.App.Services;

public sealed class OutputService
{
    private DispatcherQueue? _ui;

    public void AttachUiThread(DispatcherQueue ui) => _ui = ui;

    private void OnUi(Action a)
    {
        if (_ui is null) { a(); return; }
        if (_ui.HasThreadAccess) { a(); return; }
        _ui.TryEnqueue(() => a());
    }

    public void Dispatch(OutputMode mode, string text, bool showToast)
    {
        switch (mode)
        {
            case OutputMode.Clipboard:
                CopyToClipboard(text);
                if (showToast) Toast("Transkript in Zwischenablage", text);
                break;
            case OutputMode.Type:
                TypeText(text);
                break;
            case OutputMode.Paste:
                PasteText(text);
                break;
            case OutputMode.DirectInsert:
                // Erst UI Automation versuchen (instant, kein Clipboard, keine Tasten).
                // Falls Ziel-App das nicht unterstützt (z. B. Web-Inputs ohne ValuePattern),
                // auf zeichenweises Tippen zurückfallen — wie vom User gewünscht.
                if (!TryDirectInsert(text)) TypeText(text);
                break;
            case OutputMode.Notification:
                Toast("Transkript", text);
                break;
        }
    }

    /// <summary>
    /// Schreibt Text via UI Automation direkt in das fokussierte Eingabefeld —
    /// kein Clipboard, keine Tasten-Events. Sehr schnell in nativen Windows-Apps,
    /// funktioniert in vielen Web-/Electron-Apps nur eingeschränkt; in dem Fall
    /// fällt der Caller auf PasteText() zurück.
    /// </summary>
    public bool TryDirectInsert(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        try
        {
            using var automation = new FlaUI.UIA3.UIA3Automation();
            var focused = automation.FocusedElement();
            if (focused is null) return false;

            // 1. Versuch: ValuePattern.SetValue (für reine TextBoxen + viele Inputs)
            // Achtung: SETZT den Wert (überschreibt vorhandenen Text). Daher prüfen:
            // wenn das Feld leer ist, ist das egal — sonst lieber appenden.
            var valueP = focused.Patterns.Value.PatternOrDefault;
            if (valueP is not null && valueP.IsReadOnly.IsSupported && !valueP.IsReadOnly.Value)
            {
                var existing = valueP.Value.IsSupported ? (valueP.Value.Value ?? "") : "";
                valueP.SetValue(existing + text);
                return true;
            }

            // Kein passendes Pattern — Caller fällt auf PasteText zurück
            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn("DirectInsert failed: " + ex.Message);
            return false;
        }
    }

    public void Toast(string title, string body) => OnUi(() =>
    {
        try
        {
            var n = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body)
                .BuildNotification();
            AppNotificationManager.Default.Show(n);
        }
        catch (Exception ex)
        {
            Logger.Warn("Toast failed: " + ex.Message);
        }
    });

    public void CopyToClipboard(string text) => OnUi(() =>
    {
        try
        {
            var dp = new DataPackage();
            dp.SetText(text);
            Clipboard.SetContent(dp);
        }
        catch (Exception ex)
        {
            Logger.Error("Clipboard.SetContent failed", ex);
            CopyToClipboardWin32(text);
        }
    });

    // Win32-Fallback ohne WinRT-Apartment-Anforderungen
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    private static void CopyToClipboardWin32(string text)
    {
        if (!OpenClipboard(IntPtr.Zero)) return;
        try
        {
            EmptyClipboard();
            var bytes = (text.Length + 1) * 2;
            var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (hMem == IntPtr.Zero) return;
            var pMem = GlobalLock(hMem);
            try { Marshal.Copy(text.ToCharArray(), 0, pMem, text.Length); }
            finally { GlobalUnlock(hMem); }
            SetClipboardData(CF_UNICODETEXT, hMem);
        }
        finally { CloseClipboard(); }
    }

    // ---- SendInput für "direkt tippen" ----

    // Auf x64 muss INPUT exakt 40 Byte (4+4 Pad+32 Union) groß sein,
    // sonst lehnt SendInput die Daten still ab.
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>
    /// Echtes zeichenweises Tippen via SendInput. Wird in kleinen Batches gesendet,
    /// damit Ziel-Apps mit teurem Per-Keypress-Handling (Autocomplete, Highlighting)
    /// nicht in einen Queue-Stau geraten, der die letzten Zeichen ruckartig macht.
    /// </summary>
    public void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        // Vorbereitung außerhalb des Hot-Paths: alle Events erzeugen
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            if (ch == '\r') continue;
            if (ch == '\n')
            {
                inputs.Add(MakeVk(0x0D, false));
                inputs.Add(MakeVk(0x0D, true));
                continue;
            }
            inputs.Add(MakeUnicode(ch, false));
            inputs.Add(MakeUnicode(ch, true));
        }
        if (inputs.Count == 0) return;

        // Async + chunked: ~40 Zeichen pro Batch (80 Events), 8 ms Pause
        _ = Task.Run(() => TypeChunked(inputs));
    }

    private static void TypeChunked(List<INPUT> inputs)
    {
        const int batchEvents = 80;          // ~40 Zeichen pro Batch (Down+Up je)
        const int charsPerBatch = batchEvents / 2;
        var cps = Math.Clamp(App.Current.Settings.TypingSpeedCps, 30, 5000);
        // ms pro Batch = (chars / cps) * 1000
        var interBatchMs = (int)Math.Max(0, Math.Round(charsPerBatch * 1000.0 / cps));

        var size = Marshal.SizeOf<INPUT>();
        for (var offset = 0; offset < inputs.Count; offset += batchEvents)
        {
            var count = Math.Min(batchEvents, inputs.Count - offset);
            var arr = new INPUT[count];
            inputs.CopyTo(offset, arr, 0, count);
            var sent = SendInput((uint)count, arr, size);
            if (sent != count)
                Logger.Warn($"SendInput chunked sent {sent}/{count} at offset {offset}; lastErr={Marshal.GetLastWin32Error()}");
            if (offset + count < inputs.Count && interBatchMs > 0)
                Thread.Sleep(interBatchMs);
        }
    }

    /// <summary>
    /// "Tippt" Text via Clipboard + Ctrl+V — sehr schnell (4 Events statt 2×N).
    /// Sichert vorher den vollständigen Clipboard-Inhalt (Text/HTML/RTF/Bilder/
    /// Dateien/Links) und stellt ihn nach dem Paste wieder her, damit die
    /// Nutzer-Daten nicht überschrieben bleiben.
    /// </summary>
    public void PasteText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _ = PasteWithRestoreAsync(text);
    }

    private async Task PasteWithRestoreAsync(string text)
    {
        DataPackage? backup = null;
        try { backup = await SnapshotClipboardOnUiAsync(); }
        catch (Exception ex) { Logger.Warn("Clipboard snapshot failed: " + ex.Message); }

        CopyToClipboard(text);
        await Task.Delay(60);
        SendCtrlV();

        if (backup is null) return;

        // Genug Zeit lassen, damit das Ziel-Programm den Paste vollständig
        // verarbeitet hat, bevor wir den alten Inhalt zurückschreiben.
        await Task.Delay(300);
        OnUi(() =>
        {
            try { Clipboard.SetContent(backup); }
            catch (Exception ex) { Logger.Warn("Clipboard restore failed: " + ex.Message); }
        });
    }

    private Task<DataPackage?> SnapshotClipboardOnUiAsync()
    {
        var tcs = new TaskCompletionSource<DataPackage?>();
        OnUi(async () =>
        {
            try
            {
                var view = Clipboard.GetContent();
                var dp = new DataPackage();
                var any = false;

                if (view.Contains(StandardDataFormats.Text))
                {
                    try { dp.SetText(await view.GetTextAsync()); any = true; }
                    catch (Exception ex) { Logger.Warn("Clipboard backup (Text): " + ex.Message); }
                }
                if (view.Contains(StandardDataFormats.Html))
                {
                    try { dp.SetHtmlFormat(await view.GetHtmlFormatAsync()); any = true; }
                    catch (Exception ex) { Logger.Warn("Clipboard backup (Html): " + ex.Message); }
                }
                if (view.Contains(StandardDataFormats.Rtf))
                {
                    try { dp.SetRtf(await view.GetRtfAsync()); any = true; }
                    catch (Exception ex) { Logger.Warn("Clipboard backup (Rtf): " + ex.Message); }
                }
                if (view.Contains(StandardDataFormats.Bitmap))
                {
                    try { dp.SetBitmap(await view.GetBitmapAsync()); any = true; }
                    catch (Exception ex) { Logger.Warn("Clipboard backup (Bitmap): " + ex.Message); }
                }
                if (view.Contains(StandardDataFormats.StorageItems))
                {
                    try
                    {
                        var items = await view.GetStorageItemsAsync();
                        if (items is { Count: > 0 }) { dp.SetStorageItems(items); any = true; }
                    }
                    catch (Exception ex) { Logger.Warn("Clipboard backup (Files): " + ex.Message); }
                }
                if (view.Contains(StandardDataFormats.WebLink))
                {
                    try { dp.SetWebLink(await view.GetWebLinkAsync()); any = true; }
                    catch (Exception ex) { Logger.Warn("Clipboard backup (WebLink): " + ex.Message); }
                }
                if (view.Contains(StandardDataFormats.ApplicationLink))
                {
                    try { dp.SetApplicationLink(await view.GetApplicationLinkAsync()); any = true; }
                    catch (Exception ex) { Logger.Warn("Clipboard backup (AppLink): " + ex.Message); }
                }

                tcs.SetResult(any ? dp : null);
            }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    private static void SendCtrlV()
    {
        const ushort VK_CONTROL = 0x11;
        const ushort VK_V = 0x56;
        var size = Marshal.SizeOf<INPUT>();
        var inputs = new[]
        {
            MakeVk(VK_CONTROL, false),
            MakeVk(VK_V, false),
            MakeVk(VK_V, true),
            MakeVk(VK_CONTROL, true),
        };
        var sent = SendInput((uint)inputs.Length, inputs, size);
        if (sent != inputs.Length)
            Logger.Warn($"SendInput Ctrl+V sent {sent}/{inputs.Length}; lastErr={Marshal.GetLastWin32Error()}");
    }

    private static INPUT MakeUnicode(char c, bool keyUp)
    {
        var flags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0);
        // Surrogate-Pairs müssen als EXTENDEDKEY markiert werden
        if ((c & 0xFF00) == 0xE000) flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT
        {
            Type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                Ki = new KEYBDINPUT { WVk = 0, WScan = c, DwFlags = flags }
            }
        };
    }

    private static INPUT MakeVk(ushort vk, bool keyUp) => new()
    {
        Type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            Ki = new KEYBDINPUT
            {
                WVk = vk,
                WScan = 0,
                DwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
            }
        }
    };
}
