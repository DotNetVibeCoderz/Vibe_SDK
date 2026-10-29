using System.IO.Compression;
using System.Text.Json.Serialization;

namespace DepthAI.Gallery.Inference;

/// <summary>Makna warna sebuah temuan: dipakai overlay dan daftar hasil secara konsisten.</summary>
public enum ResultTone
{
    /// <summary>Objek biasa; warnanya diambil dari peta Turbo per label.</summary>
    Neutral,
    /// <summary>Sesuai aturan, misalnya pekerja yang memakai helm.</summary>
    Good,
    /// <summary>Perlu dilihat, tapi belum pelanggaran.</summary>
    Notice,
    /// <summary>Pelanggaran atau bahaya.</summary>
    Alert,
}

/// <summary>Efek yang diterapkan pada gambar sebelum overlay digambar.</summary>
public enum ImageEffect
{
    None,
    /// <summary>Setiap kotak dipikselkan: penyamaran wajah.</summary>
    PixelateBoxes,
    /// <summary>Piksel di luar mask alfa diganti latar virtual.</summary>
    ReplaceBackground,
}

public enum MaskKind
{
    /// <summary>Nilai adalah indeks kelas; 255 berarti tidak ada kelas.</summary>
    Classes,
    /// <summary>Nilai 0–255 adalah keyakinan latar depan.</summary>
    Alpha,
    /// <summary>Nilai adalah nomor kotak di <see cref="GalleryResult.Boxes"/> ditambah satu; 0 berarti kosong.</summary>
    Instances,
}

/// <summary>Titik ternormalisasi di gambar asli.</summary>
public sealed record ResultPoint(float X, float Y, float Score);

/// <summary>Satu temuan berbentuk kotak, ternormalisasi terhadap gambar asli (0–1).</summary>
public sealed record ResultBox
{
    public required float X0 { get; init; }

    public required float Y0 { get; init; }

    public required float X1 { get; init; }

    public required float Y1 { get; init; }

    public required string Label { get; init; }

    public float Score { get; init; }

    public ResultTone Tone { get; init; }

    /// <summary>Keterangan tambahan di bawah label, misalnya "Pria · 31 th".</summary>
    public string? Caption { get; init; }

    /// <summary>Titik kunci: landmark wajah, sendi tubuh, atau ruas jari.</summary>
    public IReadOnlyList<ResultPoint>? Keypoints { get; init; }

    public float Area => Math.Max(0, X1 - X0) * Math.Max(0, Y1 - Y0);
}

/// <summary>Mask per piksel yang menutupi seluruh gambar asli.</summary>
public sealed class ResultMask
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required MaskKind Kind { get; init; }

    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>Nilai mask baris demi baris.</summary>
    [JsonIgnore]
    public byte[] Values { get; set; } = [];

    /// <summary>Bentuk tersimpan <see cref="Values"/>: Brotli lalu base64, agar rekaman tetap kecil.</summary>
    public string Data
    {
        get
        {
            using var output = new MemoryStream();
            using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize))
            {
                brotli.Write(Values);
            }

            return Convert.ToBase64String(output.ToArray());
        }
        set
        {
            using var input = new BrotliStream(new MemoryStream(Convert.FromBase64String(value)), CompressionMode.Decompress);
            using var output = new MemoryStream();
            input.CopyTo(output);
            Values = output.ToArray();
        }
    }
}

/// <summary>Peta nilai kontinu (misalnya kepadatan kerumunan) yang menutupi seluruh gambar.</summary>
public sealed class ResultHeatmap
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Nilai 0–1, baris demi baris.</summary>
    public required float[] Values { get; init; }
}

/// <summary>Satu batang skor klasifikasi.</summary>
public sealed record ResultScore(string Label, float Value);

/// <summary>Angka ringkas yang tampil di panel hasil.</summary>
public sealed record ResultMetric(string Label, string Value, ResultTone Tone = ResultTone.Neutral);

/// <summary>Asal sebuah hasil: perangkat apa, kapan, dan berapa lama.</summary>
public sealed record RunInfo
{
    public required string DeviceName { get; init; }

    public required string DeviceId { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Waktu dari gambar dikirim sampai semua hasil kembali ke host, milidetik.</summary>
    public required double RoundTripMilliseconds { get; init; }

    /// <summary>Jumlah inferensi di kamera untuk hasil ini; lebih dari satu pada use case bertahap.</summary>
    public int InferenceCount { get; init; } = 1;
}

/// <summary>Hasil satu use case pada satu gambar.</summary>
public sealed class GalleryResult
{
    /// <summary>Kalimat ringkas yang menjawab pertanyaan use case, misalnya "1 pekerja tanpa helm".</summary>
    public string Headline { get; set; } = string.Empty;

    public List<ResultBox> Boxes { get; set; } = [];

    /// <summary>Pasangan indeks titik kunci yang disambung garis (kerangka pose, jari).</summary>
    public List<int[]> Skeleton { get; set; } = [];

    public ResultMask? Mask { get; set; }

    public ResultHeatmap? Heatmap { get; set; }

    public List<ResultScore> Scores { get; set; } = [];

    public List<ResultMetric> Metrics { get; set; } = [];

    public ImageEffect Effect { get; set; }

    public RunInfo? Run { get; set; }
}
