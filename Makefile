.PHONY: proto
proto:
	@echo "Regenerating protobuf code from client.proto..."
	protoc --csharp_out=src/Centrifugal.Centrifuge/Protocol --proto_path=. client.proto
	@mv src/Centrifugal.Centrifuge/Protocol/Client.cs src/Centrifugal.Centrifuge/Protocol/Client.g.cs
	@echo "Protobuf code regenerated successfully at src/Centrifugal.Centrifuge/Protocol/Client.g.cs"

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

.PHONY: run-example
run-example:
	@echo "Running console example program..."
	dotnet run --project examples/Centrifugal.Centrifuge.ConsoleExample/Centrifugal.Centrifuge.ConsoleExample.csproj

.PHONY: run-blazor-example
run-blazor-example:
	@echo "Running Blazor WebAssembly example..."
	@echo "Opening browser at http://localhost:5000"
	@echo "Events will be displayed on screen with color-coded categories"
	cd examples/Centrifugal.Centrifuge.BlazorExample && \
		dotnet run --urls "http://localhost:5000" --launch-profile "http"

.PHONY: help
help:
	@echo "Available targets:"
	@echo "  build              - Build the solution in Release mode"
	@echo "  test               - Run all tests (unit + integration)"
	@echo "  test-unit          - Run unit tests only (excludes integration tests)"
	@echo "  clean              - Clean build artifacts"
	@echo "  restore            - Restore NuGet packages"
	@echo "  proto              - Regenerate C# protobuf code from client.proto"
	@echo "  run-example        - Run the console example program"
	@echo "  run-blazor-example - Run the Blazor WebAssembly example (port 5000)"
	@echo "  help               - Show this help message"
