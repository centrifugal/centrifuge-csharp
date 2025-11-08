.PHONY: proto
proto:
	@echo "Regenerating protobuf code from client.proto..."
	protoc --csharp_out=src/Centrifugal.Centrifuge --proto_path=. client.proto
	@echo "Protobuf code regenerated successfully at src/Centrifugal.Centrifuge/Client.g.cs"

.PHONY: build
build:
	@echo "Building solution..."
	dotnet build Centrifugal.Centrifuge.sln --configuration Release

.PHONY: test
test:
	@echo "Running all tests (unit + integration)..."
	dotnet test Centrifugal.Centrifuge.sln --configuration Release --logger "console;verbosity=normal"

.PHONY: test-unit
test-unit:
	@echo "Running unit tests only..."
	dotnet test tests/Centrifugal.Centrifuge.Tests/Centrifugal.Centrifuge.Tests.csproj --configuration Release --filter "FullyQualifiedName!~IntegrationTests" --logger "console;verbosity=normal"

.PHONY: clean
clean:
	@echo "Cleaning build artifacts..."
	dotnet clean Centrifugal.Centrifuge.sln

.PHONY: restore
restore:
	@echo "Restoring NuGet packages..."
	dotnet restore Centrifugal.Centrifuge.sln

.PHONY: help
help:
	@echo "Available targets:"
	@echo "  build      - Build the solution in Release mode"
	@echo "  test       - Run all tests (unit + integration)"
	@echo "  test-unit  - Run unit tests only (excludes integration tests)"
	@echo "  clean      - Clean build artifacts"
	@echo "  restore    - Restore NuGet packages"
	@echo "  proto      - Regenerate C# protobuf code from client.proto"
	@echo "  help       - Show this help message"
