#include <Jolt/Jolt.h>
#include <Jolt/RegisterTypes.h>
#include <Jolt/Core/Factory.h>
#include <Jolt/Core/TempAllocator.h>
#include <Jolt/Core/JobSystemSingleThreaded.h>
#include <Jolt/Physics/PhysicsSystem.h>
#include <Jolt/Physics/Body/BodyCreationSettings.h>
#include <Jolt/Physics/Collision/Shape/BoxShape.h>
#include <Jolt/Physics/Constraints/DistanceConstraint.h>
#include <Jolt/Physics/StateRecorderImpl.h>
#include <array>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <iomanip>
#include <iostream>
#include <locale>
#include <stdexcept>
#include <vector>
using namespace JPH;
namespace {
#if defined(__aarch64__) || defined(_M_ARM64)
constexpr const char *Architecture = "arm64";
#elif defined(__x86_64__) || defined(_M_X64)
constexpr const char *Architecture = "x86_64";
#else
constexpr const char *Architecture = "other";
#endif
constexpr double Step = 1.0 / 120;
constexpr int WarmupSteps = 24, ContinuationSteps = 120;
class BroadLayers final : public BroadPhaseLayerInterface {
    public :
    uint GetNumBroadPhaseLayers() const override { return 2;
    }
    BroadPhaseLayer GetBroadPhaseLayer(ObjectLayer layer) const override { return BroadPhaseLayer(static_cast<uint8>(layer));
    }
};
class PairFilter final : public ObjectLayerPairFilter {
    public :
    bool ShouldCollide(ObjectLayer a, ObjectLayer b) const override { return a == 1 || b == 1;
    }
};
class BroadFilter final : public ObjectVsBroadPhaseLayerFilter {
    public :
    bool ShouldCollide(ObjectLayer a, BroadPhaseLayer b) const override { return a == 1 || b == BroadPhaseLayer(1);
    }
};
// Own all objects until Update has returned; no callbacks escape this single-threaded fixture.

struct World {
    BroadLayers layers;
    PairFilter pairs;
    BroadFilter broad;
    TempAllocatorImpl allocator{16 * 1024 * 1024};
    JobSystemSingleThreaded jobs{cMaxPhysicsJobs};
    PhysicsSystem physics;
    std::vector<BodyID> bodies;
    Ref<Constraint> constraint;
    World() {
        physics.Init(16, 0, 64, 64, layers, broad, pairs);
        physics.SetGravity(Vec3(0, -9.81f, 0));
        PhysicsSettings settings;
        settings.mNumVelocitySteps = 10;
        settings.mNumPositionSteps = 2;
        settings.mAllowSleeping = false;
        physics.SetPhysicsSettings(settings);
    }
    ~World() {
        if (constraint != nullptr) physics.RemoveConstraint(constraint);
        constraint = nullptr;
        auto &api = physics.GetBodyInterface();
        for (BodyID id : bodies) {
            api.RemoveBody(id);
            api.DestroyBody(id);
        }
    }
    Body &Add(const Shape *shape, RVec3 position, double mass, bool dynamic = true) {
        BodyCreationSettings settings(shape, position, Quat::sIdentity(),
        dynamic ? EMotionType::Dynamic : EMotionType::Static, dynamic ? 1 : 0);
        settings.mLinearDamping = 0;
        settings.mAngularDamping = 0;
        settings.mAllowSleeping = false;
        settings.mFriction = .6f;
        settings.mRestitution = 0;
        if (dynamic) {
            settings.mOverrideMassProperties = EOverrideMassProperties::CalculateInertia;
            settings.mMassPropertiesOverride.mMass = static_cast<float>(mass);
        }
        auto &api = physics.GetBodyInterface();
        Body *body = api.CreateBody(settings);
        if (body == nullptr) throw std::runtime_error("Body creation failed");
        bodies.push_back(body->GetID());
        api.AddBody(body->GetID(), dynamic ? EActivation::Activate : EActivation::DontActivate);
        return *body;
    }
    double Update(double dt, int substeps) {
        auto begin = std::chrono::steady_clock::now();
        auto status = physics.Update(static_cast<float>(dt), substeps, &allocator, &jobs);
        auto end = std::chrono::steady_clock::now();
        if (status != EPhysicsUpdateError::None) throw std::runtime_error("Jolt Update capacity/error status");
        return std::chrono::duration<double, std::milli>(end - begin).count();
    }
};

struct Pose {
    RVec3 position;
    Quat rotation;
    Vec3 linear, angular;
};
using State = std::array<double, 26>;
struct Sample {
    int step;
    State state;
    double error, drift, ms;
    std::array<bool, 2> active;
};

Pose Capture(World &w, int index) {
    auto &api = w.physics.GetBodyInterface();
    BodyID id = w.bodies[index];
    return {api.GetPosition(id), api.GetRotation(id), api.GetLinearVelocity(id), api.GetAngularVelocity(id)};
}

void Fixture(World &w, const std::array<Pose, 2> *poses = nullptr) {
    RefConst<Shape> floor = new BoxShape(Vec3(20, .5f, 20));
    RefConst<Shape> box = new BoxShape(Vec3(.4f, .25f, .4f));
    w.Add(floor, RVec3(0, -.5, 0), 1, false);
    Body &a = w.Add(box, RVec3(-.6, .26, 0), 1), &b = w.Add(box, RVec3(.6, .26, 0), 1);
    auto &api = w.physics.GetBodyInterface();
    if (poses) {
        for (int i = 0; i < 2; i++) {
            const auto &p = (*poses)[i];
            api.SetPositionAndRotation(w.bodies[i + 1], p.position, p.rotation, EActivation::Activate);
            api.SetLinearAndAngularVelocity(w.bodies[i + 1], p.linear, p.angular);
        }
    } else {
        api.SetLinearAndAngularVelocity(a.GetID(), Vec3(.8f, 0, 0), Vec3(0, 0, .2f));
        api.SetLinearAndAngularVelocity(b.GetID(), Vec3(.1f, 0, 0), Vec3(0, 0, -.2f));
    }
    DistanceConstraintSettings joint;
    joint.mSpace = EConstraintSpace::LocalToBodyCOM;
    joint.mPoint1 = joint.mPoint2 = RVec3::sZero();
    joint.mMinDistance = joint.mMaxDistance = 1.2f;
    w.constraint = joint.Create(a, b);
    w.physics.AddConstraint(w.constraint);
    w.physics.OptimizeBroadPhase();
}

State Values(World &w) {
    State result{};
    for (int i = 0; i < 2; i++) {
        Pose p = Capture(w, i + 1);
        int o = i * 13;
        result[o] = p.position.GetX();
        result[o + 1] = p.position.GetY();
        result[o + 2] = p.position.GetZ();
        result[o + 3] = p.rotation.GetX();
        result[o + 4] = p.rotation.GetY();
        result[o + 5] = p.rotation.GetZ();
        result[o + 6] = p.rotation.GetW();
        result[o + 7] = p.linear.GetX();
        result[o + 8] = p.linear.GetY();
        result[o + 9] = p.linear.GetZ();
        result[o + 10] = p.angular.GetX();
        result[o + 11] = p.angular.GetY();
        result[o + 12] = p.angular.GetZ();
    }
    for (double x : result) {
        if (!std::isfinite(x)) throw std::runtime_error("Nonfinite trajectory");
    }
    return result;
}

std::vector<Sample> Continue(World &w) {
    std::vector<Sample> result;
    auto start = Values(w);
    double center = (start[0] + start[13]) * .5;
    for (int i = 0; i <= ContinuationSteps; i++) {
        if (i == 30) w.physics.GetBodyInterface().AddImpulse(w.bodies[1], Vec3(.15f, 0, .05f));
        double ms = i ? w.Update(Step, 1) : 0;
        auto s = Values(w);
        double dx = s[0] - s[13], dy = s[1] - s[14], dz = s[2] - s[15];
        result.push_back({i, s, std::abs(std::sqrt(dx * dx + dy * dy + dz * dz) - 1.2), (s[0] + s[13]) * .5 - center, ms, {w.physics.GetBodyInterface().IsActive(w.bodies[1]), w.physics.GetBodyInterface().IsActive(w.bodies[2])}});
    }
    return result;
}

void Write(const char *name, const std::vector<Sample> &samples) {
    std::cout << "{\"name\":\"" << name << "\",\"samples\":[";
    for (size_t i = 0; i < samples.size(); i++) {
        if (i) std::cout << ',';
        const auto &s = samples[i];
        std::cout << "{\"step\":" << s.step << ",\"active\":[" << s.active[0] << ',' << s.active[1] << "],\"state\":[";
        for (size_t j = 0; j < s.state.size(); j++) {
            if (j) std::cout << ',';
            std::cout << s.state[j];
        }
        std::cout << "],\"constraintErrorM\":" << s.error << ",\"centerXDisplacementM\":" << s.drift << ",\"updateMilliseconds\":" << s.ms << '}';
    }
    std::cout << "]}";
}
}

int main() {
    RegisterDefaultAllocator();
    Factory::sInstance = new Factory();
    RegisterTypes();
    int code = 1;
    try {
        World world;
        Fixture(world);
        for (int i = 0; i < WarmupSteps; i++) {
            if (i == WarmupSteps - 1) world.physics.GetBodyInterface().AddImpulse(world.bodies[1], Vec3(.15f, 0, .05f));
            world.Update(Step, 1);
        }
        bool contact = world.physics.WereBodiesInContact(world.bodies[0], world.bodies[1]) || world.physics.WereBodiesInContact(world.bodies[0], world.bodies[2]);
        std::array<Pose, 2> poses = {Capture(world, 1), Capture(world, 2)};
        double speed = std::max(poses[0].linear.Length(), poses[1].linear.Length());
        StateRecorderImpl checkpoint;
        world.physics.SaveState(checkpoint);
        if (checkpoint.IsFailed()) throw std::runtime_error("Save failed");
        auto original = Continue(world);
        checkpoint.Rewind();
        if (!world.physics.RestoreState(checkpoint) || checkpoint.IsFailed()) throw std::runtime_error("Restore failed; world abandoned");
        StateRecorderImpl restored;
        world.physics.SaveState(restored);
        bool bytesEqual = checkpoint.GetData() == restored.GetData();
        auto saved = Continue(world);
        World cold;
        Fixture(cold, &poses);
        auto reconstructed = Continue(cold);
        bool exact = true;
        double difference = 0;
        double maxPosition = 0, maxVelocity = 0, maxAngular = 0, maxQuaternion = 0;
        for (size_t i = 0; i < original.size(); i++) {
            for (size_t j = 0; j < 26; j++) {
                exact = exact && original[i].state[j] == saved[i].state[j];
                difference = std::max(difference, std::abs(original[i].state[j] - reconstructed[i].state[j]));
            }
        }
        for (size_t i = 0; i < original.size(); i++) {
            for (size_t body = 0; body < 2; body++) {
                double position = 0, velocity = 0, angular = 0;
                for (size_t k = 0; k < 3; k++) {
                    auto delta = [&](size_t component) {
                        return original[i].state[body * 13 + component] - reconstructed[i].state[body * 13 + component];
                    };
                    position += delta(k) * delta(k);
                    velocity += delta(k + 7) * delta(k + 7);
                    angular += delta(k + 10) * delta(k + 10);
                }
                maxPosition = std::max(maxPosition, std::sqrt(position));
                maxVelocity = std::max(maxVelocity, std::sqrt(velocity));
                maxAngular = std::max(maxAngular, std::sqrt(angular));
                for (size_t k = 3; k < 7; k++) {
                    maxQuaternion = std::max(maxQuaternion, std::abs(
                        original[i].state[body * 13 + k] - reconstructed[i].state[body * 13 + k]));
                }
            }
        }
        bool qualified = contact && speed > 1e-5 && exact && bytesEqual && original[0].state == reconstructed[0].state;
        std::cout.imbue(std::locale::classic());
        std::cout << std::boolalpha << std::setprecision(17)
            << "{\"schema\":\"ksp-continuum-checkpoint/v1\",\"qualified\":" << qualified
            << ",\"compiler\":\"" CONTINUUM_COMPILER "\",\"actualStepSeconds\":" << static_cast<float>(Step)
            << ",\"originalBodyIds\":[" << world.bodies[1].GetIndexAndSequenceNumber() << ',' << world.bodies[2].GetIndexAndSequenceNumber() << "],\"coldBodyIds\":[" << cold.bodies[1].GetIndexAndSequenceNumber() << ',' << cold.bodies[2].GetIndexAndSequenceNumber() << ']'
            << ",\"coldMaxPositionDifferenceM\":" << maxPosition << ",\"coldMaxVelocityDifferenceMps\":" << maxVelocity << ",\"coldMaxAngularVelocityDifferenceRadps\":" << maxAngular << ",\"coldMaxQuaternionComponentDifference\":" << maxQuaternion
            << ",\"joltCommit\":\"e77f175595e64cb44218cc9d9d56fc365ad0e36a\",\"architecture\":\"" << Architecture << "\","
            << "\"physicalAccuracyQualified\":false,\"crossPlatformReplayQualified\":false,\"callbackReplayQualified\":false,\"gameIntegrated\":false,"
            << "\"checkpointContact\":" << contact << ",\"checkpointSpeed\":" << speed << ",\"checkpointBytes\":" << checkpoint.GetDataSize()
            << ",\"restoredBytesEqual\":" << bytesEqual << ",\"savedExact\":" << exact << ",\"coldExact\":" << (difference == 0)
            << ",\"coldMaxStateComponentDifference\":" << difference << ",\"stepSeconds\":" << Step << ",\"warmupSteps\":" << WarmupSteps
            << ",\"workerThreads\":0,\"sleeping\":false,\"velocityIterations\":10,\"positionIterations\":2,\"collisionSteps\":1,"
            << "\"fixture\":{\"gravity\":[0,-9.81,0],\"massKg\":1,\"halfExtentsM\":[0.4,0.25,0.4],\"jointLengthM\":1.2,\"friction\":0.6,\"restitution\":0,\"damping\":0,\"preCheckpointImpulseStep\":23,\"impulseStep\":30,\"impulseKgMps\":[0.15,0,0.05]},"
            << "\"stateLayout\":\"two bodies: xyz position, xyzw quaternion, xyz linear velocity, xyz angular velocity\","
            << "\"measurement\":\"steady_clock around synchronous Update only; fixed order original,saved,cold; no performance comparison qualified\","
            << "\"coldScope\":\"fresh world with same definitions/settings/ID creation order, copied exposed pose and velocities, active bodies, recreated local COM anchors; caches and previous timestep are not restored\",\"runs\":[";
        Write("uninterrupted", original);
        std::cout << ',';
        Write("saved", saved);
        std::cout << ',';
        Write("cold", reconstructed);
        std::cout << "]}\n";
        code = qualified ? 0 : 2;
    } catch (const std::exception &e) {
        std::cerr << e.what() << '\n';
    }
    UnregisterTypes();
    delete Factory::sInstance;
    Factory::sInstance = nullptr;
    return code;
}
