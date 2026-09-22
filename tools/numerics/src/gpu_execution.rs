use bytemuck::{Pod, Zeroable};
use std::sync::mpsc;
use wgpu::util::DeviceExt;

use crate::execution_view::IntegrationView;

const SHADER: &str = r#"
struct Parameters {
    count: u32,
    steps: u32,
    dt: f32,
    _padding: u32,
}

@group(0) @binding(0) var<storage, read_write> positions: array<vec4<f32>>;
@group(0) @binding(1) var<storage, read_write> velocities: array<vec4<f32>>;
@group(0) @binding(2) var<storage, read> forces_and_inverse_mass: array<vec4<f32>>;
@group(0) @binding(3) var<uniform> parameters: Parameters;

@compute @workgroup_size(256)
fn integrate(@builtin(global_invocation_id) invocation: vec3<u32>) {
    let index = invocation.x;
    if index >= parameters.count {
        return;
    }
    var position = positions[index];
    var velocity = velocities[index];
    let force_mass = forces_and_inverse_mass[index];
    for (var step = 0u; step < parameters.steps; step += 1u) {
        let scale = parameters.dt * force_mass.w;
        velocity.x += force_mass.x * scale;
        velocity.y += force_mass.y * scale;
        velocity.z += force_mass.z * scale;
        position.x += velocity.x * parameters.dt;
        position.y += velocity.y * parameters.dt;
        position.z += velocity.z * parameters.dt;
    }
    positions[index] = position;
    velocities[index] = velocity;
}
"#;

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct Parameters {
    count: u32,
    steps: u32,
    dt: f32,
    _padding: u32,
}

#[derive(Debug)]
pub struct GpuOutput {
    pub position_x: Vec<f64>,
    pub position_y: Vec<f64>,
    pub position_z: Vec<f64>,
    pub velocity_x: Vec<f64>,
    pub velocity_y: Vec<f64>,
    pub velocity_z: Vec<f64>,
}

pub struct GpuIntegrator {
    adapter_info: wgpu::AdapterInfo,
    device: wgpu::Device,
    queue: wgpu::Queue,
    layout: wgpu::BindGroupLayout,
    pipeline: wgpu::ComputePipeline,
}

pub struct GpuResidentState {
    bind_group: wgpu::BindGroup,
    positions: wgpu::Buffer,
    velocities: wgpu::Buffer,
    readback: wgpu::Buffer,
    count: usize,
    byte_len: u64,
    position_anchors: Vec<[f64; 3]>,
}

impl GpuIntegrator {
    pub async fn new() -> Result<Self, String> {
        let instance = wgpu::Instance::new(&wgpu::InstanceDescriptor {
            backends: wgpu::Backends::METAL,
            ..Default::default()
        });
        let adapter = instance
            .request_adapter(&wgpu::RequestAdapterOptions {
                power_preference: wgpu::PowerPreference::HighPerformance,
                force_fallback_adapter: false,
                compatible_surface: None,
            })
            .await
            .map_err(|error| format!("no Metal adapter: {error}"))?;
        let adapter_info = adapter.get_info();
        let (device, queue) = adapter
            .request_device(&wgpu::DeviceDescriptor {
                label: Some("Continuum execution benchmark"),
                required_features: wgpu::Features::empty(),
                required_limits: wgpu::Limits::downlevel_defaults()
                    .using_resolution(adapter.limits()),
                memory_hints: wgpu::MemoryHints::Performance,
                trace: wgpu::Trace::Off,
            })
            .await
            .map_err(|error| format!("cannot create Metal device: {error}"))?;
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("Continuum SoA integration"),
            source: wgpu::ShaderSource::Wgsl(SHADER.into()),
        });
        let layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("Continuum integration columns"),
            entries: &[
                storage_entry(0, false),
                storage_entry(1, false),
                storage_entry(2, true),
                wgpu::BindGroupLayoutEntry {
                    binding: 3,
                    visibility: wgpu::ShaderStages::COMPUTE,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false,
                        min_binding_size: None,
                    },
                    count: None,
                },
            ],
        });
        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("Continuum integration pipeline layout"),
            bind_group_layouts: &[&layout],
            push_constant_ranges: &[],
        });
        let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
            label: Some("Continuum SoA integration pipeline"),
            layout: Some(&pipeline_layout),
            module: &shader,
            entry_point: Some("integrate"),
            compilation_options: Default::default(),
            cache: None,
        });
        Ok(Self {
            adapter_info,
            device,
            queue,
            layout,
            pipeline,
        })
    }

    pub fn adapter_identity(&self) -> String {
        format!(
            "{} ({:?}, {:?}, driver {})",
            self.adapter_info.name,
            self.adapter_info.backend,
            self.adapter_info.device_type,
            self.adapter_info.driver
        )
    }

    pub fn cold_integrate(&self, view: &IntegrationView, dt: f32) -> Result<GpuOutput, String> {
        let resident = self.upload(view, dt, 1);
        self.dispatch_and_read(&resident, 1)
    }

    pub fn upload_resident(&self, view: &IntegrationView, dt: f32) -> GpuResidentState {
        self.upload(view, dt, 1)
    }

    pub fn integrate_resident(
        &self,
        resident: &GpuResidentState,
        dispatches: u32,
    ) -> Result<GpuOutput, String> {
        self.dispatch_and_read(resident, dispatches)
    }

    fn upload(&self, view: &IntegrationView, dt: f32, steps: u32) -> GpuResidentState {
        let count = view.len();
        let mut positions = Vec::<[f32; 4]>::with_capacity(count);
        let mut velocities = Vec::<[f32; 4]>::with_capacity(count);
        let mut forces = Vec::<[f32; 4]>::with_capacity(count);
        for index in 0..count {
            positions.push([0.0; 4]);
            velocities.push([
                view.velocity_x[index] as f32,
                view.velocity_y[index] as f32,
                view.velocity_z[index] as f32,
                0.0,
            ]);
            forces.push([
                view.force_x[index] as f32,
                view.force_y[index] as f32,
                view.force_z[index] as f32,
                view.inverse_mass[index] as f32,
            ]);
        }
        let positions = self
            .device
            .create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some("Continuum positions"),
                contents: bytemuck::cast_slice(&positions),
                usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_SRC,
            });
        let velocities = self
            .device
            .create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some("Continuum velocities"),
                contents: bytemuck::cast_slice(&velocities),
                usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_SRC,
            });
        let forces = self
            .device
            .create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some("Continuum forces and inverse mass"),
                contents: bytemuck::cast_slice(&forces),
                usage: wgpu::BufferUsages::STORAGE,
            });
        let parameters = self
            .device
            .create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some("Continuum integration parameters"),
                contents: bytemuck::bytes_of(&Parameters {
                    count: count as u32,
                    steps,
                    dt,
                    _padding: 0,
                }),
                usage: wgpu::BufferUsages::UNIFORM,
            });
        let byte_len = (count * std::mem::size_of::<[f32; 4]>()) as u64;
        let readback = self.device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("Continuum integration readback"),
            size: byte_len * 2,
            usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
            mapped_at_creation: false,
        });
        let bind_group = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("Continuum integration columns"),
            layout: &self.layout,
            entries: &[
                binding(0, &positions),
                binding(1, &velocities),
                binding(2, &forces),
                binding(3, &parameters),
            ],
        });
        let position_anchors = (0..count)
            .map(|index| {
                [
                    view.position_x[index],
                    view.position_y[index],
                    view.position_z[index],
                ]
            })
            .collect();
        GpuResidentState {
            bind_group,
            positions,
            velocities,
            readback,
            count,
            byte_len,
            position_anchors,
        }
    }

    fn dispatch_and_read(
        &self,
        resident: &GpuResidentState,
        dispatches: u32,
    ) -> Result<GpuOutput, String> {
        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("Continuum integration dispatch"),
            });
        {
            let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
                label: Some("Continuum integration"),
                timestamp_writes: None,
            });
            pass.set_pipeline(&self.pipeline);
            pass.set_bind_group(0, &resident.bind_group, &[]);
            for _ in 0..dispatches {
                pass.dispatch_workgroups((resident.count as u32).div_ceil(256), 1, 1);
            }
        }
        encoder.copy_buffer_to_buffer(
            &resident.positions,
            0,
            &resident.readback,
            0,
            resident.byte_len,
        );
        encoder.copy_buffer_to_buffer(
            &resident.velocities,
            0,
            &resident.readback,
            resident.byte_len,
            resident.byte_len,
        );
        self.queue.submit(Some(encoder.finish()));
        let slice = resident.readback.slice(..);
        let (sender, receiver) = mpsc::channel();
        slice.map_async(wgpu::MapMode::Read, move |result| {
            let _ = sender.send(result);
        });
        self.device
            .poll(wgpu::PollType::Wait)
            .map_err(|error| format!("GPU wait failed: {error}"))?;
        receiver
            .recv()
            .map_err(|error| format!("GPU map callback failed: {error}"))?
            .map_err(|error| format!("GPU map failed: {error}"))?;
        let mapped = slice.get_mapped_range();
        let values: &[[f32; 4]] = bytemuck::cast_slice(&mapped);
        let (positions, velocities) = values.split_at(resident.count);
        let mut output = GpuOutput {
            position_x: Vec::with_capacity(resident.count),
            position_y: Vec::with_capacity(resident.count),
            position_z: Vec::with_capacity(resident.count),
            velocity_x: Vec::with_capacity(resident.count),
            velocity_y: Vec::with_capacity(resident.count),
            velocity_z: Vec::with_capacity(resident.count),
        };
        for (index, value) in positions.iter().enumerate() {
            output
                .position_x
                .push(resident.position_anchors[index][0] + value[0] as f64);
            output
                .position_y
                .push(resident.position_anchors[index][1] + value[1] as f64);
            output
                .position_z
                .push(resident.position_anchors[index][2] + value[2] as f64);
        }
        for value in velocities {
            output.velocity_x.push(value[0] as f64);
            output.velocity_y.push(value[1] as f64);
            output.velocity_z.push(value[2] as f64);
        }
        drop(mapped);
        resident.readback.unmap();
        Ok(output)
    }
}

fn storage_entry(binding: u32, read_only: bool) -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::COMPUTE,
        ty: wgpu::BindingType::Buffer {
            ty: wgpu::BufferBindingType::Storage { read_only },
            has_dynamic_offset: false,
            min_binding_size: None,
        },
        count: None,
    }
}

fn binding(binding: u32, buffer: &wgpu::Buffer) -> wgpu::BindGroupEntry<'_> {
    wgpu::BindGroupEntry {
        binding,
        resource: buffer.as_entire_binding(),
    }
}
