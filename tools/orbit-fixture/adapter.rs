use gimbal_orbit::{PrimaryBody, StateVector, TwoBodyProblem};
use std::io::{self, BufRead};

fn main() {
    for line in io::stdin().lock().lines() {
        let line = line.expect("fixture input must be readable");
        let fields: Vec<_> = line.split(',').collect();
        assert_eq!(fields.len(), 9, "fixture input has fixed shape");
        let id: usize = fields[0].parse().expect("fixture ID must be integer");
        let numbers: Vec<f64> = fields[1..].iter().map(|value| value.parse().expect("fixture input must be numeric")).collect();
        assert!(numbers.iter().all(|value| value.is_finite()) && numbers[0] > 0.0);
        let problem = TwoBodyProblem::new(PrimaryBody::new("fixture", "Point mass", numbers[0]));
        let initial = StateVector {
            position_m: [numbers[2], numbers[3], numbers[4]],
            velocity_mps: [numbers[5], numbers[6], numbers[7]],
        };
        match std::panic::catch_unwind(|| problem.propagate_coast(initial, numbers[1])) {
            Ok(state) if state.position_m.iter().chain(state.velocity_mps.iter()).all(|value| value.is_finite()) => {
                println!("{id},OK,{:.17e},{:.17e},{:.17e},{:.17e},{:.17e},{:.17e}",
                    state.position_m[0], state.position_m[1], state.position_m[2],
                    state.velocity_mps[0], state.velocity_mps[1], state.velocity_mps[2]);
            }
            _ => println!("{id},ERROR"),
        }
    }
}
