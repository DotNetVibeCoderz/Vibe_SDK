use siware_dsp::*;

fn close(actual: &[f64], expected: &[f64]) {
    assert_eq!(actual.len(), expected.len());
    for (a, e) in actual.iter().zip(expected) {
        assert!((a - e).abs() < 1e-12, "{a} != {e}");
    }
}

#[test]
fn snv_uses_sample_deviation() {
    close(&snv(&[1., 2., 3.]).unwrap(), &[-1., 0., 1.]);
    close(&snv(&[-f64::MAX, 0., f64::MAX]).unwrap(), &[-1., 0., 1.]);
}

#[test]
fn snv_rejects_degenerate_and_nonfinite_inputs() {
    assert_eq!(snv(&[]), Err(Error::InvalidArgument));
    assert_eq!(snv(&[1.]), Err(Error::InvalidArgument));
    assert_eq!(snv(&[0.1; 10]), Err(Error::Domain));
    assert_eq!(snv(&[0., 0.]), Err(Error::Domain));
    assert_eq!(snv(&[f64::NAN, 1.]), Err(Error::Domain));
    assert_eq!(snv(&[f64::INFINITY, 1.]), Err(Error::Domain));
}

#[test]
fn absorbance_dark_correction_and_wide_dynamic_range() {
    close(
        &absorbance(&[11., 2., 101.], &[1., 1., 1.], &[101.; 3]).unwrap(),
        &[1., 2., 0.],
    );
    close(&absorbance(&[1e-300], &[0.], &[1e300]).unwrap(), &[600.]);
}

#[test]
fn absorbance_rejects_invalid_domains() {
    assert_eq!(absorbance(&[], &[], &[]), Err(Error::InvalidArgument));
    assert_eq!(absorbance(&[1.], &[], &[2.]), Err(Error::InvalidArgument));
    for (s, d, r) in [
        (0., 0., 1.),
        (1., 2., 3.),
        (1., 0., 0.),
        (1., 0., f64::NAN),
        (f64::MAX, -f64::MAX, 1.),
    ] {
        assert_eq!(absorbance(&[s], &[d], &[r]), Err(Error::Domain));
    }
}

#[test]
fn smoothing_truncates_edges() {
    close(
        &smooth(&[1., 2., 3., 4., 5.], 3).unwrap(),
        &[1.5, 2., 3., 4., 4.5],
    );
    close(
        &smooth(&[1., 2., 3., 4., 5.], 5).unwrap(),
        &[2., 2.5, 3., 3.5, 4.],
    );
    assert_eq!(smooth(&[f64::MAX; 3], 3).unwrap(), vec![f64::MAX; 3]);
    assert_eq!(smooth(&[1e-300, 1e300], 1).unwrap(), vec![1e-300, 1e300]);
    assert_eq!(smooth(&[0.; 3], 3).unwrap(), vec![0.; 3]);
}

#[test]
fn smoothing_rejects_bad_windows() {
    for window in [0, 2, 4, 5] {
        assert_eq!(smooth(&[1., 2., 3.], window), Err(Error::InvalidArgument));
    }
    assert_eq!(smooth(&[], 1), Err(Error::InvalidArgument));
    assert_eq!(smooth(&[1., f64::NEG_INFINITY, 3.], 3), Err(Error::Domain));
}

#[test]
fn ffi_success_all_kernels() {
    let input = [1., 2., 3.];
    let mut output = [0.; 3];
    assert_eq!(siware_dsp_abi_version(), 1);
    // SAFETY: all buffers are valid, sized, aligned and disjoint.
    unsafe {
        assert_eq!(siware_dsp_snv(input.as_ptr(), output.as_mut_ptr(), 3), 0);
        close(&output, &[-1., 0., 1.]);
        assert_eq!(
            siware_dsp_smooth(input.as_ptr(), output.as_mut_ptr(), 3, 3),
            0
        );
        close(&output, &[1.5, 2., 2.5]);
        assert_eq!(
            siware_dsp_absorbance(
                input.as_ptr(),
                [0.; 3].as_ptr(),
                input.as_ptr(),
                output.as_mut_ptr(),
                3
            ),
            0
        );
        close(&output, &[0.; 3]);
    }
}

#[test]
fn ffi_rejects_structurally_invalid_buffers_before_dereferencing() {
    let input = [1., 2., 3.];
    let mut output = [42.; 3];
    // SAFETY: intentionally malformed descriptors are rejected before dereference.
    unsafe {
        assert_eq!(siware_dsp_snv(std::ptr::null(), output.as_mut_ptr(), 3), 1);
        assert_eq!(siware_dsp_snv(input.as_ptr(), std::ptr::null_mut(), 3), 1);
        assert_eq!(siware_dsp_snv(input.as_ptr(), output.as_mut_ptr(), 0), 1);
        assert_eq!(
            siware_dsp_snv(input.as_ptr(), output.as_mut_ptr(), usize::MAX),
            1
        );
        assert_eq!(siware_dsp_snv(output.as_ptr(), output.as_mut_ptr(), 3), 1);
        assert_eq!(
            siware_dsp_snv(
                input.as_ptr().cast::<u8>().add(1).cast(),
                output.as_mut_ptr(),
                2
            ),
            1
        );
    }
    assert_eq!(output, [42.; 3]);
}

#[test]
fn ffi_errors_never_partially_modify_output() {
    let mut output = [42.; 3];
    // SAFETY: live, aligned, sized and disjoint buffers.
    unsafe {
        assert_eq!(
            siware_dsp_absorbance(
                [1., 2., 0.].as_ptr(),
                [0.; 3].as_ptr(),
                [10.; 3].as_ptr(),
                output.as_mut_ptr(),
                3
            ),
            2
        );
        assert_eq!(output, [42.; 3]);
        assert_eq!(
            siware_dsp_snv([1., 2., f64::NAN].as_ptr(), output.as_mut_ptr(), 3),
            2
        );
        assert_eq!(output, [42.; 3]);
        assert_eq!(
            siware_dsp_smooth([1., 2., 3.].as_ptr(), output.as_mut_ptr(), 3, 2),
            1
        );
        assert_eq!(output, [42.; 3]);
    }
}
