help:
	@egrep "^#" Makefile

# target: up                        - Start Arma HungDetector docker container
up:
	docker compose up -d

# target: build                     - Build Arma HungDetector docker container
build:
	docker compose up -d --build

ut: up-test
up-test:
	docker compose -f docker-compose.testcontainer.yml up -d