using System.Windows;
using BatteryCapsule.Services;

namespace BatteryCapsule.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow()
    {
        InitializeComponent();
        var app = (App)System.Windows.Application.Current;
        _settings = app.Settings.Current;

        StartWithWindowsBox.IsChecked = _settings.StartWithWindows;
        AlwaysOnTopBox.IsChecked = _settings.AlwaysOnTop;
        ShowEstimatedBox.IsChecked = _settings.ShowEstimatedPercent;
        ShowTimeBox.IsChecked = _settings.ShowTimeRemaining;
        TransparencySlider.Value = _settings.Transparency;
        ScaleSlider.Value = _settings.CapsuleScale;
        FrequencySlider.Value = _settings.UpdateFrequencySeconds;

        NotificationsEnabledBox.IsChecked = _settings.NotificationsEnabled;
        Notify20Box.IsChecked = _settings.NotifyAt20Percent;
        Notify10Box.IsChecked = _settings.NotifyAt10Percent;
        NotifyLowTimeBox.IsChecked = _settings.NotifyLowTime;
        NotifyMismatchBox.IsChecked = _settings.NotifyReportedMismatch;
        NotifyLowHealthBox.IsChecked = _settings.NotifyLowHealth;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)System.Windows.Application.Current;

        _settings.StartWithWindows = StartWithWindowsBox.IsChecked == true;
        _settings.AlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
        _settings.ShowEstimatedPercent = ShowEstimatedBox.IsChecked == true;
        _settings.ShowTimeRemaining = ShowTimeBox.IsChecked == true;
        _settings.Transparency = TransparencySlider.Value;
        _settings.CapsuleScale = ScaleSlider.Value;
        _settings.UpdateFrequencySeconds = (int)FrequencySlider.Value;

        _settings.NotificationsEnabled = NotificationsEnabledBox.IsChecked == true;
        _settings.NotifyAt20Percent = Notify20Box.IsChecked == true;
        _settings.NotifyAt10Percent = Notify10Box.IsChecked == true;
        _settings.NotifyLowTime = NotifyLowTimeBox.IsChecked == true;
        _settings.NotifyReportedMismatch = NotifyMismatchBox.IsChecked == true;
        _settings.NotifyLowHealth = NotifyLowHealthBox.IsChecked == true;

        AutoStartService.SetEnabled(_settings.StartWithWindows);
        app.Settings.Save();
        app.ApplyUpdateFrequency(_settings.UpdateFrequencySeconds);

        if (app.Capsule != null)
        {
            app.Capsule.Topmost = _settings.AlwaysOnTop;
            app.Capsule.ApplyLiveSettings();
        }

        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void ResetLearning_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)System.Windows.Application.Current;
        app.Engine.ResetLearning();
        System.Windows.MessageBox.Show(this, "Battery learning data has been reset.", "Battery Capsule",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
