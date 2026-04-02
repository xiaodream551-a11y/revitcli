# Contributing to RevitCli

Thank you for considering contributing to RevitCli! This document provides guidelines and instructions for contributing.

## Development Setup

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Git

### Getting Started

```bash
git clone https://github.com/xiaodream551-a11y/revitcli.git
cd revitcli
dotnet build
dotnet test
```

### Project Structure

```
src/RevitCli/              # CLI console app (.NET 8)
src/RevitCli.Addin/        # Revit add-in (EmbedIO HTTP server)
shared/RevitCli.Shared/    # Shared DTOs (netstandard2.0)
tests/RevitCli.Tests/      # CLI unit tests
tests/RevitCli.Addin.Tests/ # Add-in + integration tests
```

## How to Contribute

### Reporting Bugs

1. Check if the issue already exists in [Issues](https://github.com/xiaodream551-a11y/revitcli/issues)
2. If not, create a new issue with:
   - Steps to reproduce
   - Expected vs actual behavior
   - Revit version (if applicable)
   - `revitcli doctor` output

### Suggesting Features

Open an issue with the `enhancement` label. Describe:
- The problem you're solving
- Your proposed solution
- Alternative approaches considered

### Submitting Changes

1. Fork the repository
2. Create a feature branch: `git checkout -b feat/my-feature`
3. Make your changes
4. Add/update tests
5. Run the full test suite: `dotnet test`
6. Commit with a descriptive message (see Commit Convention below)
7. Push and open a Pull Request

### Commit Convention

We follow [Conventional Commits](https://www.conventionalcommits.org/):

- `feat:` — New feature
- `fix:` — Bug fix
- `test:` — Adding or updating tests
- `docs:` — Documentation changes
- `ci:` — CI/CD changes
- `chore:` — Maintenance tasks

Examples:
```
feat: add parameter search command
fix: handle null document path in status
test: add integration tests for audit endpoint
```

## Code Guidelines

### Architecture

- **CLI commands** go in `src/RevitCli/Commands/`
- **API handlers** go in `src/RevitCli.Addin/Handlers/`
- **Shared DTOs** go in `shared/RevitCli.Shared/`
- Each command has two code paths:
  - `Create()` handler — interactive terminal with Spectre.Console
  - `ExecuteAsync()` — plain text via `TextWriter` (testable, pipe-friendly)

### Testing

- Write tests for `ExecuteAsync()` using `FakeHttpHandler` and `StringWriter`
- Integration tests go in `tests/RevitCli.Addin.Tests/Integration/`
- Run tests before submitting: `dotnet test`

### Adding a New Command

1. Create the command in `src/RevitCli/Commands/MyCommand.cs`
2. Add shared DTOs if needed in `shared/RevitCli.Shared/`
3. Add API handler in `src/RevitCli.Addin/Handlers/`
4. Register the handler in `ApiServer.cs`
5. Register the command in `Program.cs`
6. Add tests in `tests/RevitCli.Tests/Commands/`
7. Update shell completions in `CompletionsCommand.cs`

## License

By contributing, you agree that your contributions will be licensed under the MIT License.
