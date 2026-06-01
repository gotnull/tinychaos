/*
 * entropy_app.c - tinychaos application glue for the NUCLEO-H753ZI.
 *
 * Stage 1 bring-up: 12-bit counter pattern -> tinychaos frame -> USART3
 * (ST-LINK VCP, PD8/PD9) at 115200, blocking transmit. Deliberately the
 * simplest possible path (no ADC, DMA, ring buffer, or BSP) so a clean
 * stream on the host proves clock + protocol + transport before we add
 * complexity. LD1 (PB0) blinks once per packet as a heartbeat.
 *
 * USART3 is brought up by hand here rather than via CubeMX, so it survives
 * regeneration and keeps the .ioc barebones. HAL_UART is enabled through a
 * compile definition in CMakeLists (-DHAL_UART_MODULE_ENABLED).
 */

#include "entropy_app.h"

#include "main.h"               /* CubeMX HAL (stm32h7xx_hal.h etc.) */
#include "entropy_config.h"
#include "entropy_protocol.h"

/* USART3 handle. serial_stream.c (added in a later stage) externs this; for
 * stage 1 we own it and transmit synchronously. */
UART_HandleTypeDef huart3;

static uint32_t s_seq = 0;
static uint8_t  s_pkt[ENTROPY_PACKET_MAX_BYTES];
static uint16_t s_samples[PACKET_SAMPLE_COUNT];

/* LD1 green LED = PB0 on the NUCLEO-H753ZI. */
#define LD1_PORT   GPIOB
#define LD1_PIN    GPIO_PIN_0

static void led_init(void)
{
  __HAL_RCC_GPIOB_CLK_ENABLE();
  GPIO_InitTypeDef g = {0};
  g.Pin   = LD1_PIN;
  g.Mode  = GPIO_MODE_OUTPUT_PP;
  g.Pull  = GPIO_NOPULL;
  g.Speed = GPIO_SPEED_FREQ_LOW;
  HAL_GPIO_Init(LD1_PORT, &g);
}

/* Bring up USART3 on the VCP pins: PD8 = USART3_TX, PD9 = USART3_RX (AF7). */
static void uart3_init(void)
{
  __HAL_RCC_USART3_CLK_ENABLE();
  __HAL_RCC_GPIOD_CLK_ENABLE();

  GPIO_InitTypeDef g = {0};
  g.Pin       = GPIO_PIN_8 | GPIO_PIN_9;
  g.Mode      = GPIO_MODE_AF_PP;
  g.Pull      = GPIO_PULLUP;
  g.Speed     = GPIO_SPEED_FREQ_VERY_HIGH;
  g.Alternate = GPIO_AF7_USART3;
  HAL_GPIO_Init(GPIOD, &g);

  huart3.Instance          = USART3;
  huart3.Init.BaudRate     = 115200;
  huart3.Init.WordLength   = UART_WORDLENGTH_8B;
  huart3.Init.StopBits     = UART_STOPBITS_1;
  huart3.Init.Parity       = UART_PARITY_NONE;
  huart3.Init.Mode         = UART_MODE_TX_RX;
  huart3.Init.HwFlowCtl    = UART_HWCONTROL_NONE;
  huart3.Init.OverSampling = UART_OVERSAMPLING_16;
  huart3.Init.OneBitSampling = UART_ONE_BIT_SAMPLE_DISABLE;
  huart3.Init.ClockPrescaler = UART_PRESCALER_DIV1;
  huart3.AdvancedInit.AdvFeatureInit = UART_ADVFEATURE_NO_INIT;
  if (HAL_UART_Init(&huart3) != HAL_OK) {
    Error_Handler();
  }
}

void entropy_app_init(void)
{
  led_init();
  uart3_init();
}

void entropy_app_task(void)
{
  static uint16_t counter = 0;

  /* 12-bit counter ramp so the host can spot drops/corruption at a glance. */
  for (size_t i = 0; i < PACKET_SAMPLE_COUNT; ++i) {
    s_samples[i] = (uint16_t)(counter++ & 0x0FFFU);
  }

  /* HAL_GetTick() is milliseconds; scale to the protocol's microsecond field.
   * Good enough for bring-up (the host derives rate and tolerates the coarse
   * resolution); a 1 MHz timer replaces this when we add real ADC capture. */
  const uint32_t now_us = HAL_GetTick() * 1000U;

  const size_t n = entropy_packet_encode(s_pkt, sizeof s_pkt,
                                         s_seq++, now_us,
                                         s_samples, PACKET_SAMPLE_COUNT);
  if (n > 0) {
    HAL_UART_Transmit(&huart3, s_pkt, (uint16_t)n, HAL_MAX_DELAY);
  }

  HAL_GPIO_TogglePin(LD1_PORT, LD1_PIN);   /* heartbeat */
  HAL_Delay(13);                            /* ~78 packets/s */
}
