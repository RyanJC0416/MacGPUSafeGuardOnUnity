# SceneGuard

Mac-only Unity Editor fix for SceneView black / white / missing content on Metal.

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

## App apply (GpuSafeGuard) — three channels

| # | Channel | Bundle path | Settings button |
|---|---------|-------------|-----------------|
| 1 | Mac GPU Safe Guard | `Resources/templates/` | **Apply Mac GPU Safe Guard** |
| 2 | SceneGuard (core) | `Resources/scene-guard/` | **Apply SceneGuard** |
| 3 | SceneGuard Tools | `Resources/scene-guard-tools/` | **Apply SceneGuard tools** |

Refresh bundles from git mirror: `./tools/sync_scene_guard_bundles.sh` then `./build.sh`.

## Related

- Unity project (Perforce): `WorkSpace_Ryan_Mac`
- Design doc (Perforce): `Tools&Docs/MacGPUSafeGuard/SceneGuard/SceneGuardDesignDoc.md`
- Runtime GPU protection (separate feature): `Resources/templates/MacGPUSafeGuard.cs`
