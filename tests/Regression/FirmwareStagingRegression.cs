using NDIJobConfigurator.Core;

static class FirmwareStagingRegression
{
    public static async Task SelectedModels(AppStateStore store, IWebHostEnvironment environment)
    {
        var service = new FirmwareService(store, null!, null!, environment);
        // Explicit onboarding coverage must take precedence over an older mixed fleet.
        await store.UpdateAsync(_ => new AppState([Device("N6"), Device("N60")]));
        foreach (var model in new[] { "N6", "N60" })
        {
            var files = new[] { "N6", "N60" }
                .Select(item => new Upload(item, item == model ? item + ".bin" : "", item == model ? [1, 2, 3] : []))
                .ToArray();
            var staged = await Stage(service, [model], files);
            Check(staged.Status == "staged" && staged.Packages.Count == 1 && staged.Packages[0].Model == model,
                $"{model}-only staging included or required the absent model.");
            Check(File.ReadAllBytes(staged.Packages[0].LocalPath).SequenceEqual(new byte[] { 1, 2, 3 }),
                "The selected firmware bytes were not preserved.");
            Check((await store.ReloadAsync()).FirmwareJob?.Packages.Single().Model == model,
                "Staged coverage was not persisted.");
        }

        await store.UpdateAsync(_ => AppState.Empty);
        var mixed = await Stage(service, ["N6", "N60"], [new("N6", "n6.bin", [1]), new("N60", "n60.bin", [2])]);
        Check(mixed.Packages.Count == 2, "A mixed onboarding fleet did not stage both packages.");
    }

    public static async Task RequiredCoverage(AppStateStore store, IWebHostEnvironment environment)
    {
        var service = new FirmwareService(store, null!, null!, environment);
        await store.UpdateAsync(_ => new AppState([Device("N6"), Device("N60")]));
        foreach (var missing in new[] { "N6", "N60" })
        {
            var present = missing == "N6" ? "N60" : "N6";
            Upload[] files = [new(present, present + ".bin", [1]), new(missing, "", [])];
            await Reject(service, store, ["N6", "N60"], files, $"Select the latest {missing} firmware package.");
            await Reject(service, store, null, files, $"Select the latest {missing} firmware package.");
            await Reject(service, store, [missing], [new(missing, "", [])], $"Select the latest {missing} firmware package.");
        }
    }

    public static async Task InvalidUploads(AppStateStore store, IWebHostEnvironment environment)
    {
        var service = new FirmwareService(store, null!, null!, environment);
        await store.UpdateAsync(_ => AppState.Empty);
        await Stage(service, ["N6"], [new("N6", "previous.bin", [42])]);
        await Reject(service, store, ["N6"], [new("N6", "empty.bin", [])], "The N6 firmware package is empty.");
        await Reject(service, store, ["N6"], [new("N6", "invalid.txt", [1])], "The N6 firmware package must be a .bin file.");
        await Reject(service, store, ["N6"], [new("N6", "one.bin", [1]), new("N6", "two.bin", [2])],
            "Only one N6 firmware package can be staged at a time.");
    }

    private static async Task<FirmwareJob> Stage(FirmwareService service, string[]? required, Upload[] files)
    {
        using var form = new MultipartFormDataContent();
        foreach (var file in files)
        {
            var part = new ByteArrayContent(file.Bytes);
            part.Headers.ContentType = new("application/octet-stream");
            // A browser includes filename="" for an enabled file input with no selection.
            part.Headers.ContentDisposition = new("form-data")
            {
                Name = $"\"{file.Model.ToLowerInvariant()}Firmware\"",
                FileName = $"\"{file.FileName}\""
            };
            form.Add(part);
        }
        await using var body = new MemoryStream(await form.ReadAsByteArrayAsync());
        var request = new DefaultHttpContext().Request;
        request.ContentType = form.Headers.ContentType!.ToString();
        request.Body = body;
        return await service.StageMultipartAsync(request, required, CancellationToken.None);
    }

    private static async Task Reject(FirmwareService service, AppStateStore store, string[]? required, Upload[] files, string expected)
    {
        var previous = (await store.ReadAsync()).FirmwareJob;
        var directory = Path.Combine(AppDataPaths.ResolveDataDirectory(""), "firmware");
        var before = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order().ToArray();
        try
        {
            await Stage(service, required, files);
            throw new InvalidOperationException("Invalid firmware upload was accepted.");
        }
        catch (ArgumentException ex)
        {
            Check(ex.Message == expected, $"Expected '{expected}', got '{ex.Message}'.");
        }
        Check((await store.ReadAsync()).FirmwareJob == previous, "Failed staging replaced the prior firmware job.");
        Check(Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order().SequenceEqual(before),
            "Failed staging left partial or extra firmware files.");
    }

    private static ManagedDevice Device(string model) => new()
    {
        Id = model,
        Hostname = model,
        IpAddress = model == "N60" ? "192.0.2.61" : "192.0.2.6",
        MacAddress = "00:11:22:33:44:55",
        Model = model,
        Family = model == "N60" ? DeviceFamily.N60 : DeviceFamily.N6,
        IsOnboarded = true
    };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record Upload(string Model, string FileName, byte[] Bytes);
}
