/*
 * Shows a full-screen image once the amp has started up.
 *   opentone patch stock.bin out.bin --hook tick=hooks/boot-image.c
 *
 * The startup splash and the first main screen are drawn after the main loop starts, so this waits
 * BOOT_IMAGE_DELAY_MS from power-on before drawing (once). The image stays until the firmware next
 * redraws the screen: a knob, a button or a host connecting.
 *
 * The image is ../assets/hello-red.rle, made from the BMP with:
 *     python hooks/tools/bmp2rle.py assets/hello-red.bmp assets/hello-red.rle
 * (5.7 KB instead of 61 KB.) A BMP can be embedded directly and drawn with ot_lcd_bmp() instead.
 */
#include "opentone.h"

#ifndef BOOT_IMAGE_DELAY_MS
#define BOOT_IMAGE_DELAY_MS 6000
#endif

OT_INCBIN(boot_image, "../assets/hello-red.rle");

static bool shown;

OT_TICK(mgr)
{
    (void)mgr;
    if (shown || ot_millis() < BOOT_IMAGE_DELAY_MS) return;
    shown = true;
    ot_lcd_rle(boot_image, (size_t)(boot_image_end - boot_image), 0, 0);
}
