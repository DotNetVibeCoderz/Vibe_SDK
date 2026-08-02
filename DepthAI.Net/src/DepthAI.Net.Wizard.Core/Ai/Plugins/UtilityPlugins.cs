using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.SemanticKernel;

namespace DepthAI.Wizard.Ai.Plugins;

/// <summary>
/// Tanggal dan waktu. Model bahasa tidak punya jam, jadi setiap pertanyaan soal
/// "sekarang" harus dijawab lewat fungsi, bukan dari ingatan model.
/// </summary>
public sealed class TimePlugin
{
    [KernelFunction("get_current_datetime")]
    [Description("Mengembalikan tanggal dan waktu saat ini pada mesin pengguna, beserta zona waktunya.")]
    public string GetCurrentDateTime()
    {
        var now = DateTimeOffset.Now;
        return string.Create(CultureInfo.InvariantCulture,
            $"{now:dddd, dd MMMM yyyy HH:mm:ss} (UTC{now.Offset:hh\\:mm}, {TimeZoneInfo.Local.StandardName})");
    }

    [KernelFunction("get_utc_datetime")]
    [Description("Mengembalikan tanggal dan waktu UTC saat ini dalam format ISO 8601.")]
    public string GetUtcDateTime() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    [KernelFunction("add_to_date")]
    [Description("Menambahkan sejumlah hari, jam, atau menit ke sebuah tanggal dan mengembalikan hasilnya.")]
    public string AddToDate(
        [Description("Tanggal awal dalam format ISO 8601. Kosongkan untuk memakai waktu sekarang.")] string? startDate,
        [Description("Jumlah hari yang ditambahkan; boleh negatif.")] double days = 0,
        [Description("Jumlah jam yang ditambahkan; boleh negatif.")] double hours = 0,
        [Description("Jumlah menit yang ditambahkan; boleh negatif.")] double minutes = 0)
    {
        var start = string.IsNullOrWhiteSpace(startDate)
            ? DateTimeOffset.Now
            : DateTimeOffset.Parse(startDate, CultureInfo.InvariantCulture);

        var result = start.AddDays(days).AddHours(hours).AddMinutes(minutes);
        return result.ToString("O", CultureInfo.InvariantCulture);
    }

    [KernelFunction("days_between")]
    [Description("Menghitung selisih hari antara dua tanggal.")]
    public string DaysBetween(
        [Description("Tanggal pertama, ISO 8601.")] string firstDate,
        [Description("Tanggal kedua, ISO 8601.")] string secondDate)
    {
        var first = DateTimeOffset.Parse(firstDate, CultureInfo.InvariantCulture);
        var second = DateTimeOffset.Parse(secondDate, CultureInfo.InvariantCulture);
        var span = second - first;

        return string.Create(CultureInfo.InvariantCulture,
            $"{span.TotalDays:F2} hari ({span.TotalHours:F1} jam)");
    }
}

/// <summary>
/// Perhitungan aritmetika. Model bahasa sering keliru pada aritmetika multi-langkah,
/// jadi hitungan diserahkan ke evaluator sungguhan.
/// </summary>
public sealed class MathPlugin
{
    [KernelFunction("calculate")]
    [Description("Menghitung ekspresi matematika. Mendukung + - * / % ^, kurung, dan fungsi "
        + "sqrt, abs, sin, cos, tan, log, log10, exp, floor, ceil, round, min, max, pow.")]
    public string Calculate(
        [Description("Ekspresi yang dihitung, misalnya '(1920*1080*3)/1024/1024'.")] string expression)
    {
        try
        {
            var value = ExpressionEvaluator.Evaluate(expression);
            return value.ToString("G15", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or DivideByZeroException)
        {
            return $"Tidak bisa menghitung '{expression}': {ex.Message}";
        }
    }

    [KernelFunction("frame_bandwidth")]
    [Description("Menghitung kebutuhan bandwidth dan memori untuk stream video mentah.")]
    public string FrameBandwidth(
        [Description("Lebar frame, piksel.")] int width,
        [Description("Tinggi frame, piksel.")] int height,
        [Description("Frame per detik.")] int fps,
        [Description("Byte per piksel: 1 untuk grayscale, 2 untuk depth, 3 untuk BGR.")] int bytesPerPixel = 3)
    {
        var frameBytes = (long)width * height * bytesPerPixel;
        var perSecond = frameBytes * fps;

        return string.Create(CultureInfo.InvariantCulture,
            $"Satu frame {width}x{height} = {frameBytes / 1024.0:F1} KB. "
            + $"Pada {fps} fps butuh {perSecond / (1024.0 * 1024.0):F1} MB/s "
            + $"({perSecond * 8 / 1_000_000.0:F0} Mbps).");
    }
}

/// <summary>
/// Evaluator ekspresi rekursif-turun.
/// </summary>
/// <remarks>
/// Ditulis sendiri alih-alih memakai <c>DataTable.Compute</c>: pendekatan itu memakai
/// budaya lokal untuk pemisah desimal, tidak mendukung fungsi matematika, dan
/// mengembalikan pesan error yang tidak membantu.
/// </remarks>
internal static class ExpressionEvaluator
{
    public static double Evaluate(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        var position = 0;
        var value = ParseExpression(expression, ref position);

        SkipWhitespace(expression, ref position);
        if (position < expression.Length)
        {
            throw new FormatException($"karakter tak terduga '{expression[position]}' pada posisi {position}");
        }

        return value;
    }

    private static double ParseExpression(string text, ref int position)
    {
        var left = ParseTerm(text, ref position);

        while (true)
        {
            SkipWhitespace(text, ref position);
            if (position >= text.Length)
            {
                return left;
            }

            var op = text[position];
            if (op is not ('+' or '-'))
            {
                return left;
            }

            position++;
            var right = ParseTerm(text, ref position);
            left = op == '+' ? left + right : left - right;
        }
    }

    private static double ParseTerm(string text, ref int position)
    {
        var left = ParsePower(text, ref position);

        while (true)
        {
            SkipWhitespace(text, ref position);
            if (position >= text.Length)
            {
                return left;
            }

            var op = text[position];
            if (op is not ('*' or '/' or '%'))
            {
                return left;
            }

            position++;
            var right = ParsePower(text, ref position);

            if (op is '/' or '%' && right == 0)
            {
                throw new DivideByZeroException("pembagian dengan nol");
            }

            left = op switch
            {
                '*' => left * right,
                '/' => left / right,
                _ => left % right,
            };
        }
    }

    private static double ParsePower(string text, ref int position)
    {
        var left = ParseUnary(text, ref position);

        SkipWhitespace(text, ref position);
        if (position < text.Length && text[position] == '^')
        {
            position++;
            // Pemangkatan asosiatif kanan: 2^3^2 sama dengan 2^(3^2).
            return Math.Pow(left, ParsePower(text, ref position));
        }

        return left;
    }

    private static double ParseUnary(string text, ref int position)
    {
        SkipWhitespace(text, ref position);

        if (position < text.Length && text[position] == '-')
        {
            position++;
            return -ParseUnary(text, ref position);
        }

        if (position < text.Length && text[position] == '+')
        {
            position++;
        }

        return ParsePrimary(text, ref position);
    }

    private static double ParsePrimary(string text, ref int position)
    {
        SkipWhitespace(text, ref position);

        if (position >= text.Length)
        {
            throw new FormatException("ekspresi berakhir lebih cepat dari yang diharapkan");
        }

        if (text[position] == '(')
        {
            position++;
            var value = ParseExpression(text, ref position);
            Expect(text, ref position, ')');
            return value;
        }

        if (char.IsLetter(text[position]))
        {
            return ParseFunctionOrConstant(text, ref position);
        }

        var start = position;
        while (position < text.Length && (char.IsDigit(text[position]) || text[position] == '.'))
        {
            position++;
        }

        if (start == position)
        {
            throw new FormatException($"karakter tak terduga '{text[position]}' pada posisi {position}");
        }

        return double.Parse(text.AsSpan(start, position - start), CultureInfo.InvariantCulture);
    }

    private static double ParseFunctionOrConstant(string text, ref int position)
    {
        var start = position;
        while (position < text.Length && char.IsLetterOrDigit(text[position]))
        {
            position++;
        }

        var name = text[start..position].ToLowerInvariant();

        SkipWhitespace(text, ref position);
        if (position >= text.Length || text[position] != '(')
        {
            return name switch
            {
                "pi" => Math.PI,
                "e" => Math.E,
                _ => throw new FormatException($"konstanta '{name}' tidak dikenal"),
            };
        }

        position++;
        var first = ParseExpression(text, ref position);

        SkipWhitespace(text, ref position);
        if (position < text.Length && text[position] == ',')
        {
            position++;
            var second = ParseExpression(text, ref position);
            Expect(text, ref position, ')');

            return name switch
            {
                "min" => Math.Min(first, second),
                "max" => Math.Max(first, second),
                "pow" => Math.Pow(first, second),
                "log" => Math.Log(first, second),
                _ => throw new FormatException($"fungsi '{name}' tidak menerima dua argumen"),
            };
        }

        Expect(text, ref position, ')');

        return name switch
        {
            "sqrt" => Math.Sqrt(first),
            "abs" => Math.Abs(first),
            "sin" => Math.Sin(first),
            "cos" => Math.Cos(first),
            "tan" => Math.Tan(first),
            "log" => Math.Log(first),
            "log10" => Math.Log10(first),
            "exp" => Math.Exp(first),
            "floor" => Math.Floor(first),
            "ceil" => Math.Ceiling(first),
            "round" => Math.Round(first),
            _ => throw new FormatException($"fungsi '{name}' tidak dikenal"),
        };
    }

    private static void Expect(string text, ref int position, char expected)
    {
        SkipWhitespace(text, ref position);

        if (position >= text.Length || text[position] != expected)
        {
            throw new FormatException($"'{expected}' tidak ditemukan pada posisi {position}");
        }

        position++;
    }

    private static void SkipWhitespace(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }
    }
}
