using System.ComponentModel;
using System.Text;
using DepthAI.Pipelines;
using DepthAI.Wizard.Projects;
using Microsoft.SemanticKernel;

namespace DepthAI.Wizard.Ai.Plugins;

/// <summary>Konteks workspace yang dibutuhkan plugin untuk menulis berkas ke proyek yang terbuka.</summary>
public interface IWorkspaceContext
{
    /// <summary>Direktori akar proyek yang sedang terbuka, atau null bila belum ada.</summary>
    string? ProjectDirectory { get; }

    /// <summary>Nama proyek yang sedang terbuka.</summary>
    string? ProjectName { get; }

    /// <summary>Menulis berkas ke dalam proyek dan menampilkannya di editor.</summary>
    Task WriteFileAsync(string relativePath, string content, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fungsi khusus DepthAI: melihat template, membuat pipeline, menulis kode ke proyek,
/// dan membaca referensi API.
/// </summary>
/// <remarks>
/// Fungsi <c>describe_sdk_api</c> ada karena satu alasan konkret: tanpa itu, model
/// menebak nama tipe dan menghasilkan kode yang tidak bisa dikompilasi. Memberinya
/// tanda tangan API yang sungguhan jauh lebih murah daripada membiarkan pengguna
/// men-debug kode yang salah.
/// </remarks>
public sealed class DepthAiPlugin(IWorkspaceContext workspace)
{
    private readonly IWorkspaceContext _workspace = workspace
        ?? throw new ArgumentNullException(nameof(workspace));

    [KernelFunction("list_templates")]
    [Description("Menampilkan seluruh template aplikasi computer vision yang tersedia, "
        + "beserta id, bentuk aplikasi, dan penjelasannya.")]
    public string ListTemplates(
        [Description("Saring menurut bentuk aplikasi: Console, Desktop, atau Web. Kosongkan untuk semua.")]
        string? kind = null)
    {
        var templates = TemplateCatalog.All.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(kind) && Enum.TryParse<ProjectKind>(kind, ignoreCase: true, out var parsed))
        {
            templates = templates.Where(t => t.Kind == parsed);
        }

        var builder = new StringBuilder();
        foreach (var template in templates)
        {
            builder.AppendLine(
                $"- {template.Id} ({template.Kind}, {template.Category}): {template.Description}");
        }

        return builder.Length == 0 ? "Tidak ada template yang cocok." : builder.ToString();
    }

    [KernelFunction("list_pipeline_presets")]
    [Description("Menampilkan preset pipeline bawaan SDK yang bisa dipakai langsung dalam kode.")]
    public string ListPipelinePresets()
        => string.Join(Environment.NewLine,
            PipelinePresets.Available.Select(p => $"- {p.Key}: {p.Value}"));

    [KernelFunction("generate_pipeline_json")]
    [Description("Membuat berkas pipeline JSON dari preset dan menyimpannya ke proyek yang terbuka. "
        + "Pipeline dalam bentuk JSON bisa diubah tanpa mengubah kode C#.")]
    public async Task<string> GeneratePipelineJsonAsync(
        [Description("Nama preset: rgb-preview, stereo-depth, object-detection, spatial-detection, record-rgbd, imu-stream.")]
        string preset,
        [Description("Nama berkas keluaran; bawaan pipeline.json.")] string fileName = "pipeline.json",
        [Description("Frame per detik kamera.")] int fps = 30,
        CancellationToken cancellationToken = default)
    {
        Pipeline pipeline;
        try
        {
            pipeline = PipelinePresets.Create(preset, model: null, fps);
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }

        var json = pipeline.ToJson();

        if (_workspace.ProjectDirectory is null)
        {
            return "Belum ada proyek terbuka, jadi berkas tidak disimpan. Ini isinya:"
                + Environment.NewLine + "```json" + Environment.NewLine + json + Environment.NewLine + "```";
        }

        await _workspace.WriteFileAsync(fileName, json, cancellationToken);

        var validation = pipeline.Validate();
        var notes = validation.Errors.Count > 0
            ? Environment.NewLine + "Masih perlu dilengkapi: " + string.Join("; ", validation.Errors)
            : string.Empty;

        return $"Pipeline '{preset}' disimpan ke {fileName} "
            + $"({pipeline.Nodes.Count} node, {pipeline.OutputStreams.Count} stream keluaran).{notes}";
    }

    [KernelFunction("write_project_file")]
    [Description("Menulis atau menimpa berkas di dalam proyek yang sedang terbuka, lalu membukanya di editor. "
        + "Pakai ini untuk menyerahkan kode C# yang sudah kamu tulis kepada pengguna.")]
    public async Task<string> WriteProjectFileAsync(
        [Description("Path relatif terhadap akar proyek, misalnya 'Program.cs' atau 'Services/Vision.cs'.")]
        string relativePath,
        [Description("Isi lengkap berkas.")] string content,
        CancellationToken cancellationToken = default)
    {
        if (_workspace.ProjectDirectory is null)
        {
            return "Tidak ada proyek yang terbuka. Minta pengguna membuat atau membuka proyek dulu.";
        }

        if (Path.IsPathRooted(relativePath) || relativePath.Contains("..", StringComparison.Ordinal))
        {
            // Path absolut atau menaik akan menulis di luar proyek — ditolak, bukan
            // dinormalkan diam-diam, supaya niatnya jelas terlihat.
            return $"Path '{relativePath}' ditolak: harus relatif dan tidak boleh keluar dari folder proyek.";
        }

        // Berkas C# diperiksa terhadap permukaan API sungguhan sebelum ditulis. Model
        // terbukti menulis metode yang tidak ada walau punya akses ke describe_sdk_api;
        // menolaknya di sini memberi umpan balik yang bisa langsung diperbaiki, alih-alih
        // menyerahkan kode yang tidak bisa dikompilasi kepada pengguna.
        if (relativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            var problems = CSharpApiVerifier.Verify(
                content,
                CompanionSources(relativePath),
                FindReferenceDirectory());

            if (problems.Count > 0)
            {
                return CSharpApiVerifier.DescribeProblems(problems);
            }
        }

        await _workspace.WriteFileAsync(relativePath, content, cancellationToken);
        return $"Ditulis ke {relativePath} ({content.Length} karakter) dan dibuka di editor.";
    }

    /// <summary>
    /// Berkas C# lain di proyek, supaya tipe yang didefinisikan di sana tidak terlihat
    /// seperti tipe yang tidak ada saat berkas baru diverifikasi.
    /// </summary>
    private IEnumerable<string> CompanionSources(string excludeRelativePath)
    {
        if (_workspace.ProjectDirectory is null)
        {
            yield break;
        }

        var excluded = Path.GetFullPath(Path.Combine(_workspace.ProjectDirectory, excludeRelativePath));

        foreach (var file in Directory.EnumerateFiles(_workspace.ProjectDirectory, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || string.Equals(Path.GetFullPath(file), excluded, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string content;
            try
            {
                content = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }

            yield return content;
        }
    }

    /// <summary>
    /// Keluaran build terakhir proyek, bila ada. Dari sana dependensi NuGet seperti
    /// ImageSharp bisa diresolusi; tanpanya, kode yang memakainya akan tampak salah.
    /// </summary>
    private string? FindReferenceDirectory()
    {
        if (_workspace.ProjectDirectory is null)
        {
            return null;
        }

        var binDirectory = Path.Combine(_workspace.ProjectDirectory, "bin");
        if (!Directory.Exists(binDirectory))
        {
            return null;
        }

        return Directory
            .EnumerateDirectories(binDirectory, "net*", SearchOption.AllDirectories)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    [KernelFunction("validate_pipeline_json")]
    [Description("Memeriksa apakah pipeline JSON valid sebelum diserahkan ke pengguna.")]
    public string ValidatePipelineJson(
        [Description("Isi pipeline JSON.")] string json)
    {
        try
        {
            var pipeline = Pipeline.FromJson(json);
            var result = pipeline.Validate();

            var summary = $"{pipeline.Nodes.Count} node, {pipeline.Links.Count} link, "
                + $"{pipeline.OutputStreams.Count} stream keluaran.";

            if (result.Errors.Count == 0)
            {
                return "Pipeline valid. " + summary;
            }

            return "Pipeline belum valid. " + summary + Environment.NewLine
                + string.Join(Environment.NewLine, result.Errors.Select(e => "- " + e));
        }
        catch (Exception ex)
        {
            return $"JSON tidak bisa dibaca: {ex.Message}";
        }
    }

    [KernelFunction("list_devices")]
    [Description("Memindai kamera OAK yang terhubung ke mesin ini dan melaporkan kemampuannya.")]
    public string ListDevices()
    {
        var devices = DepthAi.ListDevices();

        if (devices.Count == 0)
        {
            return "Tidak ada perangkat yang terdeteksi.";
        }

        var builder = new StringBuilder();

        if (!DepthAi.IsNativeAvailable)
        {
            builder.AppendLine(
                $"Runtime native tidak tersedia ({DepthAi.NativeUnavailableReason}), "
                + "jadi ini perangkat simulasi. Kode tetap bisa dikembangkan dan dijalankan.");
        }

        foreach (var device in devices)
        {
            builder.AppendLine(
                $"- {device.Name} [{device.SerialNumber}] {device.Protocol}: "
                + $"{device.Capabilities.ColorCameraCount} kamera warna, "
                + $"{device.Capabilities.MonoCameraCount} mono, "
                + $"stereo={device.Capabilities.SupportsStereoDepth}, "
                + $"IMU={device.Capabilities.HasImu}, "
                + $"{device.Capabilities.ShaveCores} SHAVE core");
        }

        return builder.ToString();
    }

    [KernelFunction("describe_sdk_api")]
    [Description("Mengembalikan tanda tangan dan contoh pemakaian API DepthAI.Net yang sesungguhnya. "
        + "Panggil ini sebelum menulis kode agar nama tipe dan metodenya tepat.")]
    public string DescribeSdkApi(
        [Description("Topik: device, pipeline, streaming, detection, depth, imaging, atau all.")]
        string topic = "all")
    {
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["device"] = ApiReference.Device,
            ["pipeline"] = ApiReference.Pipeline,
            ["streaming"] = ApiReference.Streaming,
            ["detection"] = ApiReference.Detection,
            ["depth"] = ApiReference.Depth,
            ["imaging"] = ApiReference.Imaging,
        };

        if (sections.TryGetValue(topic, out var section))
        {
            return section;
        }

        return string.Join(Environment.NewLine + Environment.NewLine, sections.Values);
    }

    [KernelFunction("get_project_context")]
    [Description("Melaporkan proyek apa yang sedang terbuka dan berkas apa saja yang ada di dalamnya.")]
    public string GetProjectContext()
    {
        if (_workspace.ProjectDirectory is null)
        {
            return "Belum ada proyek yang terbuka.";
        }

        var files = Directory
            .EnumerateFiles(_workspace.ProjectDirectory, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(_workspace.ProjectDirectory, f))
            .Take(100)
            .ToList();

        return $"Proyek '{_workspace.ProjectName}' di {_workspace.ProjectDirectory}" + Environment.NewLine
            + "Berkas:" + Environment.NewLine
            + string.Join(Environment.NewLine, files.Select(f => "- " + f));
    }

    [KernelFunction("read_project_file")]
    [Description("Membaca isi berkas dari proyek yang sedang terbuka.")]
    public async Task<string> ReadProjectFileAsync(
        [Description("Path relatif terhadap akar proyek.")] string relativePath,
        CancellationToken cancellationToken = default)
    {
        if (_workspace.ProjectDirectory is null)
        {
            return "Belum ada proyek yang terbuka.";
        }

        if (Path.IsPathRooted(relativePath) || relativePath.Contains("..", StringComparison.Ordinal))
        {
            return $"Path '{relativePath}' ditolak: harus relatif dan tidak boleh keluar dari folder proyek.";
        }

        var fullPath = Path.Combine(_workspace.ProjectDirectory, relativePath);
        if (!File.Exists(fullPath))
        {
            return $"Berkas '{relativePath}' tidak ditemukan.";
        }

        return await File.ReadAllTextAsync(fullPath, cancellationToken);
    }
}
