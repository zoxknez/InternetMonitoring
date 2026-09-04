using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using IEM.App.Linux.ViewModels;

namespace IEM.App.Linux;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is LinuxShellViewModel viewModel)
        {
            await viewModel.InitializeAsync();
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is LinuxShellViewModel viewModel)
        {
            await viewModel.DisposeAsync();
        }
    }
}
