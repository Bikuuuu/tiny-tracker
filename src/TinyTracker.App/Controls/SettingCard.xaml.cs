using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace TinyTracker.App.Controls;

// A Settings row: a title, an optional description, and its control on the right, or under them with large text.
// The texts grey out with the control, and Narrator reads the title and description as the control's name and help.
[ContentProperty(Name = nameof(Setting))]
public sealed partial class SettingCard : UserControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingCard), new PropertyMetadata("", (d, _) => ((SettingCard)d).Update()));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingCard), new PropertyMetadata("", (d, _) => ((SettingCard)d).Update()));

    public static readonly DependencyProperty SettingProperty =
        DependencyProperty.Register(nameof(Setting), typeof(Control), typeof(SettingCard), new PropertyMetadata(null, (d, e) => ((SettingCard)d).Attach(e.OldValue as Control)));

    public SettingCard()
    {
        InitializeComponent();
        Loaded += (_, _) => Greyed();
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public Control? Setting
    {
        get => (Control?)GetValue(SettingProperty);
        set => SetValue(SettingProperty, value);
    }

    private void Attach(Control? old)
    {
        if (old is not null) old.IsEnabledChanged -= OnSettingEnabledChanged;
        Host.Content = Setting;
        if (Setting is not null) Setting.IsEnabledChanged += OnSettingEnabledChanged;
        Update();
    }

    private void OnSettingEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) => Greyed();

    private void Update()
    {
        HeaderText.Text = Header;
        DescriptionText.Text = Description;
        DescriptionText.Visibility = string.IsNullOrEmpty(Description) ? Visibility.Collapsed : Visibility.Visible;
        if (Setting is not null)
        {
            AutomationProperties.SetName(Setting, Header);
            AutomationProperties.SetHelpText(Setting, Description ?? "");
        }
        Greyed();
    }

    private void Greyed() => VisualStateManager.GoToState(this, Setting is { IsEnabled: false } ? "Disabled" : "Normal", false);
}
