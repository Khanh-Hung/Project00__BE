# PR #27 Stage 1 Benchmark Report: Synthetic Latency Simulation

> [!NOTE]
> **Mode**: Synthetic Statistical Simulation (Monte Carlo)
> **Notice**: PR #27 does not claim real GPU performance validation (which requires live dedicated ComfyUI worker nodes).
> This report models the theoretical distribution of discrete pipeline latency phases across 100 requests.

- **Total Requests**: 100
- **Success Rate**: 100.0%
- **Mean Identity Similarity**: 0.8633
- **Mean Feature Score**: 0.7874

## ⏱️ Granular Latency Percentiles (ms)

| Stage | P50 (Median) | P90 | P95 | P99 | Mean | Min | Max |
|---|---|---|---|---|---|---|---|
| **Queue Latency** | 13.41 | 20.4 | 21.35 | 21.84 | 13.17 | 5.09 | 21.94 |
| **Generation Latency (Simulated Provider)** | 1111.34 | 1257.04 | 1268.25 | 1347.33 | 1121.89 | 926.48 | 1383.94 |
| **Evaluation Latency (Simulated CLIP)** | 189.79 | 222.15 | 231.09 | 237.84 | 191.14 | 123.58 | 243.7 |
| **Acceptance Latency (CAS Transaction)** | 30.44 | 39.56 | 40.74 | 41.68 | 30.21 | 18.22 | 41.83 |
| **Total End-to-End Latency** | 1355.03 | 1497.45 | 1517.9 | 1626.98 | 1356.41 | 1123.7 | 1631.52 |
