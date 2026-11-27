/*
 * Links all hooks into one blob placed right after the stock image. The patcher passes:
 *   OT_BASE                                 address of the first byte after the stock image
 *   fw_*                                    firmware functions found in the image or in runtime/*.sym
 * (Thumb functions are passed with bit 0 set.)
 *
 * Hook RAM: DTCM 0x20018000-0x2001f000 (28 KB). The firmware's .bss ends at 0x2000ece0 and nothing in
 * the image addresses DTCM above it (no literal or initialised pointer refers to it; on hardware it held
 * unchanged power-on contents). The top of DTCM held the bootloader's stack, which is dead once the app
 * runs, and is left out. ot_boot_entry (runtime.c) copies .data and zeroes .bss before the main loop.
 */
ENTRY(ot_entry)

MEMORY
{
    FLASH (rx)  : ORIGIN = OT_BASE,    LENGTH = 0x08200000 - OT_BASE
    RAM   (rw)  : ORIGIN = 0x20018000, LENGTH = 0x7000
}

SECTIONS
{
    .text :
    {
        KEEP(*(.text.ot_entry))          /* must be first: the default case branches to OT_BASE */
        KEEP(*(.text.ot_boot_entry))     /* called from the patched main loop only, so nothing here refers to them */
        KEEP(*(.text.ot_tick_entry))
        *(.text .text.*)
        KEEP(*(.rodata.ot_hooks))
        *(.rodata .rodata.*)
        . = ALIGN(4);
    } > FLASH

    .data : ALIGN(4)
    {
        ot_data_start = .;
        *(.data .data.*)
        . = ALIGN(4);
        ot_data_end = .;
    } > RAM AT > FLASH
    ot_data_load = LOADADDR(.data);

    .bss (NOLOAD) : ALIGN(4)
    {
        ot_bss_start = .;
        *(.bss .bss.* COMMON)
        . = ALIGN(4);
        ot_bss_end = .;
    } > RAM

    /DISCARD/ : { *(.ARM.exidx*) *(.ARM.extab*) *(.comment) *(.note*) }
}

ASSERT(ot_entry == OT_BASE, "ot_entry must be at OT_BASE")
