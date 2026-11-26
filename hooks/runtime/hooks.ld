/*
 * Links all hooks into one blob placed right after the stock image. The patcher passes:
 *   OT_BASE                                 address of the first byte after the stock image
 *   fw_session_active, fw_dispatch_exit, fw_dispatch_resume, fw_new, fw_sized_delete, fw_send
 * (Thumb functions are passed with bit 0 set.)
 */
ENTRY(ot_entry)

SECTIONS
{
    . = OT_BASE;
    .text :
    {
        KEEP(*(.text.ot_entry))          /* must be first: the default case branches to OT_BASE */
        *(.text .text.*)
        KEEP(*(.rodata.ot_hooks))
        *(.rodata .rodata.*)
    }
    .data : { *(.data .data.*) }
    .bss  : { *(.bss .bss.* COMMON) }
    /DISCARD/ : { *(.ARM.exidx*) *(.ARM.extab*) *(.comment) *(.note*) }
}

ASSERT(ot_entry == OT_BASE, "ot_entry must be at OT_BASE")
ASSERT(SIZEOF(.data) == 0 && SIZEOF(.bss) == 0, "Hooks cannot have writable globals: nothing initialises RAM for hook code.")
