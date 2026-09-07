using System.Windows;
using System.Windows.Controls;

namespace KemonoDownloader.Wpf.Helpers;

public static class PasswordBoxAssistant
{
    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword", typeof(string), typeof(PasswordBoxAssistant), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    private static readonly DependencyProperty UpdatingProperty = DependencyProperty.RegisterAttached("Updating", typeof(bool), typeof(PasswordBoxAssistant));

    public static string GetBoundPassword(DependencyObject value) => (string)value.GetValue(BoundPasswordProperty);
    public static void SetBoundPassword(DependencyObject value, string password) => value.SetValue(BoundPasswordProperty, password);

    private static void OnBoundPasswordChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not PasswordBox passwordBox) return;
        passwordBox.PasswordChanged -= HandlePasswordChanged;
        if (!(bool)passwordBox.GetValue(UpdatingProperty)) passwordBox.Password = args.NewValue as string ?? string.Empty;
        passwordBox.PasswordChanged += HandlePasswordChanged;
    }

    private static void HandlePasswordChanged(object sender, RoutedEventArgs args)
    {
        var passwordBox = (PasswordBox)sender;
        passwordBox.SetValue(UpdatingProperty, true);
        SetBoundPassword(passwordBox, passwordBox.Password);
        passwordBox.SetValue(UpdatingProperty, false);
    }
}
