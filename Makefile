.PHONY: init infra migrate api worker web test build e2e compose docker-test railpack edge keycloak trust-local-sso
init:
	bash scripts/init-local.sh
infra:
	bash scripts/start-infra.sh
migrate:
	dotnet tool restore
	bash scripts/with-env.sh dotnet ef database update --project src/ForgeDock.Infrastructure --startup-project src/ForgeDock.Api
api:
	bash scripts/with-env.sh dotnet run --project src/ForgeDock.Api --no-launch-profile
worker:
	bash scripts/with-env.sh dotnet run --project src/ForgeDock.Worker
web:
	npm --prefix web run dev
build:
	dotnet build ForgeDock.sln
	npm --prefix web ci
	npm --prefix web run build
test:
	dotnet test ForgeDock.sln
	npm --prefix web run check

e2e:
	bash scripts/with-env.sh npm --prefix web run test:e2e

compose:
	bash scripts/install-compose.sh

docker-test:
	FORGEDOCK_DOCKER_TESTS=1 dotnet test tests/ForgeDock.Tests

railpack:
	bash scripts/install-railpack.sh

edge:
	bash scripts/start-edge.sh

keycloak:
	bash scripts/start-keycloak-local.sh

trust-local-sso:
	bash scripts/trust-keycloak-local.sh
