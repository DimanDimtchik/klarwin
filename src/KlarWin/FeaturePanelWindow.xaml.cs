using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KlarWin;

public partial class FeaturePanelWindow : Window
{
    public FeaturePanelWindow()
    {
        InitializeComponent();
    }

    public StackPanel HeaderStackPanel => HeaderStack;
    public StackPanel ButtonHostPanel => ButtonHost;
    public StackPanel ContentHostPanel => ContentHost;
    public Border ExtraHostBorder => ExtraHost;

    public void AttachHeader(params UIElement[] elements)
    {
        HeaderStack.Children.Clear();
        foreach (var element in elements)
        {
            Detach(element);
            HeaderStack.Children.Add(element);
        }
    }

    public void AttachButtons(params UIElement[] elements)
    {
        ButtonHost.Children.Clear();
        foreach (var element in elements)
        {
            Detach(element);
            if (element is FrameworkElement fe)
            {
                fe.Margin = new Thickness(10, 0, 0, 0);
            }

            ButtonHost.Children.Add(element);
        }
    }

    public void AttachContent(params UIElement[] elements)
    {
        ContentHost.Children.Clear();
        foreach (var element in elements)
        {
            Detach(element);
            ContentHost.Children.Add(element);
        }
    }

    public void AttachExtra(UIElement? element)
    {
        ExtraHost.Child = null;
        ExtraHost.Visibility = Visibility.Collapsed;
        if (element is null) return;
        Detach(element);
        ExtraHost.Child = element;
        ExtraHost.Visibility = Visibility.Visible;
    }

    private static void Detach(UIElement element)
    {
        if (element is not FrameworkElement fe) return;
        var parent = VisualTreeHelper.GetParent(fe);
        switch (parent)
        {
            case Panel panel:
                panel.Children.Remove(fe);
                break;
            case Border border:
                if (ReferenceEquals(border.Child, fe)) border.Child = null;
                break;
            case Decorator decorator:
                if (ReferenceEquals(decorator.Child, fe)) decorator.Child = null;
                break;
        }
    }

    private void OnScrollViewerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer) return;
        scrollViewer.ScrollToVerticalOffset(Math.Max(0, scrollViewer.VerticalOffset - e.Delta));
        e.Handled = true;
    }
}
