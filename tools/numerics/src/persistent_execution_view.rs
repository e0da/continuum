use crate::execution_view::{Body, IntegrationView};

#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub struct EntityKey {
    pub slot: u32,
    pub generation: u64,
}

#[derive(Clone, Copy, Debug)]
struct Slot {
    generation: u64,
    body: Option<Body>,
}

#[derive(Clone, Debug, Default)]
pub struct CanonicalWorld {
    slots: Vec<Slot>,
    topology_revision: u64,
}

impl CanonicalWorld {
    pub fn insert(&mut self, slot: u32, body: Body) -> EntityKey {
        let index = slot as usize;
        if self.slots.len() <= index {
            self.slots.resize(
                index + 1,
                Slot {
                    generation: 0,
                    body: None,
                },
            );
        }
        let entry = &mut self.slots[index];
        assert!(entry.body.is_none(), "slot {slot} is already occupied");
        entry.body = Some(body);
        let generation = entry.generation;
        self.advance_topology();
        EntityKey { slot, generation }
    }

    pub fn remove(&mut self, key: EntityKey) -> Option<Body> {
        let entry = self.slots.get_mut(key.slot as usize)?;
        if entry.generation != key.generation {
            return None;
        }
        let body = entry.body.take()?;
        entry.generation = entry
            .generation
            .checked_add(1)
            .expect("entity generation exhausted");
        self.advance_topology();
        Some(body)
    }

    pub fn get(&self, key: EntityKey) -> Option<&Body> {
        let entry = self.slots.get(key.slot as usize)?;
        (entry.generation == key.generation)
            .then_some(entry.body.as_ref())
            .flatten()
    }

    pub fn get_mut(&mut self, key: EntityKey) -> Option<&mut Body> {
        let entry = self.slots.get_mut(key.slot as usize)?;
        (entry.generation == key.generation)
            .then_some(entry.body.as_mut())
            .flatten()
    }

    pub fn topology_revision(&self) -> u64 {
        self.topology_revision
    }

    fn advance_topology(&mut self) {
        self.topology_revision = self
            .topology_revision
            .checked_add(1)
            .expect("topology revision exhausted");
    }

    fn live(&self) -> impl Iterator<Item = (EntityKey, &Body)> {
        self.slots.iter().enumerate().filter_map(|(slot, entry)| {
            entry.body.as_ref().map(|body| {
                (
                    EntityKey {
                        slot: slot as u32,
                        generation: entry.generation,
                    },
                    body,
                )
            })
        })
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Refresh {
    Unchanged,
    DirtyRows(usize),
    TopologyRebuilt(usize),
}

#[derive(Clone, Debug)]
pub struct PersistentIntegrationView {
    topology_revision: u64,
    keys: Vec<EntityKey>,
    dense_row_by_slot: Vec<Option<(u64, usize)>>,
    pub view: IntegrationView,
}

impl PersistentIntegrationView {
    pub fn pack(world: &CanonicalWorld) -> Self {
        let mut result = Self {
            topology_revision: u64::MAX,
            keys: Vec::new(),
            dense_row_by_slot: Vec::new(),
            view: IntegrationView::pack(&[]),
        };
        result.rebuild(world);
        result
    }

    pub fn refresh(
        &mut self,
        world: &CanonicalWorld,
        dirty: &[EntityKey],
    ) -> Result<Refresh, EntityKey> {
        if self.topology_revision != world.topology_revision() {
            let count = self.rebuild(world);
            return Ok(Refresh::TopologyRebuilt(count));
        }
        if dirty.is_empty() {
            return Ok(Refresh::Unchanged);
        }
        for &key in dirty {
            let row = self.row(key).ok_or(key)?;
            let body = world.get(key).ok_or(key)?;
            self.write(row, body);
        }
        Ok(Refresh::DirtyRows(dirty.len()))
    }

    pub fn keys(&self) -> &[EntityKey] {
        &self.keys
    }

    fn rebuild(&mut self, world: &CanonicalWorld) -> usize {
        let live: Vec<_> = world.live().collect();
        let bodies: Vec<_> = live.iter().map(|(_, body)| **body).collect();
        self.keys = live.iter().map(|(key, _)| *key).collect();
        self.view = IntegrationView::pack(&bodies);
        self.dense_row_by_slot.clear();
        self.dense_row_by_slot.resize(world.slots.len(), None);
        for (row, key) in self.keys.iter().copied().enumerate() {
            self.dense_row_by_slot[key.slot as usize] = Some((key.generation, row));
        }
        self.topology_revision = world.topology_revision();
        self.keys.len()
    }

    fn row(&self, key: EntityKey) -> Option<usize> {
        let (generation, row) = self.dense_row_by_slot.get(key.slot as usize)?.as_ref()?;
        (*generation == key.generation).then_some(*row)
    }

    fn write(&mut self, row: usize, body: &Body) {
        self.view.position_x[row] = body.position[0];
        self.view.position_y[row] = body.position[1];
        self.view.position_z[row] = body.position[2];
        self.view.velocity_x[row] = body.velocity[0];
        self.view.velocity_y[row] = body.velocity[1];
        self.view.velocity_z[row] = body.velocity[2];
        self.view.force_x[row] = body.force[0];
        self.view.force_y[row] = body.force[1];
        self.view.force_z[row] = body.force[2];
        self.view.inverse_mass[row] = body.inverse_mass;
    }
}

pub fn fixture_world(count: usize) -> CanonicalWorld {
    let mut world = CanonicalWorld::default();
    for (slot, body) in crate::execution_view::fixture(count)
        .into_iter()
        .enumerate()
    {
        world.insert(slot as u32, body);
    }
    world
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dirty_refresh_matches_full_repack() {
        let mut world = fixture_world(257);
        let mut cached = PersistentIntegrationView::pack(&world);
        let dirty = [cached.keys()[3], cached.keys()[128], cached.keys()[256]];
        for key in dirty {
            let body = world.get_mut(key).unwrap();
            body.force[1] += key.slot as f64;
            body.velocity[2] -= 2.5;
        }
        assert_eq!(cached.refresh(&world, &dirty), Ok(Refresh::DirtyRows(3)));
        let rebuilt = PersistentIntegrationView::pack(&world);
        assert_eq!(cached.keys, rebuilt.keys);
        assert_eq!(cached.view.checksum(), rebuilt.view.checksum());
        assert_eq!(cached.view.force_y, rebuilt.view.force_y);
    }

    #[test]
    fn membership_change_rebuilds_in_stable_slot_order() {
        let mut world = fixture_world(5);
        let mut cached = PersistentIntegrationView::pack(&world);
        let removed = cached.keys()[2];
        world.remove(removed).unwrap();
        let replacement = world.insert(2, crate::execution_view::fixture(1)[0]);
        assert_ne!(removed, replacement);
        assert_eq!(cached.refresh(&world, &[]), Ok(Refresh::TopologyRebuilt(5)));
        assert_eq!(cached.keys()[2], replacement);
        assert!(
            cached
                .keys()
                .windows(2)
                .all(|pair| pair[0].slot < pair[1].slot)
        );
    }

    #[test]
    fn stale_generation_cannot_refresh_recycled_slot() {
        let mut world = fixture_world(2);
        let mut cached = PersistentIntegrationView::pack(&world);
        let stale = cached.keys()[1];
        world.remove(stale).unwrap();
        let current = world.insert(1, crate::execution_view::fixture(1)[0]);
        cached.refresh(&world, &[]).unwrap();
        assert_eq!(cached.refresh(&world, &[stale]), Err(stale));
        assert_eq!(
            cached.refresh(&world, &[current]),
            Ok(Refresh::DirtyRows(1))
        );
    }

    #[test]
    fn persistent_view_reuses_exact_scalar_and_parallel_kernels() {
        let world = fixture_world(16_385);
        let mut scalar = PersistentIntegrationView::pack(&world);
        let mut parallel = scalar.clone();
        for _ in 0..11 {
            scalar.view.integrate_scalar(1.0 / 60.0);
            parallel.view.integrate_parallel(1.0 / 60.0);
        }
        assert_eq!(scalar.keys, parallel.keys);
        assert_eq!(scalar.view.checksum(), parallel.view.checksum());
    }
}
