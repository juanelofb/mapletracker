using MapleTracker.App.Services;
using System.Windows.Threading;

namespace MapleTracker.App;

public partial class TransparencyControl : UserControl
{
    private TrackerController? controller;
    private Action? persist;
    private bool syncing;
    private readonly DispatcherTimer saveDelay = new() { Interval = TimeSpan.FromMilliseconds(500) };
    public TransparencyControl()
    {
        InitializeComponent();
        saveDelay.Tick += (_, _) => { saveDelay.Stop(); persist?.Invoke(); };
        Unloaded += (_, _) => { saveDelay.Stop(); if (controller != null) controller.AppearanceChanged -= Sync; };
    }
    public void Initialize(TrackerController value, Action save)
    {
        controller = value; persist = save; controller.AppearanceChanged += Sync; Sync();
    }
    private void Sync()
    {
        if (controller == null) return;
        syncing = true; TransparencySlider.Value = (1 - controller.Settings.Opacity) * 100;
        PercentText.Text = $"{TransparencySlider.Value:0}%"; syncing = false;
    }
    private void ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PercentText != null) PercentText.Text = $"{e.NewValue:0}%";
        if (syncing || controller == null) return;
        controller.SetOpacity(1 - e.NewValue / 100); saveDelay.Stop(); saveDelay.Start();
    }
}
