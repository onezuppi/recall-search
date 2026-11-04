# Contributing

Thanks for your interest. The project is small and the rules are simple.

## Bugs and ideas

Open an issue from a template. For a bug, attach the version, the Windows version and the last lines of `%LOCALAPPDATA%\RecallSearch\recall.log` without personal paths.

## Code changes

1. Fork the repository and make a branch from `main`.
2. Build and run the tests with `dotnet test`. Instructions: [docs/development.md](docs/development.md).
3. Write a test with the change. Tests live in `tests/`.
4. Compiler warnings are errors (`TreatWarningsAsErrors`). The build must pass without them.
5. If the behavior changes, update the [user guide](docs/guide.md) and [CHANGELOG.md](CHANGELOG.md).
6. Open a pull request from the template. One PR makes one change.

## Project principles

- Everything is local. New network requests and telemetry are not accepted, except model downloads that the user selects.
- Secrets are never read or indexed. No setting may weaken this rule.
- The agent stays out of the way: low priority, and a pause during games and presentations.

By contributing, you agree that your work is distributed under the [MIT](LICENSE) license.
