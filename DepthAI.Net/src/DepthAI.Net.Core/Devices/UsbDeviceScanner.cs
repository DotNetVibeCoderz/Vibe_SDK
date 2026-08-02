using System.Runtime.InteropServices;

namespace DepthAI.Devices;

/// <summary>Perangkat OAK yang terlihat di bus USB, terlepas dari tersedianya runtime native.</summary>
public sealed record UsbDeviceDescriptor
{
    /// <summary>Vendor ID; Luxonis OAK memakai VID Intel Movidius 0x03E7.</summary>
    public required ushort VendorId { get; init; }

    public required ushort ProductId { get; init; }

    /// <summary>Identitas perangkat menurut sistem operasi.</summary>
    public required string SystemPath { get; init; }

    /// <summary>Nama yang bisa dibaca manusia, diturunkan dari product ID.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Tahap boot perangkat. MyriadX muncul sebagai ROM bootloader sampai host
    /// mengunggah firmware; setelah itu ia muncul kembali dengan product ID berbeda.
    /// </summary>
    public required DeviceState State { get; init; }

    /// <summary>Serial number dari deskriptor USB, bila sistem operasi memaparkannya.</summary>
    public string? SerialNumber { get; init; }

    public override string ToString()
        => $"{Description} [{VendorId:X4}:{ProductId:X4}] {State}";
}

/// <summary>
/// Mendeteksi perangkat OAK yang tercolok langsung dari bus USB.
/// </summary>
/// <remarks>
/// <para>
/// Ada karena satu alasan konkret: tanpa runtime native, <see cref="DepthAi.ListDevices"/>
/// hanya melaporkan perangkat simulasi — padahal bisa jadi ada OAK sungguhan yang
/// tercolok. Melaporkan "tidak ada perangkat" dalam keadaan itu menyesatkan, dan
/// membuat pengguna mengira kameranya rusak padahal yang kurang adalah pustaka native.
/// </para>
/// <para>
/// Pemindai ini <b>tidak</b> bisa membuka perangkat atau menjalankan pipeline; itu tetap
/// membutuhkan depthai-core. Yang bisa dijawabnya hanya: apakah ada perangkat OAK
/// yang secara fisik terpasang.
/// </para>
/// </remarks>
public static partial class UsbDeviceScanner
{
    /// <summary>Vendor ID Intel Movidius, dipakai semua board OAK berbasis RVC2.</summary>
    public const ushort MovidiusVendorId = 0x03E7;

    /// <summary>Product ID yang dikenali beserta arti tahap boot-nya.</summary>
    private static readonly Dictionary<ushort, (string Description, DeviceState State)> KnownProducts = new()
    {
        // ROM bootloader: perangkat baru dicolok dan belum menerima firmware.
        [0x2485] = ("OAK / Movidius MyriadX (belum di-boot)", DeviceState.Booting),

        // Firmware sudah berjalan; perangkat siap menerima pipeline.
        [0xF63B] = ("OAK / Movidius MyriadX (sudah di-boot)", DeviceState.Booted),

        // Generasi Myriad2 lama.
        [0x2150] = ("Movidius Myriad2 (belum di-boot)", DeviceState.Booting),
    };

    /// <summary>
    /// Memindai bus USB untuk perangkat OAK.
    /// </summary>
    /// <remarks>
    /// Mengembalikan daftar kosong pada platform yang belum punya implementasi, bukan
    /// melempar: gagal memindai bukan alasan untuk menjatuhkan enumerasi perangkat.
    /// </remarks>
    public static IReadOnlyList<UsbDeviceDescriptor> Scan()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return ScanWindows();
            }

            return RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? ScanLinux() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>True bila ada perangkat OAK yang secara fisik terpasang.</summary>
    public static bool AnyDevicePresent() => Scan().Count > 0;

    /// <summary>
    /// Membaca perangkat USB yang <b>sedang</b> terpasang lewat Configuration Manager.
    /// </summary>
    /// <remarks>
    /// Registry <c>Enum\USB</c> sengaja tidak dipakai: kunci di sana bertahan setelah
    /// perangkat dicabut, dan OAK muncul dengan dua product ID berbeda sebelum dan
    /// sesudah boot — hasilnya satu kamera fisik terbaca sebagai dua perangkat.
    /// <c>CM_Get_Device_ID_List</c> dengan filter PRESENT memberi daftar yang sama
    /// dengan yang dipakai Windows sendiri untuk menentukan perangkat mana yang hadir.
    /// </remarks>
    private static List<UsbDeviceDescriptor> ScanWindows()
    {
        var results = new List<UsbDeviceDescriptor>();

        foreach (var deviceId in EnumeratePresentUsbDeviceIds())
        {
            if (!TryParseWindowsId(deviceId, out var vendorId, out var productId)
                || vendorId != MovidiusVendorId)
            {
                continue;
            }

            var known = KnownProducts.TryGetValue(productId, out var entry)
                ? entry
                : ($"Perangkat Movidius tak dikenal (PID {productId:X4})", DeviceState.Unknown);

            // Segmen terakhir device ID adalah instance; untuk perangkat yang sudah
            // di-boot, isinya persis MxId perangkat.
            var instance = deviceId[(deviceId.LastIndexOf('\\') + 1)..];

            results.Add(new UsbDeviceDescriptor
            {
                VendorId = vendorId,
                ProductId = productId,
                SystemPath = deviceId,
                Description = known.Item1,
                State = known.Item2,
                SerialNumber = LooksLikeSerial(instance) ? instance : null,
            });
        }

        return results;
    }

    /// <summary>Mengambil device ID USB yang saat ini hadir dari Configuration Manager.</summary>
    private static IEnumerable<string> EnumeratePresentUsbDeviceIds()
    {
        const uint FilterEnumerator = 0x00000001;
        const uint FilterPresent = 0x00000100;
        const uint Success = 0;

        if (NativeConfigManager.GetDeviceIdListSize(out var length, "USB", FilterEnumerator | FilterPresent) != Success
            || length == 0)
        {
            return [];
        }

        var buffer = new char[length];
        if (NativeConfigManager.GetDeviceIdList("USB", buffer, length, FilterEnumerator | FilterPresent) != Success)
        {
            return [];
        }

        // Hasilnya multi-string: entri dipisah NUL dan diakhiri NUL ganda.
        return new string(buffer)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static partial class NativeConfigManager
    {
        [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial uint GetDeviceIdListSize(out uint length, string? filter, uint flags);

        [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial uint GetDeviceIdList(string? filter, [Out] char[] buffer, uint bufferLength, uint flags);
    }

    /// <summary>Membaca perangkat USB dari sysfs pada Linux.</summary>
    private static List<UsbDeviceDescriptor> ScanLinux()
    {
        var results = new List<UsbDeviceDescriptor>();
        const string Root = "/sys/bus/usb/devices";

        if (!Directory.Exists(Root))
        {
            return results;
        }

        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            var vendorPath = Path.Combine(directory, "idVendor");
            var productPath = Path.Combine(directory, "idProduct");

            if (!File.Exists(vendorPath) || !File.Exists(productPath))
            {
                continue;
            }

            if (!ushort.TryParse(File.ReadAllText(vendorPath).Trim(),
                    System.Globalization.NumberStyles.HexNumber, null, out var vendorId)
                || vendorId != MovidiusVendorId)
            {
                continue;
            }

            if (!ushort.TryParse(File.ReadAllText(productPath).Trim(),
                    System.Globalization.NumberStyles.HexNumber, null, out var productId))
            {
                continue;
            }

            var known = KnownProducts.TryGetValue(productId, out var entry)
                ? entry
                : ($"Perangkat Movidius tak dikenal (PID {productId:X4})", DeviceState.Unknown);

            var serialPath = Path.Combine(directory, "serial");

            results.Add(new UsbDeviceDescriptor
            {
                VendorId = vendorId,
                ProductId = productId,
                SystemPath = directory,
                Description = known.Item1,
                State = known.Item2,
                SerialNumber = File.Exists(serialPath) ? File.ReadAllText(serialPath).Trim() : null,
            });
        }

        return results;
    }

    /// <summary>Membaca "VID_03E7&amp;PID_2485" menjadi pasangan angka.</summary>
    private static bool TryParseWindowsId(string key, out ushort vendorId, out ushort productId)
    {
        vendorId = 0;
        productId = 0;

        var vendorIndex = key.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        var productIndex = key.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);

        if (vendorIndex < 0 || productIndex < 0)
        {
            return false;
        }

        return TryParseHex(key, vendorIndex + 4, out vendorId)
            && TryParseHex(key, productIndex + 4, out productId);
    }

    private static bool TryParseHex(string text, int start, out ushort value)
    {
        value = 0;

        return start + 4 <= text.Length
            && ushort.TryParse(
                text.AsSpan(start, 4),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
    }

    /// <summary>
    /// MxId perangkat OAK berupa hex panjang. Instance USB yang bukan serial
    /// sesungguhnya terlihat seperti "5&amp;1f800324&amp;0&amp;9", jadi mudah dibedakan.
    /// </summary>
    private static bool LooksLikeSerial(string instanceName)
        => instanceName.Length >= 12 && instanceName.All(char.IsAsciiLetterOrDigit);
}
