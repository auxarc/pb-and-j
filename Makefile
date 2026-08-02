# pb-and-j build pipeline. All dotnet work runs inside the pb-dev distrobox.
# deploy is gated on: tests green + 100% line/branch/method coverage + game build hash match.

MOD_ID      := pb-and-j
REPO        := $(patsubst %/,%,$(dir $(abspath $(lastword $(MAKEFILE_LIST)))))
DBX         := distrobox enter pb-dev --
DOTNET_ENV  := export NUGET_PACKAGES=$(REPO)/.packages;
GAME_ASM    := $(HOME)/.local/share/Steam/steamapps/common/Phantom Brigade/PhantomBrigade_Data/Managed/Assembly-CSharp.dll
MODS_DIR    := $(HOME)/.local/share/Steam/steamapps/compatdata/553540/pfx/drive_c/users/steamuser/AppData/Local/PhantomBrigade/Mods
PINNED_SHA  := $(shell grep -o '`[a-f0-9]\{64\}`' GAME_BUILD.md | tr -d '`')
PLAYER_LOG  := $(HOME)/.local/share/Steam/steamapps/compatdata/553540/pfx/drive_c/users/steamuser/AppData/LocalLow/Brace Yourself Games/Phantom Brigade/Player.log

.PHONY: test build dist deploy check-game-hash clean log

test:
	$(DBX) bash -lc '$(DOTNET_ENV) cd $(REPO) && dotnet test tests/PBAndJ.Core.Tests \
		/p:CollectCoverage=true /p:Threshold=100 /p:ThresholdType=line%2cbranch%2cmethod /p:ThresholdStat=total'

build: test
	$(DBX) bash -lc '$(DOTNET_ENV) cd $(REPO) && dotnet build src/PBAndJ.Mod -c Release'

dist: build
	rm -rf dist/$(MOD_ID)
	mkdir -p dist/$(MOD_ID)/Libraries
	cp mod/metadata.yaml dist/$(MOD_ID)/
	cp src/PBAndJ.Mod/bin/Release/net472/PBAndJ.Mod.dll dist/$(MOD_ID)/Libraries/
	cp src/PBAndJ.Mod/bin/Release/net472/PBAndJ.Core.dll dist/$(MOD_ID)/Libraries/

check-game-hash:
	@test -n "$(PINNED_SHA)" || { echo "FATAL: no pinned SHA found in GAME_BUILD.md"; exit 1; }
	@actual=$$(sha256sum "$(GAME_ASM)" | cut -d' ' -f1); \
	if [ "$$actual" != "$(PINNED_SHA)" ]; then \
		echo "FATAL: game Assembly-CSharp.dll hash mismatch — game updated? Re-vendor and re-verify (see GAME_BUILD.md)."; \
		echo "  pinned:  $(PINNED_SHA)"; \
		echo "  actual:  $$actual"; \
		exit 1; \
	fi; \
	echo "game build hash OK"

deploy: dist check-game-hash
	mkdir -p "$(MODS_DIR)"
	rm -rf "$(MODS_DIR)/$(MOD_ID)"
	cp -r dist/$(MOD_ID) "$(MODS_DIR)/$(MOD_ID)"
	@echo "deployed to $(MODS_DIR)/$(MOD_ID)"

log:
	tail -n 100 -f "$(PLAYER_LOG)"

clean:
	rm -rf dist src/PBAndJ.Mod/bin src/PBAndJ.Mod/obj src/PBAndJ.Core/bin src/PBAndJ.Core/obj \
		tests/PBAndJ.Core.Tests/bin tests/PBAndJ.Core.Tests/obj tests/PBAndJ.Core.Tests/coverage.json
