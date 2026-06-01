/*
 * entropy_app.c - tinychaos application glue for the NUCLEO-H753ZI.
 *
 * Fully DMA-driven capture + transport (CPU only frames + enqueues):
 *
 *   TIM3 update --triggers--> ADC1 (PA3 / ADC1_INP15) at a precise rate
 *   --DMA circular--> adc_buf (double buffer, 2 x PACKET_SAMPLE_COUNT)
 *   --half/full callback--> frame one half --serial_stream--> USART3 TX DMA
 *   --> ST-LINK VCP at 921600.
 *
 * Nothing busy-waits: the ADC is paced by the timer, both the ADC capture
 * and the UART transmit run on DMA, and the main loop just turns ready
 * half-buffers into framed packets.
 *
 * H7 memory note: DMA1/DMA2 cannot reach DTCM. The linker script places the
 * RAM sections (.bss/.data, incl. adc_buf and the serial_stream ring) in AXI
 * SRAM (0x24000000, D1, reachable by the DMA bus matrix); the stack stays in
 * DTCM (reset SP). D-cache is off, so no cache maintenance is needed.
 *
 * USART3 + ADC1 + TIM3 are configured by hand here (not via CubeMX) so they
 * survive regeneration and keep the .ioc barebones; the HAL UART/ADC/TIM
 * modules are enabled via compile definitions in CMakeLists.
 */

#include "entropy_app.h"

#include "main.h"               /* CubeMX HAL */
#include "entropy_config.h"
#include "entropy_protocol.h"
#include "serial_stream.h"      /* non-blocking UART TX ring (DMA) */

/* TIM3 reload value that paces the ADC. This is a register period, NOT a
 * verified Hz figure: the true rate depends on the APB1 timer-clock, which on
 * this project does NOT equal the naive SYSCLK (measured capture comes out at
 * ~35-40 kSa/s with this value, implying an ~70-80 MHz timer clock - the
 * effective clock has not been pinned down). What matters for correctness:
 *   - the rate sits safely above the 20 kSa/s design target, and
 *   - packets/s (rate / PACKET_SAMPLE_COUNT, ~140-155/s) stays under the
 *     ~174 packets/s the 921600 UART can carry, so the TX ring never overruns
 *     (verified: 0 sequence gaps).
 * TODO(next dev): if a precise sample rate is needed, read the actual APB1
 * timer clock (HAL_RCC_GetPCLK1Freq() x timer-multiplier) at runtime and
 * compute the reload from it instead of this fixed value.
 *
 * One conversion per trigger, PACKET_SAMPLE_COUNT conversions per half-buffer
 * => one packet per half-complete / full-complete DMA callback. */
#define TIM3_RELOAD_PERIOD   2000U

UART_HandleTypeDef huart3;
ADC_HandleTypeDef  hadc1;
TIM_HandleTypeDef  htim3;
DMA_HandleTypeDef  hdma_usart3_tx;
DMA_HandleTypeDef  hdma_adc1;

static uint32_t s_seq = 0;
static uint8_t  s_pkt[ENTROPY_PACKET_MAX_BYTES];

/* ADC double buffer: first half [0..N), second half [N..2N). The DMA fills it
 * circularly; the half-complete callback hands off [0..N) while the controller
 * fills [N..2N), and vice-versa. In AXI SRAM (via .bss), DMA-reachable. */
#define ADC_BUF_SAMPLES   (2U * PACKET_SAMPLE_COUNT)
static uint16_t adc_buf[ADC_BUF_SAMPLES] __attribute__((aligned(32)));
static volatile uint8_t s_half_ready = 0;   /* first half ready  */
static volatile uint8_t s_full_ready = 0;   /* second half ready */

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

  HAL_NVIC_SetPriority(DMA1_Stream0_IRQn, 6, 0);
  HAL_NVIC_EnableIRQ(DMA1_Stream0_IRQn);
  HAL_NVIC_SetPriority(USART3_IRQn, 6, 0);
  HAL_NVIC_EnableIRQ(USART3_IRQn);
}

/* TIM3 free-runs and emits a TRGO pulse on each counter overflow (reload);
 * that TRGO is wired as the ADC's external trigger, so the ADC takes exactly
 * one sample per overflow. Prescaler 0 + reload = TIM3_RELOAD_PERIOD-1 sets
 * the period (see the TIM3_RELOAD_PERIOD note for the rate caveat). */
static void tim3_init(void)
{
  __HAL_RCC_TIM3_CLK_ENABLE();
  htim3.Instance = TIM3;
  htim3.Init.Prescaler         = 0;
  htim3.Init.CounterMode       = TIM_COUNTERMODE_UP;
  htim3.Init.Period            = TIM3_RELOAD_PERIOD - 1U;
  htim3.Init.ClockDivision     = TIM_CLOCKDIVISION_DIV1;
  htim3.Init.AutoReloadPreload = TIM_AUTORELOAD_PRELOAD_DISABLE;
  if (HAL_TIM_Base_Init(&htim3) != HAL_OK) { Error_Handler(); }

  TIM_MasterConfigTypeDef m = {0};
  m.MasterOutputTrigger = TIM_TRGO_UPDATE;     /* update -> TRGO -> ADC */
  m.MasterSlaveMode     = TIM_MASTERSLAVEMODE_DISABLE;
  if (HAL_TIMEx_MasterConfigSynchronization(&htim3, &m) != HAL_OK) { Error_Handler(); }
}

/* ADC1 on PA3 (ADC1_INP15), triggered by TIM3 TRGO, DMA circular into
 * adc_buf. Kernel clock from per_ck (CLKP = HSI), /4 prescale. */
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

  /* ADC DMA: DMA1 Stream1, ADC1 request, circular, half-word. */
  hdma_adc1.Instance                 = DMA1_Stream1;
  hdma_adc1.Init.Request             = DMA_REQUEST_ADC1;
  hdma_adc1.Init.Direction           = DMA_PERIPH_TO_MEMORY;
  hdma_adc1.Init.PeriphInc           = DMA_PINC_DISABLE;
  hdma_adc1.Init.MemInc              = DMA_MINC_ENABLE;
  hdma_adc1.Init.PeriphDataAlignment = DMA_PDATAALIGN_HALFWORD;
  hdma_adc1.Init.MemDataAlignment    = DMA_MDATAALIGN_HALFWORD;
  hdma_adc1.Init.Mode                = DMA_CIRCULAR;
  hdma_adc1.Init.Priority            = DMA_PRIORITY_HIGH;
  hdma_adc1.Init.FIFOMode            = DMA_FIFOMODE_DISABLE;
  if (HAL_DMA_Init(&hdma_adc1) != HAL_OK) { Error_Handler(); }
  __HAL_LINKDMA(&hadc1, DMA_Handle, hdma_adc1);
  HAL_NVIC_SetPriority(DMA1_Stream1_IRQn, 5, 0);
  HAL_NVIC_EnableIRQ(DMA1_Stream1_IRQn);

  hadc1.Instance = ADC1;
  hadc1.Init.ClockPrescaler        = ADC_CLOCK_ASYNC_DIV4;
  hadc1.Init.Resolution            = ADC_RESOLUTION_12B;
  hadc1.Init.ScanConvMode          = ADC_SCAN_DISABLE;
  hadc1.Init.EOCSelection          = ADC_EOC_SINGLE_CONV;
  hadc1.Init.LowPowerAutoWait      = DISABLE;
  hadc1.Init.ContinuousConvMode    = DISABLE;          /* timer-paced */
  hadc1.Init.NbrOfConversion       = 1;
  hadc1.Init.DiscontinuousConvMode = DISABLE;
  hadc1.Init.ExternalTrigConv      = ADC_EXTERNALTRIG_T3_TRGO;
  hadc1.Init.ExternalTrigConvEdge  = ADC_EXTERNALTRIGCONVEDGE_RISING;
  hadc1.Init.ConversionDataManagement = ADC_CONVERSIONDATA_DMA_CIRCULAR;
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
  tim3_init();
  adc1_init();

  /* Start DMA capture, then the timer that paces it. */
  if (HAL_ADC_Start_DMA(&hadc1, (uint32_t *)adc_buf, ADC_BUF_SAMPLES) != HAL_OK) {
    Error_Handler();
  }
  if (HAL_TIM_Base_Start(&htim3) != HAL_OK) { Error_Handler(); }
}

/* Frame one half-buffer of samples and hand it to the TX ring. */
static void emit_half(const uint16_t *half)
{
  const uint32_t now_us = HAL_GetTick() * 1000U;
  const size_t n = entropy_packet_encode(s_pkt, sizeof s_pkt,
                                         s_seq++, now_us,
                                         half, PACKET_SAMPLE_COUNT);
  if (n > 0) {
    (void)serial_stream_send(s_pkt, n);
  }
}

void entropy_app_task(void)
{
  serial_stream_pump();   /* keep the TX ring draining */

  if (s_half_ready) {
    s_half_ready = 0;
    emit_half(&adc_buf[0]);
    HAL_GPIO_TogglePin(LD1_PORT, LD1_PIN);
  }
  if (s_full_ready) {
    s_full_ready = 0;
    emit_half(&adc_buf[PACKET_SAMPLE_COUNT]);
  }
}

/* ---- DMA / UART completion callbacks + ISR plumbing ----
 * These override the weak startup defaults; stm32h7xx_it.c doesn't define
 * them (the peripherals aren't in the .ioc), so there's no conflict. */
void HAL_ADC_ConvHalfCpltCallback(ADC_HandleTypeDef *hadc)
{
  if (hadc->Instance == ADC1) s_half_ready = 1;
}

void HAL_ADC_ConvCpltCallback(ADC_HandleTypeDef *hadc)
{
  if (hadc->Instance == ADC1) s_full_ready = 1;
}

void HAL_UART_TxCpltCallback(UART_HandleTypeDef *huart)
{
  if (huart->Instance == USART3) serial_stream_on_tx_complete();
}

void DMA1_Stream0_IRQHandler(void)   /* USART3 TX */
{
  HAL_DMA_IRQHandler(&hdma_usart3_tx);
}

void DMA1_Stream1_IRQHandler(void)   /* ADC1 */
{
  HAL_DMA_IRQHandler(&hdma_adc1);
}

void USART3_IRQHandler(void)
{
  HAL_UART_IRQHandler(&huart3);
}
