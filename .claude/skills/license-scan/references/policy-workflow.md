# Policy and baseline workflow

Use a baseline to acknowledge reviewed unresolved evidence, not to declare a license allowed. Keep the SPDX allow-list, baseline, and scan report conceptually separate:

- The scan report records dependency and license evidence.
- The allow-list states which resolved SPDX licenses policy permits.
- The baseline fingerprints unresolved evidence that reviewers accept without pretending it resolved.

## Triage check results

| Result | Action | Baseline eligible? |
|---|---|---|
| `matched` and allowed | Keep the policy unchanged. | No; it already passes. |
| `matched` but not allowed | Remove/replace the dependency, or explicitly approve its SPDX identifier in the primary or proven-development allow-list. | Never. |
| `unknown`, `ambiguous`, `conflict`, or `invalid` | Investigate the candidates and warnings. Fix evidence when practical. Acknowledge only evidence the organization has reviewed and accepts. | Only when no recognizable candidate is rejected by the active allow-list. |
| `error` | Repair authentication, network, registry, cache, or collector failure and scan again. | Never. |

An ambiguous license listing whose every possible reading is already allowed does not need baseline acknowledgement. Do not invent a concluded license to make an unresolved component pass.

## Adopt a baseline

Start with an ordinary check and preserve its violations:

```text
ol check --report ol-report.json --allow-licenses <approved-SPDX-ids>
```

Review each unresolved violation. After the review, write a deterministic complete snapshot:

```text
ol check --report ol-report.json \
  --allow-licenses <approved-SPDX-ids> \
  --baseline baseline.json \
  --update-baseline
```

`--update-baseline` replaces the file; it does not append one exception. Inspect the generated raw evidence and fingerprints, verify that forbidden resolved licenses and errors remain violations, then commit `baseline.json` with the policy change.

Verify the steady-state command without the update option:

```text
ol check --report ol-report.json \
  --allow-licenses <approved-SPDX-ids> \
  --baseline baseline.json
```

Changed component identity, status, or evidence changes its fingerprint and expires the acknowledgement. New unresolved components are not accepted by an existing baseline.

## Run in CI

Regenerate aligned resolved inputs and the canonical JSON report for the audited build, then run the steady-state check above. Keep these policy inputs versioned where practical:

- the primary SPDX allow-list;
- any resolver-proven development-only allow-list;
- `baseline.json`;
- exclusions, if the organization deliberately defines policy scope that way.

Never update a baseline automatically in CI. A failing check is the review gate. Classify each change as:

1. a dependency/evidence problem to fix;
2. a resolved SPDX license to approve or reject through the allow-list;
3. an inherently unresolved result to acknowledge after review.

Only the third case belongs in a baseline update. Regenerate the complete baseline locally or in a reviewed maintenance workflow, inspect the diff for additions, removals, and changed fingerprints, rerun steady-state `check`, and commit the reviewed update.
