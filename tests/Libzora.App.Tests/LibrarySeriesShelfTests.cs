using System.Reflection;
using Avalonia.Controls;
using Libzora.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Libzora.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task SeriesStackingCollapsesShelfAndPromotesVolumes() => Run(async () =>
    {
        await using var scope = await TestWindow.Create(new AppSettings
        {
            UiLanguage = "zh-CN",
            OnboardingCompleted = true,
            NetworkEnabled = false,
            AutoUpdateCheckEnabled = false,
            AutoConnectDevice = false,
            GridGalleryDisplay = false,
            StackBookSeries = true
        });

        await InsertBookAsync(scope.Paths.Database, "第二部", 2);
        await InsertBookAsync(scope.Paths.Database, "第一部", 1);
        await InsertBookAsync(scope.Paths.Database, "第三部", 3);
        await scope.Window.ViewModel.RefreshViewAsync();
        await Render();

        var viewModel = scope.Window.ViewModel;
        Assert.Equal(3, viewModel.Books.Count);
        var stack = Assert.Single(viewModel.ShelfBooks);
        Assert.True(stack.IsSeriesStack);
        Assert.Equal(3, stack.SeriesCount);
        Assert.Equal("测试系列", stack.DisplayTitle);
        Assert.Equal("第一部", stack.Title);
        Assert.Equal(["第一部", "第二部", "第三部"], stack.SeriesMembers.Select(member => member.Title));
        Assert.Same(viewModel.ShelfBooks, scope.Get<ListBox>("BookGrid").ItemsSource);

        var second = stack.SeriesMembers.Single(member => member.Title == "第二部");
        viewModel.PromoteSeriesVolume(stack, second);
        await Render();
        var promoted = Assert.Single(viewModel.ShelfBooks);
        Assert.Same(second, promoted);
        Assert.Equal(["第二部", "第一部", "第三部"], promoted.SeriesMembers.Select(member => member.Title));

        scope.Get<ToggleSwitch>("StackBookSeriesCheck").IsChecked = false;
        await Render();
        Assert.Equal(3, viewModel.ShelfBooks.Count);

        scope.Get<ToggleSwitch>("StackBookSeriesCheck").IsChecked = true;
        await Render();
        Assert.Single(viewModel.ShelfBooks);

        var deviceCards = new[]
        {
            new KindleBookCardViewModel(new KindleBook { RelativePath = "docs/sea-1.epub", Title = "星海 第1卷", Authors = "甲" }),
            new KindleBookCardViewModel(new KindleBook { RelativePath = "docs/sea-2.epub", Title = "星海 第2卷", Authors = "甲" }),
            new KindleBookCardViewModel(new KindleBook { RelativePath = "docs/sea-3.epub", Title = "星海 第3卷", Authors = "甲" })
        };
        foreach (var card in deviceCards)
            scope.Window.DeviceBooks.Add(card);
        InvokeApplyDeviceBookFilter(scope.Window);
        await Render();

        var deviceStack = Assert.Single(scope.Window.VisibleDeviceShelfBooks);
        Assert.True(deviceStack.IsSeriesStack);
        Assert.Equal(3, deviceStack.SeriesCount);
        Assert.Same(scope.Get<ItemsControl>("DeviceBookGridItems").ItemsSource,
            scope.Window.VisibleDeviceShelfBooks);

        var deviceSecond = deviceStack.SeriesMembers.Single(member => member.Title == "星海 第2卷");
        var promotion = (Task)scope.Call("PromoteDeviceSeriesVolume", deviceStack, deviceSecond)!;
        await promotion;
        await Render();
        var promotedDevice = Assert.Single(scope.Window.VisibleDeviceShelfBooks);
        Assert.Same(deviceSecond, promotedDevice);
        Assert.Equal("星海 第2卷", promotedDevice.Title);
    });

    private static async Task InsertBookAsync(string databasePath, string title, double seriesIndex)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO Books(Id,Title,Authors,Series,SeriesIndex,CreatedAt,UpdatedAt) " +
            "VALUES($id,$title,'测试作者','测试系列',$index,$time,$time);";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$index", seriesIndex);
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static void InvokeApplyDeviceBookFilter(MainWindow window)
    {
        var method = typeof(MainWindow).GetMethod(
            "ApplyDeviceBookFilter",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(bool)],
            modifiers: null)!;
        method.Invoke(window, [true]);
    }
}
