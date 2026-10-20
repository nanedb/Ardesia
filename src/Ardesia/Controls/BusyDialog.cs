using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Ardesia.Controls;

/// <summary>
/// Finestrella «Apro da Google Drive…» con barra di avanzamento e «Annulla», per le operazioni di rete che l'utente
/// deve aspettare (aprire, creare, eliminare un blocco). Compare solo se l'operazione dura più di un attimo.
/// </summary>
public sealed class BusyDialog : Window
{
    readonly ProgressBar _bar;
    readonly CancellationTokenSource _cts = new();
    bool _done;

    BusyDialog(string message, string cancelText)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 15;
        Opacity = 0; // compare dopo un attimo, se serve

        _bar = new ProgressBar { Height = 6, Width = 380, IsIndeterminate = true, Margin = new Thickness(0, 14, 0, 0) };
        if (Application.Current.TryFindResource("AccentBrush") is Brush accent) _bar.Foreground = accent;
        var cancel = new Button { Content = cancelText, MinWidth = 110, Padding = new Thickness(16, 7, 16, 7), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        if (Application.Current.TryFindResource("PlainButton") is Style st) cancel.Style = st;
        cancel.Click += (_, _) =>
        {
            cancel.IsEnabled = false;
            _cts.Cancel();
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 });
        panel.Children.Add(_bar);
        panel.Children.Add(cancel);
        Content = new Border
        {
            Background = Brushes.White,
            BorderBrush = (Brush?)Application.Current.TryFindResource("LineBrush") ?? Brushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(24, 20, 24, 18),
            Margin = new Thickness(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.25 },
            Child = panel,
        };
        Closing += (_, e) =>
        {
            if (_done) return;
            e.Cancel = true; // si chiude da sola alla fine; Alt+F4 vale come «Annulla»
            _cts.Cancel();
        };
    }

    /// <summary>
    /// Esegue <paramref name="work"/> mostrando la finestrella (modale) e ne restituisce il risultato.
    /// Le eccezioni passano al chiamante; «Annulla» dà OperationCanceledException.
    /// </summary>
    public static T Run<T>(Window? owner, string message, Func<IProgress<double>, CancellationToken, Task<T>> work,
        string cancelText = "Annulla")
    {
        var dlg = new BusyDialog(message, cancelText);
        if (owner is { IsLoaded: true }) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        T result = default!;
        ExceptionDispatchInfo? error = null;
        var progress = new Progress<double>(p =>
        {
            dlg._bar.IsIndeterminate = false;
            dlg._bar.Value = p * 100;
        });
        dlg.Loaded += async (_, _) =>
        {
            var show = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            show.Tick += (_, _) =>
            {
                show.Stop();
                dlg.Opacity = 1;
            };
            show.Start();
            try
            {
                result = await work(progress, dlg._cts.Token);
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }
            show.Stop();
            dlg._done = true;
            dlg.Close();
        };
        dlg.ShowDialog();
        error?.Throw();
        return result;
    }
}
