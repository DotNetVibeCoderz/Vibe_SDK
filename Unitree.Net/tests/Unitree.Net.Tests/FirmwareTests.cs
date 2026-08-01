using System.Text.Json;
using Shouldly;
using Unitree.Net.Core;
using Unitree.Net.Firmware;

namespace Unitree.Net.Tests;

public sealed class FirmwareManagerTests : IDisposable
{
    private readonly string _workingDirectory =
        Path.Combine(Path.GetTempPath(), "unitree-net-tests", Guid.NewGuid().ToString("N"));

    public FirmwareManagerTests() => Directory.CreateDirectory(_workingDirectory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; a leftover temp directory is not worth failing a test run over.
        }
    }

    [Fact]
    public async Task SuccessfulInstallAdvancesTheVersion()
    {
        FirmwarePackage package = await CreatePackageAsync("2.0.0", [1, 2, 3, 4]);
        var channel = new InMemoryFirmwareChannel();
        channel.SetInstalledVersion(FirmwareComponent.MainController, "1.0.0");

        FirmwareManager manager = CreateManager(channel);

        FirmwareInstallRecord record = await manager.InstallAsync(package);

        record.Outcome.ShouldBe(FirmwareInstallOutcome.Succeeded);
        record.FromVersion.ShouldBe("1.0.0");
        record.ToVersion.ShouldBe("2.0.0");

        (await channel.GetInstalledVersionAsync(FirmwareComponent.MainController)).ShouldBe("2.0.0");
    }

    [Fact]
    public async Task MatchingVersionIsSkipped()
    {
        FirmwarePackage package = await CreatePackageAsync("1.0.0", [1, 2, 3, 4]);
        var channel = new InMemoryFirmwareChannel();
        channel.SetInstalledVersion(FirmwareComponent.MainController, "1.0.0");

        FirmwareInstallRecord record = await CreateManager(channel).InstallAsync(package);

        record.Outcome.ShouldBe(FirmwareInstallOutcome.AlreadyInstalled);
        channel.StagedPayloads.ShouldBeEmpty();
    }

    [Fact]
    public async Task ForcedInstallProceedsOverAMatchingVersion()
    {
        FirmwarePackage package = await CreatePackageAsync("1.0.0", [1, 2, 3, 4]);
        var channel = new InMemoryFirmwareChannel();
        channel.SetInstalledVersion(FirmwareComponent.MainController, "1.0.0");

        FirmwareInstallRecord record = await CreateManager(channel).InstallAsync(package, force: true);

        record.Outcome.ShouldBe(FirmwareInstallOutcome.Succeeded);
    }

    /// <summary>
    /// A failed health check must revert the robot, not leave it running an image that does not work.
    /// </summary>
    [Fact]
    public async Task FailedHealthCheckTriggersRollback()
    {
        FirmwarePackage package = await CreatePackageAsync("2.0.0", [9, 9, 9]);
        var channel = new InMemoryFirmwareChannel { FailHealthCheck = true };
        channel.SetInstalledVersion(FirmwareComponent.MainController, "1.0.0");

        FirmwareManager manager = CreateManager(channel);
        manager.ActivationSettleDelay = TimeSpan.Zero;

        FirmwareInstallRecord record = await manager.InstallAsync(package);

        record.Outcome.ShouldBe(FirmwareInstallOutcome.RolledBack);
        (await channel.GetInstalledVersionAsync(FirmwareComponent.MainController)).ShouldBe("1.0.0");
    }

    [Fact]
    public async Task FailedRollbackIsReportedAsUnrecoverable()
    {
        FirmwarePackage package = await CreatePackageAsync("2.0.0", [9, 9, 9]);
        var channel = new InMemoryFirmwareChannel { FailHealthCheck = true, FailRollback = true };
        channel.SetInstalledVersion(FirmwareComponent.MainController, "1.0.0");

        FirmwareManager manager = CreateManager(channel);
        manager.ActivationSettleDelay = TimeSpan.Zero;

        FirmwareInstallRecord record = await manager.InstallAsync(package);

        record.Outcome.ShouldBe(FirmwareInstallOutcome.RollbackFailed);
        record.Detail.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task UnsupportedModelIsRejectedBeforeAnythingIsStaged()
    {
        FirmwarePackage package = await CreatePackageAsync("2.0.0", [1], supportedModels: [RobotModel.G1]);
        var channel = new InMemoryFirmwareChannel();

        // The manager targets a Go2; the package only lists G1.
        FirmwareException exception = await Should.ThrowAsync<FirmwareException>(
            () => CreateManager(channel).InstallAsync(package));

        exception.Message.ShouldContain("Go2");
        channel.StagedPayloads.ShouldBeEmpty();
    }

    [Fact]
    public async Task MinimumVersionGateBlocksASkippedUpgrade()
    {
        FirmwarePackage package = await CreatePackageAsync("3.0.0", [1], minimumCurrentVersion: "2.0.0");
        var channel = new InMemoryFirmwareChannel();
        channel.SetInstalledVersion(FirmwareComponent.MainController, "1.0.0");

        FirmwareException exception = await Should.ThrowAsync<FirmwareException>(
            () => CreateManager(channel).InstallAsync(package));

        exception.Message.ShouldContain("2.0.0");
        channel.StagedPayloads.ShouldBeEmpty();
    }

    [Fact]
    public async Task CorruptedPayloadFailsVerification()
    {
        string directory = Path.Combine(_workingDirectory, "corrupt");
        Directory.CreateDirectory(directory);

        byte[] payload = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(Path.Combine(directory, "payload.bin"), payload);

        var manifest = new FirmwareManifest(
            FirmwareComponent.MainController,
            "1.0.0",
            // Deliberately the wrong hash.
            new string('a', 64),
            payload.Length,
            [RobotModel.Go2]);

        await WriteManifestAsync(directory, manifest);

        FirmwareException exception = await Should.ThrowAsync<FirmwareException>(
            () => FirmwarePackage.LoadAsync(directory));

        exception.Message.ShouldContain("checksum mismatch");
    }

    [Fact]
    public async Task SizeMismatchFailsVerification()
    {
        string directory = Path.Combine(_workingDirectory, "wrongsize");
        Directory.CreateDirectory(directory);

        byte[] payload = [1, 2, 3, 4];
        string path = Path.Combine(directory, "payload.bin");
        await File.WriteAllBytesAsync(path, payload);

        var manifest = new FirmwareManifest(
            FirmwareComponent.MainController,
            "1.0.0",
            await FirmwarePackage.ComputeSha256Async(path),
            SizeBytes: 999,
            [RobotModel.Go2]);

        await WriteManifestAsync(directory, manifest);

        FirmwareException exception = await Should.ThrowAsync<FirmwareException>(
            () => FirmwarePackage.LoadAsync(directory));

        exception.Message.ShouldContain("size mismatch");
    }

    [Fact]
    public async Task InstallHistoryIsJournalled()
    {
        FirmwarePackage package = await CreatePackageAsync("2.0.0", [1, 2]);
        var channel = new InMemoryFirmwareChannel();
        channel.SetInstalledVersion(FirmwareComponent.MainController, "1.0.0");

        FirmwareManager manager = CreateManager(channel);
        await manager.InstallAsync(package);

        IReadOnlyList<FirmwareInstallRecord> history = await manager.GetHistoryAsync();

        history.Count.ShouldBe(1);
        history[0].Outcome.ShouldBe(FirmwareInstallOutcome.Succeeded);
    }

    [Fact]
    public async Task ProgressIsReportedDuringStaging()
    {
        FirmwarePackage package = await CreatePackageAsync("2.0.0", new byte[128 * 1024]);
        var channel = new InMemoryFirmwareChannel();

        // Concurrent, because Progress<T> raises its callbacks on the thread pool when there is no
        // synchronisation context — which is the case under the test runner. A List<T> here is a data
        // race that loses reports on a busy machine.
        var reported = new System.Collections.Concurrent.ConcurrentQueue<double>();
        var finished = new TaskCompletionSource();

        var progress = new Progress<double>(value =>
        {
            reported.Enqueue(value);

            if (value >= 0.999)
            {
                finished.TrySetResult();
            }
        });

        await CreateManager(channel).InstallAsync(package, progress);

        // The final callback can arrive after InstallAsync returns. Waiting for the value rather than
        // for a fixed delay is what makes this deterministic when the suite is running in parallel.
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));

        reported.ShouldNotBeEmpty();
        reported.Last().ShouldBe(1.0, 0.01);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0.1", "1.0.0", 1)]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("2.0", "1.9.9", 1)]
    [InlineData("1.2.3-beta", "1.2.3", -1)]
    public void VersionComparisonOrdersNumerically(string left, string right, int expectedSign)
    {
        Math.Sign(FirmwareManager.CompareVersions(left, right)).ShouldBe(expectedSign);
    }

    private FirmwareManager CreateManager(IFirmwareChannel channel) =>
        new(channel, RobotModel.Go2, Path.Combine(_workingDirectory, "journal.json"));

    private async Task<FirmwarePackage> CreatePackageAsync(
        string version,
        byte[] payload,
        RobotModel[]? supportedModels = null,
        string? minimumCurrentVersion = null)
    {
        string directory = Path.Combine(_workingDirectory, $"pkg-{version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        string payloadPath = Path.Combine(directory, "payload.bin");
        await File.WriteAllBytesAsync(payloadPath, payload);

        var manifest = new FirmwareManifest(
            FirmwareComponent.MainController,
            version,
            await FirmwarePackage.ComputeSha256Async(payloadPath),
            payload.Length,
            supportedModels ?? [RobotModel.Go2],
            minimumCurrentVersion);

        await WriteManifestAsync(directory, manifest);
        return await FirmwarePackage.LoadAsync(directory);
    }

    private static async Task WriteManifestAsync(string directory, FirmwareManifest manifest)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters =
            {
                new System.Text.Json.Serialization.JsonStringEnumConverter(),
            },
        };

        await File.WriteAllTextAsync(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(manifest, options));
    }
}
