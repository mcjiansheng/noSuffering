# Snapshot load barrier regression

Run against the freshly built mod and the actual target game assemblies:

```sh
.tools/dotnet/dotnet run --project tests/load-barrier/LoadBarrier.csproj -c Release \
  -p:ModAssembly=/absolute/path/NoSuffering.dll \
  -p:Sts2AssemblyDir=/absolute/path/game-assemblies
```

The test invokes the production receive/enter paths with a captured host transport
and no live RunManager. It covers early peer installation, roster authentication,
stale operation/epoch/revision/context rejection, deduplication, waiting for local
installation, one Enter broadcast, released messages and preserved wire values.
Actual native synchronizer replacement and room entry are checked by the two-peer
game probe and cross-machine gameplay.
