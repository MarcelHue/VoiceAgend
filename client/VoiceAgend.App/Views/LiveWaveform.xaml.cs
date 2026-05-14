using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace VoiceAgend.App.Views;

public sealed partial class LiveWaveform : UserControl
{
    private readonly Rectangle[] _bars;
    private readonly DispatcherQueueTimer _timer;
    private double _phase;

    public int BarCount { get; }

    /// <summary>Wenn false: statische, leise gedimmte Bars (idle).</summary>
    public bool IsActive { get; set; } = true;

    public LiveWaveform() : this(80) { }

    public LiveWaveform(int barCount)
    {
        InitializeComponent();
        BarCount = barCount;
        _bars = new Rectangle[BarCount];

        for (var i = 0; i < BarCount; i++)
        {
            BarsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var r = new Rectangle
            {
                Margin = new Thickness(1, 0, 1, 0),
                RadiusX = 2, RadiusY = 2,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = (Brush)Application.Current.Resources["VAAccentBrush"],
            };
            Grid.SetColumn(r, i);
            BarsHost.Children.Add(r);
            _bars[i] = r;
        }

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(70);
        _timer.Tick += (_, _) => Tick();

        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
        SizeChanged += (_, _) => UpdateHeights();
    }

    /// <summary>Beim Theme-Wechsel von außen aufrufen, damit die Bar-Fills aktualisieren.</summary>
    public void RefreshBrush()
    {
        var b = (Brush)Application.Current.Resources["VAAccentBrush"];
        foreach (var r in _bars) r.Fill = b;
    }

    private void Tick()
    {
        _phase += 0.18;
        UpdateHeights();
    }

    private void UpdateHeights()
    {
        var h = Math.Max(8, ActualHeight);
        for (var i = 0; i < _bars.Length; i++)
        {
            double v;
            if (IsActive)
            {
                var noise = Math.Abs(Math.Sin(i * 0.36 + _phase) * Math.Cos(i * 0.21 + _phase * 0.4));
                var env = Math.Sin(((double)i / _bars.Length) * Math.PI);
                v = Math.Min(1, (0.35 + 0.55 * noise) * (0.5 + env * 0.8));
            }
            else
            {
                var noise = Math.Abs(Math.Sin(i * 0.7) * Math.Cos(i * 0.31));
                v = 0.15 + 0.18 * noise;
            }
            var bar = _bars[i];
            bar.Height = Math.Max(6, v * h);
            bar.Opacity = IsActive ? (0.55 + v * 0.45) : 0.35;
        }
    }
}
