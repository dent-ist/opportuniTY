# Thin wrapper around ./opportunity.sh (which passes ../../versions.env and .env to docker compose).
# IMAGES=ghcr uses the published images (needs OPPORTUNITY_IMAGE_TAG); default builds them locally.
IMAGES ?= local
SH := ./opportunity.sh --images $(IMAGES)

.PHONY: help init preflight up down reset logs ps seed config
help:
	@$(SH) help
init preflight up down ps seed:
	@$(SH) $@
reset:
	@$(SH) reset $(if $(YES),-y)
logs:
	@$(SH) logs $(SERVICE)
config:
	@$(SH) compose config
