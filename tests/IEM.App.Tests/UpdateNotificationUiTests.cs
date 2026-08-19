using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IEM.App.Updates;
using IEM.App.ViewModels;
using IEM.Presentation.Updates;
using Xunit;

namespace IEM.App.Tests;

public sealed class UpdateNotificationUiTests
{
    private static T OnStaThread<T>(Func<T> work)
    {
        var result = default(T)!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        if (failure is not null)
        {
            throw new InvalidOperationException("STA operacija nije uspela.", failure);
        }

        return result;
    }

    [Fact]
    public void AboutWindow_renders_update_section_and_button()
    {
        OnStaThread(() =>
        {
            var dialog = new AboutWindow();
            dialog.Show();

            var checkBtn = (Button)dialog.FindName("CheckUpdatesButton");
            var statusText = (TextBlock)dialog.FindName("UpdateStatusText");

            Assert.NotNull(checkBtn);
            Assert.NotNull(statusText);
            Assert.True(checkBtn.IsEnabled);
            Assert.Contains("Automatska provera", statusText.Text);

            dialog.Close();
            return true;
        });
    }

    [Fact]
    public void UpdateCheckService_saves_and_reloads_preferences()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"iem-test-pref-{Guid.NewGuid():N}.json");
        try
        {
            var service = new UpdateCheckService(tempFile, "3.0.0");
            Assert.True(service.Preferences.AutoCheckEnabled);
            Assert.Equal(UpdateChannel.Stable, service.Preferences.SelectedChannel);

            service.SavePreferences(new UpdatePreferences
            {
                AutoCheckEnabled = false,
                SelectedChannel = UpdateChannel.Preview,
                SkippedVersion = "3.0.1"
            });

            var service2 = new UpdateCheckService(tempFile, "3.0.0");
            Assert.False(service2.Preferences.AutoCheckEnabled);
            Assert.Equal(UpdateChannel.Preview, service2.Preferences.SelectedChannel);
            Assert.Equal("3.0.1", service2.Preferences.SkippedVersion);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ShellViewModel_update_banner_properties_and_commands_function()
    {
        var host = new StubMonitorHost();
        var tempDir = Path.Combine(Path.GetTempPath(), $"iem-test-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var vm = new ShellViewModel(host, tempDir)
            {
                UpdateVersionText = "3.0.1",
                UpdateReleaseNotesUrl = "https://github.com/zoxknez/InternetMonitoring/releases/tag/v3.0.1",
                UpdateDownloadUrl = "https://github.com/zoxknez/InternetMonitoring/releases/download/v3.0.1/InternetEvidenceMonitor-3.0.1-win-x64.exe",
                IsUpdateBannerVisible = true
            };

            Assert.True(vm.IsUpdateBannerVisible);
            Assert.Equal("3.0.1", vm.UpdateVersionText);

            vm.DismissUpdateCommand.Execute(null);
            Assert.False(vm.IsUpdateBannerVisible);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task UpdateCheckService_can_fetch_live_github_manifest()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"iem-test-pref-{Guid.NewGuid():N}.json");
        try
        {
            var service = new UpdateCheckService(tempFile, "3.0.0");
            var result = await service.CheckForUpdatesAsync(force: true);
            Assert.NotEqual(UpdateAvailability.Unknown, result.Availability);
            Assert.NotNull(result.Manifest);
            Assert.NotEmpty(result.Manifest.Version);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
