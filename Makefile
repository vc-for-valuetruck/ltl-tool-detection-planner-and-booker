.PHONY: up down build logs reset api-test web-build

up:
	docker compose up -d

build:
	docker compose up -d --build

down:
	docker compose down

logs:
	docker compose logs -f

reset:
	docker compose down -v
	docker compose up -d --build

api-test:
	dotnet test

web-build:
	cd web && npm install && npm run build
