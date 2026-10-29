namespace DepthAI.Gallery.Showcase;

/// <summary>Letak data galeri di folder keluaran dan, saat dijalankan dari repo, di sumbernya.</summary>
internal static class GalleryPaths
{
    public static string SamplesDirectory => Path.Combine(AppContext.BaseDirectory, "Samples");

    public static string ModelConfigDirectory => Path.Combine(AppContext.BaseDirectory, "ModelConfigs");

    /// <summary>
    /// Folder Samples di sumber proyek, dicari naik dari folder keluaran. Perekaman menulis ke
    /// sana supaya hasilnya ikut di-commit; null bila galeri tidak dijalankan dari repo.
    /// </summary>
    public static string? SourceSamplesDirectory
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "src", "DepthAI.Net.Gallery", "Samples");
                if (File.Exists(Path.Combine(dir.FullName, "DepthAI.Net.slnx")) && Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }
    }

    public static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DepthAI.Net.Gallery/1.0");
        return http;
    }
}
