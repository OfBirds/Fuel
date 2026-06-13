# Contributing to Fuel

Thanks for considering a contribution! This is a small, single-maintainer
project, so the process is lightweight — but a few rules are non-negotiable.

## Ground rules

1. **Every change is reviewed by a human.** AI-assisted contributions are
   welcome (a good share of this codebase was written with AI assistants —
   see the README), but nothing merges on AI say-so alone. The maintainer
   reads and understands every diff before it lands.
2. **Tests come with the code.** New behavior needs new tests; bug fixes need
   a test that would have caught the bug. Backend tests live in
   `backend/Api.Tests` (xUnit), frontend tests next to the code they cover
   (Vitest + Testing Library). See [docs/testing.md](docs/testing.md).
3. **The pipeline must be green.** CI runs both test suites on every push and
   PR; a red pipeline means no merge, no exceptions.
4. **Be honest about data.** This app handles sensitive personal data. Changes
   that send user data to third parties without consent will not be
   accepted.

## Workflow

1. Open an issue first for anything bigger than a typo fix — it saves you
   from building something that won't be merged.
2. Fork, branch from `main`, and keep changes focused; one concern per PR.
3. Run the tests locally before pushing:

   ```bash
   dotnet test backend/Fuel.slnx -c Release
   npm test --prefix frontend -- --run
   ```

4. Open a PR against `main` with a short description of what and why.
   `release` is deploy-only — PRs against it are closed.

## Code style

Match the surrounding code. Notable conventions: TypeScript strict mode, no
external state-management or chart libraries on the frontend, EF Core
migrations for any schema change, and structured logging via Serilog.

## Licensing of contributions

The project is licensed under AGPL-3.0. By submitting a contribution you agree
that it is licensed under the same terms.

## Code of conduct

Be kind. The full text lives in [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
