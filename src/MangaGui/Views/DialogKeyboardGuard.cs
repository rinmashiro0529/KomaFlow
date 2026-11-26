using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace MangaGui.Views;

/// <summary>
/// Esc routing while an in-window dialog is shown. WPF-UI 3.0.5 <see cref="ContentDialog"/>
/// handles keys only when keyboard focus is inside its own visual tree; while a dialog is up
/// the page content is disabled, so focus can legitimately sit on the window chrome (e.g. after
/// a deactivate/reactivate or a title-bar click). In that case this guard forwards Esc as the
/// billing-safe <see cref="ContentDialogResult.None"/>. When focus IS inside the dialog its own
/// <c>OnKeyDown</c> handles Esc — the guard steps aside to avoid a double <c>Hide</c>.
/// </summary>
public static class DialogKeyboardGuard
{
    /// <returns>True when the forwarded Esc was delivered to the hosted dialog.</returns>
    public static bool TryHandleEscape(ContentPresenter host)
    {
        if (host.Content is AppContentDialog dialog && !dialog.IsKeyboardFocusWithin)
        {
            dialog.HideAsCancelled();
            return true;
        }

        return false;
    }
}
