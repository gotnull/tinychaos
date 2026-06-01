/*
 * entropy_app.c - tinychaos application glue for the NUCLEO-H753ZI.
 *
 * Real ADC capture, non-blocking DMA transport:
 *   ADC1 samples PA3 (ADC1_INP15, Arduino A0) - PACKET_SAMPLE_COUNT raw 12-bit
 *   conversions per packet - framed with the tinychaos wire protocol - sent
 *   over USART3 (ST-LINK VCP, PD8/PD9) at 921600 via a DMA TX ring
 *   (serial_stream.c) so the CPU keeps sampling instead of blocking on the
 *   UART.
 *
 * H7 memory note: DMA1/DMA2 cannot reach DTCM, where the linker defaults
 * .bss/.data. The linker script for this project therefore places the RAM
 * sections in AXI SRAM (0x24000000, D1, reachable by the DMA bus matrix)
 * while keeping the stack in DTCM (the reset SP must be valid immediately).
 * The serial_stream ring buffer lives in .bss -> AXI SRAM, so the TX DMA can
 * read it. D-cache is off, so no cache maintenance is needed.
 *
 * USART3 + ADC1 are brought up by hand here (not via CubeMX) so they survive
 * regeneration and keep the .ioc barebones; HAL UART + ADC modules are
 * enabled via compile definitions in CMakeLists.
 */

#include "entropy_app.h"

#include "main.h"               /* CubeMX HAL */
#include "entropy_config.h"
#include "entropy_protocol.h"
#include "serial_stream.h"      /* non-blocking UART TX ring (DMA) */

UART_HandleTypeDef huart3;
ADC_HandleTypeDef  hadc1;
DMA_HandleTypeDef  hdma_usart3_tx;

static uint32_t s_seq = 0;
static uint8_t  s_pkt[ENTROPY_PACKET_MAX_BYTES];
static uint16_t s_samples[PACKET_SAMPLE_COUNT];

/* LD1 green LED = PB0. */
#define LD1_PORT   GPIOB
#define LD1_PIN    GPIO_PIN_0

static void led_init(void)
{
  __HAL_RCC_GPIOB_CLK_ENABLE();
  GPIO_InitTypeDef g = {0};
  g.Pin = LD1_PIN; g.Mode = GPIO_MODE_OUTPUT_PP;
  g.Pull = GPIO_NOPULL; g.Speed = GPIO_SPEED_FREQ_LOW;
  HAL_GPIO_Init(LD1_PORT, &g);
}

/* USART3 on the VCP pins (PD8=TX, PD9=RX, AF7) + its TX DMA stream. */
static void uart3_init(void)
{
  __HAL_RCC_USART3_CLK_ENABLE();
  __HAL_RCC_GPIOD_CLK_ENABLE();
  GPIO_InitTypeDef g = {0};
  g.Pin = GPIO_PIN_8 | GPIO_PIN_9;
  g.Mode = GPIO_MODE_AF_PP; g.Pull = GPIO_PULLUP;
  g.Speed = GPIO_SPEED_FREQ_VERY_HIGH; g.Alternate = GPIO_AF7_USART3;
  HAL_GPIO_Init(GPIOD, &g);

  huart3.Instance = USART3;
  huart3.Init.BaudRate = 921600;
  huart3.Init.WordLength = UART_WORDLENGTH_8B;
  huart3.Init.StopBits = UART_STOPBITS_1;
  huart3.Init.Parity = UART_PARITY_NONE;
  huart3.Init.Mode = UART_MODE_TX_RX;
  huart3.Init.HwFlowCtl = UART_HWCONTROL_NONE;
  huart3.Init.OverSampling = UART_OVERSAMPLING_16;
  huart3.Init.OneBitSampling = UART_ONE_BIT_SAMPLE_DISABLE;
  huart3.Init.ClockPrescaler = UART_PRESCALER_DIV1;
  huart3.AdvancedInit.AdvFeatureInit = UART_ADVFEATURE_NO_INIT;
  if (HAL_UART_Init(&huart3) != HAL_OK) { Error_Handler(); }

  /* TX DMA: DMA1 Stream0, USART3_TX request, mem->periph, one packet per
   * (DMA_NORMAL) transfer. Buffers live in AXI SRAM (see linker), reachable
   * by DMA1. */
  __HAL_RCC_DMA1_CLK_ENABLE();
  hdma_usart3_tx.Instance                 = DMA1_Stream0;
  hdma_usart3_tx.Init.Request             = DMA_REQUEST_USART3_TX;
  hdma_usart3_tx.Init.Direction           = DMA_MEMORY_TO_PERIPH;
  hdma_usart3_tx.Init.PeriphInc           = DMA_PINC_DISABLE;
  hdma_usart3_tx.Init.MemInc              = DMA_MINC_ENABLE;
  hdma_usart3_tx.Init.PeriphDataAlignment = DMA_PDATAALIGN_BYTE;
  hdma_usart3_tx.Init.MemDataAlignment    = DMA_MDATAALIGN_BYTE;
  hdma_usart3_tx.Init.Mode                = DMA_NORMAL;
  hdma_usart3_tx.Init.Priority            = DMA_PRIORITY_LOW;
  hdma_usart3_tx.Init.FIFOMode            = DMA_FIFOMODE_DISABLE;
  if (HAL_DMA_Init(&hdma_usart3_tx) != HAL_OK) { Error_Handler(); }
  __HAL_LINKDMA(&huart3, hdmatx, hdma_usart3_tx);

  /* Completion: DMA-TC IRQ -> UART-TC IRQ -> HAL_UART_TxCpltCallback, so both
   * the DMA stream and the USART3 line IRQs must be enabled. */
  HAL_NVIC_SetPriority(DMA1_Stream0_IRQn, 6, 0);
  HAL_NVIC_EnableIRQ(DMA1_Stream0_IRQn);
  HAL_NVIC_SetPriority(USART3_IRQn, 6, 0);
  HAL_NVIC_EnableIRQ(USART3_IRQn);
}

/* ADC1, single channel on PA3 (ADC1_INP15), continuous free-run, polled.
 * Kernel clock from per_ck (CLKP, = HSI by default), /4 prescale. */
static void adc1_init(void)
{
  RCC_PeriphCLKInitTypeDef pclk = {0};
  pclk.PeriphClockSelection = RCC_PERIPHCLK_ADC;
  pclk.AdcClockSelection    = RCC_ADCCLKSOURCE_CLKP;
  if (HAL_RCCEx_PeriphCLKConfig(&pclk) != HAL_OK) { Error_Handler(); }

  __HAL_RCC_ADC12_CLK_ENABLE();
  __HAL_RCC_GPIOA_CLK_ENABLE();
  GPIO_InitTypeDef g = {0};
  g.Pin = GPIO_PIN_3; g.Mode = GPIO_MODE_ANALOG; g.Pull = GPIO_NOPULL;
  HAL_GPIO_Init(GPIOA, &g);

  hadc1.Instance = ADC1;
  hadc1.Init.ClockPrescaler        = ADC_CLOCK_ASYNC_DIV4;
  hadc1.Init.Resolution            = ADC_RESOLUTION_12B;
  hadc1.Init.ScanConvMode          = ADC_SCAN_DISABLE;
  hadc1.Init.EOCSelection          = ADC_EOC_SINGLE_CONV;
  hadc1.Init.LowPowerAutoWait      = DISABLE;
  hadc1.Init.ContinuousConvMode    = ENABLE;
  hadc1.Init.NbrOfConversion       = 1;
  hadc1.Init.DiscontinuousConvMode = DISABLE;
  hadc1.Init.ExternalTrigConv      = ADC_SOFTWARE_START;
  hadc1.Init.ExternalTrigConvEdge  = ADC_EXTERNALTRIGCONVEDGE_NONE;
  hadc1.Init.ConversionDataManagement = ADC_CONVERSIONDATA_DR;
  hadc1.Init.Overrun               = ADC_OVR_DATA_OVERWRITTEN;
  hadc1.Init.LeftBitShift          = ADC_LEFTBITSHIFT_NONE;
  hadc1.Init.OversamplingMode      = DISABLE;
  if (HAL_ADC_Init(&hadc1) != HAL_OK) { Error_Handler(); }

  if (HAL_ADCEx_Calibration_Start(&hadc1, ADC_CALIB_OFFSET,
                                  ADC_SINGLE_ENDED) != HAL_OK) { Error_Handler(); }

  ADC_ChannelConfTypeDef ch = {0};
  ch.Channel      = ADC_CHANNEL_15;          /* PA3 */
  ch.Rank         = ADC_REGULAR_RANK_1;
  ch.SamplingTime = ADC_SAMPLETIME_64CYCLES_5;
  ch.SingleDiff   = ADC_SINGLE_ENDED;
  ch.OffsetNumber = ADC_OFFSET_NONE;
  ch.Offset       = 0;
  if (HAL_ADC_ConfigChannel(&hadc1, &ch) != HAL_OK) { Error_Handler(); }
}

void entropy_app_init(void)
{
  led_init();
  uart3_init();
  serial_stream_init();
  adc1_init();
}

void entropy_app_task(void)
{
  /* Always drain the TX ring. */
  serial_stream_pump();

  /* Pace production just under the UART ceiling. A 528-byte frame takes
   * ~5.7 ms on the wire at 921600, i.e. ~174 packets/s max; producing at
   * ~140/s (7 ms period) leaves headroom so the ring never overflows and
   * the sequence stays gap-free. A timer-triggered ADC (next stage) sets a
   * precise rate and removes this software pacing. */
  static uint32_t s_last_ms = 0;
  if ((HAL_GetTick() - s_last_ms) < 7U) {
    return;
  }
  s_last_ms = HAL_GetTick();

  HAL_ADC_Start(&hadc1);
  for (size_t i = 0; i < PACKET_SAMPLE_COUNT; ++i) {
    if (HAL_ADC_PollForConversion(&hadc1, 5) == HAL_OK) {
      s_samples[i] = (uint16_t)HAL_ADC_GetValue(&hadc1);
    } else {
      s_samples[i] = 0;
    }
  }
  HAL_ADC_Stop(&hadc1);

  const uint32_t now_us = HAL_GetTick() * 1000U;
  const size_t n = entropy_packet_encode(s_pkt, sizeof s_pkt,
                                         s_seq++, now_us,
                                         s_samples, PACKET_SAMPLE_COUNT);
  if (n > 0) {
    (void)serial_stream_send(s_pkt, n);   /* enqueue; DMA drains it */
  }
  serial_stream_pump();                    /* bootstrap / recover a missed TC */
  HAL_GPIO_TogglePin(LD1_PORT, LD1_PIN);
}

/* ---- ISR plumbing for the TX DMA path ----
 * Override the weak startup defaults. stm32h7xx_it.c doesn't define these
 * (USART3/DMA aren't in the .ioc), so there's no conflict. */
void DMA1_Stream0_IRQHandler(void)
{
  HAL_DMA_IRQHandler(&hdma_usart3_tx);
}

void USART3_IRQHandler(void)
{
  HAL_UART_IRQHandler(&huart3);
}

void HAL_UART_TxCpltCallback(UART_HandleTypeDef *huart)
{
  if (huart->Instance == USART3) {
    serial_stream_on_tx_complete();
  }
}
