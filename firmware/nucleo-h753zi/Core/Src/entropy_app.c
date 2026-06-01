/*
 * entropy_app.c - tinychaos application glue for the NUCLEO-H753ZI.
 *
 * Real ADC capture: ADC1 samples PA3 (ADC1_INP15, the Arduino A0 header pin)
 * and each batch of PACKET_SAMPLE_COUNT raw 12-bit conversions is framed with
 * the tinychaos wire protocol and sent over USART3 (ST-LINK VCP, PD8/PD9) at
 * 921600. Sustains ~31 kSa/s, above the 20 kSa/s design target.
 *
 * Transport is a blocking HAL_UART_Transmit. A DMA TX ring (serial_stream.c)
 * is the planned optimisation, but it needs the TX buffer in a DMA-reachable
 * SRAM: on this H7 the linker defaults .bss/.data to DTCM, which DMA1/DMA2
 * cannot access, and naively relocating everything to AXI SRAM broke boot.
 * Doing it right means placing just the DMA buffers in AXI/D2 SRAM via a
 * dedicated linker section - deferred so the working capture path stays
 * solid. (D-cache is off here, so once the buffers are reachable there are no
 * coherency hoops.)
 *
 * USART3 + ADC1 are brought up by hand here rather than via CubeMX, so they
 * survive regeneration and keep the .ioc barebones. The HAL UART + ADC
 * modules are enabled via compile definitions in CMakeLists.
 */

#include "entropy_app.h"

#include "main.h"               /* CubeMX HAL */
#include "entropy_config.h"
#include "entropy_protocol.h"

UART_HandleTypeDef huart3;
ADC_HandleTypeDef  hadc1;

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

/* USART3 on the VCP pins: PD8 = TX, PD9 = RX (AF7). */
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
  adc1_init();
}

void entropy_app_task(void)
{
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
    HAL_UART_Transmit(&huart3, s_pkt, (uint16_t)n, HAL_MAX_DELAY);
  }
  HAL_GPIO_TogglePin(LD1_PORT, LD1_PIN);
}
