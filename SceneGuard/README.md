# SceneGuard

Mac-only Unity Editor fix for SceneView black / missing content on Metal.

## Current stable release

See **[unity-patches/README.md](unity-patches/README.md)** for file list, install path, and defaults.

Devlog: **[devlogs/2026-06-08-sceneview-fallback-stable.md](devlogs/2026-06-08-sceneview-fallback-stable.md)**

## Layout

| Path | Purpose |
|------|---------|
| `unity-patches/` | Git mirror of verified `Assets/Editor/SceneGuard*` Unity sources |
| `devlogs/` | Dated investigation and confirmation notes |
| `plans/` | Phase plans |
| `specs/` | Design spec |

## App apply (GpuSafeGuard)

| Channel | Bundle path | Settings button |
|---------|-------------|-----------------|
| Runtime GPU safe | `Resources/templates/` | **Apply unity inner safe** |
| SceneGuard tools | `Resources/scene-guard-tools/` | **Apply SceneGuard tools** |

Refresh bundle from git mirror: `./tools/sync_scene_guard_tools.sh` then `./build.sh`.

## Related

- Unity project (Perforce): `WorkSpace_Ryan_Mac`
- Design doc (Perforce): `Tools&Docs/MacGPUSafeGuard/SceneGuard/SceneGuardDesignDoc.md`
- Runtime GPU protection (separate feature): `Resources/templates/MacGPUSafeGuard.cs`
