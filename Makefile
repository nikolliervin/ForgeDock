.PHONY: init infra migrate api worker web test build
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
