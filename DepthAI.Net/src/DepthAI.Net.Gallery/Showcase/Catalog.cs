using DepthAI.Gallery.Inference;
using SkiaSharp;

namespace DepthAI.Gallery.Showcase;

/// <summary>
/// Use case yang dipamerkan galeri. Tiap entri memakai model publik Luxonis Model Zoo yang
/// benar-benar berjalan di OAK (RVC2), dan analisisnya menjawab pertanyaan yang diajukan
/// penggunanya di lapangan, bukan sekadar menggambar kotak.
/// </summary>
public static class Catalog
{
    public static IReadOnlyList<UseCase> All { get; } =
    [
        Traffic(),
        Ppe(),
        Plates(),
        Parcels(),
        PrivacyBlur(),
        Audience(),
        Posture(),
        Instances(),
        Scene(),
        VirtualBackground(),
        Crowd(),
        Documents(),
        Specimen(),
        Touchless(),
    ];

    /// <summary>Saringan sektor, urut sesuai bidang yang paling sering ditanyakan.</summary>
    public static IReadOnlyList<string> Sectors { get; } =
    [
        "Industri", "Perkantoran", "Pendidikan", "Keamanan", "Hiburan",
        "Ritel", "Kota pintar", "Transportasi", "Logistik", "Kesehatan", "Robotik",
    ];

    private static readonly Dictionary<string, string> CocoNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["person"] = "orang", ["bicycle"] = "sepeda", ["car"] = "mobil", ["motorcycle"] = "motor",
        ["bus"] = "bus", ["truck"] = "truk", ["traffic light"] = "lampu lalu lintas", ["stop sign"] = "rambu stop",
        ["bench"] = "bangku", ["dog"] = "anjing", ["cat"] = "kucing", ["backpack"] = "ransel",
        ["handbag"] = "tas tangan", ["umbrella"] = "payung", ["bottle"] = "botol", ["cup"] = "cangkir",
        ["chair"] = "kursi", ["couch"] = "sofa", ["potted plant"] = "tanaman", ["dining table"] = "meja",
        ["tv"] = "televisi", ["laptop"] = "laptop", ["mouse"] = "tetikus", ["keyboard"] = "papan ketik",
        ["cell phone"] = "ponsel", ["book"] = "buku", ["clock"] = "jam", ["vase"] = "vas",
        ["bird"] = "burung", ["fire hydrant"] = "hidran",
    };

    private static readonly HashSet<string> Vehicles = new(StringComparer.OrdinalIgnoreCase)
    {
        "bicycle", "car", "motorcycle", "bus", "truck",
    };

    private static string Coco(string label) => CocoNames.TryGetValue(label, out var name) ? name : label;

    /// <summary>"3 orang, 2 mobil" dari daftar label, terbanyak dulu.</summary>
    private static string CountPhrase(IEnumerable<string> labels, int take = 4)
        => string.Join(", ", labels.GroupBy(l => l).OrderByDescending(g => g.Count()).Take(take).Select(g => $"{g.Count()} {g.Key}"));

    private static UseCase Traffic() => new()
    {
        Id = "traffic",
        Title = "Hitung arus simpang jalan",
        Summary = "Menghitung pejalan kaki dan kendaraan per jenis di persimpangan.",
        Application = "Dinas perhubungan menyetel durasi lampu dan merencanakan penyeberangan dari hitungan "
            + "per simpang. Inferensi terjadi di kamera, jadi yang dikirim ke pusat hanya angka, bukan video warga.",
        Sectors = ["Kota pintar", "Transportasi"],
        Primary = new("yolov6-nano", "r2-coco-512x288"),
        Samples = ["street-intersection.jpg", "suburban-street.jpg"],
        Snippet = SnippetStyle.Detections,
        SnippetBody = """
            var pejalanKaki = result.Detections.Count(d => d.Label == "person");
            var kendaraan = result.Detections.Count(d => d.Label is "car" or "bus" or "truck" or "motorcycle" or "bicycle");
            Console.WriteLine($"{pejalanKaki} pejalan kaki, {kendaraan} kendaraan");
            """,
        Analyze = ctx =>
        {
            var detections = YoloDetections.Decode(ctx.Tensors, ctx.Metadata);
            var result = new GalleryResult();
            foreach (var d in detections)
            {
                result.Boxes.Add(ctx.Detection(d, Coco(d.Label)));
            }

            var people = detections.Count(d => d.Label == "person");
            var vehicles = detections.Where(d => Vehicles.Contains(d.Label)).ToList();
            result.Headline = $"{people} pejalan kaki, {vehicles.Count} kendaraan";
            result.Metrics.Add(new("Pejalan kaki", people.ToString()));
            result.Metrics.Add(new("Kendaraan", vehicles.Count.ToString()));
            foreach (var group in vehicles.GroupBy(d => Coco(d.Label)).OrderByDescending(g => g.Count()))
            {
                result.Metrics.Add(new($"  {group.Key}", group.Count().ToString()));
            }

            return Task.FromResult(result);
        },
    };

    private static UseCase Ppe() => new()
    {
        Id = "ppe",
        Title = "Kepatuhan APD di proyek",
        Summary = "Menandai pekerja tanpa helm atau rompi keselamatan.",
        Application = "Pengawas K3 di proyek konstruksi dan pabrik mendapat peringatan saat ada yang masuk area "
            + "kerja tanpa APD, lengkap dengan jumlah pelanggaran per shift untuk laporan.",
        Sectors = ["Industri", "Keamanan"],
        Primary = new("ppe-detection", "640x640"),
        Samples = ["construction-site.jpg"],
        Snippet = SnippetStyle.Detections,
        SnippetBody = """
            var pelanggaran = result.Detections
                .Where(d => d.Label is "NO-Hardhat" or "NO-Safety Vest" or "NO-Mask")
                .ToList();
            if (pelanggaran.Count > 0)
                Console.WriteLine($"Peringatan K3: {pelanggaran.Count} pelanggaran APD");
            """,
        Analyze = ctx =>
        {
            var names = new Dictionary<string, (string Name, ResultTone Tone)>
            {
                ["Hardhat"] = ("helm", ResultTone.Good),
                ["Safety Vest"] = ("rompi", ResultTone.Good),
                ["Mask"] = ("masker", ResultTone.Good),
                ["NO-Hardhat"] = ("tanpa helm", ResultTone.Alert),
                ["NO-Safety Vest"] = ("tanpa rompi", ResultTone.Alert),
                ["NO-Mask"] = ("tanpa masker", ResultTone.Notice),
                ["Person"] = ("pekerja", ResultTone.Neutral),
                ["Safety Cone"] = ("kerucut", ResultTone.Neutral),
                ["machinery"] = ("alat berat", ResultTone.Neutral),
                ["vehicle"] = ("kendaraan", ResultTone.Neutral),
            };

            var detections = YoloDetections.Decode(ctx.Tensors, ctx.Metadata);
            var result = new GalleryResult();
            foreach (var d in detections)
            {
                var (name, tone) = names.TryGetValue(d.Label, out var known) ? known : (d.Label, ResultTone.Neutral);
                result.Boxes.Add(ctx.Detection(d, name, tone));
            }

            int Count(string label) => detections.Count(d => d.Label == label);
            var violations = Count("NO-Hardhat") + Count("NO-Safety Vest");
            var parts = new List<string>();
            if (Count("NO-Hardhat") > 0) parts.Add($"{Count("NO-Hardhat")} tanpa helm");
            if (Count("NO-Safety Vest") > 0) parts.Add($"{Count("NO-Safety Vest")} tanpa rompi");

            result.Headline = violations == 0
                ? "Tidak ada pelanggaran helm atau rompi"
                : $"{violations} pelanggaran: {string.Join(", ", parts)}";
            result.Metrics.Add(new("Pekerja", Count("Person").ToString()));
            result.Metrics.Add(new("Memakai helm", Count("Hardhat").ToString(), ResultTone.Good));
            result.Metrics.Add(new("Memakai rompi", Count("Safety Vest").ToString(), ResultTone.Good));
            result.Metrics.Add(new("Pelanggaran", violations.ToString(), violations > 0 ? ResultTone.Alert : ResultTone.Good));
            return Task.FromResult(result);
        },
    };

    private static UseCase Plates() => new()
    {
        Id = "plates",
        Title = "Gerbang parkir otomatis",
        Summary = "Menemukan plat nomor kendaraan yang mendekati palang.",
        Application = "Pengelola gedung dan kawasan memakai lokasi plat untuk memotong gambar yang dikirim ke OCR, "
            + "membuka palang untuk kendaraan terdaftar, dan mencatat jam masuk tanpa karcis.",
        Sectors = ["Transportasi", "Keamanan"],
        Primary = new("license-plate-detection", "640x640"),
        Samples = ["parking-gate.jpg"],
        Note = "Model ini menemukan plat, bukan membaca hurufnya. Pembacaan karakter dijalankan sebagai tahap kedua pada potongan platnya.",
        Snippet = SnippetStyle.Detections,
        SnippetBody = """
            foreach (var plat in result.Detections)
            {
                var (x, y, w, h) = plat.Box.ToPixels(frame.Width, frame.Height);
                // Potong wilayah ini lalu kirim ke OCR untuk membaca nomornya.
                Console.WriteLine($"Plat di ({x}, {y}) {w}×{h} px, yakin {plat.Confidence:P0}");
            }
            """,
        Analyze = ctx =>
        {
            var detections = YoloDetections.Decode(ctx.Tensors, ctx.Metadata);
            var result = new GalleryResult();
            foreach (var d in detections)
            {
                result.Boxes.Add(ctx.Detection(d, "plat nomor"));
            }

            result.Headline = detections.Count switch
            {
                0 => "Tidak ada plat terlihat",
                1 => "1 plat siap dibaca",
                var n => $"{n} plat siap dibaca",
            };
            result.Metrics.Add(new("Plat terdeteksi", detections.Count.ToString()));
            if (detections.Count > 0)
            {
                result.Metrics.Add(new("Keyakinan tertinggi", $"{detections.Max(d => d.Confidence):P0}"));
            }

            return Task.FromResult(result);
        },
    };

    private static UseCase Parcels() => new()
    {
        Id = "parcels",
        Title = "Pemindaian label paket",
        Summary = "Menemukan semua kode QR pada tumpukan paket sekaligus.",
        Application = "Gudang dan pusat sortir memindai satu palet penuh dari jarak dua meter alih-alih satu per satu "
            + "dengan pemindai tangan; posisi tiap kode dipakai untuk memotong gambar sebelum didekode.",
        Sectors = ["Logistik", "Ritel"],
        Primary = new("qrdet", "nano-512x288"),
        Samples = ["warehouse-parcels.jpg"],
        Note = "Model menemukan letak kode QR. Isi kodenya dibaca di host dengan pustaka QR biasa pada potongannya.",
        Snippet = SnippetStyle.Detections,
        SnippetBody = """
            Console.WriteLine($"{result.Count} kode QR pada palet ini");
            foreach (var kode in result.Detections.OrderBy(d => d.Box.YMin))
                Console.WriteLine($"  QR di {kode.Box}");
            """,
        Analyze = ctx =>
        {
            var detections = YoloDetections.Decode(ctx.Tensors, ctx.Metadata);
            var result = new GalleryResult();
            foreach (var d in detections)
            {
                result.Boxes.Add(ctx.Detection(d, "QR"));
            }

            result.Headline = detections.Count == 0 ? "Tidak ada kode QR terlihat" : $"{detections.Count} kode QR ditemukan";
            result.Metrics.Add(new("Kode QR", detections.Count.ToString()));
            return Task.FromResult(result);
        },
    };

    private static UseCase PrivacyBlur() => new()
    {
        Id = "privacy",
        Title = "Penyamaran wajah otomatis",
        Summary = "Memikselkan setiap wajah sebelum gambar meninggalkan kamera.",
        Application = "CCTV kantor dan ruang publik bisa merekam alur orang untuk analitik tanpa menyimpan identitas: "
            + "wajah sudah disamarkan sebelum gambar disimpan atau dikirim, sejalan dengan UU Pelindungan Data Pribadi.",
        Sectors = ["Perkantoran", "Keamanan"],
        Primary = new("yunet", "640x480"),
        Samples = ["office-lobby.jpg", "mall-kiosk.jpg"],
        SnippetBody = """
            // YuNetDecoder ada di src/DepthAI.Net.Gallery/Inference/Decoders.cs
            var wajah = YuNetDecoder.Decode(result.Tensors, 640, 480);
            foreach (var w in wajah)
                Pixelate(gambar, w.Box);   // samarkan sebelum disimpan
            """,
        Analyze = ctx =>
        {
            var faces = YuNetDecoder.Decode(ctx.Tensors, ctx.InputWidth, ctx.InputHeight);
            var result = new GalleryResult { Effect = ImageEffect.PixelateBoxes };
            foreach (var face in faces)
            {
                result.Boxes.Add(ctx.Box(face.Box, "wajah", keypoints: [.. face.Landmarks.Select(l => ctx.Point(l.X, l.Y))]));
            }

            result.Headline = faces.Count == 0 ? "Tidak ada wajah terlihat" : $"{faces.Count} wajah disamarkan";
            result.Metrics.Add(new("Wajah", faces.Count.ToString()));
            return Task.FromResult(result);
        },
    };

    private static UseCase Audience() => new()
    {
        Id = "audience",
        Title = "Analitik pengunjung",
        Summary = "Memperkirakan umur, gender, dan ekspresi orang yang melihat layar.",
        Application = "Pemilik layar iklan dan kios di mal mengukur siapa yang benar-benar menonton dan bagaimana "
            + "reaksinya, dalam bentuk angka agregat. Wajah tidak disimpan; yang tercatat hanya statistik.",
        Sectors = ["Ritel", "Hiburan"],
        Primary = new("yunet", "640x480"),
        Stages =
        [
            new("age-gender", new("age-gender-recognition", "62x62")),
            new("emotion", new("emotion-recognition", "260x260")),
        ],
        Samples = ["mall-kiosk.jpg", "office-lobby.jpg"],
        Note = "Umur dan gender adalah perkiraan statistik dari penampilan, bukan identitas. Gunakan hanya sebagai angka agregat.",
        LiveFps = 10,
        SnippetBody = """
            // Tahap 1: wajah. Potongannya dikirim ke model umur/gender dan emosi di kamera yang sama;
            // hasilnya muncul di stream "age-gender" dan "emotion" (umur = age_conv3 × 100).
            foreach (var wajah in YuNetDecoder.Decode(result.Tensors, 640, 480))
            {
                using var potongan = ImageIo.CropToModel(gambar, Persegi(wajah.Box), 62, 62);
                device.SendFrame("age-gender-in", potongan);
            }
            """,
        Analyze = async ctx =>
        {
            var emotionNames = new Dictionary<string, string>
            {
                ["Anger"] = "marah", ["Contempt"] = "sinis", ["Disgust"] = "jijik", ["Fear"] = "takut",
                ["Happiness"] = "senang", ["Neutral"] = "netral", ["Sadness"] = "sedih", ["Surprise"] = "terkejut",
            };
            var emotionLabels = ctx.StageMetadata("emotion").Labels;

            var faces = YuNetDecoder.Decode(ctx.Tensors, ctx.InputWidth, ctx.InputHeight)
                .OrderByDescending(f => f.Box.Area)
                .Take(8)
                .ToList();

            var result = new GalleryResult();
            var ages = new List<float>();
            var genders = new List<string>();
            var emotions = new List<string>();

            foreach (var face in faces)
            {
                var box = ctx.Box(face.Box, "wajah");
                var region = Square(ctx.PixelRect(box), 1.2f, ctx.Image);

                using var ageInput = ImageIo.CropToModel(ctx.Image, region, 62, 62);
                var ageGender = await ctx.RunStageAsync("age-gender", ageInput);
                var age = ageGender["age_conv3"].Span[0] * 100f;
                var gender = ageGender["prob"].Span[1] > ageGender["prob"].Span[0] ? "pria" : "wanita";

                // Diuji di OAK-1 dengan urutan RGB juga: emosi teratasnya sama, jadi tetap BGR
                // seperti konvensi semua arsip Luxonis (reverse_channels false).
                using var emotionInput = ImageIo.CropToModel(ctx.Image, region, 260, 260);
                var emotionTensors = await ctx.RunStageAsync("emotion", emotionInput);
                var probabilities = ScoreMath.AsProbabilities(emotionTensors.Values.First().Span);
                var best = Array.IndexOf(probabilities, probabilities.Max());
                var emotion = emotionNames.GetValueOrDefault(emotionLabels[best], emotionLabels[best]);
                emotions.Add(emotion);
                ages.Add(age);
                genders.Add(gender);
                result.Boxes.Add(box with
                {
                    Label = emotion,
                    Caption = $"{gender} · ±{age:0} th",
                    Tone = emotion == "senang" ? ResultTone.Good : emotion is "marah" or "jijik" or "sedih" ? ResultTone.Notice : ResultTone.Neutral,
                    Keypoints = [.. face.Landmarks.Select(l => ctx.Point(l.X, l.Y))],
                });
            }

            if (faces.Count == 0)
            {
                result.Headline = "Belum ada yang melihat layar";
                return result;
            }

            // Sebaran dihitung dari emosi teratas tiap wajah. Rata-rata probabilitas sempat
            // dicoba, tapi keluaran model ini landai sehingga emosi yang tidak dominan di
            // wajah mana pun bisa "menang".
            var shares = emotions.GroupBy(e => e)
                .Select(g => new ResultScore(g.Key, g.Count() / (float)faces.Count))
                .OrderByDescending(s => s.Value)
                .ToList();
            result.Headline = shares[0].Value > 0.5f
                ? $"{faces.Count} penonton, kebanyakan {shares[0].Label}"
                : $"{faces.Count} penonton, ekspresi beragam";
            result.Metrics.Add(new("Penonton", faces.Count.ToString()));
            result.Metrics.Add(new("Rata-rata umur", $"±{ages.Average():0} th"));
            result.Metrics.Add(new("Pria / wanita", $"{genders.Count(g => g == "pria")} / {genders.Count(g => g == "wanita")}"));
            result.Scores.AddRange(shares.Take(5));
            return result;
        },
    };

    /// <summary>Wilayah persegi berpusat di kotak, diperbesar, dan dijaga di dalam gambar.</summary>
    private static SKRect Square(SKRect box, float scale, SKBitmap image)
    {
        var side = Math.Min(Math.Max(box.Width, box.Height) * scale, Math.Min(image.Width, image.Height));
        var x = Math.Clamp(box.MidX - (side / 2), 0, image.Width - side);
        var y = Math.Clamp(box.MidY - (side / 2), 0, image.Height - side);
        return SKRect.Create(x, y, side, side);
    }

    private static UseCase Posture() => new()
    {
        Id = "posture",
        Title = "Pelatih gerak dan postur",
        Summary = "Membaca 17 sendi tubuh lalu mengenali gerakan dan sudut lutut.",
        Application = "Guru olahraga, fisioterapis, dan program K3 ergonomi memantau apakah gerakan dilakukan dengan "
            + "benar, misalnya kedalaman squat atau postur mengangkat beban, tanpa sensor yang dipasang di badan.",
        Sectors = ["Pendidikan", "Kesehatan"],
        Primary = new("yolov8-nano-pose-estimation", "coco-512x288"),
        Samples = ["pe-class.jpg", "office-lobby.jpg"],
        SnippetBody = """
            foreach (var orang in PoseDecoder.Decode(result.Tensors))
            {
                var lutut = Sudut(orang.Keypoints[11], orang.Keypoints[13], orang.Keypoints[15]); // pinggul–lutut–mata kaki
                if (lutut < 110) Console.WriteLine($"Squat cukup dalam ({lutut:0}°)");
            }
            """,
        Analyze = ctx =>
        {
            var people = PoseDecoder.Decode(ctx.Tensors);
            var result = new GalleryResult { Skeleton = [.. PoseDecoder.Skeleton] };
            var moves = new List<string>();

            foreach (var person in people)
            {
                var (move, caption) = ReadPose(person.Keypoints);
                moves.Add(move);
                result.Boxes.Add(ctx.Box(
                    person.Box,
                    move,
                    move == "berdiri" ? ResultTone.Neutral : ResultTone.Good,
                    caption,
                    [.. person.Keypoints.Select(k => ctx.Point(k.X, k.Y, k.Score))]));
            }

            result.Headline = people.Count == 0 ? "Tidak ada orang terlihat utuh" : CountPhrase(moves);
            result.Metrics.Add(new("Orang", people.Count.ToString()));
            foreach (var group in moves.GroupBy(m => m))
            {
                result.Metrics.Add(new($"  {group.Key}", group.Count().ToString()));
            }

            return Task.FromResult(result);
        },
    };

    /// <summary>Aturan sederhana dari sudut sendi; cukup untuk latihan dasar di kelas.</summary>
    internal static (string Move, string Caption) ReadPose((float X, float Y, float Score)[] k)
    {
        const float visible = 0.5f;
        bool Seen(params int[] joints) => joints.All(j => k[j].Score >= visible);

        float Angle(int a, int b, int c)
        {
            var (ax, ay) = (k[a].X - k[b].X, k[a].Y - k[b].Y);
            var (cx, cy) = (k[c].X - k[b].X, k[c].Y - k[b].Y);
            var cos = ((ax * cx) + (ay * cy)) / (MathF.Sqrt((ax * ax) + (ay * ay)) * MathF.Sqrt((cx * cx) + (cy * cy)) + 1e-6f);
            return MathF.Acos(Math.Clamp(cos, -1, 1)) * 180f / MathF.PI;
        }

        float? left = Seen(11, 13, 15) ? Angle(11, 13, 15) : null;
        float? right = Seen(12, 14, 16) ? Angle(12, 14, 16) : null;
        var knees = $"lutut {(left is { } l ? $"{l:0}°" : "–")} / {(right is { } r ? $"{r:0}°" : "–")}";

        // Koordinat gambar: y kecil berarti lebih tinggi.
        if (Seen(0, 9, 10) && k[9].Y < k[0].Y && k[10].Y < k[0].Y)
        {
            return ("tangan di atas", knees);
        }

        if (left is { } lk && right is { } rk)
        {
            if (lk < 125 && rk < 125)
            {
                return ("squat", knees);
            }

            if (Math.Min(lk, rk) < 125 && Math.Max(lk, rk) > 125)
            {
                return ("lunge", knees);
            }
        }

        return ("berdiri", knees);
    }

    private static UseCase Instances() => new()
    {
        Id = "instances",
        Title = "Inventaris ruang kerja",
        Summary = "Memisahkan setiap objek sampai ke bentuknya, bukan hanya kotaknya.",
        Application = "Pengelola gedung menghitung kursi terpakai dan perangkat di ruang bersama; robot layanan memakai "
            + "bentuk objek untuk memilih titik pegang atau jalur yang aman.",
        Sectors = ["Perkantoran", "Robotik"],
        Primary = new("yolov8-instance-segmentation-nano", "coco-512x288"),
        Samples = ["office-pantry.jpg", "street-intersection.jpg"],
        SnippetBody = """
            var objek = InstanceSegmentationDecoder.Decode(result.Tensors, inputWidth: 512);
            foreach (var o in objek)
            {
                var piksel = o.Mask.Count(m => m);   // luas bentuk objek di resolusi prototipe
                Console.WriteLine($"{Label(o.Box.Label)}: {piksel} piksel");
            }
            """,
        Analyze = ctx =>
        {
            var instances = InstanceSegmentationDecoder.Decode(ctx.Tensors, ctx.InputWidth);
            var labels = ctx.Metadata.Labels;
            var result = new GalleryResult();

            foreach (var instance in instances)
            {
                var name = Coco(instance.Box.Label < labels.Count ? labels[instance.Box.Label] : instance.Box.Label.ToString());
                result.Boxes.Add(ctx.Box(instance.Box, name));
            }

            if (instances.Count > 0)
            {
                var (mw, mh) = (instances[0].MaskWidth, instances[0].MaskHeight);
                var (width, height) = MapResampler.OutputSize(ctx.Image.Width, ctx.Image.Height);
                result.Mask = new ResultMask
                {
                    Width = width,
                    Height = height,
                    Kind = MaskKind.Instances,
                    // Instans terurut skor menurun; yang lebih yakin menang di piksel yang tumpang tindih.
                    Values = MapResampler.Resample(mw, mh, ctx.Letterbox, ctx.Image.Width, ctx.Image.Height, (x, y) =>
                    {
                        for (var i = 0; i < instances.Count; i++)
                        {
                            if (instances[i].Mask[(y * mw) + x])
                            {
                                return (byte)(i + 1);
                            }
                        }

                        return (byte)0;
                    }),
                };
            }

            result.Headline = instances.Count == 0 ? "Tidak ada objek dikenali" : CountPhrase(result.Boxes.Select(b => b.Label));
            foreach (var group in result.Boxes.GroupBy(b => b.Label).OrderByDescending(g => g.Count()).Take(5))
            {
                result.Metrics.Add(new(group.Key, group.Count().ToString()));
            }

            return Task.FromResult(result);
        },
    };

    private static UseCase Scene() => new()
    {
        Id = "scene",
        Title = "Pemahaman adegan",
        Summary = "Memberi label kelas pada setiap piksel: orang, kendaraan, hewan, perabot.",
        Application = "Kendaraan otonom kecil, robot pengantar, dan kursi roda pintar perlu tahu bagian mana dari "
            + "pandangan yang berisi orang, kendaraan, atau rintangan sebelum memilih jalur.",
        Sectors = ["Robotik", "Transportasi"],
        Primary = new("deeplab-v3-plus", "256x256"),
        Samples = ["suburban-street.jpg", "office-pantry.jpg"],
        LiveFps = 10,
        SnippetBody = """
            // Cast_2 sudah berisi indeks kelas pemenang per piksel (256×256).
            var kelas = result["Cast_2"].Span;
            var orang = 0;
            foreach (var k in kelas) if ((int)k == 15) orang++;          // 15 = person (VOC)
            Console.WriteLine($"Orang menutupi {orang * 100f / kelas.Length:0.#}% pandangan");
            """,
        Analyze = ctx =>
        {
            var names = new[]
            {
                "latar", "pesawat", "sepeda", "burung", "perahu", "botol", "bus", "mobil", "kucing", "kursi", "sapi",
                "meja", "anjing", "kuda", "motor", "orang", "tanaman", "domba", "sofa", "kereta", "monitor",
            };

            var map = ctx.Tensors["Cast_2"];
            var (sw, sh) = (map.Shape[^1], map.Shape[^2]);
            var data = map.Span.ToArray();
            var (width, height) = MapResampler.OutputSize(ctx.Image.Width, ctx.Image.Height);
            var values = MapResampler.Resample(sw, sh, ctx.Letterbox, ctx.Image.Width, ctx.Image.Height, (x, y) =>
            {
                var c = (int)data[(y * sw) + x];
                return c <= 0 || c >= names.Length ? byte.MaxValue : (byte)c;
            });

            var result = new GalleryResult
            {
                Mask = new ResultMask { Width = width, Height = height, Kind = MaskKind.Classes, Labels = names, Values = values },
            };

            var coverage = values.Where(v => v != byte.MaxValue).GroupBy(v => v)
                .Select(g => new ResultScore(names[g.Key], g.Count() / (float)values.Length))
                .Where(s => s.Value >= 0.002f)
                .OrderByDescending(s => s.Value)
                .ToList();

            result.Scores.AddRange(coverage.Take(6));
            result.Headline = coverage.Count == 0 ? "Hanya latar yang terlihat" : $"Terlihat: {string.Join(", ", coverage.Take(4).Select(s => s.Label))}";
            result.Metrics.Add(new("Kelas terlihat", coverage.Count.ToString()));
            return Task.FromResult(result);
        },
    };

    private static UseCase VirtualBackground() => new()
    {
        Id = "background",
        Title = "Latar virtual konferensi",
        Summary = "Memisahkan orang dari latar dan menggantinya, langsung di kamera.",
        Application = "Kamera rapat dan kamera streaming bisa mengganti atau mengaburkan latar tanpa membebani laptop; "
            + "berguna untuk ruang kerja di rumah, kelas daring, dan studio kreator konten.",
        Sectors = ["Hiburan", "Perkantoran"],
        Primary = new("mediapipe-selfie-segmentation", "256x144"),
        Samples = ["video-call.jpg", "kiosk-palm.jpg"],
        LiveFps = 20,
        SnippetBody = """
            // Satu kanal 256×144: peluang tiap piksel adalah bagian orang.
            var peluang = result["output"].Span;
            for (var i = 0; i < peluang.Length; i++)
                alfa[i] = (byte)(Math.Clamp(peluang[i], 0, 1) * 255);   // campur dengan latar baru
            """,
        Analyze = ctx =>
        {
            var output = ctx.Tensors["output"];
            var (sw, sh) = (output.Shape[^1], output.Shape[^2]);
            var raw = output.Span.ToArray();
            var logits = raw.Any(v => v < 0 || v > 1);
            var (width, height) = MapResampler.OutputSize(ctx.Image.Width, ctx.Image.Height);
            var alpha = MapResampler.Resample(sw, sh, ctx.Letterbox, ctx.Image.Width, ctx.Image.Height, (x, y) =>
            {
                var v = raw[(y * sw) + x];
                return (byte)(Math.Clamp(logits ? PoseDecoder.Sigmoid(v) : v, 0, 1) * 255);
            });

            var person = alpha.Count(a => a >= 128) / (float)alpha.Length;
            return Task.FromResult(new GalleryResult
            {
                Effect = ImageEffect.ReplaceBackground,
                Mask = new ResultMask { Width = width, Height = height, Kind = MaskKind.Alpha, Values = alpha },
                Headline = person < 0.01f ? "Tidak ada orang di depan kamera" : "Latar diganti, orang tetap utuh",
                Metrics = [new("Area orang", $"{person:P0}")],
            });
        },
    };

    private static UseCase Crowd() => new()
    {
        Id = "crowd",
        Title = "Kepadatan kerumunan",
        Summary = "Memperkirakan jumlah orang dan titik terpadat dari peta kepadatan.",
        Application = "Panitia acara, stasiun, dan pengelola stadion memantau penumpukan di gerbang secara langsung "
            + "dan membuka jalur tambahan sebelum kerumunan menjadi berbahaya.",
        Sectors = ["Kota pintar", "Keamanan"],
        Primary = new("dm-count", "shb-426x240"),
        Samples = ["concert-crowd.jpg"],
        Note = "Dilatih untuk kerumunan padat dilihat dari atas: pada jalan yang lengang hitungannya berlebih. "
            + "Model ini juga berat untuk RVC2, sekitar 1,5 detik per gambar.",
        LiveFps = 2,
        SnippetBody = """
            // Jumlah semua sel peta kepadatan adalah perkiraan jumlah orang.
            var kepadatan = result["density_map"].Span;
            var jumlah = 0f;
            foreach (var d in kepadatan) jumlah += d;
            if (jumlah > 300) Console.WriteLine("Buka gerbang tambahan");
            """,
        Analyze = ctx =>
        {
            var map = ctx.Tensors["density_map"];
            var (sw, sh) = (map.Shape[^1], map.Shape[^2]);
            var data = map.Span.ToArray();
            var count = data.Sum(v => Math.Max(0, v));
            var peak = Math.Max(1e-6f, data.Max());

            var (width, height) = MapResampler.OutputSize(ctx.Image.Width, ctx.Image.Height);
            var heat = MapResampler.Resample(sw, sh, ctx.Letterbox, ctx.Image.Width, ctx.Image.Height,
                (x, y) => Math.Clamp(data[(y * sw) + x] / peak, 0, 1));

            // Sepertiga kiri, tengah, kanan: cukup untuk mengarahkan petugas ke gerbang yang tepat.
            var thirds = new float[3];
            for (var y = 0; y < sh; y++)
            {
                for (var x = 0; x < sw; x++)
                {
                    thirds[Math.Min(2, x * 3 / sw)] += Math.Max(0, data[(y * sw) + x]);
                }
            }

            var busiest = new[] { "kiri", "tengah", "kanan" }[Array.IndexOf(thirds, thirds.Max())];
            return Task.FromResult(new GalleryResult
            {
                Heatmap = new ResultHeatmap { Width = width, Height = height, Values = heat },
                Headline = $"Sekitar {count:0} orang, terpadat di {busiest}",
                Metrics =
                [
                    new("Perkiraan jumlah", $"±{count:0}", count > 300 ? ResultTone.Alert : ResultTone.Neutral),
                    new("Zona terpadat", busiest, ResultTone.Notice),
                ],
            });
        },
    };

    private static UseCase Documents() => new()
    {
        Id = "documents",
        Title = "Digitalisasi dokumen",
        Summary = "Menemukan setiap baris teks pada halaman yang difoto.",
        Application = "Loket layanan, bagian arsip, dan bank memotret surat atau formulir lalu hanya mengirim "
            + "potongan baris teks ke OCR, sehingga pemrosesan lebih cepat dan bagian kosong tidak ikut diunggah.",
        Sectors = ["Perkantoran", "Pendidikan"],
        Primary = new("paddle-text-detection", "320x576"),
        Samples = ["business-letter.jpg"],
        Note = "Model ini menemukan baris teks. Membaca isinya dilakukan tahap OCR pada tiap potongan.",
        LiveFps = 8,
        SnippetBody = """
            // Peta probabilitas teks 320×576 → kotak per baris.
            var baris = TextDetectionDecoder.Decode(result["output"]);
            foreach (var b in baris.OrderBy(b => b.Y0))
                KirimKeOcr(Potong(gambar, b));
            """,
        Analyze = ctx =>
        {
            var lines = TextDetectionDecoder.Decode(ctx.Tensors["output"]);
            var result = new GalleryResult();
            foreach (var line in lines.OrderBy(l => l.Y0))
            {
                result.Boxes.Add(ctx.Box(line, "teks"));
            }

            result.Headline = lines.Count == 0 ? "Tidak ada teks terlihat" : $"{lines.Count} baris teks siap dibaca";
            result.Metrics.Add(new("Baris teks", lines.Count.ToString()));
            return Task.FromResult(result);
        },
    };

    private static UseCase Specimen() => new()
    {
        Id = "specimen",
        Title = "Kenali objek untuk belajar",
        Summary = "Menebak objek utama di gambar dari 1.000 kategori ImageNet.",
        Application = "Alat peraga sains di sekolah dan museum: siswa mengarahkan kamera ke serangga, tumbuhan, atau "
            + "benda, lalu layar menampilkan namanya sebagai pintu masuk ke materi pelajaran.",
        Sectors = ["Pendidikan", "Ritel"],
        Primary = new("efficientnet-lite", "lite0-224x224"),
        Samples = ["butterfly.jpg"],
        Snippet = SnippetStyle.Classification,
        LiveFps = 15,
        SnippetBody = """
            foreach (var tebakan in result.Results.Take(3))
                Console.WriteLine($"{tebakan.Label}: {tebakan.Confidence:P1}");
            """,
        Analyze = ctx =>
        {
            var probabilities = ScoreMath.AsProbabilities(ctx.Tensors.Values.First().Span);
            var labels = ctx.Metadata.Labels;
            var top = probabilities
                .Select((p, i) => new ResultScore(i < labels.Count ? labels[i].Split(',')[0] : i.ToString(), p))
                .OrderByDescending(s => s.Value)
                .Take(5)
                .ToList();

            return Task.FromResult(new GalleryResult
            {
                Scores = top,
                Headline = $"{top[0].Label} ({top[0].Value:P0})",
                Metrics = [new("Tebakan utama", top[0].Label), new("Keyakinan", $"{top[0].Value:P1}")],
            });
        },
    };

    private static UseCase Touchless() => new()
    {
        Id = "touchless",
        Title = "Kios tanpa sentuh",
        Summary = "Mendeteksi telapak tangan terbuka sebagai tombol di udara.",
        Application = "Kios rumah sakit, lift, dan mesin antrean bisa dioperasikan dengan mengangkat tangan, "
            + "mengurangi titik sentuh bersama. Instalasi interaktif di museum memakai cara yang sama.",
        Sectors = ["Kesehatan", "Hiburan"],
        Primary = new("mediapipe-palm-detection", "192x192"),
        Samples = ["kiosk-palm.jpg"],
        LiveFps = 20,
        SnippetBody = """
            var telapak = PalmDecoder.Decode(result.Tensors);
            if (telapak.Count > 0)
                TekanTombol("Lanjut");   // telapak terbuka di depan kios
            """,
        Analyze = ctx =>
        {
            var palms = PalmDecoder.Decode(ctx.Tensors, ctx.InputWidth);
            var result = new GalleryResult { Skeleton = [[0, 1], [1, 2], [2, 3], [3, 4], [0, 5], [5, 6]] };
            foreach (var (box, points) in palms)
            {
                result.Boxes.Add(ctx.Normalized(box.X0, box.Y0, box.X1, box.Y1, "telapak", box.Score, ResultTone.Good,
                    keypoints: [.. points.Select(p => ctx.Point(p.X * ctx.InputWidth, p.Y * ctx.InputHeight))]));
            }

            result.Headline = palms.Count == 0 ? "Angkat telapak tangan ke arah kamera" : "Telapak terdeteksi: tombol ditekan";
            result.Metrics.Add(new("Telapak", palms.Count.ToString(), palms.Count > 0 ? ResultTone.Good : ResultTone.Neutral));
            return Task.FromResult(result);
        },
    };
}
