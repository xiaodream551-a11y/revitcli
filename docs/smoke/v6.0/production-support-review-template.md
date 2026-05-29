# RevitCli v6.0 Production Support Review Summary

Use this public-safe summary only after private support review is complete. Do
not include client names, model paths, ticket contents, or private notes.
Blank fields are not claim-ready; fill each field after private approval before
running `release pilot claim --production-support`.

- Production support review:
- private support review approved:
- office rollout completion:
- production support claim:

## Boundary Summary

- Public-safe evidence only.
- The private support review remains outside the repository.
- `release pilot support-review validate --path docs/smoke/v6.0/<support-review>.md --output json` must pass before any production support claim dry-run.
- `release pilot claim --production-support --support-review docs/smoke/v6.0/<support-review>.md --output json` must dry-run before any `--yes` write.
