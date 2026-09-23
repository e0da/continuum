use rayon::prelude::*;

pub const BLOCK_WIDTH: usize = 8;

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Body {
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

#[derive(Clone, Debug)]
pub struct AosView(pub Vec<Body>);

impl AosView {
    pub fn integrate(&mut self, dt: f64) {
        for body in &mut self.0 {
            integrate_body(body, dt);
        }
    }
}

#[derive(Clone, Debug)]
pub struct SoaView {
    pub px: Vec<f64>,
    pub py: Vec<f64>,
    pub pz: Vec<f64>,
    pub vx: Vec<f64>,
    pub vy: Vec<f64>,
    pub vz: Vec<f64>,
    pub fx: Vec<f64>,
    pub fy: Vec<f64>,
    pub fz: Vec<f64>,
    pub inverse_mass: Vec<f64>,
}

impl SoaView {
    pub fn from_bodies(bodies: &[Body]) -> Self {
        let mut view = Self {
            px: Vec::with_capacity(bodies.len()),
            py: Vec::with_capacity(bodies.len()),
            pz: Vec::with_capacity(bodies.len()),
            vx: Vec::with_capacity(bodies.len()),
            vy: Vec::with_capacity(bodies.len()),
            vz: Vec::with_capacity(bodies.len()),
            fx: Vec::with_capacity(bodies.len()),
            fy: Vec::with_capacity(bodies.len()),
            fz: Vec::with_capacity(bodies.len()),
            inverse_mass: Vec::with_capacity(bodies.len()),
        };
        for body in bodies {
            view.px.push(body.px);
            view.py.push(body.py);
            view.pz.push(body.pz);
            view.vx.push(body.vx);
            view.vy.push(body.vy);
            view.vz.push(body.vz);
            view.fx.push(body.fx);
            view.fy.push(body.fy);
            view.fz.push(body.fz);
            view.inverse_mass.push(body.inverse_mass);
        }
        view
    }

    pub fn integrate(&mut self, dt: f64) {
        self.px
            .iter_mut()
            .zip(&mut self.py)
            .zip(&mut self.pz)
            .zip(&mut self.vx)
            .zip(&mut self.vy)
            .zip(&mut self.vz)
            .zip(&self.fx)
            .zip(&self.fy)
            .zip(&self.fz)
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
}

#[derive(Clone, Debug)]
pub struct Block {
    pub px: [f64; BLOCK_WIDTH],
    pub py: [f64; BLOCK_WIDTH],
    pub pz: [f64; BLOCK_WIDTH],
    pub vx: [f64; BLOCK_WIDTH],
    pub vy: [f64; BLOCK_WIDTH],
    pub vz: [f64; BLOCK_WIDTH],
    pub fx: [f64; BLOCK_WIDTH],
    pub fy: [f64; BLOCK_WIDTH],
    pub fz: [f64; BLOCK_WIDTH],
    pub inverse_mass: [f64; BLOCK_WIDTH],
}

impl Default for Block {
    fn default() -> Self {
        Self {
            px: [0.0; BLOCK_WIDTH],
            py: [0.0; BLOCK_WIDTH],
            pz: [0.0; BLOCK_WIDTH],
            vx: [0.0; BLOCK_WIDTH],
            vy: [0.0; BLOCK_WIDTH],
            vz: [0.0; BLOCK_WIDTH],
            fx: [0.0; BLOCK_WIDTH],
            fy: [0.0; BLOCK_WIDTH],
            fz: [0.0; BLOCK_WIDTH],
            inverse_mass: [0.0; BLOCK_WIDTH],
        }
    }
}

#[derive(Clone, Debug)]
pub struct BlockedView {
    pub blocks: Vec<Block>,
    pub len: usize,
}

impl BlockedView {
    pub fn from_bodies(bodies: &[Body]) -> Self {
        let mut blocks = vec![Block::default(); bodies.len().div_ceil(BLOCK_WIDTH)];
        for (index, body) in bodies.iter().enumerate() {
            let block = &mut blocks[index / BLOCK_WIDTH];
            let lane = index % BLOCK_WIDTH;
            block.px[lane] = body.px;
            block.py[lane] = body.py;
            block.pz[lane] = body.pz;
            block.vx[lane] = body.vx;
            block.vy[lane] = body.vy;
            block.vz[lane] = body.vz;
            block.fx[lane] = body.fx;
            block.fy[lane] = body.fy;
            block.fz[lane] = body.fz;
            block.inverse_mass[lane] = body.inverse_mass;
        }
        Self {
            blocks,
            len: bodies.len(),
        }
    }

    pub fn integrate(&mut self, dt: f64) {
        self.blocks
            .iter_mut()
            .for_each(|block| integrate_block(block, dt));
    }
    pub fn integrate_parallel(&mut self, dt: f64) {
        self.blocks
            .par_iter_mut()
            .for_each(|block| integrate_block(block, dt));
    }
}

fn integrate_block(block: &mut Block, dt: f64) {
    for lane in 0..BLOCK_WIDTH {
        let scale = dt * block.inverse_mass[lane];
        block.vx[lane] += block.fx[lane] * scale;
        block.vy[lane] += block.fy[lane] * scale;
        block.vz[lane] += block.fz[lane] * scale;
        block.px[lane] += block.vx[lane] * dt;
        block.py[lane] += block.vy[lane] * dt;
        block.pz[lane] += block.vz[lane] * dt;
    }
}

fn integrate_body(body: &mut Body, dt: f64) {
    let scale = dt * body.inverse_mass;
    body.vx += body.fx * scale;
    body.vy += body.fy * scale;
    body.vz += body.fz * scale;
    body.px += body.vx * dt;
    body.py += body.vy * dt;
    body.pz += body.vz * dt;
}

pub fn fixture(count: usize) -> Vec<Body> {
    (0..count)
        .map(|i| {
            let x = i as f64;
            Body {
                px: x * 0.25,
                py: (i % 31) as f64,
                pz: -x * 0.125,
                vx: 0.5 + x * 0.000_01,
                vy: -0.25,
                vz: (i % 7) as f64 * 0.01,
                fx: ((i % 13) as f64 - 6.0) * 0.2,
                fy: -9.81,
                fz: (i % 5) as f64 * 0.1,
                inverse_mass: 1.0 / (1.0 + (i % 97) as f64),
            }
        })
        .collect()
}

pub fn checksum_bodies(bodies: &[Body]) -> u64 {
    let mut hash = 0xcbf2_9ce4_8422_2325_u64;
    for body in bodies {
        for value in [body.px, body.py, body.pz, body.vx, body.vy, body.vz] {
            hash ^= value.to_bits();
            hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
        }
    }
    hash
}

pub fn materialize_soa(view: &SoaView) -> Vec<Body> {
    (0..view.px.len())
        .map(|i| Body {
            px: view.px[i],
            py: view.py[i],
            pz: view.pz[i],
            vx: view.vx[i],
            vy: view.vy[i],
            vz: view.vz[i],
            fx: view.fx[i],
            fy: view.fy[i],
            fz: view.fz[i],
            inverse_mass: view.inverse_mass[i],
        })
        .collect()
}

pub fn materialize_blocked(view: &BlockedView) -> Vec<Body> {
    (0..view.len)
        .map(|i| {
            let b = &view.blocks[i / BLOCK_WIDTH];
            let l = i % BLOCK_WIDTH;
            Body {
                px: b.px[l],
                py: b.py[l],
                pz: b.pz[l],
                vx: b.vx[l],
                vy: b.vy[l],
                vz: b.vz[l],
                fx: b.fx[l],
                fy: b.fy[l],
                fz: b.fz[l],
                inverse_mass: b.inverse_mass[l],
            }
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn all_layouts_produce_exact_output_including_partial_block() {
        let initial = fixture(16_387);
        let mut aos = AosView(initial.clone());
        let mut soa = SoaView::from_bodies(&initial);
        let mut blocked = BlockedView::from_bodies(&initial);
        let mut parallel = blocked.clone();
        for _ in 0..7 {
            aos.integrate(1.0 / 60.0);
            soa.integrate(1.0 / 60.0);
            blocked.integrate(1.0 / 60.0);
            parallel.integrate_parallel(1.0 / 60.0);
        }
        assert_eq!(aos.0, materialize_soa(&soa));
        assert_eq!(aos.0, materialize_blocked(&blocked));
        assert_eq!(aos.0, materialize_blocked(&parallel));
    }
}
