/*
 * Shows a full-screen image stored in the data flash once the amp has started up.
 *   opentone set asset 120 assets/hello-red.rle -d mustang-lt25     (once; survives firmware updates)
 *   opentone patch stock.bin out.bin --hook tick=hooks/boot-asset.c
 *
 * Like boot-image.c, but the image lives in data-flash slots (about 1.1 MB available) instead of the
 * firmware, so it can be changed without reflashing. Either an OpenTone .rle (hooks/tools/bmp2rle.py) or
 * a 24-bit BMP works. Nothing is drawn if the slot holds no valid asset.
 */
#include "opentone.h"

#ifndef BOOT_IMAGE_DELAY_MS
#define BOOT_IMAGE_DELAY_MS 6000
#endif
#ifndef BOOT_IMAGE_SLOT
#define BOOT_IMAGE_SLOT 120
#endif

static bool shown;

OT_TICK(mgr)
{
    if (shown || ot_millis() < BOOT_IMAGE_DELAY_MS) return;
    shown = true;

    size_t len;
    uint8_t *image = ot_asset_load(mgr, BOOT_IMAGE_SLOT, &len);
    if (!image) return;
    if (!ot_lcd_rle(image, len, 0, 0)) ot_lcd_bmp(image, len, 0, 0);
    ot_free(image);
}
