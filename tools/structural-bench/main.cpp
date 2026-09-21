#include <Jolt/Jolt.h>
#include <Jolt/RegisterTypes.h>
#include <Jolt/Core/Factory.h>
#include <Jolt/Core/TempAllocator.h>
#include <Jolt/Core/JobSystemSingleThreaded.h>
#include <Jolt/Physics/PhysicsSystem.h>
#include <Jolt/Physics/Body/BodyCreationSettings.h>
#include <Jolt/Physics/Collision/Shape/SphereShape.h>
#include <Jolt/Physics/Collision/Shape/BoxShape.h>
#include <Jolt/Physics/Constraints/DistanceConstraint.h>
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
constexpr double Mass1 = 2, Mass2 = 5, Stiffness = 25, Damping = 0.5;
constexpr double RestLength = 2, Extension = 0.2, Step = 0.02;
constexpr int Steps = 250;

class BroadLayers final : public BroadPhaseLayerInterface {
public:
    uint GetNumBroadPhaseLayers() const override { return 2; }
    BroadPhaseLayer GetBroadPhaseLayer(ObjectLayer layer) const override { return BroadPhaseLayer(static_cast<uint8>(layer)); }
};
class PairFilter final : public ObjectLayerPairFilter {
public:
    bool ShouldCollide(ObjectLayer a, ObjectLayer b) const override { return a == 1 || b == 1; }
};
class BroadFilter final : public ObjectVsBroadPhaseLayerFilter {
public:
    bool ShouldCollide(ObjectLayer a, BroadPhaseLayer b) const override { return a == 1 || b == BroadPhaseLayer(1); }
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
        physics.SetGravity(Vec3::sZero());
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
        for (BodyID id : bodies) { api.RemoveBody(id); api.DestroyBody(id); }
    }
    Body &Add(const Shape *shape, RVec3 position, double mass, bool dynamic = true) {
        BodyCreationSettings settings(shape, position, Quat::sIdentity(),
            dynamic ? EMotionType::Dynamic : EMotionType::Static, dynamic ? 1 : 0);
        settings.mLinearDamping = 0;
        settings.mAngularDamping = 0;
        settings.mAllowSleeping = false;
        settings.mFriction = 0;
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

struct Sample { double t, q, v, com, momentum, milliseconds; };
struct Run {
    int substeps, repeat;
    std::vector<Sample> samples;
    double qError = 0, vError = 0, comError = 0, momentumError = 0;
    bool Qualified() const { return qError <= .005 && vError <= .02 && comError <= 1e-5 && momentumError <= 1e-5; }
};

void Reference(double t, double &q, double &v) {
    double mu = Mass1 * Mass2 / (Mass1 + Mass2);
    double decay = Damping / (2 * mu);
    double omega = std::sqrt(Stiffness / mu - decay * decay);
    q = Extension * std::exp(-decay * t) * (std::cos(omega * t) + decay / omega * std::sin(omega * t));
    v = -Extension * (Stiffness / mu) / omega * std::exp(-decay * t) * std::sin(omega * t);
}

Run Oscillator(int substeps, int repeat) {
    World world;
    RefConst<Shape> sphere = new SphereShape(.1f);
    const double separation = RestLength + Extension;
    Body &a = world.Add(sphere, RVec3(-separation * Mass2 / (Mass1 + Mass2), 0, 0), Mass1);
    Body &b = world.Add(sphere, RVec3(separation * Mass1 / (Mass1 + Mass2), 0, 0), Mass2);
    DistanceConstraintSettings joint;
    joint.mPoint1 = a.GetPosition();
    joint.mPoint2 = b.GetPosition();
    joint.mMinDistance = joint.mMaxDistance = static_cast<float>(RestLength);
    joint.mLimitsSpringSettings = SpringSettings(ESpringMode::StiffnessAndDamping, Stiffness, Damping);
    world.constraint = joint.Create(a, b);
    world.physics.AddConstraint(world.constraint);
    world.physics.OptimizeBroadPhase();
    Run result{substeps, repeat, {}};
    result.samples.reserve(Steps + 1);
    for (int i = 0; i <= Steps; ++i) {
        double ms = i == 0 ? 0 : world.Update(Step, substeps);
        double x1 = a.GetPosition().GetX(), x2 = b.GetPosition().GetX();
        double v1 = a.GetLinearVelocity().GetX(), v2 = b.GetLinearVelocity().GetX();
        Sample s{i * Step, x2 - x1 - RestLength, v2 - v1,
            (Mass1 * x1 + Mass2 * x2) / (Mass1 + Mass2), Mass1 * v1 + Mass2 * v2, ms};
        double q, v;
        Reference(s.t, q, v);
        if (!std::isfinite(s.q) || !std::isfinite(s.v) || !std::isfinite(s.com) || !std::isfinite(s.momentum))
            throw std::runtime_error("Nonfinite oscillator observation");
        result.qError = std::max(result.qError, std::abs(s.q - q));
        result.vError = std::max(result.vError, std::abs(s.v - v));
        result.comError = std::max(result.comError, std::abs(s.com));
        result.momentumError = std::max(result.momentumError, std::abs(s.momentum));
        result.samples.push_back(s);
    }
    return result;
}

struct DropSample { double t, height, velocity, milliseconds; };
struct DropResult { std::vector<DropSample> samples; bool qualified = true; };
DropResult Drop() {
    World world;
    world.physics.SetGravity(Vec3(0, -9.81f, 0));
    RefConst<Shape> floor = new BoxShape(Vec3(5, .5f, 5));
    RefConst<Shape> sphere = new SphereShape(.25f);
    world.Add(floor, RVec3(0, -.5, 0), 1, false);
    Body &body = world.Add(sphere, RVec3(0, 2, 0), 1);
    world.physics.OptimizeBroadPhase();
    DropResult result;
    for (int i = 0; i <= 500; ++i) {
        double ms = i == 0 ? 0 : world.Update(.01, 4);
        DropSample s{i * .01, body.GetPosition().GetY(), body.GetLinearVelocity().GetY(), ms};
        if (!std::isfinite(s.height) || !std::isfinite(s.velocity)) throw std::runtime_error("Nonfinite drop observation");
        if (s.height < .225 || (i <= 50 && std::abs(s.height - (2 - .5 * 9.81 * s.t * s.t)) >= .015)
            || (i > 400 && (std::abs(s.height - .25) >= .025 || std::abs(s.velocity) >= .05))) result.qualified = false;
        result.samples.push_back(s);
    }
    return result;
}

void WriteRun(const Run &r) {
    std::cout << "{\"collisionSteps\":" << r.substeps << ",\"repeat\":" << r.repeat
        << ",\"qualified\":" << r.Qualified() << ",\"maxExtensionErrorM\":" << r.qError
        << ",\"maxVelocityErrorMps\":" << r.vError << ",\"maxCenterOfMassDriftM\":" << r.comError
        << ",\"maxMomentumKgMps\":" << r.momentumError << ",\"samples\":[";
    bool comma = false;
    for (const auto &s : r.samples) {
        if (comma) std::cout << ',';
        comma = true;
        std::cout << "{\"timeSeconds\":" << s.t << ",\"extensionM\":" << s.q
            << ",\"relativeVelocityMps\":" << s.v << ",\"centerOfMassM\":" << s.com
            << ",\"momentumKgMps\":" << s.momentum << ",\"updateMilliseconds\":" << s.milliseconds << '}';
    }
    std::cout << "]}";
}
}

int main(int argc, char **) {
    if (argc != 1) { std::cerr << "This fixed fixture accepts no arguments.\n"; return 1; }
    RegisterDefaultAllocator();
    Factory::sInstance = new Factory();
    RegisterTypes();
    int exitCode = 0;
    try {
        std::vector<Run> runs;
        const int configurations[] = {1, 2, 4, 8, 16};
        for (int n : configurations) Oscillator(n, -1);
        for (int repeat = 0; repeat < 3; ++repeat)
            for (int index = 0; index < 5; ++index)
                runs.push_back(Oscillator(configurations[(index + repeat * 2) % 5], repeat));
        DropResult drop = Drop();
        bool qualified = drop.qualified;
        for (const auto &r : runs) if (r.substeps == 16 && !r.Qualified()) qualified = false;
        std::cout.imbue(std::locale::classic());
        std::cout << std::boolalpha << std::setprecision(17)
            << "{\"schema\":\"ksp-continuum-structural/v1\",\"joltVersion\":\"5.6.0\","
            << "\"joltCommit\":\"e77f175595e64cb44218cc9d9d56fc365ad0e36a\","
            << "\"compiler\":\"" CONTINUUM_COMPILER "\",\"architecture\":\"" << Architecture << "\","
            << "\"units\":\"metres, kilograms, seconds, newtons\",\"doublePrecisionPositions\":true,"
            << "\"crossPlatformDeterministicBuild\":true,\"assertions\":false,\"lto\":false,"
            << "\"workerThreads\":0,\"velocityIterations\":10,\"positionIterations\":2,\"sleeping\":false,"
            << "\"measurement\":\"steady_clock around synchronous PhysicsSystem::Update only; setup, observation, validation and JSON excluded\","
            << "\"warmup\":\"one discarded complete oscillator per configuration; measured runs recreate worlds; drop has no warmup\","
            << "\"strategyOrder\":\"repeat index rotates configuration order by two positions\","
            << "\"stockPhysicsSpeedupMeasured\":false,\"qualified\":" << qualified
            << ",\"macroStepSeconds\":" << Step
            << ",\"oscillator\":{\"mass1Kg\":2,\"mass2Kg\":5,\"stiffnessNpm\":25,\"dampingNsPm\":0.5,"
            << "\"restLengthM\":2,\"initialExtensionM\":0.2,\"gravityMps2\":0,\"bodyLinearDamping\":0,\"bodyAngularDamping\":0},"
            << "\"criteria\":{\"maxExtensionErrorM\":0.005,\"maxVelocityErrorMps\":0.02,\"maxCenterOfMassDriftM\":0.00001,\"maxMomentumKgMps\":0.00001},"
            << "\"oscillatorRuns\":[";
        for (size_t i = 0; i < runs.size(); ++i) { if (i) std::cout << ','; WriteRun(runs[i]); }
        std::cout << "],\"drop\":{\"qualified\":" << drop.qualified
            << ",\"macroStepSeconds\":0.01,\"collisionSteps\":4,\"radiusM\":0.25,\"gravityMps2\":9.81,"
            << "\"restitution\":0,\"friction\":0,\"motionQuality\":\"Discrete\",\"samples\":[";
        for (size_t i = 0; i < drop.samples.size(); ++i) {
            if (i) std::cout << ',';
            const auto &s = drop.samples[i];
            std::cout << "{\"timeSeconds\":" << s.t << ",\"heightM\":" << s.height
                << ",\"velocityMps\":" << s.velocity << ",\"updateMilliseconds\":" << s.milliseconds << '}';
        }
        std::cout << "]}}\n";
        exitCode = qualified ? 0 : 2;
    } catch (const std::exception &error) { std::cerr << error.what() << '\n'; exitCode = 1; }
    UnregisterTypes();
    delete Factory::sInstance;
    Factory::sInstance = nullptr;
    return exitCode;
}
