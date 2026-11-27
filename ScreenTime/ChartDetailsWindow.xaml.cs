using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;

namespace ScreenTime;

public sealed class ChartDetailItem
{
    public string Heading { get; }
    public string Values { get; }
    public string Subtitle { get; }
    public double BarRatio { get; }
    public bool IsMuted { get; }
    public bool IsFuture { get; }

    public double RowOpacity => IsFuture ? 0.45 : IsMuted ? 0.68 : 1.0;
    public Visibility BarVisibility => BarRatio > 0.001 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SubtitleVisibility => string.IsNullOrEmpty(Subtitle) ? Visibility.Collapsed : Visibility.Visible;
    public GridLength BarFillLength => new(Math.Clamp(BarRatio, 0, 1), GridUnitType.Star);
    public GridLength BarRestLength => new(Math.Max(0, 1 - Math.Clamp(BarRatio, 0, 1)), GridUnitType.Star);
    public string AccessibleText => string.IsNullOrEmpty(Subtitle) ? $"{Heading}，{Values}" : $"{Heading}，{Values}，{Subtitle}";

    public ChartDetailItem(string heading, string values, string subtitle, double barRatio, bool isMuted, bool isFuture)
    {
        Heading = heading;
        Values = values;
        Subtitle = subtitle;
        BarRatio = barRatio;
        IsMuted = isMuted;
        IsFuture = isFuture;
    }
}

public partial class ChartDetailsWindow : Window
{
    public ChartDetailsWindow(string title, ChartDetailItem[] periods, bool dark)
    {
        InitializeComponent();
        Title = title;
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            WindowChrome.GetWindowChrome(this).GlassFrameThickness = new Thickness(1);
            SourceInitialized += (_, _) =>
            {
                int pref = 2;
                DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref pref, sizeof(int));
            };
        }
        ThemeManager.Apply(this, dark);
        Periods.ItemsSource = periods;
        Periods.SelectedIndex = 0;
        CountCaption.Text = periods.Length > 1 ? $"共 {periods.Length} 个时段 · 方向键可浏览" : "";
        Loaded += (_, _) =>
        {
            if (Periods.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem first)
                Keyboard.Focus(first);
            else Periods.Focus();
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
