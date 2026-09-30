using KnowledgeCapture.Core.Services.Anonymization;
using KnowledgeCapture.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace KnowledgeCapture.Views;

/// <summary>Pure view helpers for x:Bind function bindings (no converters needed).</summary>
public static class Bind
{
    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public static Visibility Visible(bool v) => v ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Collapsed(bool v) => v ? Visibility.Collapsed : Visibility.Visible;
    public static bool Not(bool v) => !v;

    public static HorizontalAlignment Align(bool isUser) => isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public static Brush BubbleBrush(bool isUser) => Res(isUser ? "AccentFillColorDefaultBrush" : "CardBackgroundFillColorDefaultBrush");
    public static Brush BubbleForeground(bool isUser) => Res(isUser ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");

    public static string SlotGlyph(bool covered) => covered ? "" : ""; // CheckMark / circle outline
    public static Brush SlotBrush(bool covered) => Res(covered ? "SystemFillColorSuccessBrush" : "TextFillColorTertiaryBrush");
    public static double SlotOpacity(bool covered) => covered ? 1.0 : 0.7;

    public static Brush StatusBrush(ModelStatus s) => Res(s switch
    {
        ModelStatus.Ready => "SystemFillColorSuccessBrush",
        ModelStatus.Down => "SystemFillColorCriticalBrush",
        _ => "SystemFillColorCautionBrush",
    });

    public static InfoBarSeverity StorageSeverity(bool warning) => warning ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
    public static InfoBarSeverity ResultSeverity(bool error) => error ? InfoBarSeverity.Error : InfoBarSeverity.Success;
}

/// <summary>TextBlock attached property: sets the text and highlights every &lt;TYPE_n&gt; placeholder.</summary>
public static class Highlight
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Highlight), new PropertyMetadata("", OnTextChanged));

    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string value) => d.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        var text = e.NewValue as string ?? "";
        tb.TextHighlighters.Clear();
        tb.Text = text;
        var matches = EntityTypes.PlaceholderRegex.Matches(text);
        if (matches.Count == 0) return;
        var h = new TextHighlighter
        {
            Background = (Brush)Application.Current.Resources["AccentFillColorSecondaryBrush"],
            Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"],
        };
        foreach (System.Text.RegularExpressions.Match m in matches)
            h.Ranges.Add(new TextRange { StartIndex = m.Index, Length = m.Length });
        tb.TextHighlighters.Add(h);
    }
}
