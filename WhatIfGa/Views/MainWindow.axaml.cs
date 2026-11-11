using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WhatIfGa.ViewModels;

namespace WhatIfGa.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is MainViewModel model) await model.InitializeAsync();
        };
    }

    public Task<string?> SelectCsvAsync() => SelectUsageFileAsync();

    public Task<string?> SelectUsageFileAsync() =>
        SelectCsvOrExcelFileAsync("Import electricity usage", "Usage data");

    public Task<string?> SelectBillHistoryFileAsync() =>
        SelectCsvOrExcelFileAsync("Import bill and payment history", "Bill history");

    private async Task<string?> SelectCsvOrExcelFileAsync(string title, string fileDescription)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType($"{fileDescription} (CSV or Excel)")
                {
                    Patterns = new[] { "*.csv", "*.xlsx" },
                    MimeTypes = new[] { "text/csv", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" }
                },
                new FilePickerFileType("CSV file") { Patterns = new[] { "*.csv" }, MimeTypes = new[] { "text/csv" } },
                new FilePickerFileType("Excel workbook")
                {
                    Patterns = new[] { "*.xlsx" },
                    MimeTypes = new[] { "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" }
                }
            }
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private void OverviewClick(object? sender, RoutedEventArgs e) => WorkspaceTabs.SelectedIndex = 0;
    private void ScenariosClick(object? sender, RoutedEventArgs e) => WorkspaceTabs.SelectedIndex = 1;
    private void BillsClick(object? sender, RoutedEventArgs e) => WorkspaceTabs.SelectedIndex = 2;
}
