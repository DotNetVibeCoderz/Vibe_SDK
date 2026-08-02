using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DepthAI.Wizard.Ai.Plugins;

/// <summary>Satu masalah yang ditemukan pada kode yang ditulis asisten.</summary>
public sealed record ApiProblem(string Id, int Line, string Message)
{
    public override string ToString() => Line > 0 ? $"baris {Line}: {Message}" : Message;
}

/// <summary>
/// Mengompilasi kode C# yang ditulis asisten terhadap assembly SDK yang sungguhan,
/// dan melaporkan rujukan API yang tidak ada.
/// </summary>
/// <remarks>
/// <para>
/// Versi pertama pemeriksa ini memakai regex dan refleksi. Itu menangkap
/// <c>Pipeline.FromJsonFile</c>, tapi meloloskan tiga kesalahan sekaligus pada satu
/// berkas yang benar-benar dihasilkan asisten: <c>using DepthAI.Oak</c>,
/// <c>using DepthAI.Extensions</c>, dan <c>Device.CreateAsync(...)</c> — semuanya fiktif,
/// tapi tak satu pun berbentuk "tipe DepthAI yang dikenal titik anggota", jadi polanya
/// tidak cocok.
/// </para>
/// <para>
/// Kompiler tidak punya celah seperti itu. Roslyn menjawab pertanyaan yang sama persis
/// dengan yang nanti ditanyakan <c>dotnet build</c>, jadi tidak ada kelas kesalahan yang
/// bisa menyelinap lewat.
/// </para>
/// </remarks>
public static class CSharpApiVerifier
{
    /// <summary>
    /// Diagnostik yang berarti "API yang dirujuk tidak ada atau dipakai salah".
    /// </summary>
    /// <remarks>
    /// Sengaja dibatasi. Berkas tunggal yang diverifikasi di luar konteks proyek penuh
    /// akan menghasilkan galat lain yang bukan kesalahan asisten — misalnya tidak ada
    /// entry point. Menolak kode karena itu hanya akan membuat asisten berputar-putar.
    /// </remarks>
    private static readonly HashSet<string> ApiDiagnostics =
    [
        "CS0103", // nama tidak ada di konteks ini
        "CS0117", // tipe tidak memuat definisi anggota
        "CS0234", // tipe atau namespace tidak ada di dalam namespace
        "CS0246", // tipe atau namespace tidak ditemukan
        "CS1061", // tidak ada definisi anggota atau metode ekstensi
        "CS1501", // tidak ada kelebihan beban dengan jumlah argumen itu
        "CS1503", // argumen tidak bisa dikonversi
        "CS7036", // tidak ada argumen untuk parameter wajib
        "CS0122", // anggota tidak bisa diakses karena tingkat proteksinya
        "CS1929", // tipe tidak memuat definisi; metode ekstensi butuh penerima lain
    ];

    private static readonly Lazy<ImmutableArray<MetadataReference>> BaseReferences = new(LoadBaseReferences);

    /// <summary>
    /// Global using yang ditambahkan Microsoft.NET.Sdk saat <c>ImplicitUsings</c> aktif,
    /// yang berlaku pada semua proyek hasil template wizard.
    /// </summary>
    private const string ImplicitUsings = """
        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        global using global::System.Net.Http;
        global using global::System.Threading;
        global using global::System.Threading.Tasks;
        """;

    /// <summary>
    /// Memeriksa kode dan mengembalikan masalah yang ditemukan; kosong berarti bersih.
    /// </summary>
    /// <param name="code">Isi berkas yang akan ditulis.</param>
    /// <param name="companionSources">
    /// Berkas <c>.cs</c> lain di proyek yang sama. Tanpa ini, tipe yang didefinisikan di
    /// berkas lain akan terlihat seperti tipe yang tidak ada.
    /// </param>
    /// <param name="referenceDirectory">
    /// Folder berisi DLL dependensi proyek, biasanya keluaran build sebelumnya. Dipakai
    /// agar paket seperti ImageSharp bisa diresolusi.
    /// </param>
    public static IReadOnlyList<ApiProblem> Verify(
        string code,
        IEnumerable<string>? companionSources = null,
        string? referenceDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return [];
        }

        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);

        var trees = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(code, parseOptions, path: "Generated.cs"),

            // Proyek hasil template mengaktifkan ImplicitUsings, jadi kompilasi
            // verifikasi harus melakukan hal yang sama — tanpa ini, kode yang benar
            // ditolak hanya karena tidak menulis 'using System;'.
            CSharpSyntaxTree.ParseText(ImplicitUsings, parseOptions, path: "ImplicitUsings.cs"),
        };

        foreach (var source in companionSources ?? [])
        {
            trees.Add(CSharpSyntaxTree.ParseText(source, parseOptions));
        }

        var compilation = CSharpCompilation.Create(
            "DepthAiVerification",
            trees,
            BuildReferences(referenceDirectory),
            new CSharpCompilationOptions(
                OutputKind.ConsoleApplication,
                // Kode yang diverifikasi sering berupa top-level statements; membiarkan
                // kompiler mencari entry point sendiri menghindari galat palsu.
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));

        var problems = new List<ApiProblem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var diagnostic in compilation.GetDiagnostics())
        {
            if (diagnostic.Severity != DiagnosticSeverity.Error
                || !ApiDiagnostics.Contains(diagnostic.Id))
            {
                continue;
            }

            // Hanya galat pada berkas yang sedang ditulis yang dilaporkan; berkas
            // pendamping ada untuk konteks, bukan untuk dinilai.
            var location = diagnostic.Location;
            if (location.IsInSource && location.SourceTree?.FilePath != "Generated.cs")
            {
                continue;
            }

            var line = location.IsInSource ? location.GetLineSpan().StartLinePosition.Line + 1 : 0;
            var message = diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture);

            if (seen.Add($"{diagnostic.Id}|{message}"))
            {
                problems.Add(new ApiProblem(diagnostic.Id, line, message));
            }
        }

        return problems;
    }

    /// <summary>Menyusun pesan yang dikembalikan ke model saat kode ditolak.</summary>
    public static string DescribeProblems(IReadOnlyList<ApiProblem> problems)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Kode ditolak: tidak bisa dikompilasi. Galat dari kompiler C#:");
        builder.AppendLine();

        foreach (var problem in problems)
        {
            builder.Append("- ").AppendLine(problem.ToString());
        }

        builder.AppendLine();
        builder.AppendLine(
            "Panggil describe_sdk_api untuk melihat nama tipe dan tanda tangan yang benar, lalu tulis ulang. "
            + "Jangan menebak nama namespace atau metode.");

        return builder.ToString();
    }

    private static ImmutableArray<MetadataReference> BuildReferences(string? referenceDirectory)
    {
        if (string.IsNullOrWhiteSpace(referenceDirectory) || !Directory.Exists(referenceDirectory))
        {
            return BaseReferences.Value;
        }

        var references = BaseReferences.Value.ToBuilder();

        // Nama assembly yang sudah ada tidak ditambahkan lagi; referensi ganda
        // membuat kompilasi gagal dengan galat yang tidak ada hubungannya dengan kode.
        var known = BaseReferences.Value
            .OfType<PortableExecutableReference>()
            .Select(r => Path.GetFileName(r.FilePath))
            .Where(name => name is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var dll in Directory.EnumerateFiles(referenceDirectory, "*.dll"))
        {
            if (known.Add(Path.GetFileName(dll)))
            {
                if (TryCreateReference(dll) is { } reference)
                {
                    references.Add(reference);
                }
            }
        }

        return references.ToImmutable();
    }

    /// <summary>
    /// Assembly yang sudah dimuat proses ini, yang mencakup runtime .NET dan seluruh
    /// assembly DepthAI.Net karena wizard merujuknya langsung.
    /// </summary>
    private static ImmutableArray<MetadataReference> LoadBaseReferences()
    {
        var references = ImmutableArray.CreateBuilder<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
            {
                continue;
            }

            if (seen.Add(assembly.Location) && TryCreateReference(assembly.Location) is { } reference)
            {
                references.Add(reference);
            }
        }

        // Assembly SDK bisa saja belum tersentuh saat pemeriksaan pertama, jadi
        // pemuatannya dipaksa lewat tipe yang pasti ada.
        foreach (var anchor in new[] { typeof(DepthAiDevice).Assembly, typeof(object).Assembly })
        {
            if (!string.IsNullOrEmpty(anchor.Location)
                && seen.Add(anchor.Location)
                && TryCreateReference(anchor.Location) is { } reference)
            {
                references.Add(reference);
            }
        }

        return references.ToImmutable();
    }

    private static MetadataReference? TryCreateReference(string path)
    {
        try
        {
            return MetadataReference.CreateFromFile(path);
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            // Berkas yang bukan assembly terkelola dilewati; keberadaannya di folder
            // build bukan alasan untuk menggagalkan verifikasi.
            return null;
        }
    }
}
