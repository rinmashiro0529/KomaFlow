using System.Windows;
using MangaGui.Models;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MangaGui.Services;

/// <summary>
/// Manages Light / Dark / Follow-System theme switching on top of WPF-UI's native theme
/// infrastructure: <see cref="SystemThemeWatcher"/> for follow-system, <see cref="ApplicationThemeManager"/>
/// for explicit choices, and <see cref="ApplicationAccentColorManager"/> for the system accent.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private readonly SessionStore _store;
    private readonly Func<bool> _detectSystemDarkMode;
    private Window? _watchedWindow;
    private bool _disposed;

    public ThemePreference Preference { get; private set; } = ThemePreference.System;
    public bool IsDarkMode { get; private set; }

    public event Action<ThemePreference, bool>? ThemeChanged;

    public ThemeService(SessionStore store, Func<bool>? detectSystemDarkMode = null)
    {
        _store = store;
        _detectSystemDarkMode = detectSystemDarkMode ?? ReadWindowsSystemDarkMode;
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
    }

    public void InitializeFromSettings()
    {
        var saved = _store.LoadSettings().Theme;
        var pref = Enum.TryParse<ThemePreference>(saved, ignoreCase: true, out var parsed)
            ? parsed
            : ThemePreference.System;
        Apply(pref, persist: false);
    }

    public void Apply(ThemePreference preference, bool persist = true)
    {
        Preference = preference;
        bool useDark = preference switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => _detectSystemDarkMode()
        };

        IsDarkMode = useDark;

        if (persist)
        {
            var settings = _store.LoadSettings();
            settings.Theme = preference.ToString();
            _store.TrySaveSettings(settings);
        }

        ApplyToWpfResources(useDark);
        ThemeChanged?.Invoke(Preference, IsDarkMode);
    }

    public static string FormatPreferenceLabel(ThemePreference preference, bool isDark) => preference switch
    {
        ThemePreference.System => isDark ? "跟随系统 · 深色" : "跟随系统 · 浅色",
        ThemePreference.Dark => "深色模式",
        ThemePreference.Light => "浅色模式",
        _ => "跟随系统"
    };

    /// <summary>
    /// Gives the service a concrete window to watch once it exists (the main window is created after
    /// startup theme initialization). Subsequent applies use <see cref="SystemThemeWatcher"/> in
    /// follow-system mode so Mica and live OS theme changes are handled natively.
    /// </summary>
    public void Attach(Window window)
    {
        _watchedWindow = window;
        ApplyToWpfResources(IsDarkMode);
    }

    private void ApplyToWpfResources(bool useDark)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        void Update()
        {
            try
            {
                var window = _watchedWindow ?? app.MainWindow;
                if (window is not null && Preference == ThemePreference.System)
                {
                    // Watcher applies theme + Mica backdrop + system accent and keeps following
                    // OS theme changes by itself.
                    _watchedWindow = window;
                    SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, true);
                }
                else
                {
                    if (_watchedWindow is not null)
                    {
                        SystemThemeWatcher.UnWatch(_watchedWindow);
                    }
                    ApplicationThemeManager.Apply(
                        useDark ? ApplicationTheme.Dark : ApplicationTheme.Light,
                        WindowBackdropType.Mica,
                        updateAccent: true);
                }

                ApplicationAccentColorManager.ApplySystemAccent();
            }
            catch
            {
                // Safe fallback when running in headless test Application host.
            }
        }

        if (app.Dispatcher.CheckAccess())
        {
            Update();
        }
        else
        {
            app.Dispatcher.Invoke(Update);
        }
    }

    private void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || Preference != ThemePreference.System)
        {
            return;
        }

        bool systemDark = _detectSystemDarkMode();
        if (systemDark != IsDarkMode)
        {
            // The watcher already refreshed resources; only our IsDarkMode snapshot and label
            // subscribers need updating here.
            IsDarkMode = systemDark;
            ThemeChanged?.Invoke(Preference, IsDarkMode);
        }
    }

    public static bool ReadWindowsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
            {
                return value == 0;
            }
        }
        catch
        {
            // Default to light if registry is inaccessible.
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        if (_watchedWindow is not null)
        {
            try { SystemThemeWatcher.UnWatch(_watchedWindow); }
            catch { }
            _watchedWindow = null;
        }
    }
}
