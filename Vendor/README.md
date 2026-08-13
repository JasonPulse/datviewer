# Vendored sources

These files are **copied** from [JasonPulse/vellichor](https://github.com/JasonPulse/vellichor) so the
DAT viewer builds and releases as a self-contained repo (a Godot C# export runs `dotnet publish`,
which does not reliably resolve project references outside the project tree). Do **not** edit them
here — fix them in Vellichor and re-copy.

| here | source (in `../Vellichor`) |
|------|----------------------------|
| `Dat/*.cs` | `Vellichor.Dat/*.cs` — pure FFXI DAT decoders (FTABLE/VTABLE, chunks, IMG, MMB, models, skeletons) |
| `Render/CharacterModel.cs` etc. (4 files) | `Render/{CharacterModel,SkinnedMeshBuilder,AnimationDriver,ModelResolver}.cs` — Godot-typed posed/skinned build |
| `XiProtocol/EntityLook.cs` | the single `EntityLook` struct from `XiProtocol/vendor/Game/WorldState.cs` |

## Resync

```sh
V=../../Vellichor          # a Vellichor checkout
cp $V/Vellichor.Dat/*.cs Dat/
cp $V/Render/CharacterModel.cs $V/Render/SkinnedMeshBuilder.cs \
   $V/Render/AnimationDriver.cs $V/Render/ModelResolver.cs Render/
# EntityLook.cs: hand-check against XiProtocol/vendor/Game/WorldState.cs (struct EntityLook)
```

Vellichor also publishes `Vellichor.Dat`/`Vellichor.XiProtocol` as NuGet packages and a
`Vellichor.Render.Source-vX.zip` release asset; this vendored copy is the source-level equivalent.
