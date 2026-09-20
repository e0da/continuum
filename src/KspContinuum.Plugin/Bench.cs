using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KspContinuum
{
    [Serializable] public sealed class BenchReport
    {
        public string schema = "ksp-continuum-bench/v1";
        public string status = "synthetic-engine-benchmark-only";
        public string utc = DateTime.UtcNow.ToString("o");
        public string unity = Application.unityVersion;
        public string ksp = Versioning.GetVersionString();
        public string plugin = typeof(Bench).Assembly.GetName().Version.ToString();
        public string platform = Application.platform.ToString();
        public float stepSeconds = 0.02f;
        public int warmupSteps = 50, measuredSteps = 200, solverIterations = 6, solverVelocityIterations = 1;
        public Sample[] samples;
        public float splitLinearMomentumError, splitAngularMomentumError;
        public bool splitPassed, collisionPassed;
        public float collisionFinalY;
    }
    [Serializable] public sealed class Sample
    {
        public int boxes, bodies, joints, pair, colliderRayHits;
        public bool compound;
        public double millisecondsPerStep;
        public float maxSpacingError;
    }

    public sealed class Bench : IDisposable
    {
        Scene active;
        const float Dt = 0.02f;
        public void Dispose()
        {
            if (active.IsValid() && active.isLoaded) SceneManager.UnloadSceneAsync(active);
            active = default(Scene);
        }
        void NewScene()
        {
            active = SceneManager.CreateScene("KspContinuum-" + Guid.NewGuid().ToString("N"), new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            if (active.GetPhysicsScene() == Physics.defaultPhysicsScene) throw new InvalidOperationException("Physics scene was not isolated");
        }
        GameObject NewObject(string name, Vector3 position)
        {
            var obj = new GameObject(name);
            SceneManager.MoveGameObjectToScene(obj, active);
            obj.transform.position = position;
            return obj;
        }
        static Vector3 V(Vec v) { return new Vector3((float)v.X, (float)v.Y, (float)v.Z); }
        static Rigidbody Body(GameObject obj, double mass, Vec inertia)
        {
            var body = obj.AddComponent<Rigidbody>();
            body.useGravity = false; body.drag = 0; body.angularDrag = 0;
            body.sleepThreshold = 0; body.solverIterations = 6; body.solverVelocityIterations = 1;
            body.maxAngularVelocity = 100;
            body.mass = (float)mass; body.inertiaTensor = V(inertia); body.inertiaTensorRotation = Quaternion.identity;
            return body;
        }
        static Box[] Boxes(int count)
        {
            var boxes = new Box[count];
            for (int i = 0; i < count; i++) boxes[i] = new Box(1 + i % 3, i * 1.1, 1, 1, 1);
            return boxes;
        }
        Rigidbody Compound(Box[] boxes, out Transform[] shapes)
        {
            var props = AssemblyModel.Combine(boxes);
            var root = NewObject("compound", Vector3.zero);
            shapes = new Transform[boxes.Length];
            for (int i = 0; i < boxes.Length; i++)
            {
                var obj = NewObject("box", new Vector3((float)boxes[i].CenterX, 0, 0));
                obj.transform.SetParent(root.transform, true);
                obj.AddComponent<BoxCollider>().size = new Vector3((float)boxes[i].X, (float)boxes[i].Y, (float)boxes[i].Z);
                shapes[i] = obj.transform;
            }
            var rb = Body(root, props.Mass, props.Inertia);
            rb.centerOfMass = new Vector3((float)props.CenterX, 0, 0);
            return rb;
        }
        Sample Measure(int count, bool compound, int pair)
        {
            var boxes = Boxes(count);
            Transform[] shapes;
            Rigidbody driven;
            if (compound) driven = Compound(boxes, out shapes);
            else
            {
                shapes = new Transform[count];
                Rigidbody previous = null; driven = null;
                for (int i = 0; i < count; i++)
                {
                    var obj = NewObject("jointed-box", new Vector3((float)boxes[i].CenterX, 0, 0));
                    obj.AddComponent<BoxCollider>();
                    var rb = Body(obj, boxes[i].Mass, boxes[i].Inertia);
                    if (previous != null)
                    {
                        var joint = obj.AddComponent<FixedJoint>();
                        joint.connectedBody = previous;
                        joint.anchor = new Vector3(-0.55f, 0, 0);
                        joint.autoConfigureConnectedAnchor = false;
                        joint.connectedAnchor = new Vector3(0.55f, 0, 0);
                        joint.enableCollision = false;
                    }
                    if (driven == null) driven = rb;
                    previous = rb; shapes[i] = obj.transform;
                }
            }
            var physics = active.GetPhysicsScene();
            physics.Simulate(Dt);
            int hits = 0;
            foreach (var shape in shapes)
                if (physics.Raycast(shape.position + Vector3.up * 2, Vector3.down, 4)) hits++;
            for (int i = 0; i < 50; i++) Step(physics, driven, shapes[0]);
            var timer = Stopwatch.StartNew();
            for (int i = 0; i < 200; i++) Step(physics, driven, shapes[0]);
            timer.Stop();
            float error = 0;
            for (int i = 1; i < count; i++) error = Mathf.Max(error, Mathf.Abs(Vector3.Distance(shapes[i].position, shapes[i - 1].position) - 1.1f));
            return new Sample { boxes = count, bodies = compound ? 1 : count, joints = compound ? 0 : count - 1,
                compound = compound, pair = pair, colliderRayHits = hits, millisecondsPerStep = timer.Elapsed.TotalMilliseconds / 200, maxSpacingError = error };
        }
        static void Step(PhysicsScene scene, Rigidbody driven, Transform end)
        {
            driven.AddForceAtPosition(Vector3.up, end.position, ForceMode.Force);
            scene.Simulate(Dt);
        }
        static Vector3 SpinMomentum(Rigidbody rb)
        {
            var principal = rb.rotation * rb.inertiaTensorRotation;
            return principal * Vector3.Scale(rb.inertiaTensor, Quaternion.Inverse(principal) * rb.angularVelocity);
        }
        void Split(BenchReport report)
        {
            var boxes = Boxes(3);
            Transform[] shapes;
            var parent = Compound(boxes, out shapes);
            parent.rotation = Quaternion.Euler(20, 30, 40);
            parent.velocity = new Vector3(2, 3, 4); parent.angularVelocity = new Vector3(0.2f, -0.3f, 0.4f);
            var center = parent.worldCenterOfMass;
            var momentum = parent.mass * parent.velocity;
            var angular = SpinMomentum(parent);
            var sumP = Vector3.zero; var sumL = Vector3.zero;
            for (int i = 0; i < boxes.Length; i++)
            {
                var position = shapes[i].position;
                var velocity = parent.GetPointVelocity(position);
                shapes[i].SetParent(null, true);
                var child = Body(shapes[i].gameObject, boxes[i].Mass, boxes[i].Inertia);
                child.velocity = velocity; child.angularVelocity = parent.angularVelocity;
                var p = child.mass * child.velocity;
                sumP += p; sumL += SpinMomentum(child) + Vector3.Cross(child.worldCenterOfMass - center, p);
            }
            parent.gameObject.SetActive(false);
            report.splitLinearMomentumError = (sumP - momentum).magnitude / Mathf.Max(1, momentum.magnitude);
            report.splitAngularMomentumError = (sumL - angular).magnitude / Mathf.Max(1, angular.magnitude);
            report.splitPassed = report.splitLinearMomentumError < 1e-5f && report.splitAngularMomentumError < 1e-5f;
        }
        void Collision(BenchReport report)
        {
            Transform[] shapes;
            var body = Compound(Boxes(3), out shapes);
            var floor = NewObject("floor", new Vector3(1.1f, -2, 0));
            floor.AddComponent<BoxCollider>().size = new Vector3(10, 1, 10);
            body.velocity = Vector3.down;
            for (int i = 0; i < 200; i++) active.GetPhysicsScene().Simulate(Dt);
            report.collisionFinalY = body.position.y;
            report.collisionPassed = body.position.y > -1.1f && body.position.y < -0.8f && Mathf.Abs(body.velocity.y) < 0.05f;
        }
        public IEnumerator Run(Action<BenchReport> complete)
        {
            var report = new BenchReport();
            var samples = new List<Sample>();
            try
            {
                foreach (int count in new[] { 8, 32, 128 })
                    for (int pair = 0; pair < 6; pair++)
                        for (int order = 0; order < 2; order++)
                        {
                            NewScene();
                            samples.Add(Measure(count, (pair + order) % 2 == 0, pair));
                            var cleanup = SceneManager.UnloadSceneAsync(active); active = default(Scene);
                            yield return cleanup;
                        }
                NewScene(); Split(report);
                var splitCleanup = SceneManager.UnloadSceneAsync(active); active = default(Scene);
                yield return splitCleanup;
                NewScene(); Collision(report);
                var collisionCleanup = SceneManager.UnloadSceneAsync(active); active = default(Scene);
                yield return collisionCleanup;
                report.samples = samples.ToArray();
                complete(report);
            }
            finally { Dispose(); }
        }
    }
}
