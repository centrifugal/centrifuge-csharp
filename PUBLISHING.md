# Publishing Centrifuge C# SDK to NuGet

This guide explains how to publish the Centrifuge C# SDK to NuGet.org.

## Prerequisites

1. **NuGet.org Account**
   - Create an account at [nuget.org](https://www.nuget.org/)
   - Verify your email address

2. **API Key**
   - Go to your [NuGet API Keys page](https://www.nuget.org/account/apikeys)
   - Click "Create"
   - Give it a name (e.g., "centrifuge-csharp-publish")
   - Select "Push" scope
   - Select "Push new packages and package versions"
   - For package glob pattern, enter: `Centrifuge.Client*`
   - Click "Create"
   - **IMPORTANT**: Copy the API key immediately - you won't see it again!

3. **GitHub Repository Setup**
   - Create a new repository: `centrifugal/centrifuge-csharp`
   - Add your NuGet API key as a GitHub secret:
     - Go to repository Settings → Secrets and variables → Actions
     - Click "New repository secret"
     - Name: `NUGET_API_KEY`
     - Value: (paste your NuGet API key)
     - Click "Add secret"

## Initial Setup

### 1. Initialize Git Repository

```bash
cd centrifuge-csharp

# Initialize git
git init

# Add files
git add .

# Commit
git commit -m "Initial commit: Centrifuge C# SDK v1.0.0"

# Add remote (replace with your repository URL)
git remote add origin https://github.com/centrifugal/centrifuge-csharp.git

# Push to GitHub
git branch -M main
git push -u origin main
```

## Publishing Workflow

### Automatic Publishing (Recommended)

Update version in Centrifuge/Centrifuge.csproj before tagging, push.

The repository is configured with GitHub Actions for automatic publishing:

```bash
# 2. Create and push a version tag
git tag v1.0.0
git push origin v1.0.0

# GitHub Actions will automatically:
# - Build the project
# - Run tests
# - Pack the NuGet package
# - Publish to NuGet.org
# - Create a GitHub release
```

### Manual Publishing

If you prefer to publish manually:

Update version in Centrifuge/Centrifuge.csproj before tagging, push.

```bash
# 1. Clean and restore
dotnet clean
dotnet restore

# 2. Build in Release mode
dotnet build --configuration Release

# 3. Run tests
dotnet test --configuration Release

# 4. Pack the NuGet package
dotnet pack src/Centrifuge/Centrifuge.csproj --configuration Release --output ./artifacts

# 5. Publish to NuGet.org
dotnet nuget push ./artifacts/Centrifuge.Client.*.nupkg \
    --api-key YOUR_API_KEY \
    --source https://api.nuget.org/v3/index.json
```

## Version Management

### Updating the Version

Edit `src/Centrifuge/Centrifuge.csproj`:

```xml
<PropertyGroup>
    <Version>1.0.1</Version>
</PropertyGroup>
```

## Release Checklist

Before each release:

- [ ] All tests pass
- [ ] Version updated in `.csproj`
- [ ] CHANGELOG.md updated
- [ ] README.md reviewed
- [ ] Examples tested
- [ ] Documentation updated
- [ ] No debug code left

## Publishing Pre-release Versions

For beta or RC versions:

```xml
<PropertyGroup>
    <Version>1.1.0-beta.1</Version>
</PropertyGroup>
```

Then tag and push:
```bash
git tag v1.1.0-beta.1
git push origin v1.1.0-beta.1
```

Pre-release versions won't be installed by default unless explicitly requested:
```bash
dotnet add package Centrifuge.Client --version 1.1.0-beta.1
```

## Troubleshooting

### Package Already Exists

If you get "package already exists" error:
- Increment the version number
- You cannot republish the same version

### Authentication Failed

If authentication fails:
- Check your API key is correct
- Ensure API key has "Push" permissions
- Verify package glob pattern matches "Centrifuge.Client*"

### Package Not Appearing

After publishing:
- It may take 10-15 minutes to index
- Check [nuget.org/packages/Centrifuge.Client](https://www.nuget.org/packages/Centrifuge.Client)
- Verify no errors in GitHub Actions logs

## Package Metadata

The package metadata is defined in `src/Centrifuge/Centrifuge.csproj`:

```xml
<PropertyGroup>
    <PackageId>Centrifuge.Client</PackageId>
    <Version>1.0.0</Version>
    <Authors>Centrifugal</Authors>
    <Company>Centrifugal</Company>
    <Description>C# client SDK for Centrifugo and Centrifuge</Description>
    <PackageProjectUrl>https://github.com/centrifugal/centrifuge-csharp</PackageProjectUrl>
    <RepositoryUrl>https://github.com/centrifugal/centrifuge-csharp</RepositoryUrl>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <PackageTags>centrifugo;centrifuge;websocket;real-time;messaging</PackageTags>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    <PackageIcon>icon.png</PackageIcon>
</PropertyGroup>
```

## Monitoring

After publishing:

1. **NuGet.org Stats**
   - View download stats at [nuget.org/packages/Centrifuge.Client](https://www.nuget.org/packages/Centrifuge.Client)
   - Monitor for support requests

2. **GitHub**
   - Watch for issues
   - Review pull requests
   - Monitor discussions

3. **CI/CD**
   - Check GitHub Actions for build status
   - Review any failed builds

## Support

For questions about publishing:
- NuGet Documentation: https://docs.microsoft.com/en-us/nuget/
- GitHub Actions: https://docs.github.com/en/actions

For Centrifugo-specific questions:
- Centrifugal Docs: https://centrifugal.dev/
- GitHub Issues: https://github.com/centrifugal/centrifuge-csharp/issues
