help:
	@egrep "^#" Makefile

# target: up                        - Start rabbit in docker container
up:
	docker compose up -d

# target: up                        - Start rabbit in docker container
build:
	docker compose up -d --build

ut: up-test
up-test:
	docker compose -f docker-compose.testcontainer.yml up -d