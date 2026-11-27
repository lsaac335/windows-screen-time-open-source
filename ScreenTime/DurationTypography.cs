using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace ScreenTime;

internal static class DurationTypography
{
    public static void Set(TextBlock text, string duration)
    {
        Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
        if (text.Text == duration) return;

        text.Text = duration;
        text.Inlines.Clear();
        for (int start = 0, end; start < duration.Length; start = end)
        {
            bool isNumber = char.IsDigit(duration[start]);
            end = start + 1;
            while (end < duration.Length && char.IsDigit(duration[end]) == isNumber)
                end++;
            var run = new Run(duration[start..end]);
            if (!isNumber && duration[start] != '—')
            {
                run.FontSize = text.FontSize * .48;
                run.FontWeight = FontWeights.Normal;
            }
            text.Inlines.Add(run);
        }
    }
}
