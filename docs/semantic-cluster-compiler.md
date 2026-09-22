# Semantic cluster compiler prototype

The cluster compiler is an engine-generic test of the smallest useful form of assembly baking: it partitions a logical object attachment graph into solver clusters while retaining every original object ID. Ordinary rigid attachments join one cluster. Detachable, articulated, compliant and external-interface attachments become split seams. Objects declared independently simulated or externally addressable cut every incident attachment.

KSP names do not enter the compiler. A KSP/Gimbal adapter can classify decouplers as detachable, robotics as articulated, explicit-flex links as compliant, wheels and control surfaces as independently simulated, and docking ports as external interfaces. Another Continuum consumer can supply different source semantics through the same metadata.

Each cluster ID is the SHA-256 digest of its sorted logical member IDs. Input enumeration, attachment order and endpoint orientation therefore cannot change membership or cluster identity. The plan also returns a complete part-to-cluster map and an ordered seam list with all applicable reasons. A UI or mod integration layer can continue addressing an original part through that map even when a future physics adapter represents several parts with one rigid body.

`StructuralClusterCandidateCompiler` adds the read-only admission layer for captured facts. Each part names its observed native body and an optional boundary classification. Each attachment names its optional native joint and optional behavior classification. Missing classifications become `UnknownSemantics` split boundaries and explicit abstentions; missing census bodies and joints remain in the projected counts. The compiler refuses a proposal when one captured native body would cross a semantic seam. It does not infer behavior from part names, modules, collider shapes or mod identity.

The representative fixture contains 17 logical parts, 10 observed bodies, 9 observed joints and 17 observed colliders. Explicit rigid attachment facts compile it to six proposed bodies and five preserved joints: a reduction of four bodies and four joints. One science part lacks boundary semantics, so it remains isolated behind an `UnknownSemantics` seam and produces one abstention. All observed bodies and joints are accounted for. A second case retains one unmapped body and one unmapped joint instead of treating them as removable work.

This candidate deliberately stops before geometry, mass properties, resources, modules, damage, collision refinement or live KSP mutation. The 40% body-count reduction is a graph result for the fixture, not a solver speedup or approval to bake a live vessel. The next decision is whether a read-only KSP adapter can populate the same facts for one representative vessel without semantic guesses. Only then should a geometry experiment construct and validate a compound collider for one fully admitted cluster.

Run the portable experiment with:

```sh
dotnet run --project tests/KspContinuum.ClusterCompiler.Tests -c Release
```
