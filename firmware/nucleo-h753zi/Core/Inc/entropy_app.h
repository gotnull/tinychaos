/*
 * entropy_app.h - tinychaos application glue for the NUCLEO-H753ZI.
 *
 * Self-contained integration that sits on top of the CubeMX-generated HAL.
 * Keeps all our logic out of the generated main.c (which only gets two hook
 * calls), so a CubeMX regeneration never clobbers it.
 *
 * Bring-up stage 1: streams a 12-bit counter pattern over USART3 (the
 * ST-LINK Virtual COM Port, PD8/PD9) at 115200 baud, framed with the
 * tinychaos wire protocol. No ADC, no DMA, no BSP - the simplest thing that
 * proves clock + protocol + host decode end to end.
 */

#ifndef TINYCHAOS_ENTROPY_APP_H
#define TINYCHAOS_ENTROPY_APP_H

#ifdef __cplusplus
extern "C" {
#endif

/* Call once from main(), in USER CODE BEGIN 2 (after MX_GPIO_Init). Brings
 * up USART3 on the VCP pins and the LD1 heartbeat. */
void entropy_app_init(void);

/* Call repeatedly from the main while(1) loop. Emits one framed counter
 * packet and paces itself. */
void entropy_app_task(void);

#ifdef __cplusplus
}
#endif

#endif /* TINYCHAOS_ENTROPY_APP_H */
