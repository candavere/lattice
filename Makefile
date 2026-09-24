# One-command targets for the Lattice repo (macOS/Linux).
# `make setup` is equivalent to ./setup.sh.

.PHONY: setup test bench replay site

setup: ## SDK check, restore, build, test, reproduce one committed claim
	./setup.sh

test: ## Full test suite in Release
	dotnet test Lattice.sln -c Release

bench: ## Five-case benchmark matrix
	dotnet run -c Release --project Cli -- benchmark

replay: ## Verify the golden trajectory reproduces per-step serialized results
	dotnet run -c Release --project Cli -- replay Tests/fixtures/golden_trajectory.jsonl --verify

site: ## Serve the committed replay viewer locally (override port with PORT=...)
	cd site && python3 -m http.server $${PORT:-8124}