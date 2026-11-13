using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using WhatIfGa.ViewModels;
using WhatIfGa.Views;
using WhatIfGa.Core.Services;
using WhatIfGa.Services;

namespace WhatIfGa;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var controller = new AppController(new DataManager(Environment.GetEnvironmentVariable("WHATIFGA_DATA_DIRECTORY")), new RateCalculator());
            var viewModel = new MainViewModel(window.SelectUsageFileAsync, controller, window.SelectBillHistoryFileAsync);
            window.DataContext = viewModel;
            window.Closed += (_, _) => viewModel.Dispose();
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
