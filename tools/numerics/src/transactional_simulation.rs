use std::collections::{BTreeMap, BTreeSet};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

static NEXT_WORLD_ID: AtomicU64 = AtomicU64::new(1);

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Vec3 {
    pub x: f64,
    pub y: f64,
    pub z: f64,
}

impl Vec3 {
    pub const ZERO: Self = Self {
        x: 0.0,
        y: 0.0,
        z: 0.0,
    };

    fn is_finite(self) -> bool {
        self.x.is_finite() && self.y.is_finite() && self.z.is_finite()
    }
}

impl std::ops::Add for Vec3 {
    type Output = Self;
    fn add(self, rhs: Self) -> Self {
        Self {
            x: self.x + rhs.x,
            y: self.y + rhs.y,
            z: self.z + rhs.z,
        }
    }
}

impl std::ops::Mul<f64> for Vec3 {
    type Output = Self;
    fn mul(self, rhs: f64) -> Self {
        Self {
            x: self.x * rhs,
            y: self.y * rhs,
            z: self.z * rhs,
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct BodyState {
    pub id: u64,
    pub generation: u64,
    pub mass: f64,
    pub radius: f64,
    pub position: Vec3,
    pub velocity: Vec3,
}

impl BodyState {
    pub fn new(
        id: u64,
        generation: u64,
        mass: f64,
        radius: f64,
        position: Vec3,
        velocity: Vec3,
    ) -> Result<Self, ValidationError> {
        if !mass.is_finite() || mass <= 0.0 || !radius.is_finite() || radius <= 0.0 {
            return Err(ValidationError::InvalidBody);
        }
        if !position.is_finite() || !velocity.is_finite() {
            return Err(ValidationError::InvalidBody);
        }
        Ok(Self {
            id,
            generation,
            mass,
            radius,
            position,
            velocity,
        })
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct Snapshot {
    frame: Arc<str>,
    base_epoch: f64,
    offset: f64,
    bodies: Arc<[BodyState]>,
}

impl Snapshot {
    pub fn new(
        frame: impl Into<Arc<str>>,
        base_epoch: f64,
        offset: f64,
        bodies: impl IntoIterator<Item = BodyState>,
    ) -> Result<Self, ValidationError> {
        let frame = frame.into();
        let mut bodies: Vec<_> = bodies.into_iter().collect();
        bodies.sort_unstable_by_key(|body| body.id);
        if frame.is_empty()
            || !base_epoch.is_finite()
            || !offset.is_finite()
            || offset < 0.0
            || bodies.is_empty()
        {
            return Err(ValidationError::InvalidSnapshot);
        }
        if bodies.windows(2).any(|pair| pair[0].id == pair[1].id) {
            return Err(ValidationError::InvalidSnapshot);
        }
        Ok(Self {
            frame,
            base_epoch,
            offset,
            bodies: bodies.into(),
        })
    }

    pub fn frame(&self) -> &str {
        &self.frame
    }
    pub fn base_epoch(&self) -> f64 {
        self.base_epoch
    }
    pub fn offset(&self) -> f64 {
        self.offset
    }
    pub fn bodies(&self) -> &[BodyState] {
        &self.bodies
    }
}

#[derive(Clone, Debug)]
pub struct Plan {
    world_id: u64,
    revision: u64,
    frame: Arc<str>,
    base_epoch: f64,
    offset: f64,
    members: Arc<[(u64, u64)]>,
}

#[derive(Clone, Debug)]
pub struct AdmittedBatch {
    snapshot: Snapshot,
    target_offset: f64,
}

impl AdmittedBatch {
    pub fn snapshot(&self) -> &Snapshot {
        &self.snapshot
    }
    pub fn target_offset(&self) -> f64 {
        self.target_offset
    }
}

#[derive(Clone, Debug)]
pub struct SolvedBatch {
    pub frame: Arc<str>,
    pub base_epoch: f64,
    pub offset: f64,
    pub bodies: Vec<BodyState>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ValidationError {
    InvalidBody,
    InvalidSnapshot,
    MissingBody,
    DuplicateBody,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SolveError {
    Cancelled,
    Failed,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TransactionStatus {
    Committed,
    Busy,
    Cancelled,
    SolverFailed,
    Stale,
    InvalidResult,
}

#[derive(Default)]
pub struct Cancellation {
    cancelled: AtomicBool,
}

impl Cancellation {
    pub fn cancel(&self) {
        self.cancelled.store(true, Ordering::Release);
    }
    pub fn is_cancelled(&self) -> bool {
        self.cancelled.load(Ordering::Acquire)
    }
}

struct WorldState {
    snapshot: Arc<Snapshot>,
    revision: u64,
    active: bool,
}

pub struct SimulationWorld {
    id: u64,
    state: Mutex<WorldState>,
}

impl SimulationWorld {
    pub fn new(initial: Snapshot) -> Self {
        Self {
            id: NEXT_WORLD_ID.fetch_add(1, Ordering::Relaxed),
            state: Mutex::new(WorldState {
                snapshot: Arc::new(initial),
                revision: 1,
                active: false,
            }),
        }
    }

    pub fn capture(&self) -> Arc<Snapshot> {
        Arc::clone(
            &self
                .state
                .lock()
                .expect("simulation world poisoned")
                .snapshot,
        )
    }

    pub fn revision(&self) -> u64 {
        self.state
            .lock()
            .expect("simulation world poisoned")
            .revision
    }

    pub fn replace(&self, snapshot: Snapshot) {
        let mut state = self.state.lock().expect("simulation world poisoned");
        state.snapshot = Arc::new(snapshot);
        state.revision = state.revision.checked_add(1).expect("revision overflow");
    }

    pub fn plan(&self, member_ids: &[u64]) -> Result<Plan, ValidationError> {
        let state = self.state.lock().expect("simulation world poisoned");
        let requested: BTreeSet<_> = member_ids.iter().copied().collect();
        if requested.len() != member_ids.len() {
            return Err(ValidationError::DuplicateBody);
        }
        let members: Vec<_> = state
            .snapshot
            .bodies
            .iter()
            .filter(|body| requested.contains(&body.id))
            .map(|body| (body.id, body.generation))
            .collect();
        if members.len() != requested.len() || members.is_empty() {
            return Err(ValidationError::MissingBody);
        }
        Ok(Plan {
            world_id: self.id,
            revision: state.revision,
            frame: Arc::clone(&state.snapshot.frame),
            base_epoch: state.snapshot.base_epoch,
            offset: state.snapshot.offset,
            members: members.into(),
        })
    }

    pub fn execute<F>(
        &self,
        plan: &Plan,
        target_offset: f64,
        cancellation: &Cancellation,
        solver: F,
    ) -> TransactionStatus
    where
        F: FnOnce(AdmittedBatch, &Cancellation) -> Result<SolvedBatch, SolveError>,
    {
        let (admitted, admitted_revision) = {
            let mut state = self.state.lock().expect("simulation world poisoned");
            if cancellation.is_cancelled() {
                return TransactionStatus::Cancelled;
            }
            if state.active {
                return TransactionStatus::Busy;
            }
            if !plan_matches(self.id, plan, &state) {
                return TransactionStatus::Stale;
            }
            if !target_offset.is_finite() || target_offset < state.snapshot.offset {
                return TransactionStatus::InvalidResult;
            }
            let members: BTreeSet<_> = plan.members.iter().map(|member| member.0).collect();
            let selected: Vec<_> = state
                .snapshot
                .bodies
                .iter()
                .filter(|body| members.contains(&body.id))
                .cloned()
                .collect();
            let snapshot = Snapshot::new(
                Arc::clone(&state.snapshot.frame),
                state.snapshot.base_epoch,
                state.snapshot.offset,
                selected,
            )
            .expect("admitted state was previously validated");
            state.active = true;
            (
                AdmittedBatch {
                    snapshot,
                    target_offset,
                },
                state.revision,
            )
        };

        let solved = solver(admitted.clone(), cancellation);
        let mut state = self.state.lock().expect("simulation world poisoned");
        state.active = false;
        if cancellation.is_cancelled() || matches!(solved, Err(SolveError::Cancelled)) {
            return TransactionStatus::Cancelled;
        }
        if state.revision != admitted_revision || !plan_matches(self.id, plan, &state) {
            return TransactionStatus::Stale;
        }
        let output = match solved {
            Ok(output) => output,
            Err(SolveError::Failed) => return TransactionStatus::SolverFailed,
            Err(SolveError::Cancelled) => unreachable!(),
        };
        let candidate = match validate_and_merge(&state.snapshot, &admitted, output) {
            Ok(candidate) => candidate,
            Err(_) => return TransactionStatus::InvalidResult,
        };
        state.snapshot = Arc::new(candidate);
        state.revision = state.revision.checked_add(1).expect("revision overflow");
        TransactionStatus::Committed
    }
}

fn plan_matches(world_id: u64, plan: &Plan, state: &WorldState) -> bool {
    if plan.world_id != world_id
        || plan.revision != state.revision
        || plan.frame.as_ref() != state.snapshot.frame.as_ref()
        || plan.base_epoch != state.snapshot.base_epoch
        || plan.offset != state.snapshot.offset
    {
        return false;
    }
    plan.members.iter().all(|&(id, generation)| {
        state
            .snapshot
            .bodies
            .iter()
            .any(|body| body.id == id && body.generation == generation)
    })
}

fn validate_and_merge(
    authoritative: &Snapshot,
    admitted: &AdmittedBatch,
    output: SolvedBatch,
) -> Result<Snapshot, ValidationError> {
    if output.frame.as_ref() != admitted.snapshot.frame()
        || output.base_epoch != admitted.snapshot.base_epoch
        || !output.offset.is_finite()
        || output.offset < admitted.snapshot.offset
        || output.offset > admitted.target_offset
        || output.bodies.len() != admitted.snapshot.bodies.len()
    {
        return Err(ValidationError::InvalidSnapshot);
    }
    let dt = output.offset - admitted.snapshot.offset;
    let mut replacements = BTreeMap::new();
    for (index, body) in output.bodies.into_iter().enumerate() {
        let before = &admitted.snapshot.bodies[index];
        if body.id != before.id {
            return Err(ValidationError::InvalidSnapshot);
        }
        if replacements.contains_key(&body.id) {
            return Err(ValidationError::DuplicateBody);
        }
        let coasted = before.position + before.velocity * dt;
        if body.generation != before.generation
            || body.mass != before.mass
            || body.radius != before.radius
            || body.position != coasted
            || !body.velocity.is_finite()
        {
            return Err(ValidationError::InvalidBody);
        }
        replacements.insert(body.id, body);
    }
    let merged: Result<Vec<_>, _> = authoritative
        .bodies
        .iter()
        .map(|body| {
            if let Some(replacement) = replacements.remove(&body.id) {
                return Ok(replacement);
            }
            BodyState::new(
                body.id,
                body.generation,
                body.mass,
                body.radius,
                body.position + body.velocity * dt,
                body.velocity,
            )
        })
        .collect();
    if !replacements.is_empty() {
        return Err(ValidationError::MissingBody);
    }
    Snapshot::new(
        Arc::clone(&authoritative.frame),
        authoritative.base_epoch,
        output.offset,
        merged?,
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::{Barrier, mpsc};
    use std::thread;

    type Corruptor = Box<dyn Fn(&mut SolvedBatch)>;

    fn body(id: u64, generation: u64, x: f64, vx: f64) -> BodyState {
        BodyState::new(
            id,
            generation,
            1.0,
            1.0,
            Vec3 { x, y: 0.0, z: 0.0 },
            Vec3 {
                x: vx,
                y: 0.0,
                z: 0.0,
            },
        )
        .unwrap()
    }

    fn initial() -> Snapshot {
        Snapshot::new(
            "local",
            1e20,
            0.0,
            [
                body(1, 0, -10.0, 1.0),
                body(2, 0, 10.0, -1.0),
                body(3, 0, 1000.0, 1.0),
            ],
        )
        .unwrap()
    }

    fn coasted(input: &AdmittedBatch, offset: f64) -> SolvedBatch {
        let dt = offset - input.snapshot.offset;
        SolvedBatch {
            frame: Arc::clone(&input.snapshot.frame),
            base_epoch: input.snapshot.base_epoch,
            offset,
            bodies: input
                .snapshot
                .bodies
                .iter()
                .cloned()
                .map(|mut body| {
                    body.position = body.position + body.velocity * dt;
                    body
                })
                .collect(),
        }
    }

    #[test]
    fn commits_nonempty_dynamics_and_coasts_quiet_bodies_atomically() {
        let world = SimulationWorld::new(initial());
        let plan = world.plan(&[1, 2]).unwrap();
        let revision = world.revision();
        let status = world.execute(&plan, 9.0, &Cancellation::default(), |input, _| {
            let mut output = coasted(&input, 9.0);
            output.bodies[0].velocity.x = -1.0;
            output.bodies[1].velocity.x = 1.0;
            Ok(output)
        });
        assert_eq!(status, TransactionStatus::Committed);
        let result = world.capture();
        assert_eq!(world.revision(), revision + 1);
        assert_eq!(result.offset(), 9.0);
        assert_eq!(result.bodies()[0].position.x, -1.0);
        assert_eq!(result.bodies()[0].velocity.x, -1.0);
        assert_eq!(result.bodies()[2].position.x, 1009.0);
    }

    #[test]
    fn cancellation_and_solver_failure_publish_nothing() {
        let world = SimulationWorld::new(initial());
        let plan = world.plan(&[1, 2]).unwrap();
        let before = world.capture();
        assert_eq!(
            world.execute(&plan, 9.0, &Cancellation::default(), |_, _| Err(
                SolveError::Failed
            )),
            TransactionStatus::SolverFailed
        );
        assert!(Arc::ptr_eq(&before, &world.capture()));
        let cancellation = Cancellation::default();
        assert_eq!(
            world.execute(&plan, 9.0, &cancellation, |input, cancellation| {
                cancellation.cancel();
                Ok(coasted(&input, 9.0))
            }),
            TransactionStatus::Cancelled
        );
        assert!(Arc::ptr_eq(&before, &world.capture()));
    }

    #[test]
    fn stale_generation_frame_and_revision_results_publish_nothing() {
        let world = SimulationWorld::new(initial());
        let valid = world.plan(&[1, 2]).unwrap();
        let before = world.capture();
        let mut changed_frame = valid.clone();
        changed_frame.frame = Arc::from("changed-frame");
        assert_eq!(
            world.execute(
                &changed_frame,
                9.0,
                &Cancellation::default(),
                |input, _| Ok(coasted(&input, 9.0))
            ),
            TransactionStatus::Stale
        );
        let mut changed_generation = valid;
        changed_generation.members = Arc::from([(1, 99), (2, 0)]);
        assert_eq!(
            world.execute(
                &changed_generation,
                9.0,
                &Cancellation::default(),
                |input, _| Ok(coasted(&input, 9.0))
            ),
            TransactionStatus::Stale
        );
        assert!(Arc::ptr_eq(&before, &world.capture()));

        for replacement in [
            Snapshot::new("other-frame", 1e20, 0.0, initial().bodies().iter().cloned()).unwrap(),
            Snapshot::new(
                "local",
                1e20,
                0.0,
                [
                    body(1, 1, -10.0, 1.0),
                    body(2, 0, 10.0, -1.0),
                    body(3, 0, 1000.0, 1.0),
                ],
            )
            .unwrap(),
            initial(),
        ] {
            let world = Arc::new(SimulationWorld::new(initial()));
            let plan = world.plan(&[1, 2]).unwrap();
            let barrier = Arc::new(Barrier::new(2));
            let (resume_tx, resume_rx) = mpsc::channel();
            let worker_world = Arc::clone(&world);
            let worker_barrier = Arc::clone(&barrier);
            let worker = thread::spawn(move || {
                worker_world.execute(&plan, 9.0, &Cancellation::default(), |input, _| {
                    worker_barrier.wait();
                    resume_rx.recv().unwrap();
                    Ok(coasted(&input, 9.0))
                })
            });
            barrier.wait();
            world.replace(replacement);
            let authoritative = world.capture();
            resume_tx.send(()).unwrap();
            assert_eq!(worker.join().unwrap(), TransactionStatus::Stale);
            assert!(Arc::ptr_eq(&authoritative, &world.capture()));
        }
    }

    #[test]
    fn malformed_or_partial_output_never_partially_publishes() {
        let mut corruptors: Vec<Corruptor> = vec![
            Box::new(|output| output.frame = Arc::from("wrong")),
            Box::new(|output| output.offset = 10.0),
            Box::new(|output| {
                output.bodies.pop();
            }),
            Box::new(|output| output.bodies[0].generation += 1),
            Box::new(|output| output.bodies[0].position.x += 0.5),
            Box::new(|output| output.bodies[0].velocity.x = f64::NAN),
            Box::new(|output| output.bodies.swap(0, 1)),
        ];
        for corrupt in corruptors.drain(..) {
            let world = SimulationWorld::new(initial());
            let plan = world.plan(&[1, 2]).unwrap();
            let before = world.capture();
            let status = world.execute(&plan, 9.0, &Cancellation::default(), |input, _| {
                let mut output = coasted(&input, 9.0);
                corrupt(&mut output);
                Ok(output)
            });
            assert_eq!(status, TransactionStatus::InvalidResult);
            assert!(Arc::ptr_eq(&before, &world.capture()));
        }
    }

    #[test]
    fn solver_runs_outside_authority_lock_and_second_transaction_is_busy() {
        let world = SimulationWorld::new(initial());
        let plan = world.plan(&[1, 2]).unwrap();
        let status = world.execute(&plan, 9.0, &Cancellation::default(), |input, _| {
            assert_eq!(world.capture().offset(), 0.0);
            assert_eq!(
                world.execute(&plan, 9.0, &Cancellation::default(), |nested, _| Ok(
                    coasted(&nested, 9.0)
                )),
                TransactionStatus::Busy
            );
            Ok(coasted(&input, 9.0))
        });
        assert_eq!(status, TransactionStatus::Committed);
    }
}
