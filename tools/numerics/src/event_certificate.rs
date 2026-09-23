use crate::curve::{Rational as Q, bounds};
use serde::Serialize;

const DEGREE: usize = 4;
const DEPTH: usize = 6;

#[derive(Clone, Copy)]
struct Motion {
    p: [Q; 2],
    v: [Q; 2],
    a: [Q; 2],
    horizon: Q,
    radius: Q,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Report {
    pub schema: &'static str,
    pub arithmetic: &'static str,
    pub cases: usize,
    pub coordinate_clears: usize,
    pub distance_clears: usize,
    pub distance_only_clears: usize,
    pub coordinate_only_clears: usize,
    pub coordinate_one_window_clears: usize,
    pub distance_one_window_clears: usize,
    pub linear_eligible: usize,
    pub linear_closed_form_clears: usize,
    pub coordinate_nodes: usize,
    pub distance_nodes: usize,
    pub false_clears_in_sampled_oracle: usize,
    pub floating_point_qualified: bool,
    pub production_implemented: bool,
    pub performance_measured: bool,
}

fn q(n: i128) -> Q {
    Q::integer(n)
}

fn choose(n: usize, k: usize) -> i128 {
    if k == 0 || k == n {
        return 1;
    }
    let mut result = 1;
    for i in 0..k {
        result = result * (n - i) as i128 / (i + 1) as i128;
    }
    result
}

fn polynomial(m: Motion) -> [Q; DEGREE + 1] {
    let mut coefficients = [q(0); DEGREE + 1];
    for axis in 0..2 {
        let terms = [m.p[axis], m.v[axis], m.a[axis] / q(2)];
        for i in 0..=2 {
            for j in 0..=2 {
                coefficients[i + j] = coefficients[i + j] + terms[i] * terms[j];
            }
        }
    }
    coefficients[0] = coefficients[0] - m.radius * m.radius;
    coefficients
}

fn bernstein(m: Motion) -> [Q; DEGREE + 1] {
    let power = polynomial(m);
    let mut scaled = [q(0); DEGREE + 1];
    let mut horizon_power = q(1);
    for k in 0..=DEGREE {
        scaled[k] = power[k] * horizon_power;
        horizon_power = horizon_power * m.horizon;
    }
    let mut controls = [q(0); DEGREE + 1];
    for (j, control) in controls.iter_mut().enumerate() {
        for k in 0..=j {
            *control = *control + scaled[k] * Q::new(choose(j, k), choose(DEGREE, k));
        }
    }
    controls
}

fn split(controls: [Q; DEGREE + 1]) -> ([Q; DEGREE + 1], [Q; DEGREE + 1]) {
    let mut temp = controls;
    let mut left = [q(0); DEGREE + 1];
    let mut right = [q(0); DEGREE + 1];
    for level in 0..=DEGREE {
        left[level] = temp[0];
        right[DEGREE - level] = temp[DEGREE - level];
        for j in 0..(DEGREE - level) {
            temp[j] = (temp[j] + temp[j + 1]) / q(2);
        }
    }
    (left, right)
}

fn distance_clear(controls: [Q; DEGREE + 1], depth: usize, nodes: &mut usize) -> bool {
    *nodes += 1;
    if controls.iter().all(|value| *value > q(0)) {
        return true;
    }
    if depth == 0 {
        return false;
    }
    let (left, right) = split(controls);
    let first = distance_clear(left, depth - 1, nodes);
    let second = distance_clear(right, depth - 1, nodes);
    first && second
}

fn coordinate_clear(m: Motion, lo: Q, hi: Q, depth: usize, nodes: &mut usize) -> bool {
    *nodes += 1;
    let mut lower_distance_squared = q(0);
    for axis in 0..2 {
        let interval = bounds(m.p[axis], m.v[axis], m.a[axis], lo, hi)[3];
        let lower_abs = if interval.0 > q(0) {
            interval.0
        } else if interval.1 < q(0) {
            q(0) - interval.1
        } else {
            q(0)
        };
        lower_distance_squared = lower_distance_squared + lower_abs * lower_abs;
    }
    if lower_distance_squared > m.radius * m.radius {
        return true;
    }
    if depth == 0 {
        return false;
    }
    let middle = (lo + hi) / q(2);
    let first = coordinate_clear(m, lo, middle, depth - 1, nodes);
    let second = coordinate_clear(m, middle, hi, depth - 1, nodes);
    first && second
}

fn sampled_contact(m: Motion) -> bool {
    let coefficients = polynomial(m);
    for index in 0..=256 {
        let t = m.horizon * Q::new(index, 256);
        let mut value = q(0);
        for coefficient in coefficients.iter().rev() {
            value = value * t + *coefficient;
        }
        if value <= q(0) {
            return true;
        }
    }
    false
}

fn linear_closed_form_clear(m: Motion) -> Option<bool> {
    if m.a != [q(0), q(0)] {
        return None;
    }
    let vv = m.v[0] * m.v[0] + m.v[1] * m.v[1];
    let mut closest_time = if vv == q(0) {
        q(0)
    } else {
        (q(0) - (m.p[0] * m.v[0] + m.p[1] * m.v[1])) / vv
    };
    if closest_time < q(0) {
        closest_time = q(0);
    }
    if closest_time > m.horizon {
        closest_time = m.horizon;
    }
    let x = m.p[0] + m.v[0] * closest_time;
    let y = m.p[1] + m.v[1] * closest_time;
    Some(x * x + y * y > m.radius * m.radius)
}

fn fixture(
    px: i128,
    py: i128,
    vx: i128,
    vy: i128,
    ax: i128,
    ay: i128,
    horizon: i128,
    radius: i128,
) -> Motion {
    Motion {
        p: [q(px), q(py)],
        v: [q(vx), q(vy)],
        a: [q(ax), q(ay)],
        horizon: q(horizon),
        radius: q(radius),
    }
}

pub fn report() -> Report {
    let mut cases = vec![
        fixture(0, 10, 1, -1, 0, 0, 10, 5),
        fixture(-5, 1, 1, 0, 0, 0, 10, 1),
        fixture(-5, 0, 1, 0, 0, 0, 10, 1),
        fixture(-5, 20, 1, -2, 0, 0, 10, 2),
    ];
    for scale in 1..=8 {
        cases.push(fixture(0, 10 * scale, scale, -scale, 0, 0, 10, 5 * scale));
    }
    for px in [-8, 0, 8] {
        for py in [-8, 0, 8] {
            for vx in [-4, 0, 4] {
                for vy in [-4, 0, 4] {
                    for ax in [-2, 0, 2] {
                        for ay in [-2, 0, 2] {
                            cases.push(fixture(px, py, vx, vy, ax, ay, 4, 1));
                        }
                    }
                }
            }
        }
    }
    let mut report = Report {
        schema: "ksp-continuum-exact-event-certificate-toy/v1",
        arithmetic: "exact rational",
        cases: cases.len(),
        coordinate_clears: 0,
        distance_clears: 0,
        distance_only_clears: 0,
        coordinate_only_clears: 0,
        coordinate_one_window_clears: 0,
        distance_one_window_clears: 0,
        linear_eligible: 0,
        linear_closed_form_clears: 0,
        coordinate_nodes: 0,
        distance_nodes: 0,
        false_clears_in_sampled_oracle: 0,
        floating_point_qualified: false,
        production_implemented: false,
        performance_measured: false,
    };
    for motion in cases {
        let coordinate = coordinate_clear(
            motion,
            q(0),
            motion.horizon,
            DEPTH,
            &mut report.coordinate_nodes,
        );
        let distance = distance_clear(bernstein(motion), DEPTH, &mut report.distance_nodes);
        report.coordinate_clears += usize::from(coordinate);
        report.distance_clears += usize::from(distance);
        report.distance_only_clears += usize::from(distance && !coordinate);
        report.coordinate_only_clears += usize::from(coordinate && !distance);
        let mut ignored = 0;
        report.coordinate_one_window_clears += usize::from(coordinate_clear(
            motion,
            q(0),
            motion.horizon,
            0,
            &mut ignored,
        ));
        report.distance_one_window_clears +=
            usize::from(distance_clear(bernstein(motion), 0, &mut ignored));
        if let Some(clear) = linear_closed_form_clear(motion) {
            report.linear_eligible += 1;
            report.linear_closed_form_clears += usize::from(clear);
            if clear {
                assert!(coordinate || distance);
            }
        }
        report.false_clears_in_sampled_oracle +=
            usize::from((coordinate || distance) && sampled_contact(motion));
    }
    assert_eq!(report.false_clears_in_sampled_oracle, 0);
    report
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn distance_certificate_beats_independent_coordinate_boxes_on_asynchronous_near_miss() {
        let motion = fixture(0, 10, 1, -1, 0, 0, 10, 5);
        let mut coordinate_nodes = 0;
        let mut distance_nodes = 0;
        assert!(!coordinate_clear(
            motion,
            q(0),
            motion.horizon,
            0,
            &mut coordinate_nodes
        ));
        assert!(distance_clear(bernstein(motion), 0, &mut distance_nodes));
    }
    #[test]
    fn permutation_matrix_has_no_sampled_false_clear() {
        let result = report();
        assert_eq!(result.cases, 741);
        assert_eq!(result.false_clears_in_sampled_oracle, 0);
    }
}
