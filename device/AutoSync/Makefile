# Device app that starts HotSync at scheduled times.
# PowerShell: wsl -d palmdev -- make -C /mnt/c/Palm/PalmSync/device/AutoSync
# Tests: wsl -d palmdev -- make -C /mnt/c/Palm/PalmSync/device/AutoSync test
# Output: build/PiloAutoSync.prc; creator: PSas.
# LAUNCHER_LANGUAGE=en selects English; START_METHOD=appswitch uses app switching.
SHELL := /bin/sh
APP_NAME := PiloAutoSync
DB_NAME := PiloAutoSync
CREATOR_ID := PSas
BUILD_DIR := build
LAUNCHER_LANGUAGE ?= ja
START_METHOD ?= key
ifeq ($(LAUNCHER_LANGUAGE),ja)
PILRC_DEFINES := -D JAPANESE_LAUNCHER
APP_LANGUAGE_DEFINE := -DAPP_LANGUAGE_JAPANESE=1
else ifeq ($(LAUNCHER_LANGUAGE),en)
PILRC_DEFINES :=
APP_LANGUAGE_DEFINE := -DAPP_LANGUAGE_JAPANESE=0
else
$(error LAUNCHER_LANGUAGE must be ja or en)
endif
ifeq ($(START_METHOD),appswitch)
START_DEFINE := -DSTART_APPSWITCH
else ifneq ($(START_METHOD),key)
$(error START_METHOD must be key or appswitch)
endif
PALM_CC ?= m68k-palmos-gcc
PILRC ?= $(BUILD_DIR)/tools/pilrc
BUILD_PRC ?= build-prc
HOST_CC ?= cc
PYTHON ?= python3
PALM_FLAGS := -palmos3.1
CPPFLAGS := -DPALMOS -DAPP_CREATOR="'$(CREATOR_ID)'" $(APP_LANGUAGE_DEFINE) $(START_DEFINE) -Isrc -Ires
CFLAGS := $(PALM_FLAGS) -O2 -Wall -W -Wstrict-prototypes -Wmissing-prototypes
LDFLAGS := $(PALM_FLAGS)
HOST_CFLAGS := -std=c90 -pedantic -O2 -Wall -Wextra -Isrc
PALM_OBJECTS := $(BUILD_DIR)/Schedule.o $(BUILD_DIR)/PiloAutoSync.o
PALM_EXECUTABLE := $(BUILD_DIR)/$(APP_NAME)
PRC := $(BUILD_DIR)/$(APP_NAME).prc
ICONS := $(foreach size,large small,$(foreach depth,bw gray color,res/icons/autosync_$(size)_$(depth).bmp))
.PHONY: all assets test verify-prc clean FORCE
all: $(PRC)
assets: $(BUILD_DIR)/icons.stamp
$(BUILD_DIR):
	mkdir -p $@
FORCE:
$(BUILD_DIR)/build-config: FORCE | $(BUILD_DIR)
	@printf '%s\n' '$(CPPFLAGS) $(CFLAGS) $(LDFLAGS) $(PILRC_DEFINES) $(DB_NAME)' > $@.tmp
	@if test -r $@ && cmp -s $@.tmp $@; then rm -f $@.tmp; else mv -f $@.tmp $@; fi
$(BUILD_DIR)/icons.stamp: scripts/generate_icon.py | $(BUILD_DIR)
	$(PYTHON) scripts/generate_icon.py --output-dir res/icons
	touch $@
$(ICONS): $(BUILD_DIR)/icons.stamp
	@test -f $@ || { $(PYTHON) scripts/generate_icon.py --output-dir res/icons; }
$(PILRC): scripts/bootstrap-pilrc.sh scripts/pilrc-3.2-lp64.patch | $(BUILD_DIR)
	sh scripts/bootstrap-pilrc.sh $@
	touch $@
$(BUILD_DIR)/%.o: src/%.c $(BUILD_DIR)/build-config | $(BUILD_DIR)
	$(PALM_CC) $(CPPFLAGS) $(CFLAGS) -c $< -o $@
$(BUILD_DIR)/Schedule.o: src/Schedule.h
$(BUILD_DIR)/PiloAutoSync.o: src/Schedule.h res/PiloAutoSyncRsc.h
$(PALM_EXECUTABLE): $(PALM_OBJECTS)
	$(PALM_CC) $(LDFLAGS) $^ -o $@
$(BUILD_DIR)/resources.stamp: res/PiloAutoSync.rcp res/PiloAutoSyncRsc.h $(ICONS) $(PILRC) $(BUILD_DIR)/build-config
	cd $(BUILD_DIR) && rm -f *.bin && $(abspath $(PILRC)) -q $(PILRC_DEFINES) -I $(CURDIR)/res $(CURDIR)/res/PiloAutoSync.rcp .
	touch $@
$(PRC): $(PALM_EXECUTABLE) $(BUILD_DIR)/resources.stamp $(BUILD_DIR)/build-config
	$(BUILD_PRC) -o $@ -c '$(CREATOR_ID)' -n '$(DB_NAME)' --backup $(PALM_EXECUTABLE) $(BUILD_DIR)/*.bin
$(BUILD_DIR)/test_schedule: src/Schedule.c src/Schedule.h tests/test_schedule.c | $(BUILD_DIR)
	$(HOST_CC) $(HOST_CFLAGS) src/Schedule.c tests/test_schedule.c -o $@
test: $(BUILD_DIR)/test_schedule
	$(BUILD_DIR)/test_schedule
verify-prc: $(PRC)
	$(PYTHON) scripts/verify_prc.py $(PRC)
clean:
	rm -rf $(BUILD_DIR)
