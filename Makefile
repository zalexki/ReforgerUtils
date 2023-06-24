help:
	@egrep "^#" Makefile

# target: up                        - Start rabbit in docker container
up:
	docker compose up -d

# target: up                        - Start rabbit in docker container
build:
	docker compose up -d --build
