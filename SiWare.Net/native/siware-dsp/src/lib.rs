//! Spectral processing with transactional output validation and a stable C ABI.
use std::{mem, panic, ptr, slice};

/// Stable status codes shared by the Rust and C APIs.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(i32)]
pub enum Error {
    InvalidArgument = 1,
    Domain = 2,
}

fn buffer(len: usize) -> Result<Vec<f64>, Error> {
    let mut result = Vec::new();
    result
        .try_reserve_exact(len)
        .map_err(|_| Error::InvalidArgument)?;
    Ok(result)
}

fn finite(input: &[f64]) -> Result<(), Error> {
    if input.iter().all(|x| x.is_finite()) {
        Ok(())
    } else {
        Err(Error::Domain)
    }
}

/// Standard normal variate using sample standard deviation (N - 1).
/// Scaling before summation avoids overflow on large finite spectra.
pub fn snv(input: &[f64]) -> Result<Vec<f64>, Error> {
    if input.len() < 2 {
        return Err(Error::InvalidArgument);
    }
    finite(input)?;
    let scale = input.iter().fold(0.0_f64, |a, x| a.max(x.abs()));
    if scale == 0.0 || input.iter().all(|x| *x == input[0]) {
        return Err(Error::Domain);
    }
    let mean = input.iter().map(|x| x / scale).sum::<f64>() / input.len() as f64;
    let variance = input
        .iter()
        .map(|x| (x / scale - mean).powi(2))
        .sum::<f64>()
        / (input.len() - 1) as f64;
    let stddev = variance.sqrt();
    if !stddev.is_finite() || stddev == 0.0 {
        return Err(Error::Domain);
    }
    let mut result = buffer(input.len())?;
    result.extend(input.iter().map(|x| (x / scale - mean) / stddev));
    finite(&result)?;
    Ok(result)
}

/// Absorbance = -log10((sample - dark) / (reference - dark)).
/// Both dark-corrected signals must be strictly positive and finite.
pub fn absorbance(sample: &[f64], dark: &[f64], reference: &[f64]) -> Result<Vec<f64>, Error> {
    if sample.is_empty() || sample.len() != dark.len() || sample.len() != reference.len() {
        return Err(Error::InvalidArgument);
    }
    finite(sample)?;
    finite(dark)?;
    finite(reference)?;
    let mut result = buffer(sample.len())?;
    for ((s, d), r) in sample.iter().zip(dark).zip(reference) {
        let numerator = s - d;
        let denominator = r - d;
        if numerator <= 0.0
            || denominator <= 0.0
            || !numerator.is_finite()
            || !denominator.is_finite()
        {
            return Err(Error::Domain);
        }
        // Difference of logarithms avoids ratio overflow/underflow.
        result.push(denominator.log10() - numerator.log10());
    }
    finite(&result)?;
    Ok(result)
}

/// Centered moving average. Odd window must be in 1..=input.len().
/// Edge windows are truncated and divided by the actual number of samples.
pub fn smooth(input: &[f64], window: usize) -> Result<Vec<f64>, Error> {
    if input.is_empty() || window == 0 || window.is_multiple_of(2) || window > input.len() {
        return Err(Error::InvalidArgument);
    }
    finite(input)?;
    let mut result = buffer(input.len())?;
    if window == 1 {
        result.extend_from_slice(input);
        return Ok(result);
    }
    let scale = input.iter().fold(0.0_f64, |a, x| a.max(x.abs()));
    if scale == 0.0 {
        result.resize(input.len(), 0.0);
        return Ok(result);
    }
    let radius = window / 2;
    let mut left = 0;
    let mut right = radius + 1;
    let mut sum = input[..right].iter().map(|x| x / scale).sum::<f64>();
    for i in 0..input.len() {
        let next_left = i.saturating_sub(radius);
        let next_right = i.saturating_add(radius).saturating_add(1).min(input.len());
        while left < next_left {
            sum -= input[left] / scale;
            left += 1;
        }
        while right < next_right {
            sum += input[right] / scale;
            right += 1;
        }
        result.push((sum / (right - left) as f64).clamp(-1.0, 1.0) * scale);
    }
    finite(&result)?;
    Ok(result)
}

fn address_range(pointer: *const f64, len: usize) -> Result<(usize, usize), Error> {
    let start = pointer as usize;
    let bytes = len
        .checked_mul(mem::size_of::<f64>())
        .ok_or(Error::InvalidArgument)?;
    if len == 0
        || pointer.is_null()
        || !start.is_multiple_of(mem::align_of::<f64>())
        || bytes > isize::MAX as usize
    {
        return Err(Error::InvalidArgument);
    }
    Ok((
        start,
        start.checked_add(bytes).ok_or(Error::InvalidArgument)?,
    ))
}

fn validate(inputs: &[*const f64], output: *mut f64, len: usize) -> Result<(), Error> {
    let (out_start, out_end) = address_range(output, len)?;
    for &input in inputs {
        let (start, end) = address_range(input, len)?;
        if start < out_end && out_start < end {
            return Err(Error::InvalidArgument);
        }
    }
    Ok(())
}

fn status(action: impl FnOnce() -> Result<(), Error> + panic::UnwindSafe) -> i32 {
    match panic::catch_unwind(action) {
        Ok(Ok(())) => 0,
        Ok(Err(error)) => error as i32,
        Err(_) => Error::Domain as i32,
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn siware_dsp_abi_version() -> u32 {
    1
}

/// # Safety
/// Input must point to `len` initialized f64 values and output to `len` writable
/// f64 values in live allocations. Buffers must be aligned and disjoint. No other
/// thread may mutate inputs or access output until return. Invalid allocations
/// cannot be detected. Output is untouched on errors.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn siware_dsp_snv(input: *const f64, output: *mut f64, len: usize) -> i32 {
    status(|| {
        validate(&[input], output, len)?;
        // SAFETY: caller guarantees readable allocation; lengths and alignment checked.
        let result = snv(unsafe { slice::from_raw_parts(input, len) })?;
        // SAFETY: caller guarantees writable output; result is a separate allocation.
        unsafe {
            ptr::copy_nonoverlapping(result.as_ptr(), output, len);
        }
        Ok(())
    })
}

/// # Safety
/// All pointers follow the allocation, alignment, lifetime and concurrency
/// contract of `siware_dsp_snv`. Each input must be disjoint from output; inputs
/// may overlap each other. Output is untouched on errors.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn siware_dsp_absorbance(
    sample: *const f64,
    dark: *const f64,
    reference: *const f64,
    output: *mut f64,
    len: usize,
) -> i32 {
    status(|| {
        validate(&[sample, dark, reference], output, len)?;
        // SAFETY: caller guarantees live readable allocations; sizes/alignment checked.
        let result = unsafe {
            absorbance(
                slice::from_raw_parts(sample, len),
                slice::from_raw_parts(dark, len),
                slice::from_raw_parts(reference, len),
            )
        }?;
        // SAFETY: caller guarantees writable output; result is a separate allocation.
        unsafe {
            ptr::copy_nonoverlapping(result.as_ptr(), output, len);
        }
        Ok(())
    })
}

/// # Safety
/// Pointers follow the allocation, alignment, lifetime, disjointness and
/// concurrency contract of `siware_dsp_snv`. Output is untouched on errors.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn siware_dsp_smooth(
    input: *const f64,
    output: *mut f64,
    len: usize,
    window: usize,
) -> i32 {
    status(|| {
        validate(&[input], output, len)?;
        // SAFETY: caller guarantees readable allocation; lengths and alignment checked.
        let result = smooth(unsafe { slice::from_raw_parts(input, len) }, window)?;
        // SAFETY: caller guarantees writable output; result is a separate allocation.
        unsafe {
            ptr::copy_nonoverlapping(result.as_ptr(), output, len);
        }
        Ok(())
    })
}
