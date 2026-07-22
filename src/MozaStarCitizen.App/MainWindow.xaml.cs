using System.ComponentModel;
using System.Windows;
using MozaStarCitizen.App.ViewModels;

namespace MozaStarCitizen.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private bool _shutdownStarted;
    private bool _shutdownReady;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.AutoStartAsync();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_shutdownReady || e.Cancel)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        await _viewModel.DisposeAsync();
        _shutdownReady = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}
