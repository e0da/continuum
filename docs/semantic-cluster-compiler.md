# Semantic cluster compiler prototype

The cluster compiler is an engine-generic test of the smallest useful form of assembly baking: it partitions a logical object attachment graph into solver clusters while retaining every original object ID. Ordinary rigid attachments join one cluster. Detachable, articulated, compliant and external-interface attachments become split seams. Objects declared independently simulated or externally addressable cut every incident attachment.

KSP names do not enter the compiler. A KSP/Gimbal adapter can classify decouplers as detachable, robotics as articulated, explicit-flex links as compliant, wheels and control surfaces as independently simulated, and docking ports as external interfaces. Another Continuum consumer can supply different source semantics through the same metadata.

Each cluster ID is the SHA-256 digest of its sorted logical member IDs. Input enumeration, attachment order and endpoint orientation therefore cannot change membership or cluster identity. The plan also returns a complete part-to-cluster map and an ordered seam list with all applicable reasons. A UI or mod integration layer can continue addressing an original part through that map even when a future physics adapter represents several parts with one rigid body.

This prototype deliberately stops before geometry, mass properties, resources, modules, damage, collision refinement or live KSP mutation. It establishes a falsifiable graph contract for those later stages. The next experiment should capture a real vessel graph, classify its stock and modded functional boundaries, and compare the compiler's projected rigid-body and joint counts with the observed graph. Only after that result should a geometry baker construct a compound collider for one admitted cluster.

Run the portable experiment with:

```sh
dotnet run --project tests/KspContinuum.ClusterCompiler.Tests -c Release
```
