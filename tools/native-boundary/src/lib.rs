use std::slice;

pub const ABI_VERSION: u32 = 1;

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct BodyF64 {
    pub px: f64,
    pub py: f64,
    pub pz: f64,
    pub vx: f64,
    pub vy: f64,
    pub vz: f64,
    pub fx: f64,
    pub fy: f64,
    pub fz: f64,
    pub inverse_mass: f64,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct BodyF32 {
    pub px: f32,
    pub py: f32,
    pub pz: f32,
    pub vx: f32,
    pub vy: f32,
    pub vz: f32,
    pub fx: f32,
    pub fy: f32,
    pub fz: f32,
    pub inverse_mass: f32,
}

#[unsafe(no_mangle)]
pub extern "C" fn continuum_boundary_abi_version() -> u32 {
    ABI_VERSION
}

#[unsafe(no_mangle)]
pub extern "C" fn continuum_boundary_noop(value: u64) -> u64 {
    value.rotate_left(17) ^ 0x9e37_79b9_7f4a_7c15
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn continuum_integrate_f64(
    bodies: *mut BodyF64,
    count: usize,
    dt: f64,
    steps: u32,
) -> i32 {
    if count == 0 {
        return 0;
    }
    if bodies.is_null() {
        return -1;
    }
    let bodies = unsafe { slice::from_raw_parts_mut(bodies, count) };
    for _ in 0..steps {
        integrate_f64(bodies, dt);
    }
    0
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn continuum_integrate_f32(
    bodies: *mut BodyF32,
    count: usize,
    dt: f32,
    steps: u32,
) -> i32 {
    if count == 0 {
        return 0;
    }
    if bodies.is_null() {
        return -1;
    }
    let bodies = unsafe { slice::from_raw_parts_mut(bodies, count) };
    for _ in 0..steps {
        integrate_f32(bodies, dt);
    }
    0
}

pub fn integrate_f64(bodies: &mut [BodyF64], dt: f64) {
    for body in bodies {
        let scale = dt * body.inverse_mass;
        body.vx += body.fx * scale;
        body.vy += body.fy * scale;
        body.vz += body.fz * scale;
        body.px += body.vx * dt;
        body.py += body.vy * dt;
        body.pz += body.vz * dt;
    }
}

pub fn integrate_f32(bodies: &mut [BodyF32], dt: f32) {
    for body in bodies {
        let scale = dt * body.inverse_mass;
        body.vx += body.fx * scale;
        body.vy += body.fy * scale;
        body.vz += body.fz * scale;
        body.px += body.vx * dt;
        body.py += body.vy * dt;
        body.pz += body.vz * dt;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn exported_f64_kernel_matches_safe_kernel() {
        let initial = BodyF64 {
            px: 12.0,
            py: -4.0,
            vx: 0.5,
            vy: -0.25,
            fx: 2.0,
            fy: -9.81,
            inverse_mass: 0.5,
            ..BodyF64::default()
        };
        let mut safe = [initial];
        let mut exported = [initial];
        for _ in 0..4 {
            integrate_f64(&mut safe, 0.02);
        }
        let status = unsafe { continuum_integrate_f64(exported.as_mut_ptr(), 1, 0.02, 4) };
        assert_eq!(status, 0);
        assert_eq!(safe, exported);
    }

    #[test]
    fn null_nonempty_input_fails_without_dereference() {
        assert_eq!(
            unsafe { continuum_integrate_f64(std::ptr::null_mut(), 1, 0.02, 1) },
            -1
        );
        assert_eq!(
            unsafe { continuum_integrate_f32(std::ptr::null_mut(), 1, 0.02, 1) },
            -1
        );
    }

    #[test]
    fn empty_input_accepts_null_pointer() {
        assert_eq!(
            unsafe { continuum_integrate_f64(std::ptr::null_mut(), 0, 0.02, 1) },
            0
        );
        assert_eq!(
            unsafe { continuum_integrate_f32(std::ptr::null_mut(), 0, 0.02, 1) },
            0
        );
    }
}
