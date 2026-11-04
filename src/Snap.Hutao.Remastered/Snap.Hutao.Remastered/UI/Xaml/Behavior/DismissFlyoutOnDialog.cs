// Copyright (c) Snap Hutao RP. All rights reserved.
// Licensed under the MIT license.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Snap.Hutao.Remastered.UI.Xaml.Behavior;

public static class DismissFlyoutOnDialog
{
    private static readonly HashSet<Flyout> OpenFlyouts = [];

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(DismissFlyoutOnDialog), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(Flyout flyout) => (bool)flyout.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(Flyout flyout, bool value) => flyout.SetValue(IsEnabledProperty, value);

    public static async Task HideAsync(XamlRoot xamlRoot)
    {
        List<(Flyout Flyout, EventHandler<object> Closed, Task Completion)> pending = [];
        foreach (Flyout flyout in OpenFlyouts.ToArray())
        {
            // FlyoutBase.XamlRoot need not be assigned when ShowAt obtains the
            // root from its target. Use the actual host instead.
            XamlRoot? hostRoot = flyout.Target?.XamlRoot ?? (flyout.Content as FrameworkElement)?.XamlRoot ?? flyout.XamlRoot;
            if (!flyout.IsOpen || hostRoot != xamlRoot)
            {
                continue;
            }

            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<object> closed = (_, _) => completion.TrySetResult();
            flyout.Closed += closed;
            pending.Add((flyout, closed, completion.Task));
        }

        try
        {
            // Observe every Closed event before hiding: dismissing a parent may
            // also dismiss its submenu synchronously.
            foreach ((Flyout Flyout, EventHandler<object> Closed, Task Completion) entry in pending)
            {
                entry.Flyout.Hide();
            }

            // Do not open the dialog until flyout animations and overlays are gone.
            await Task.WhenAll(pending.Select(entry => entry.Completion));
        }
        finally
        {
            foreach ((Flyout Flyout, EventHandler<object> Closed, Task Completion) entry in pending)
            {
                entry.Flyout.Closed -= entry.Closed;
            }
        }
    }

    private static void OnIsEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is Flyout flyout)
        {
            flyout.Opened -= OnOpened;
            flyout.Closed -= OnClosed;
            OpenFlyouts.Remove(flyout);
            if ((bool)args.NewValue)
            {
                flyout.Opened += OnOpened;
                flyout.Closed += OnClosed;
                if (flyout.IsOpen)
                {
                    OpenFlyouts.Add(flyout);
                }
            }
        }
    }

    private static void OnOpened(object? sender, object args)
    {
        if (sender is Flyout flyout)
        {
            OpenFlyouts.Add(flyout);
        }
    }

    private static void OnClosed(object? sender, object args)
    {
        if (sender is Flyout flyout)
        {
            OpenFlyouts.Remove(flyout);
        }
    }
}
