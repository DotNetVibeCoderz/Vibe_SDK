namespace DepthAI.Wizard.Prompts;

/// <summary>Kelompok prompt di galeri.</summary>
public enum PromptCategory
{
    MulaiCepat,
    Deteksi,
    Kedalaman,
    Analitik,
    Keselamatan,
    Industri,
    Web,
    Lanjutan,
}

/// <summary>Satu contoh prompt yang bisa dikirim pengguna ke asisten.</summary>
public sealed record PromptTemplate
{
    public required string Title { get; init; }

    public string TitleEnglish { get; init; } = string.Empty;

    /// <summary>Isi prompt yang dimasukkan ke kotak input.</summary>
    public required string Prompt { get; init; }

    public required PromptCategory Category { get; init; }

    public string Icon { get; init; } = "✨";
}

/// <summary>
/// Contoh prompt yang ditampilkan pada sesi chat baru.
/// </summary>
/// <remarks>
/// Isinya ditulis spesifik dan menyebut hasil yang diinginkan, bukan sekadar "buatkan
/// aplikasi": prompt yang samar membuat asisten menebak, dan tebakan itulah yang
/// biasanya harus diperbaiki pengguna.
/// </remarks>
public static class PromptGallery
{
    private static readonly Lazy<IReadOnlyList<PromptTemplate>> Cache = new(Build);

    public static IReadOnlyList<PromptTemplate> All => Cache.Value;

    public static IEnumerable<PromptTemplate> ByCategory(PromptCategory category)
        => All.Where(p => p.Category == category);

    /// <summary>Beberapa prompt acak untuk ditampilkan pada sesi kosong.</summary>
    public static IReadOnlyList<PromptTemplate> Sample(int count = 6, int? seed = null)
    {
        var random = seed is null ? Random.Shared : new Random(seed.Value);
        return [.. All.OrderBy(_ => random.Next()).Take(count)];
    }

    private static IReadOnlyList<PromptTemplate> Build() =>
    [
        // ------------------------------------------------------- Mulai cepat
        new()
        {
            Title = "Tampilkan kamera saya",
            TitleEnglish = "Show me my camera",
            Icon = "📷",
            Category = PromptCategory.MulaiCepat,
            Prompt = "Buatkan aplikasi konsol yang membuka kamera OAK saya, menampilkan preview 640x480, "
                + "dan mencetak fps sesungguhnya tiap detik. Jelaskan setiap baris kodenya singkat saja.",
        },
        new()
        {
            Title = "Perangkat apa yang terhubung?",
            TitleEnglish = "What device is connected?",
            Icon = "🔍",
            Category = PromptCategory.MulaiCepat,
            Prompt = "Periksa kamera OAK apa yang terhubung ke mesin ini, lalu jelaskan kemampuannya "
                + "dan pipeline seperti apa yang paling cocok untuk perangkat itu.",
        },
        new()
        {
            Title = "Jelaskan pipeline ini",
            TitleEnglish = "Explain this pipeline",
            Icon = "🧭",
            Category = PromptCategory.MulaiCepat,
            Prompt = "Baca pipeline.json di proyek saya, jelaskan apa yang dilakukan tiap node, "
                + "dan sebutkan apa yang akan rusak kalau saya menghapus node stereo depth.",
        },
        new()
        {
            Title = "Ubah pipeline jadi kode",
            TitleEnglish = "Turn my pipeline into code",
            Icon = "🔁",
            Category = PromptCategory.MulaiCepat,
            Prompt = "Ambil pipeline.json di proyek saya dan tulis ulang sebagai kode C# yang memakai "
                + "Pipeline.CreateBuilder(), supaya saya bisa mengubah parameternya saat runtime.",
        },

        // ---------------------------------------------------------- Deteksi
        new()
        {
            Title = "Hitung orang di ruangan",
            TitleEnglish = "Count people in the room",
            Icon = "🚶",
            Category = PromptCategory.Deteksi,
            Prompt = "Buatkan aplikasi desktop yang menghitung berapa orang yang terlihat sekarang, "
                + "menampilkan angkanya besar di tengah, dan menandai kotak tiap orang di video. "
                + "Angkanya harus stabil, jangan berkedip saat deteksi meleset satu frame.",
        },
        new()
        {
            Title = "Deteksi objek dengan suara",
            TitleEnglish = "Detection with audio cue",
            Icon = "🔔",
            Category = PromptCategory.Deteksi,
            Prompt = "Buat aplikasi konsol yang membunyikan beep ketika objek dengan label tertentu "
                + "muncul di frame, dengan jeda minimal 3 detik antar bunyi supaya tidak berisik.",
        },
        new()
        {
            Title = "Lacak satu objek",
            TitleEnglish = "Track a single object",
            Icon = "🎯",
            Category = PromptCategory.Deteksi,
            Prompt = "Saya ingin melacak satu objek yang saya klik di video, dan menampilkan jejak "
                + "pergerakannya selama 5 detik terakhir sebagai garis. Buatkan aplikasi Avalonia-nya.",
        },
        new()
        {
            Title = "Sensor wajah otomatis",
            TitleEnglish = "Automatic face blur",
            Icon = "🫥",
            Category = PromptCategory.Deteksi,
            Prompt = "Buatkan aplikasi yang menyensor setiap orang di frame sebelum ditampilkan, "
                + "lalu menyimpan hasilnya sebagai video. Pastikan sensornya tidak bisa dibalik.",
        },
        new()
        {
            Title = "Bandingkan dua model",
            TitleEnglish = "Compare two models",
            Icon = "⚖️",
            Category = PromptCategory.Deteksi,
            Prompt = "Buat aplikasi yang menjalankan dua model deteksi pada frame yang sama dan "
                + "menampilkan hasilnya berdampingan, lengkap dengan waktu inferensi masing-masing.",
        },

        // -------------------------------------------------------- Kedalaman
        new()
        {
            Title = "Ukur jarak benda",
            TitleEnglish = "Measure distance to an object",
            Icon = "📏",
            Category = PromptCategory.Kedalaman,
            Prompt = "Buatkan aplikasi desktop tempat saya bisa mengklik dua titik pada gambar, "
                + "lalu aplikasi menampilkan jarak 3D sesungguhnya antara kedua titik itu dalam sentimeter.",
        },
        new()
        {
            Title = "Peta kedalaman berwarna",
            TitleEnglish = "Colorized depth map",
            Icon = "🌊",
            Category = PromptCategory.Kedalaman,
            Prompt = "Tampilkan peta kedalaman berwarna berdampingan dengan video warna, "
                + "dengan slider untuk mengatur rentang jarak minimum dan maksimum yang dipetakan ke warna.",
        },
        new()
        {
            Title = "Deteksi ruang kosong",
            TitleEnglish = "Detect free space",
            Icon = "🅿️",
            Category = PromptCategory.Kedalaman,
            Prompt = "Pakai kedalaman untuk menentukan apakah area di depan kamera kosong atau terhalang, "
                + "lalu tampilkan status 'jalan bebas' atau 'ada halangan pada X meter'.",
        },
        new()
        {
            Title = "Rekam dataset RGB-D",
            TitleEnglish = "Record an RGB-D dataset",
            Icon = "⏺️",
            Category = PromptCategory.Kedalaman,
            Prompt = "Buat perekam yang menyimpan pasangan frame warna dan kedalaman bernomor urut, "
                + "dengan berkas metadata JSON berisi timestamp dan parameter kamera tiap frame.",
        },
        new()
        {
            Title = "Ukur tinggi orang",
            TitleEnglish = "Measure a person's height",
            Icon = "📐",
            Category = PromptCategory.Kedalaman,
            Prompt = "Gabungkan deteksi orang dengan kedalaman untuk memperkirakan tinggi tiap orang "
                + "dalam sentimeter, dan jelaskan sumber galat terbesar dari cara pengukuran ini.",
        },

        // ---------------------------------------------------------- Analitik
        new()
        {
            Title = "Heatmap lalu lintas orang",
            TitleEnglish = "Foot-traffic heatmap",
            Icon = "🔥",
            Category = PromptCategory.Analitik,
            Prompt = "Bangun heatmap yang menumpuk posisi orang selama satu jam, lalu menyimpannya "
                + "sebagai gambar PNG tiap 10 menit. Tampilkan juga versi live-nya di jendela.",
        },
        new()
        {
            Title = "Statistik antrean",
            TitleEnglish = "Queue statistics",
            Icon = "📈",
            Category = PromptCategory.Analitik,
            Prompt = "Hitung berapa lama rata-rata orang berada dalam area antrean yang saya tentukan, "
                + "dan tampilkan rata-rata bergerak 5 menit terakhir.",
        },
        new()
        {
            Title = "Laporan harian ke CSV",
            TitleEnglish = "Daily report to CSV",
            Icon = "📄",
            Category = PromptCategory.Analitik,
            Prompt = "Catat jumlah objek per menit ke berkas CSV harian, dengan rotasi berkas otomatis "
                + "tengah malam dan header kolom yang jelas.",
        },
        new()
        {
            Title = "Deteksi anomali gerakan",
            TitleEnglish = "Motion anomaly detection",
            Icon = "⚡",
            Category = PromptCategory.Analitik,
            Prompt = "Pelajari pola gerakan normal selama beberapa menit pertama, lalu beri peringatan "
                + "kalau ada gerakan yang menyimpang jauh dari pola itu.",
        },

        // ------------------------------------------------------ Keselamatan
        new()
        {
            Title = "Alarm zona berbahaya",
            TitleEnglish = "Danger zone alarm",
            Icon = "⚠️",
            Category = PromptCategory.Keselamatan,
            Prompt = "Buat monitor yang membunyikan alarm kalau ada orang lebih dekat dari 1,5 meter "
                + "dari kamera, dengan histeresis supaya alarm tidak berkedip di ambang batas.",
        },
        new()
        {
            Title = "Pemantau jarak antar orang",
            TitleEnglish = "Distance between people",
            Icon = "↔️",
            Category = PromptCategory.Keselamatan,
            Prompt = "Ukur jarak 3D antar orang dan tandai dengan garis merah pasangan yang berjarak "
                + "kurang dari 1 meter. Jelaskan kenapa jarak 3D lebih tepat daripada jarak piksel.",
        },
        new()
        {
            Title = "Deteksi orang jatuh",
            TitleEnglish = "Fall detection",
            Icon = "🆘",
            Category = PromptCategory.Keselamatan,
            Prompt = "Deteksi kemungkinan orang jatuh dari perubahan mendadak rasio tinggi-lebar kotak "
                + "orang dan posisi vertikalnya, lalu kirim notifikasi. Sebutkan keterbatasan pendekatan ini.",
        },
        new()
        {
            Title = "Cek kelengkapan APD",
            TitleEnglish = "PPE compliance check",
            Icon = "🦺",
            Category = PromptCategory.Keselamatan,
            Prompt = "Buat aplikasi yang memeriksa apakah setiap pekerja memakai helm dan rompi, "
                + "dan mencatat pelanggaran beserta cuplikan gambarnya.",
        },

        // --------------------------------------------------------- Industri
        new()
        {
            Title = "Inspeksi cacat produk",
            TitleEnglish = "Product defect inspection",
            Icon = "🔬",
            Category = PromptCategory.Industri,
            Prompt = "Buat aplikasi inspeksi yang mengklasifikasi produk lolos atau cacat, mencatat "
                + "hasilnya ke CSV, dan meneruskan hasil dengan keyakinan rendah ke pemeriksaan manual.",
        },
        new()
        {
            Title = "Hitung barang di konveyor",
            TitleEnglish = "Count items on a conveyor",
            Icon = "🏭",
            Category = PromptCategory.Industri,
            Prompt = "Hitung barang yang lewat di konveyor tanpa menghitung ganda barang yang sama, "
                + "dan tampilkan laju barang per menit.",
        },
        new()
        {
            Title = "Monitor stok rak",
            TitleEnglish = "Shelf stock monitor",
            Icon = "🏪",
            Category = PromptCategory.Industri,
            Prompt = "Pantau jumlah produk di rak dan kirim peringatan kalau stoknya turun di bawah ambang. "
                + "Haluskan angkanya supaya orang yang lewat di depan rak tidak memicu peringatan palsu.",
        },
        new()
        {
            Title = "Ukur dimensi paket",
            TitleEnglish = "Measure package dimensions",
            Icon = "📦",
            Category = PromptCategory.Industri,
            Prompt = "Pakai kedalaman untuk memperkirakan panjang, lebar, dan tinggi kardus di depan kamera, "
                + "lalu tampilkan volumenya dalam liter.",
        },

        // -------------------------------------------------------------- Web
        new()
        {
            Title = "Dashboard web live",
            TitleEnglish = "Live web dashboard",
            Icon = "⚡",
            Category = PromptCategory.Web,
            Prompt = "Buatkan aplikasi Blazor Server yang menampilkan video kamera dan daftar objek "
                + "terdeteksi secara real-time, dengan tema gelap dan tata letak yang responsif di ponsel.",
        },
        new()
        {
            Title = "REST API untuk sistem lain",
            TitleEnglish = "REST API for other systems",
            Icon = "🔌",
            Category = PromptCategory.Web,
            Prompt = "Buat REST API yang memaparkan deteksi terbaru sebagai JSON dan pembacaan jarak "
                + "pada koordinat tertentu, lengkap dengan endpoint health check.",
        },
        new()
        {
            Title = "Kirim event ke MQTT",
            TitleEnglish = "Publish events to MQTT",
            Icon = "📡",
            Category = PromptCategory.Web,
            Prompt = "Terbitkan setiap kejadian deteksi ke broker MQTT dengan topik per jenis objek, "
                + "dan sertakan penanganan koneksi putus.",
        },
        new()
        {
            Title = "Streaming MJPEG",
            TitleEnglish = "MJPEG streaming",
            Icon = "🎬",
            Category = PromptCategory.Web,
            Prompt = "Buat endpoint MJPEG yang bisa dibuka langsung di browser atau VLC, "
                + "dengan pembatasan fps supaya tidak membanjiri jaringan.",
        },

        // --------------------------------------------------------- Lanjutan
        new()
        {
            Title = "Dua kamera sekaligus",
            TitleEnglish = "Two cameras at once",
            Icon = "👯",
            Category = PromptCategory.Lanjutan,
            Prompt = "Buka dua perangkat OAK bersamaan dan tampilkan keduanya dalam satu jendela, "
                + "dengan penanganan yang benar kalau salah satu perangkat dicabut saat berjalan.",
        },
        new()
        {
            Title = "Gabungkan IMU dan video",
            TitleEnglish = "Fuse IMU with video",
            Icon = "🧭",
            Category = PromptCategory.Lanjutan,
            Prompt = "Gabungkan data IMU dengan frame video untuk menandai frame yang terekam saat "
                + "kamera bergoyang, lalu buang frame itu dari hasil deteksi.",
        },
        new()
        {
            Title = "Post-processing dengan ML.NET",
            TitleEnglish = "Post-processing with ML.NET",
            Icon = "🧮",
            Category = PromptCategory.Lanjutan,
            Prompt = "Ambil tensor keluaran mentah dari neural network dan proses lanjut dengan ML.NET "
                + "untuk mengelompokkan objek berdasarkan posisinya.",
        },
        new()
        {
            Title = "Optimalkan latensi",
            TitleEnglish = "Optimise latency",
            Icon = "🚀",
            Category = PromptCategory.Lanjutan,
            Prompt = "Tinjau kode saya dan kurangi latensi dari kamera sampai tampilan. Sebutkan "
                + "setiap perubahan beserta perkiraan berapa milidetik yang dihemat.",
        },
        new()
        {
            Title = "Tulis test untuk pipeline",
            TitleEnglish = "Write tests for my pipeline",
            Icon = "🧪",
            Category = PromptCategory.Lanjutan,
            Prompt = "Tulis unit test untuk kode vision saya memakai backend simulasi, "
                + "sehingga test bisa berjalan di CI tanpa kamera terpasang.",
        },
        new()
        {
            Title = "Siapkan untuk produksi",
            TitleEnglish = "Prepare for production",
            Icon = "🏗️",
            Category = PromptCategory.Lanjutan,
            Prompt = "Aplikasi saya sudah jalan di laptop. Apa saja yang harus saya ubah supaya bisa "
                + "berjalan berhari-hari tanpa diawasi: penanganan perangkat lepas, kebocoran memori, dan logging.",
        },
    ];
}
