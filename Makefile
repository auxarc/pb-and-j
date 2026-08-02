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

MOD_VER     := $(shell grep '^ver:' mod/metadata.yaml | awk '{print $$2}')
CORE_VER    := $(shell grep -o 'ModVersion = "[^"]*"' src/PBAndJ.Core/Net/PbjProtocol.cs | sed 's/.*"\(.*\)"/\1/')
PKG_DIR     := dist/package

.PHONY: test build dist deploy check-game-hash check-mod-version clean log peer peer-selftest peer-connect peer-listen package

# metadata.yaml is the one place PbjProtocol.ModVersion cannot reach, and a
# disagreement between them is invisible until a peer is refused by a host —
# on someone else's machine, which is where this first went wrong.
check-mod-version:
	@test -n "$(CORE_VER)" || { echo "FATAL: could not read ModVersion from PbjProtocol.cs"; exit 1; }
	@if [ "$(MOD_VER)" != "$(CORE_VER)" ]; then \
		echo "FATAL: mod version mismatch — peers would refuse each other."; \
		echo "  mod/metadata.yaml ver:       $(MOD_VER)"; \
		echo "  PbjProtocol.ModVersion:      $(CORE_VER)"; \
		exit 1; \
	fi
	@echo "mod version OK ($(MOD_VER))"

test:
	$(DBX) bash -lc '$(DOTNET_ENV) cd $(REPO) && dotnet test tests/PBAndJ.Core.Tests \
		/p:CollectCoverage=true /p:Include="[PBAndJ.Core]*" \
		/p:Threshold=100 /p:ThresholdType=line%2cbranch%2cmethod /p:ThresholdStat=total'

build: test
	$(DBX) bash -lc '$(DOTNET_ENV) cd $(REPO) && dotnet build src/PBAndJ.Mod -c Release'

dist: build check-mod-version
	rm -rf dist/$(MOD_ID)
	mkdir -p dist/$(MOD_ID)/Libraries
	cp mod/metadata.yaml dist/$(MOD_ID)/
	cp src/PBAndJ.Mod/bin/Release/net472/PBAndJ.Mod.dll dist/$(MOD_ID)/Libraries/
	cp src/PBAndJ.Mod/bin/Release/net472/PBAndJ.Core.dll dist/$(MOD_ID)/Libraries/
	cp src/PBAndJ.Mod/bin/Release/net472/PBAndJ.Net.dll dist/$(MOD_ID)/Libraries/

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

# The standalone protocol peer. Speaks the same PBAndJ.Core the mod does, so
# a running game can be tested against a real second peer.
peer: test
	$(DBX) bash -lc '$(DOTNET_ENV) cd $(REPO) && dotnet build tools/pbj-peer -c Release'

# Real sockets, no game. Gates deploy so a broken protocol cannot reach the game.
peer-selftest: peer
	$(DBX) bash -lc '$(DOTNET_ENV) cd $(REPO) && dotnet run --project tools/pbj-peer -c Release --no-build -- selftest'

HOST ?= 127.0.0.1
PORT ?= 27600
NAME ?= ally

peer-connect: peer
	$(DBX) bash -lc '$(DOTNET_ENV) cd $(REPO) && dotnet run --project tools/pbj-peer -c Release --no-build -- \
		connect --host $(HOST) --port $(PORT) --name $(NAME) $(PEER_ARGS)'

peer-listen: peer
	$(DBX) bash -lc '$(DOTNET_ENV) cd $(REPO) && dotnet run --project tools/pbj-peer -c Release --no-build -- \
		listen --bind $(HOST) --port $(PORT) --name $(NAME)'

deploy: dist check-game-hash peer-selftest
	mkdir -p "$(MODS_DIR)"
	rm -rf "$(MODS_DIR)/$(MOD_ID)"
	cp -r dist/$(MOD_ID) "$(MODS_DIR)/$(MOD_ID)"
	@echo "deployed to $(MODS_DIR)/$(MOD_ID)"

# What gets sent to someone on another machine. Gated on the same things deploy
# is, because a package that fails the gate is worse than no package: the person
# receiving it cannot tell a protocol bug from their own setup.
package: dist check-game-hash peer-selftest
	rm -rf $(PKG_DIR)
	mkdir -p $(PKG_DIR)
	cd dist && zip -qr ../$(PKG_DIR)/$(MOD_ID)-mod-v$(MOD_VER).zip $(MOD_ID)
	$(DBX) bash -lc '$(DOTNET_ENV) cd $(REPO) && dotnet publish tools/pbj-peer \
		-c Release -r win-x64 --self-contained -p:PublishSingleFile=true \
		-o $(PKG_DIR)/peer-win-x64'
	rm -f $(PKG_DIR)/peer-win-x64/*.pdb
	cd $(PKG_DIR)/peer-win-x64 && zip -qr ../pbj-peer-win-x64-v$(MOD_VER).zip .
	rm -rf $(PKG_DIR)/peer-win-x64
	cp mod/FRIEND-README.md $(PKG_DIR)/README.md
	@echo "--- package ready in $(PKG_DIR) ---"
	@ls -lh $(PKG_DIR)
	@echo
	@echo "Game build the friend must match:"
	@grep 'buildinfo.yaml' GAME_BUILD.md
	@echo "Send the two zips + README.md. Passphrase and address go separately."

log:
	tail -n 100 -f "$(PLAYER_LOG)"

clean:
	rm -rf dist src/PBAndJ.Mod/bin src/PBAndJ.Mod/obj src/PBAndJ.Core/bin src/PBAndJ.Core/obj \
		src/PBAndJ.Net/bin src/PBAndJ.Net/obj tools/pbj-peer/bin tools/pbj-peer/obj \
		tests/PBAndJ.Core.Tests/bin tests/PBAndJ.Core.Tests/obj tests/PBAndJ.Core.Tests/coverage.json
