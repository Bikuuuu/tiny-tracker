using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TinyTracker.Presentation;

namespace TinyTracker.App.Controls;

// A notice from a page's list, as a banner with its buttons and a Details button when it has a code. It shows its DataContext.
public sealed partial class NoticeBar : InfoBar
{
    public NoticeBar()
    {
        IsOpen = true;
        DataContextChanged += (_, _) => Show();
    }

    private void Show()
    {
        if (DataContext is not Notice notice) return;
        Title = notice.Title;
        Message = notice.Message;
        Severity = Ui.Severity(notice.Severity);
        IsClosable = notice.Closable;
        MainButton = null;
        Content = notice.Actions.Count > 0 || notice.Details is not null ? Buttons(notice.Actions, notice.Details) : null;
    }

    // The notice's main button, such as Track.
    public Button? MainButton { get; private set; }

    // Two buttons side by side while they fit (spec §4.8), and Details on a line of its own.
    private StackPanel Buttons(IReadOnlyList<NoticeAction> actions, string? details)
    {
        var buttons = actions.Select(Button).ToList();
        MainButton = buttons.FirstOrDefault();
        if (MainButton is not null) MainButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        if (buttons.Count > 1) panel.Children.Add(new FitOrStack { Spacing = 8, Children = { buttons[0], buttons[1] } });
        foreach (var button in buttons.Skip(buttons.Count > 1 ? 2 : 0)) panel.Children.Add(button);
        if (details is not null) panel.Children.Add(DetailsButton(details));
        return panel;
    }

    private static Button Button(NoticeAction action)
    {
        var button = new Button { Content = action.Text };
        if (action.Name is { } name) AutomationProperties.SetName(button, name);
        button.Click += (_, _) => action.Run();
        return button;
    }

    private static Button DetailsButton(string details) => new()
    {
        Content = Strings.Details,
        Style = (Style)Application.Current.Resources["LinkButton"],
        Flyout = new Flyout { Content = new TextBlock { Text = details, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 } },
    };
}
