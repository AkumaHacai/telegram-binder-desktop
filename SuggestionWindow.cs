using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace TelegramBinder;

sealed partial class SuggestionWindow : Window
{
    private const int RowHeight = 43;
    private nint handle;
    internal int Selected => list.SelectedIndex;
    internal event Action<int>? Chosen;

    internal SuggestionWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        handle = new WindowInteropHelper(this).Handle;
        SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000 | 0x80);
        HwndSource.FromHwnd(handle).AddHook((nint _, int msg, nint wp, nint lp, ref bool handled) =>
        {
            handled = msg == 0x0021;
            return handled ? 3 : 0;
        });
    }

    internal void Present(Choice[] choices, Rect bounds, nint target)
    {
        list.Items.Clear();
        for (var i = 0; i < choices.Length; i++)
        {
            var choice = choices[i];
            var index = i;
            var title = new TextBlock { Text = choice.Title, FontSize = 13, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis };
            var detail = new TextBlock { Text = choice.Detail, FontSize = 12, Margin = new Thickness(0, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            var stack = new StackPanel();
            stack.Children.Add(title);
            stack.Children.Add(detail);
            var item = new ListBoxItem { Content = stack, Height = RowHeight };
            item.PreviewMouseLeftButtonUp += (_, e) => { e.Handled = true; Chosen?.Invoke(index); };
            list.Items.Add(item);
        }
        list.SelectedIndex = 0;
        list.ScrollIntoView(list.SelectedItem);
        Place(bounds, target);
        if (!IsVisible)
        {
            Show();
            Place(bounds, target);
        }
    }

    internal void Step(int delta)
    {
        if (list.Items.Count == 0) return;
        list.SelectedIndex = (Selected + delta + list.Items.Count) % list.Items.Count;
        list.ScrollIntoView(list.SelectedItem);
    }

    internal void Place(Rect bounds, nint target)
    {
        var scale = Math.Max(96, (int)GetDpiForWindow(target)) / 96d;
        var area = Forms.Screen.FromPoint(new System.Drawing.Point((int)bounds.Left, (int)bounds.Top)).WorkingArea;
        var width = Math.Min((int)(Width * scale), area.Width);
        var frameHeight = frame.Padding.Top + frame.Padding.Bottom + frame.BorderThickness.Top + frame.BorderThickness.Bottom;
        var height = Math.Min((int)Math.Ceiling((Math.Min(list.Items.Count, 6) * RowHeight + frameHeight) * scale), area.Height);
        var x = Math.Clamp((int)bounds.Left, area.Left, area.Right - width);
        var above = (int)bounds.Top - height - 7;
        var y = above >= area.Top ? above : Math.Clamp((int)bounds.Bottom + 7, area.Top, area.Bottom - height);
        if (handle == 0)
        {
            Height = height / scale;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = x / scale;
            Top = y / scale;
        }
        else SetWindowPos(handle, -1, x, y, width, height, 0x0010 | 0x0200);
    }

    [DllImport("user32", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint h, int index);
    [DllImport("user32", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint h, int index, int value);
    [DllImport("user32")] private static extern uint GetDpiForWindow(nint h);
    [DllImport("user32")] private static extern bool SetWindowPos(nint h, nint insertAfter, int x, int y, int width, int height, uint flags);
}
