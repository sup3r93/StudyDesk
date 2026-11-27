# Repository governance

## Long-lived branches

`main` is the stable branch; `dev` is the integration branch. Changes normally enter `dev` through
short-lived branches and then reach `main` through a pull request.

Ruleset definitions under `.github/rulesets/` describe the intended GitHub configuration. Merely
committing JSON files does not activate rulesets: they must be applied through repository settings
or the GitHub API and then inspected.

Both definitions require pull requests, resolved conversations and the **Repository quality**,
**Secret scan** and **Dependency review** checks from GitHub Actions. Force pushes and branch
deletion are blocked. No independent approval is required because the maintainer cannot approve
their own pull requests. Required reviews can be increased if independent maintainers join later.

`main` also requires an up-to-date pull request branch. Merge commits preserve the ancestry of the
long-lived `dev` branch when promoting a release; squash or rebase merges remain available for
focused changes. Update feature branches with the relevant base before merging. Mandatory commit
signatures are deferred until a signing workflow is deliberately configured.

## Automated checks

- Prettier checks the repository's supported text/configuration files.
- Gitleaks scans Git history with redacted output, including a rule for assigned StudyDesk tokens.
- Dependency review examines dependency changes in pull requests for known vulnerabilities.
- Actions are pinned to full commit SHAs; the Gitleaks image is pinned by digest.
- Workflow permissions are read-only; untrusted pull requests receive no project secrets.

Application builds, analyzers, behavioral tests and CodeQL must be enabled as actual source projects
are introduced. Do not advertise them as active before code exists and their runs have been
verified.

## Dependency maintenance

Dependabot monitors the root npm tooling and GitHub Actions weekly and targets `dev` for version
updates. Security updates and alerts also depend on GitHub's repository settings and default-branch
dependency graph. Updates require the normal checks; no automatic merge is configured.

Add NuGet, application npm and container ecosystems when their manifests exist in `desktop/` or
`server/`. Listing nonexistent manifests creates update errors rather than useful protection.

## Secrets and reporting

Runtime tokens and personal data are excluded from source control. GitHub secret scanning and push
protection complement, but do not replace, local checks and review. Arbitrary random tokens may not
match GitHub's built-in detectors; the custom Gitleaks rule provides a focused additional check and
is not a guarantee of complete detection.

Use private vulnerability reporting for security issues. Repository administrators should
periodically verify enabled settings, required-check names, integration IDs and ruleset enforcement.
