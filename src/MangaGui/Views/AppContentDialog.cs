using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace MangaGui.Views;

/// <summary>
/// App base for in-window dialogs. WPF-UI 3.0.5's <see cref="ContentDialog"/> has no keyboard
/// handling of its own; its control template wires the footer buttons with IsDefault/IsCancel,
/// so Enter/Esc already work natively while the footer is visible. Esc is additionally handled
/// here unconditionally (same <see cref="ContentDialogResult.None"/> result), and for dialogs that
/// hide the footer (the time-expired dialog) Enter is routed to <see cref="OnEnterPressed"/>.
/// </summary>
public class AppContentDialog : ContentDialog
{
    public AppContentDialog()
    {
        // Implicit styles are looked up by exact runtime type, so a bare `new AppContentDialog()`
        // (the simple confirm / error dialogs created in code) would render unstyled; pin the base
        // chrome. Derived XAML dialogs re-declare the same BasedOn style in their own resources.
        SetResourceReference(StyleProperty, typeof(ContentDialog));
    }

    public AppContentDialog(ContentPresenter? dialogHost)
        : base(dialogHost)
    {
        SetResourceReference(StyleProperty, typeof(ContentDialog));
    }

    /// <summary>
    /// Closes the dialog with <see cref="ContentDialogResult.None"/> — the billing-safe result,
    /// identical to pressing Esc inside the dialog (spec 9.1-5/7). Used by
    /// <see cref="DialogKeyboardGuard"/> when Esc arrives while keyboard focus is outside the
    /// dialog's visual tree.
    /// </summary>
    public void HideAsCancelled() => Hide(ContentDialogResult.None);

    protected override Size MeasureOverride(Size availableSize)
    {
        // WPF-UI 3.0.5 derives DialogWidth/DialogHeight from the template root's DesiredSize.
        // When the content's natural (unwrapped) width makes DialogWidth exceed DialogMaxWidth,
        // ResizeWidth() clamps the width and then rewrites DialogHeight from a margin-inclusive
        // DesiredSize — leaving ~70 DIP of dead space at the bottom of the card. Capping the
        // content element's MaxWidth so it wraps during the first measure keeps DialogWidth
        // under the cap and the ResizeWidth path is never taken.
        ApplyContentWidthCap();
        Size desiredSize = base.MeasureOverride(availableSize);

        // Independently, GetNewDialogSize() adds the dialog Padding back onto the measured
        // height, so the card's MaxHeight is always ~Padding taller than what it needs even on
        // the non-clamped path. Feeding the card border's own (margin-stripped) DesiredSize
        // back into DialogHeight removes that slack — the footer Padding lives inside the card
        // and is unaffected. Converges over at most one extra measure pass.
        NormalizeDialogHeight();
        return desiredSize;
    }

    // 3.0.5 template chrome between the card border's inner edge and the content presenter:
    // the "24,10" inner grid margin plus the PassiveScrollViewer's "0,0,12,0" padding.
    private const double ContentChromeWidth = 24 + 24 + 12;

    private FrameworkElement? _widthCappedContent;

    private void ApplyContentWidthCap()
    {
        if (Content is not FrameworkElement content || double.IsInfinity(DialogMaxWidth))
        {
            return;
        }

        // GetNewDialogSize() also feeds Padding back into DialogWidth, so the cap must leave
        // room for it: desired = content + chrome + border + padding <= DialogMaxWidth.
        double cap = DialogMaxWidth
            - Padding.Left - Padding.Right
            - BorderThickness.Left - BorderThickness.Right
            - ContentChromeWidth;
        if (cap <= 0)
        {
            return;
        }

        if (ReferenceEquals(_widthCappedContent, content))
        {
            content.MaxWidth = cap;
            return;
        }

        if (content.ReadLocalValue(MaxWidthProperty) != DependencyProperty.UnsetValue)
        {
            return; // the dialog's own XAML caps the content — respect it
        }

        content.MaxWidth = cap;
        _widthCappedContent = content;
    }

    private void NormalizeDialogHeight()
    {
        if (VisualTreeHelper.GetChildrenCount(this) == 0
            || VisualTreeHelper.GetChild(this, 0) is not { } smokeGrid
            || VisualTreeHelper.GetChildrenCount(smokeGrid) == 0
            || VisualTreeHelper.GetChild(smokeGrid, 0) is not Border card)
        {
            return;
        }

        // DesiredSize is margin-inclusive, so strip the dialog margin to get the height the
        // card's content actually needs.
        double height = Math.Min(
            card.DesiredSize.Height - card.Margin.Top - card.Margin.Bottom,
            DialogMaxHeight);
        if (!double.IsInfinity(height) && height > 0 && Math.Abs(DialogHeight - height) > 0.01)
        {
            SetCurrentValue(DialogHeightProperty, height);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            // Esc always maps to None — never to a billing-changing action (spec 9.1-5/7).
            HideAsCancelled();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && !IsFooterVisible && !OriginatesInConsumingControl(e))
        {
            OnEnterPressed();
            e.Handled = true;
        }
    }

    private static bool OriginatesInConsumingControl(KeyEventArgs e)
        => e.OriginalSource is TextBoxBase or ButtonBase;

    /// <summary>
    /// Enter pressed while the footer is hidden. The default does nothing; dialogs that hide the
    /// footer override this to trigger their billing-safe accent action.
    /// </summary>
    protected virtual void OnEnterPressed()
    {
    }
}
