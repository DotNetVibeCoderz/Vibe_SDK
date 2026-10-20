using System.Runtime.InteropServices;

namespace SiWare.Sdk;

/// <summary>Optional Rust ABI v1. Explicit methods never silently fall back.</summary>
public static class NativeDsp
{
    public static bool IsAvailable
    {
        get
        {
            try { return AbiVersion() == 1; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
            catch (BadImageFormatException) { return false; }
        }
    }
    public static Spectrum StandardNormalVariate(Spectrum spectrum)
    {
        EnsureAvailable();
        var output = new double[spectrum.Count];
        Check(Snv(spectrum.Values.ToArray(), output, (nuint)output.Length));
        return spectrum.WithValues(output, MeasurementKind.Normalized);
    }
    public static Spectrum Smooth(Spectrum spectrum, int window)
    {
        EnsureAvailable();
        if (window < 1) throw new ArgumentOutOfRangeException(nameof(window));
        var output = new double[spectrum.Count];
        Check(SmoothNative(spectrum.Values.ToArray(), output, (nuint)output.Length, (nuint)window));
        return spectrum.WithValues(output, spectrum.Kind);
    }
    public static Spectrum Absorbance(Spectrum sample, Spectrum dark, Spectrum reference)
    {
        EnsureAvailable();
        if (sample.Kind != MeasurementKind.Sample || dark.Kind != MeasurementKind.Dark || reference.Kind != MeasurementKind.Reference)
            throw new ArgumentException("Expected sample, dark, and reference spectra.");
        SpectralProcessing.Match(sample, dark); SpectralProcessing.Match(sample, reference);
        var output = new double[sample.Count];
        Check(AbsorbanceNative(sample.Values.ToArray(), dark.Values.ToArray(), reference.Values.ToArray(), output, (nuint)output.Length));
        return sample.WithValues(output, MeasurementKind.Absorbance);
    }
    private static void EnsureAvailable()
    {
        if (!IsAvailable) throw new PlatformNotSupportedException("Build and deploy siware_dsp ABI v1 for this platform.");
    }
    private static void Check(int result)
    {
        if (result != 0) throw new ArgumentException($"Rust DSP rejected input (status {result}).");
    }
    [DllImport("siware_dsp", EntryPoint = "siware_dsp_abi_version", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint AbiVersion();
    [DllImport("siware_dsp", EntryPoint = "siware_dsp_snv", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Snv([In] double[] input, [Out] double[] output, nuint length);
    [DllImport("siware_dsp", EntryPoint = "siware_dsp_smooth", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SmoothNative([In] double[] input, [Out] double[] output, nuint length, nuint window);
    [DllImport("siware_dsp", EntryPoint = "siware_dsp_absorbance", CallingConvention = CallingConvention.Cdecl)]
    private static extern int AbsorbanceNative([In] double[] sample, [In] double[] dark, [In] double[] reference, [Out] double[] output, nuint length);
}
