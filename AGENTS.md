# Project instructions

## Git branch

- The user has designated `DinkyPuffer` as the branch for ongoing project work.
- Make future project commits on `DinkyPuffer` and push to `origin/DinkyPuffer` unless the user explicitly requests a different branch.
- Before committing or pushing, verify the current branch. Do not publish project work to `main` under this standing instruction.
- Preserve existing unrelated local changes and untracked files.

## DeepSeek local worker

For useful, bounded bulk tasks within the current user request, use the
`deepseek-worker` skill at
`C:/Users/AWQ/.codex/skills/deepseek-worker/SKILL.md` and its local
submit/status/result bridge. Prefer private task packets, explicit write scope,
and independently verified output. Keep long-range reasoning, evidence judgments,
integration, commits, and publishing with the primary agent. This standing
authorization does not expand the task or authorize unrelated external actions.
Do not delegate when it provides no benefit or conflicts with higher-priority
instructions.

## Audit coverage matrix

- Maintain `klink bot/docs/audit-matrix-reviews.json` for exact partial evidence scopes and current open items.
- Regenerate `out/audit/coverage-matrix` with `tools/build-audit-matrix.py` and run `tools/test-audit-matrix.py` after relevant audit changes.
- Report mentions and shared regression results are candidate links, never automatic card certification.
- Do not refresh stale evidence fingerprints without rechecking the affected scope. Missing raw evidence and version mismatches remain visible.
- Keep historical unresolved text separate from the current reviewed work list. Do not derive an overall percentage before the authoritative PvP scope and completion denominator are established.
- Maintain `klink bot/docs/audit-work-plan.json` for acceptance contracts and exact historical-claim dispositions. When updating progress estimates or this baseline, rebuild `out/audit/progress-baseline` with `tools/build-audit-baseline.py` and run `tools/test-audit-baseline.py`; use `--hash-evidence` before claiming current file provenance. File matches or changed fingerprints alone do not certify rules or establish defects.
- Use `tools/analyze-audit-history.py` and the SDK Roslyn helper in `tools/AuditHistory` for exact historical byte/member comparisons. Check current fingerprints before reusing a review. Token/trivia equality does not close callees, shared state, compilation configurations or game-rule semantics; unrecovered versions must not be replaced by nearby commits.
