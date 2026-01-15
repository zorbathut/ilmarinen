# DAG-Based Parallel Execution

## Goal

Run independent steps concurrently while respecting dependencies.

Currently steps run sequentially. Need to:
1. Add `.Needs()` to `StepBuilder` for explicit dependencies
2. Build dependency graph from step declarations
3. Execute ready steps in parallel, track completion
4. Add concurrency limit option

## Execution Algorithm

```
1. Build dependency graph from step declarations
2. Identify steps with no dependencies (ready set)
3. While steps remain:
   a. Run all ready steps in parallel
   b. Wait for any to complete
   c. Mark completed, check if dependents are now ready
   d. Add newly ready steps to ready set
4. Collect results, report failures
```

## Artifact Storage Layout

For the artifact store implementation:

```
.ilmarinen/
├── artifacts/
│   └── <build-id>/
│       ├── <step-name>/
│       │   └── <artifact-name>/
│       │       └── ... files ...
│       └── ...
└── workspace/
    └── <build-id>/
        └── ... shared workspace ...
```

## Implementation Deliverables

### DAG Scheduler
- Dependency graph builder from step declarations
- Parallel executor with completion tracking
- Configurable concurrency limit

### Artifact Store
- `LocalArtifactStore` - filesystem-based implementation
- `ctx.Output(name, glob)` - capture files
- `ctx.Input(name)` - retrieve files
- Automatic cleanup after pipeline completion
