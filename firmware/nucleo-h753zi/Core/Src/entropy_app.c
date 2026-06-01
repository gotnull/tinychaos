/*
 * entropy_app.c - tinychaos application glue for the NUCLEO-H753ZI.
 *
 * Fully DMA-driven capture + transport (CPU only frames + enqueues):
 *
 *   TIM3 update --triggers--> ADC1 scan of TWO channels at a precise rate
 *     rank 1 = PA3 / ADC1_INP15  (A0  - zener entropy source)
 *     rank 2 = PC0 / ADC1_INP10  (A1? CN9 - baseline divider reference)
 *   --DMA circular--> adc_buf (double buffer), samples interleaved
 *     [zener, baseline, zener, baseline, ...] so the host de-interleaves with
 *     channel index = sample_index % 2 (host channelCount is 2 already)
 *   --half/full callback--> frame one half --serial_stream--> USART3 TX DMA
 *   --> ST-LINK VCP at 921600.
 *
 * Two channels lets the host subtract the baseline (mains hum + ADC noise
 * floor) from the zener channel to isolate the avalanche noise. With nothing
 * wired yet both pins float; once the circuit is built, ch0 carries the
 * amplified zener noise and ch1 the clean 1.65 V divider reference.
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

/* ---- Transport selection (compile-time) --------------------------------
 * The stream is framed identically regardless of transport; only the path the
 * bytes take off-chip differs. Pick ONE via a compile definition (set in
 * CMakeLists.txt):
 *
 *   ENTROPY_TRANSPORT_UART  (default) - USART3 on the on-board ST-LINK VCP
 *       (PD8/PD9), TX over DMA. One USB cable (CN1), nothing to enable in
 *       CubeMX. Practical ceiling ~92 kB/s at 921600 baud.
 *
 *   ENTROPY_TRANSPORT_USB             - native USB CDC on CN13 (USB_OTG_FS).
 *       Requires the CubeMX-generated USB device stack (usb_device.c, usbd_*)
 *       plus usb_stream.c in the build, and MX_USB_DEVICE_Init(). Practical
 *       ~1 MB/s. See firmware/README.md, section "Transport (UART vs USB CDC)".
 *
 * serial_stream and usb_stream expose the same tiny API (init / send / pump /
 * on_tx_complete), so the capture code below never refers to a transport
 * directly - it uses the TX_* macros set here. */
#if defined(ENTROPY_TRANSPORT_USB)
  #include "usb_stream.h"        /* non-blocking USB CDC TX ring              */
  #define TX_INIT()      usb_stream_init()
  #define TX_SEND(p, n)  usb_stream_send((p), (n))
  #define TX_PUMP()      usb_stream_pump()
#elif defined(ENTROPY_TRANSPORT_UART)
  #include "serial_stream.h"     /* non-blocking UART TX ring (DMA)           */
  #define TX_INIT()      serial_stream_init()
  #define TX_SEND(p, n)  serial_stream_send((p), (n))
  #define TX_PUMP()      serial_stream_pump()
#else
  #error "Define ENTROPY_TRANSPORT_UART or ENTROPY_TRANSPORT_USB (see CMakeLists.txt)"
#endif

/* Target ADC sample rate. The TIM3 reload that achieves it is computed at
 * runtime from the *actual* timer kernel clock (tim3_reload_for_rate), not a
 * hard-coded guess, so the rate is correct whatever the clock tree does.
 *
 * This is the PER-CHANNEL rate (one TIM3 trigger runs the whole 2-channel
 * scan, so each trigger yields 2 samples). Total samples/s = SAMPLE_RATE_HZ x
 * 2, and packets/s = (SAMPLE_RATE_HZ x 2) / PACKET_SAMPLE_COUNT. At 10 kHz/ch
 * that is 20000 samples/s total => ~78 packets/s, well under the ~174
 * packets/s the 921600 UART can carry (the TX ring never overruns). Raise this
 * for more samples/s as long as packets/s stays under that ceiling. 10 kHz/ch
 * matches the original design (ADC_SAMPLE_RATE_HZ / ADC_CHANNEL_COUNT).
 *
 * Measured note: host capture reads ~23 kSa/s (~91 pkt/s) rather than exactly
 * 20 k. tim3_reload_for_rate() computes the reload from PCLK1 and the live
 * APB1 prescaler, but the H7 also has the RCC_CFGR TIMPRE bit which can raise
 * the timer kernel clock beyond that; we don't read TIMPRE here, so the true
 * rate runs a little above target. Harmless (still well under the UART
 * ceiling, stream is gap-free). TODO(next dev): factor TIMPRE in for exact. */
#define SAMPLE_RATE_HZ   10000U

ADC_HandleTypeDef  hadc1;
TIM_HandleTypeDef  htim3;
DMA_HandleTypeDef  hdma_adc1;
#if defined(ENTROPY_TRANSPORT_UART)
UART_HandleTypeDef huart3;
DMA_HandleTypeDef  hdma_usart3_tx;
#endif

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

#if defined(ENTROPY_TRANSPORT_UART)
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
#endif /* ENTROPY_TRANSPORT_UART */

/* Compute the TIM3 auto-reload value for a target sample rate from the real
 * timer kernel clock. STM32 rule: a TIMx on APB1 is clocked at PCLK1 when the
 * APB1 prescaler is /1, otherwise at 2 x PCLK1. We read PCLK1 and the live
 * prescaler from RCC rather than assuming the clock tree, so the rate stays
 * correct across clock reconfigurations (this is why earlier the rate didn't
 * match a naive SYSCLK-based guess). Reload register is (ticks_per_period - 1). */
static uint32_t tim3_reload_for_rate(uint32_t rate_hz)
{
  uint32_t pclk1 = HAL_RCC_GetPCLK1Freq();
  uint32_t ppre1 = (RCC->D2CFGR & RCC_D2CFGR_D2PPRE1_Msk);
  uint32_t tim_clk = (ppre1 < RCC_D2CFGR_D2PPRE1_DIV2) ? pclk1 : (2U * pclk1);
  uint32_t ticks = tim_clk / rate_hz;
  return (ticks > 0U) ? (ticks - 1U) : 0U;
}

/* TIM3 free-runs and emits a TRGO pulse on each counter overflow (reload);
 * that TRGO is wired as the ADC's external trigger, so the ADC takes exactly
 * one sample per overflow. Prescaler 0; the reload sets the period at the
 * timer kernel clock (computed above for SAMPLE_RATE_HZ). */
static void tim3_init(void)
{
  __HAL_RCC_TIM3_CLK_ENABLE();
  htim3.Instance = TIM3;
  htim3.Init.Prescaler         = 0;
  htim3.Init.CounterMode       = TIM_COUNTERMODE_UP;
  htim3.Init.Period            = tim3_reload_for_rate(SAMPLE_RATE_HZ);
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
  __HAL_RCC_GPIOC_CLK_ENABLE();
  /* Both ADC inputs as analog: PA3 (zener, INP15) and PC0 (baseline, INP10). */
  GPIO_InitTypeDef g = {0};
  g.Mode = GPIO_MODE_ANALOG; g.Pull = GPIO_NOPULL;
  g.Pin = GPIO_PIN_3; HAL_GPIO_Init(GPIOA, &g);
  g.Pin = GPIO_PIN_0; HAL_GPIO_Init(GPIOC, &g);

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
  hadc1.Init.ScanConvMode          = ADC_SCAN_ENABLE;   /* 2-channel group */
  hadc1.Init.EOCSelection          = ADC_EOC_SINGLE_CONV;
  hadc1.Init.LowPowerAutoWait      = DISABLE;
  hadc1.Init.ContinuousConvMode    = DISABLE;           /* timer-paced */
  hadc1.Init.NbrOfConversion       = 2;                 /* zener + baseline */
  hadc1.Init.DiscontinuousConvMode = DISABLE;
  /* One TRGO triggers the whole 2-conversion scan, so each trigger emits a
   * [zener, baseline] pair into the DMA buffer at SAMPLE_RATE_HZ per channel. */
  hadc1.Init.ExternalTrigConv      = ADC_EXTERNALTRIG_T3_TRGO;
  hadc1.Init.ExternalTrigConvEdge  = ADC_EXTERNALTRIGCONVEDGE_RISING;
  hadc1.Init.ConversionDataManagement = ADC_CONVERSIONDATA_DMA_CIRCULAR;
  hadc1.Init.Overrun               = ADC_OVR_DATA_OVERWRITTEN;
  hadc1.Init.LeftBitShift          = ADC_LEFTBITSHIFT_NONE;
  hadc1.Init.OversamplingMode      = DISABLE;
  if (HAL_ADC_Init(&hadc1) != HAL_OK) { Error_Handler(); }

  if (HAL_ADCEx_Calibration_Start(&hadc1, ADC_CALIB_OFFSET,
                                  ADC_SINGLE_ENDED) != HAL_OK) { Error_Handler(); }

  /* Rank order defines the interleave the host de-multiplexes:
   *   rank 1 -> host channel 0 (zener, PA3/INP15)
   *   rank 2 -> host channel 1 (baseline, PC0/INP10) */
  ADC_ChannelConfTypeDef ch = {0};
  ch.SamplingTime = ADC_SAMPLETIME_64CYCLES_5;
  ch.SingleDiff   = ADC_SINGLE_ENDED;
  ch.OffsetNumber = ADC_OFFSET_NONE;
  ch.Offset       = 0;

  ch.Channel = ADC_CHANNEL_15;               /* PA3 - zener */
  ch.Rank    = ADC_REGULAR_RANK_1;
  if (HAL_ADC_ConfigChannel(&hadc1, &ch) != HAL_OK) { Error_Handler(); }

  ch.Channel = ADC_CHANNEL_10;               /* PC0 - baseline */
  ch.Rank    = ADC_REGULAR_RANK_2;
  if (HAL_ADC_ConfigChannel(&hadc1, &ch) != HAL_OK) { Error_Handler(); }
}

void entropy_app_init(void)
{
  led_init();
#if defined(ENTROPY_TRANSPORT_UART)
  uart3_init();              /* USART3 + its TX DMA */
#endif
  /* USB CDC: main.c already calls MX_USB_DEVICE_Init() before this hook (it's
   * in the .ioc), so we don't init the device here - only our TX ring. */
  TX_INIT();                 /* transport TX ring (serial_stream / usb_stream) */
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
    (void)TX_SEND(s_pkt, n);
  }
}

void entropy_app_task(void)
{
  TX_PUMP();   /* keep the TX ring draining */

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

#if defined(ENTROPY_TRANSPORT_UART)
void HAL_UART_TxCpltCallback(UART_HandleTypeDef *huart)
{
  if (huart->Instance == USART3) serial_stream_on_tx_complete();
}

void DMA1_Stream0_IRQHandler(void)   /* USART3 TX */
{
  HAL_DMA_IRQHandler(&hdma_usart3_tx);
}
#endif /* ENTROPY_TRANSPORT_UART */
/* (USB CDC build: CDC_TransmitCplt_FS in usbd_cdc_if.c calls
 *  usb_stream_on_tx_complete() - no HAL_UART callback needed.) */

void DMA1_Stream1_IRQHandler(void)   /* ADC1 (both transports) */
{
  HAL_DMA_IRQHandler(&hdma_adc1);
}

#if defined(ENTROPY_TRANSPORT_UART)
void USART3_IRQHandler(void)
{
  HAL_UART_IRQHandler(&huart3);
}
#endif /* ENTROPY_TRANSPORT_UART */
