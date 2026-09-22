use rayon::prelude::*;

#[derive(Clone, Copy, Debug)]
pub struct Body {
    pub position: [f64; 3],
    pub velocity: [f64; 3],
    pub force: [f64; 3],
    pub inverse_mass: f64,
}

#[derive(Clone, Debug)]
pub struct IntegrationView {
    pub position_x: Vec<f64>,
    pub position_y: Vec<f64>,
    pub position_z: Vec<f64>,
    pub velocity_x: Vec<f64>,
    pub velocity_y: Vec<f64>,
    pub velocity_z: Vec<f64>,
    pub force_x: Vec<f64>,
    pub force_y: Vec<f64>,
    pub force_z: Vec<f64>,
    pub inverse_mass: Vec<f64>,
}

impl IntegrationView {
    pub fn pack(bodies: &[Body]) -> Self {
        let mut view = Self::with_capacity(bodies.len());
        for body in bodies {
            view.position_x.push(body.position[0]);
            view.position_y.push(body.position[1]);
            view.position_z.push(body.position[2]);
            view.velocity_x.push(body.velocity[0]);
            view.velocity_y.push(body.velocity[1]);
            view.velocity_z.push(body.velocity[2]);
            view.force_x.push(body.force[0]);
            view.force_y.push(body.force[1]);
            view.force_z.push(body.force[2]);
            view.inverse_mass.push(body.inverse_mass);
        }
        view
    }

    fn with_capacity(capacity: usize) -> Self {
        Self {
            position_x: Vec::with_capacity(capacity),
            position_y: Vec::with_capacity(capacity),
            position_z: Vec::with_capacity(capacity),
            velocity_x: Vec::with_capacity(capacity),
            velocity_y: Vec::with_capacity(capacity),
            velocity_z: Vec::with_capacity(capacity),
            force_x: Vec::with_capacity(capacity),
            force_y: Vec::with_capacity(capacity),
            force_z: Vec::with_capacity(capacity),
            inverse_mass: Vec::with_capacity(capacity),
        }
    }

    pub fn len(&self) -> usize {
        self.position_x.len()
    }

    pub fn is_empty(&self) -> bool {
        self.position_x.is_empty()
    }

    pub fn integrate_scalar(&mut self, dt: f64) {
        for index in 0..self.len() {
            let scale = dt * self.inverse_mass[index];
            self.velocity_x[index] += self.force_x[index] * scale;
            self.velocity_y[index] += self.force_y[index] * scale;
            self.velocity_z[index] += self.force_z[index] * scale;
            self.position_x[index] += self.velocity_x[index] * dt;
            self.position_y[index] += self.velocity_y[index] * dt;
            self.position_z[index] += self.velocity_z[index] * dt;
        }
    }

    pub fn integrate_parallel(&mut self, dt: f64) {
        self.position_x
            .par_iter_mut()
            .zip(&mut self.position_y)
            .zip(&mut self.position_z)
            .zip(&mut self.velocity_x)
            .zip(&mut self.velocity_y)
            .zip(&mut self.velocity_z)
            .zip(&self.force_x)
            .zip(&self.force_y)
            .zip(&self.force_z)
            .zip(&self.inverse_mass)
            .for_each(
                |(((((((((px, py), pz), vx), vy), vz), fx), fy), fz), inverse_mass)| {
                    let scale = dt * inverse_mass;
                    *vx += fx * scale;
                    *vy += fy * scale;
                    *vz += fz * scale;
                    *px += *vx * dt;
                    *py += *vy * dt;
                    *pz += *vz * dt;
                },
            );
    }

    pub fn checksum(&self) -> u64 {
        let mut hash = 0xcbf2_9ce4_8422_2325_u64;
        for columns in [
            &self.position_x,
            &self.position_y,
            &self.position_z,
            &self.velocity_x,
            &self.velocity_y,
            &self.velocity_z,
        ] {
            for value in columns {
                hash ^= value.to_bits();
                hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
            }
        }
        hash
    }
}

pub fn fixture(count: usize) -> Vec<Body> {
    (0..count)
        .map(|index| {
            let x = index as f64;
            Body {
                position: [x * 0.25, (index % 31) as f64, -(index as f64) * 0.125],
                velocity: [0.5 + x * 0.000_01, -0.25, (index % 7) as f64 * 0.01],
                force: [
                    ((index % 13) as f64 - 6.0) * 0.2,
                    -9.81,
                    (index % 5) as f64 * 0.1,
                ],
                inverse_mass: 1.0 / (1.0 + (index % 97) as f64),
            }
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn scalar_and_parallel_have_identical_results() {
        let bodies = fixture(16_385);
        let mut scalar = IntegrationView::pack(&bodies);
        let mut parallel = scalar.clone();
        for _ in 0..7 {
            scalar.integrate_scalar(1.0 / 60.0);
            parallel.integrate_parallel(1.0 / 60.0);
        }
        assert_eq!(scalar.checksum(), parallel.checksum());
        assert_eq!(scalar.position_x, parallel.position_x);
        assert_eq!(scalar.velocity_z, parallel.velocity_z);
    }

    #[test]
    fn pack_preserves_canonical_order() {
        let bodies = fixture(257);
        let view = IntegrationView::pack(&bodies);
        assert_eq!(view.len(), bodies.len());
        for (index, body) in bodies.iter().enumerate() {
            assert_eq!(view.position_x[index], body.position[0]);
            assert_eq!(view.inverse_mass[index], body.inverse_mass);
        }
    }
}
