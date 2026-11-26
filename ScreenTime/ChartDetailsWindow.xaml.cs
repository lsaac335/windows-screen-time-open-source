using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScreenTime;

public partial class ChartDetailsWindow : Window
{
    public ChartDetailsWindow(string title, string[] periods, bool dark)
    {
        InitializeComponent();
        Title = title;
        Width = System.Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = System.Math.Min(Height, SystemParameters.WorkArea.Height);
        ThemeManager.Apply(this, dark);
        Periods.ItemsSource = periods;
        Periods.SelectedIndex = 0;
        Loaded += (_, _) =>
        {
            if (Periods.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem first)
                Keyboard.Focus(first);
            else Periods.Focus();
        };
    }
}
