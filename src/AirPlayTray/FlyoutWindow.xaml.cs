using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AirPlayTray;

public partial class FlyoutWindow : Window
{
    private readonly FlyoutViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private DateTime _hiddenAt;

    public FlyoutWindow(FlyoutViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        ApplyTheme();
        // Taller than this and the content scrolls instead
        MaxHeight = SystemParameters.WorkArea.Height;

        _refreshTimer.Tick += async (_, _) => await _viewModel.RefreshAsync();
        Deactivated += (_, _) =>
        {
            Hide();
            _hiddenAt = DateTime.UtcNow;
        };
        IsVisibleChanged += (_, _) => _refreshTimer.IsEnabled = IsVisible;
        SizeChanged += (_, _) => PlaceAboveTray();
    }

    public async void Toggle()
    {
        // Clicking the tray icon deactivates the flyout first, which already hid it
        if (IsVisible || DateTime.UtcNow - _hiddenAt < TimeSpan.FromMilliseconds(300))
        {
            Hide();
            return;
        }

        Show();
        PlaceAboveTray();
        PlayOpenAnimation();
        Activate();
        await _viewModel.RefreshAsync();
    }

    // Tones follow Windows 11's own flyouts; the accent is the user's Windows accent colour
    public void ApplyTheme()
    {
        var light = WindowsSettings.AppsUseLightTheme;
        Resources["Text"] = Brush(light ? "#1B1B1B" : "#FFFFFF");
        Resources["SubtleText"] = Brush(light ? "#5C5C5C" : "#A8A8A8");
        var accent = WindowsSettings.AccentColor(light) ?? (Color)ColorConverter.ConvertFromString(light ? "#005FB8" : "#4CC2FF");
        Resources["Accent"] = new SolidColorBrush(accent);
        Resources["Surface"] = Brush(light ? "#F3F3F3" : "#202020");
        Resources["Divider"] = Brush(light ? "#E5E5E5" : "#333333");
        Resources["ButtonBackground"] = Brush(light ? "#FBFBFB" : "#373737");
        Resources["ButtonBorder"] = Brush(light ? "#D6D6D6" : "#454545");
        Resources["Track"] = Brush(light ? "#C4C4C4" : "#4A4A4A");
        Resources["Card"] = Brush(light ? "#FFFFFF" : "#2D2D2D");
    }

    private static SolidColorBrush Brush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    // Rises and fades in like Windows 11's Quick Settings
    private void PlayOpenAnimation()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(180);
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        RootOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, duration) { EasingFunction = ease });
    }

    private void PlaceAboveTray()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth;
        Top = workArea.Bottom - ActualHeight;
    }

    private void NewGroupButton_Click(object sender, RoutedEventArgs e) => _viewModel.StartNewGroup();

    private void EditGroupButton_Click(object sender, RoutedEventArgs e) =>
        _viewModel.StartEditGroup((GroupViewModel)((FrameworkElement)sender).DataContext);

    private void SaveGroupButton_Click(object sender, RoutedEventArgs e) => _viewModel.SaveGroup();

    private void CancelGroupButton_Click(object sender, RoutedEventArgs e) => _viewModel.CancelGroupEdit();

    private void DeleteGroupButton_Click(object sender, RoutedEventArgs e) => _viewModel.DeleteGroup(_viewModel.EditingGroupName!);

    private void DeleteGroupMenu_Click(object sender, RoutedEventArgs e) =>
        _viewModel.DeleteGroup(((GroupViewModel)((FrameworkElement)sender).DataContext).Name);

    private async void PairButton_Click(object sender, RoutedEventArgs e) =>
        await ((DeviceViewModel)((FrameworkElement)sender).DataContext).SubmitPinAsync();

    private async void PinBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            await ((DeviceViewModel)((FrameworkElement)sender).DataContext).SubmitPinAsync();
    }
}
