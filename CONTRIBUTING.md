# Contributing to Centrifuge C# SDK

Thank you for your interest in contributing to the Centrifuge C# SDK!

## Development Setup

### Prerequisites

- .NET 8.0 SDK or later
- Git
- A code editor (Visual Studio, VS Code, or Rider recommended)

### Getting Started

1. Fork the repository
2. Clone your fork:
   ```bash
   git clone https://github.com/YOUR_USERNAME/centrifuge-csharp.git
   cd centrifuge-csharp
   ```
3. Create a branch for your changes:
   ```bash
   git checkout -b feature/your-feature-name
   ```

### Building

```bash
# Restore dependencies
dotnet restore

# Build the project
dotnet build

# Run tests
dotnet test

# Run a specific test
dotnet test --filter "FullyQualifiedName~ClientTests"
```

## Code Style

- Follow standard C# coding conventions
- Use meaningful variable and method names
- Add XML documentation comments for public APIs
- Keep methods focused and single-purpose
- Use async/await for asynchronous operations
- Ensure code is thread-safe where applicable

### Example Code Style

```csharp
/// <summary>
/// Connects to the Centrifugo server asynchronously.
/// </summary>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>A task representing the asynchronous operation.</returns>
/// <exception cref="CentrifugeException">Thrown when connection fails.</exception>
public async Task ConnectAsync(CancellationToken cancellationToken = default)
{
    await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
        // Implementation
    }
    finally
    {
        _stateLock.Release();
    }
}
```

## Testing

### Running Tests

```bash
# Run all tests
dotnet test

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"

# Run integration tests (requires Centrifugo server)
dotnet test --filter "Category=Integration"
```

### Writing Tests

- Write unit tests for all new features
- Use xUnit for test framework
- Follow AAA pattern (Arrange, Act, Assert)
- Name tests descriptively: `MethodName_Scenario_ExpectedBehavior`

Example:

```csharp
[Fact]
public async Task Connect_WithValidEndpoint_ChangesStateToConnecting()
{
    // Arrange
    var client = new CentrifugeClient("ws://localhost:8000/connection/websocket");
    var stateChanged = false;

    client.StateChanged += (sender, args) =>
    {
        if (args.NewState == ClientState.Connecting)
        {
            stateChanged = true;
        }
    };

    // Act
    _ = client.ConnectAsync();
    await Task.Delay(100);

    // Assert
    Assert.True(stateChanged);
}
```

## Pull Request Process

1. Update documentation if needed
2. Add tests for new functionality
3. Ensure all tests pass
4. Update CHANGELOG.md with your changes
5. Create a pull request with a clear description

### PR Description Template

```markdown
## Description
Brief description of the changes

## Type of Change
- [ ] Bug fix
- [ ] New feature
- [ ] Breaking change
- [ ] Documentation update

## Testing
Description of testing performed

## Checklist
- [ ] Code follows project style guidelines
- [ ] Self-review completed
- [ ] Comments added for complex code
- [ ] Documentation updated
- [ ] Tests added/updated
- [ ] All tests pass
- [ ] No new warnings introduced
```

## Reporting Issues

### Bug Reports

When filing a bug report, please include:

- Clear title and description
- Steps to reproduce
- Expected vs actual behavior
- Code sample if applicable
- Environment details (.NET version, OS, etc.)

### Feature Requests

For feature requests, please include:

- Clear description of the feature
- Use cases and motivation
- Example API if applicable
- Willingness to contribute

## Code of Conduct

- Be respectful and inclusive
- Welcome newcomers
- Focus on what is best for the community
- Show empathy towards other community members

## License

By contributing, you agree that your contributions will be licensed under the MIT License.

## Questions?

- Open an issue for general questions
- Check existing issues and documentation first
- Join the [Centrifugal community](https://centrifugal.dev/docs/getting-started/community)

Thank you for contributing! 🎉
