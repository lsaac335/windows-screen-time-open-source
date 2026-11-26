using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ScreenTime;

internal sealed class TrayMenu : ContextMenuStrip
{
    private readonly Func<bool> _isDark;
    private readonly Font _iconFont;

    public TrayMenu(Func<bool> isDark)
    {
        _isDark = isDark;
        var iconFamily = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        _iconFont = new Font(iconFamily, 12f, FontStyle.Regular, GraphicsUnit.Point);
        ShowImageMargin = false;
        Renderer = new MenuRenderer(this);
    }

    public CommandItem AddCommand(string text, string glyph, EventHandler onClick)
    {
        var item = new CommandItem(this, text, glyph, onClick);
        Items.Add(item);
        return item;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var size = base.GetPreferredSize(proposedSize);
        // The dropdown measures its text independently from each item's preferred size.
        return new Size(Math.Max(Scale(216) + Padding.Horizontal, size.Width + Scale(54)), size.Height);
    }

    protected override void OnOpening(CancelEventArgs e)
    {
        var renderer = (MenuRenderer)Renderer;
        renderer.RefreshPalette(_isDark());
        base.OnOpening(e);
        Invalidate();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= 0 || Height <= 0) return;
        using var shape = RoundedRectangle(ClientRectangle, Scale(10));
        Region = new Region(shape);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _iconFont.Dispose();
    }

    private int Scale(int pixels) => (pixels * DeviceDpi + 48) / 96;

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal sealed class CommandItem : ToolStripMenuItem
    {
        private readonly TrayMenu _menu;

        public CommandItem(TrayMenu menu, string text, string glyph, EventHandler onClick) : base(text, null, onClick)
        {
            _menu = menu;
            Glyph = glyph;
            ShowShortcutKeys = false;
        }

        public string Glyph { get; set; }

        public override Size GetPreferredSize(Size constrainingSize)
        {
            var size = base.GetPreferredSize(constrainingSize);
            return new Size(Math.Max(_menu.Scale(216), size.Width + _menu.Scale(54)),
                Math.Max(_menu.Scale(34), size.Height));
        }
    }

    private sealed record Palette(Color Surface, Color Border, Color Text, Color Hover, Color Pressed, Color Divider, Color Check)
    {
        public static readonly Palette Light = new(Rgb(0xFBFAFD), Rgb(0xD9D5DF), Rgb(0x262331),
            Rgb(0xEEEBF4), Rgb(0xE5DFEF), Rgb(0xE4DFE9), Rgb(0x6241A7));
        public static readonly Palette Dark = new(Rgb(0x24222B), Rgb(0x484450), Rgb(0xF5F2FA),
            Rgb(0x363240), Rgb(0x413B4D), Rgb(0x423E4B), Rgb(0xD3BAFF));

        private static Color Rgb(int value) => Color.FromArgb(unchecked((int)0xFF000000) | value);
    }

    private sealed class MenuRenderer : ToolStripRenderer
    {
        private readonly TrayMenu _menu;
        private Palette _palette = Palette.Light;
        private bool _highContrast;

        public MenuRenderer(TrayMenu menu) => _menu = menu;

        public void RefreshPalette(bool isDark)
        {
            _highContrast = SystemInformation.HighContrast;
            _palette = _highContrast
                ? new Palette(SystemColors.Menu, SystemColors.MenuText, SystemColors.MenuText,
                    SystemColors.Highlight, SystemColors.Highlight, SystemColors.MenuText, SystemColors.MenuText)
                : isDark ? Palette.Dark : Palette.Light;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var fill = new SolidBrush(_palette.Surface);
            e.Graphics.FillRectangle(fill, _menu.ClientRectangle);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var bounds = _menu.ClientRectangle;
            if (bounds.Width <= 1 || bounds.Height <= 1) return;
            bounds.Width--;
            bounds.Height--;
            using var shape = RoundedRectangle(bounds, _menu.Scale(10));
            using var pen = new Pen(_palette.Border);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawPath(pen, shape);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item is not CommandItem item) return;
            var row = new Rectangle(Point.Empty, item.Size);
            if (item.Selected || item.Pressed)
            {
                var highlight = Rectangle.Inflate(row, -_menu.Scale(5), -_menu.Scale(1));
                if (highlight.Width > 0 && highlight.Height > 0)
                {
                    using var shape = RoundedRectangle(highlight, _menu.Scale(6));
                    using var fill = new SolidBrush(item.Pressed ? _palette.Pressed : _palette.Hover);
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    e.Graphics.FillPath(fill, shape);
                }
            }

            int iconWidth = _menu.Scale(24);
            var iconBounds = new Rectangle(_menu.Scale(10), 0, iconWidth, row.Height);
            const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping |
                TextFormatFlags.PreserveGraphicsTranslateTransform;
            var color = ItemTextColor(item);
            TextRenderer.DrawText(e.Graphics, item.Glyph, _menu._iconFont, iconBounds, color, flags);
            if (item.Checked)
            {
                iconBounds.X = row.Right - _menu.Scale(34);
                TextRenderer.DrawText(e.Graphics, "\uE73E", _menu._iconFont, iconBounds,
                    _highContrast ? color : _palette.Check, flags);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            int left = _menu.Scale(42);
            e.TextRectangle = new Rectangle(left, 0, Math.Max(0, e.Item.Width - left - _menu.Scale(34)), e.Item.Height);
            e.TextColor = ItemTextColor(e.Item);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int inset = _menu.Scale(12);
            using var pen = new Pen(_palette.Divider);
            int middle = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, inset, middle, e.Item.Width - inset, middle);
        }

        private Color ItemTextColor(ToolStripItem item)
        {
            if (!item.Enabled) return SystemColors.GrayText;
            return _highContrast && (item.Selected || item.Pressed) ? SystemColors.HighlightText : _palette.Text;
        }
    }
}
