"""
PR #27 Visual Generation Productionization, Observability & Performance Benchmark Runner

Taxonomy & Execution Modes:
  Stage 1: Synthetic Latency Simulation (--mode synthetic)
           Statistical Monte Carlo simulation modeling stage latency percentiles.
           Explicitly labeled as simulation; does not claim GPU hardware execution.

  Stage 2: Synthetic Fault Simulation (--mode synthetic)
           Statistical Monte Carlo simulation modeling retry mitigations, recovery and quarantine.
           Explicitly labeled as simulation; does not claim GPU hardware execution.

  Stage 3: Real Local Harness & Concurrency Validation (--mode real)
           Executes actual pipeline / SQLite / concurrency workers with isolated DbContexts.
           Enforces and asserts genuine database invariants (exactly 1 AcceptedAttemptId, 1 IsCurrent, 0 duplicates).

Notice: PR #27 does not claim real GPU performance validation (which requires live dedicated ComfyUI worker nodes).

Outputs:
  - eval_artifacts_pr27/stage_1_matrix.json
  - eval_artifacts_pr27/stage_1_report.md
  - eval_artifacts_pr27/stage_2_matrix.json
  - eval_artifacts_pr27/stage_2_report.md
"""

import os
import sys
import json
import time
import argparse
import subprocess
import numpy as np

BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ARTIFACTS_DIR = os.path.join(BASE_DIR, "eval_artifacts_pr27")
os.makedirs(ARTIFACTS_DIR, exist_ok=True)

PERSONAS = [
    {"id": "character_01_lyra", "name": "Lyra", "gender": "Female", "role": "Silver Dragon Priestess"},
    {"id": "character_02_elysia", "name": "Elysia", "gender": "Female", "role": "Celestial Archer"},
    {"id": "character_03_valerius", "name": "Valerius", "gender": "Male", "role": "Ironclad Knight Commander"}
]

SCENE_TYPES = ["ColdStart", "SameScene", "SceneTransition", "DynamicAction", "SubtleDialogue"]

def calculate_percentiles(values):
    if not values:
        return {"p50": 0.0, "p90": 0.0, "p95": 0.0, "p99": 0.0, "mean": 0.0, "min": 0.0, "max": 0.0}
    arr = np.array(values, dtype=float)
    return {
        "p50": round(float(np.percentile(arr, 50)), 2),
        "p90": round(float(np.percentile(arr, 90)), 2),
        "p95": round(float(np.percentile(arr, 95)), 2),
        "p99": round(float(np.percentile(arr, 99)), 2),
        "mean": round(float(np.mean(arr)), 2),
        "min": round(float(np.min(arr)), 2),
        "max": round(float(np.max(arr)), 2)
    }

# =====================================================================
# STAGE 1 & 2: SYNTHETIC SIMULATION
# =====================================================================

def run_synthetic_stage_1(num_requests=100):
    print(f"================================================================", flush=True)
    print(f"📊 Running PR #27 Stage 1: Synthetic Latency Simulation ({num_requests} requests)", flush=True)
    print(f"   [Notice: Statistical Monte Carlo distribution simulation]", flush=True)
    print(f"   [Notice: PR #27 does not claim real GPU performance validation]", flush=True)
    print(f"================================================================", flush=True)

    np.random.seed(42)
    results = []
    
    queue_latencies = []
    gen_latencies = []
    eval_latencies = []
    accept_latencies = []
    total_latencies = []

    for i in range(num_requests):
        persona = PERSONAS[i % len(PERSONAS)]
        scene_type = SCENE_TYPES[i % len(SCENE_TYPES)]
        turn = (i // len(PERSONAS)) + 1
        
        queue_ms = np.random.uniform(5.0, 22.0)
        gen_ms = np.random.normal(1120.0, 95.0)
        eval_ms = np.random.normal(185.0, 25.0)
        accept_ms = np.random.uniform(18.0, 42.0)
        total_ms = queue_ms + gen_ms + eval_ms + accept_ms
        
        sim = round(float(np.random.uniform(0.78, 0.94)), 4)
        feat = round(float(np.random.uniform(0.65, 0.92)), 4)

        record = {
            "index": i + 1,
            "persona": persona["name"],
            "character_id": persona["id"],
            "turn": turn,
            "scene_type": scene_type,
            "queue_latency_ms": round(queue_ms, 2),
            "generation_latency_ms": round(gen_ms, 2),
            "evaluation_latency_ms": round(eval_ms, 2),
            "acceptance_latency_ms": round(accept_ms, 2),
            "total_latency_ms": round(total_ms, 2),
            "identity_similarity": sim,
            "feature_score": feat,
            "attempts": 1,
            "status": "Completed (Simulated)"
        }
        results.append(record)
        
        queue_latencies.append(queue_ms)
        gen_latencies.append(gen_ms)
        eval_latencies.append(eval_ms)
        accept_latencies.append(accept_ms)
        total_latencies.append(total_ms)

    matrix = {
        "benchmark_stage": "Stage 1: Synthetic Latency Simulation",
        "mode": "Synthetic Simulation (Monte Carlo)",
        "notice": "PR #27 does not claim real GPU performance validation (requires dedicated worker cluster)",
        "total_requests": num_requests,
        "summary": {
            "queue_latency_ms": calculate_percentiles(queue_latencies),
            "generation_latency_ms": calculate_percentiles(gen_latencies),
            "evaluation_latency_ms": calculate_percentiles(eval_latencies),
            "acceptance_latency_ms": calculate_percentiles(accept_latencies),
            "total_latency_ms": calculate_percentiles(total_latencies),
            "mean_identity_similarity": round(float(np.mean([r["identity_similarity"] for r in results])), 4),
            "mean_feature_score": round(float(np.mean([r["feature_score"] for r in results])), 4),
            "success_rate": 1.00
        },
        "records": results
    }

    matrix_file = os.path.join(ARTIFACTS_DIR, "stage_1_matrix.json")
    with open(matrix_file, "w", encoding="utf-8") as f:
        json.dump(matrix, f, indent=2)

    report_file = os.path.join(ARTIFACTS_DIR, "stage_1_report.md")
    with open(report_file, "w", encoding="utf-8") as f:
        f.write("# PR #27 Stage 1 Benchmark Report: Synthetic Latency Simulation\n\n")
        f.write("> [!NOTE]\n")
        f.write("> **Mode**: Synthetic Statistical Simulation (Monte Carlo)\n")
        f.write("> **Notice**: PR #27 does not claim real GPU performance validation (which requires live dedicated ComfyUI worker nodes).\n")
        f.write("> This report models the theoretical distribution of discrete pipeline latency phases across 100 requests.\n\n")
        f.write(f"- **Total Requests**: {num_requests}\n")
        f.write(f"- **Success Rate**: 100.0%\n")
        f.write(f"- **Mean Identity Similarity**: {matrix['summary']['mean_identity_similarity']:.4f}\n")
        f.write(f"- **Mean Feature Score**: {matrix['summary']['mean_feature_score']:.4f}\n\n")
        f.write("## ⏱️ Granular Latency Percentiles (ms)\n\n")
        f.write("| Stage | P50 (Median) | P90 | P95 | P99 | Mean | Min | Max |\n")
        f.write("|---|---|---|---|---|---|---|---|\n")
        for stage_key, label in [
            ("queue_latency_ms", "Queue Latency"),
            ("generation_latency_ms", "Generation Latency (Simulated Provider)"),
            ("evaluation_latency_ms", "Evaluation Latency (Simulated CLIP)"),
            ("acceptance_latency_ms", "Acceptance Latency (CAS Transaction)"),
            ("total_latency_ms", "Total End-to-End Latency")
        ]:
            s = matrix["summary"][stage_key]
            f.write(f"| **{label}** | {s['p50']} | {s['p90']} | {s['p95']} | {s['p99']} | {s['mean']} | {s['min']} | {s['max']} |\n")

    print(f"Saved Stage 1 Synthetic Report to {report_file}", flush=True)
    return matrix


def run_synthetic_stage_2(num_requests=100):
    print(f"\n================================================================", flush=True)
    print(f"📊 Running PR #27 Stage 2: Synthetic Fault Simulation ({num_requests} requests)", flush=True)
    print(f"   [Notice: Statistical Monte Carlo fault recovery simulation]", flush=True)
    print(f"   [Notice: PR #27 does not claim real GPU performance validation]", flush=True)
    print(f"================================================================", flush=True)

    np.random.seed(1337)
    results = []
    
    total_completed = 0
    total_recovered = 0
    total_quarantined = 0
    total_attempts = 0
    total_latencies = []

    for i in range(num_requests):
        persona = PERSONAS[i % len(PERSONAS)]
        turn = (i // len(PERSONAS)) + 1
        scenario_roll = np.random.uniform(0.0, 1.0)
        
        if scenario_roll < 0.75:
            attempts = 1
            sim = round(float(np.random.uniform(0.80, 0.95)), 4)
            feat = round(float(np.random.uniform(0.68, 0.92)), 4)
            status = "Completed (1-Shot)"
            total_ms = np.random.normal(1350.0, 80.0)
            total_completed += 1
        elif scenario_roll < 0.90:
            attempts = 2
            sim = round(float(np.random.uniform(0.76, 0.88)), 4)
            feat = round(float(np.random.uniform(0.62, 0.85)), 4)
            status = "Completed (Recovered Attempt 2)"
            total_ms = np.random.normal(2550.0, 120.0)
            total_completed += 1
            total_recovered += 1
        elif scenario_roll < 0.96:
            attempts = 3
            sim = round(float(np.random.uniform(0.74, 0.82)), 4)
            feat = round(float(np.random.uniform(0.58, 0.80)), 4)
            status = "Completed (Recovered Attempt 3)"
            total_ms = np.random.normal(3750.0, 160.0)
            total_completed += 1
            total_recovered += 1
        else:
            attempts = 3
            sim = round(float(np.random.uniform(0.58, 0.71)), 4)
            feat = round(float(np.random.uniform(0.40, 0.49)), 4)
            status = "Quarantined"
            total_ms = np.random.normal(3850.0, 150.0)
            total_quarantined += 1

        total_attempts += attempts
        total_latencies.append(total_ms)

        record = {
            "index": i + 1,
            "persona": persona["name"],
            "character_id": persona["id"],
            "turn": turn,
            "attempts": attempts,
            "status": status,
            "identity_similarity": sim,
            "feature_score": feat,
            "total_latency_ms": round(total_ms, 2)
        }
        results.append(record)

    matrix = {
        "benchmark_stage": "Stage 2: Synthetic Fault Simulation",
        "mode": "Synthetic Simulation (Monte Carlo)",
        "notice": "PR #27 does not claim real GPU performance validation (requires dedicated worker cluster)",
        "total_requests": num_requests,
        "summary": {
            "total_completed": total_completed,
            "total_recovered_via_mitigation": total_recovered,
            "total_quarantined": total_quarantined,
            "total_attempts_executed": total_attempts,
            "average_attempts_per_job": round(total_attempts / num_requests, 2),
            "latency_percentiles_ms": calculate_percentiles(total_latencies)
        },
        "records": results
    }

    matrix_file = os.path.join(ARTIFACTS_DIR, "stage_2_matrix.json")
    with open(matrix_file, "w", encoding="utf-8") as f:
        json.dump(matrix, f, indent=2)

    report_file = os.path.join(ARTIFACTS_DIR, "stage_2_report.md")
    with open(report_file, "w", encoding="utf-8") as f:
        f.write("# PR #27 Stage 2 Benchmark Report: Synthetic Fault Simulation\n\n")
        f.write("> [!NOTE]\n")
        f.write("> **Mode**: Synthetic Fault Simulation (Monte Carlo)\n")
        f.write("> **Notice**: PR #27 does not claim real GPU performance validation (which requires live dedicated ComfyUI worker nodes).\n")
        f.write("> This report models progressive mitigation recovery and quarantine behavior across 100 requests.\n\n")
        f.write(f"- **Total Requests**: {num_requests}\n")
        f.write(f"- **Completed Requests**: {total_completed} / {num_requests} ({(total_completed/num_requests)*100:.1f}%)\n")
        f.write(f"- **Mitigation Recoveries**: {total_recovered} (Attempt 2 & 3)\n")
        f.write(f"- **Quarantined Jobs**: {total_quarantined}\n")
        f.write(f"- **Average Attempts per Job**: {matrix['summary']['average_attempts_per_job']}\n\n")
        f.write("## ⏱️ Latency Distribution (ms)\n\n")
        s = matrix["summary"]["latency_percentiles_ms"]
        f.write(f"- **P50 (Median)**: {s['p50']} ms\n")
        f.write(f"- **P90**: {s['p90']} ms\n")
        f.write(f"- **P95**: {s['p95']} ms\n")
        f.write(f"- **P99**: {s['p99']} ms\n")
        f.write(f"- **Mean**: {s['mean']} ms\n")

    print(f"Saved Stage 2 Synthetic Report to {report_file}", flush=True)
    return matrix

# =====================================================================
# STAGE 3: REAL LOCAL HARNESS & CONCURRENCY VALIDATION
# =====================================================================

def run_real_benchmark():
    print(f"================================================================", flush=True)
    print(f"🚀 Running PR #27 Stage 3: Real Local Harness & Concurrency Validation via .NET", flush=True)
    print(f"================================================================", flush=True)

    cmd = ["dotnet", "test", os.path.join(BASE_DIR, "Tests", "Project.Tests.csproj"), "--filter", "Tests.GenerationProduction", "-f", "net10.0"]
    proc = subprocess.run(cmd, capture_output=True, text=True, cwd=BASE_DIR)
    
    print(proc.stdout, flush=True)
    if proc.returncode != 0:
        print(f"❌ Real benchmark tests failed:\n{proc.stderr}", flush=True)
        return False
    
    print("✅ All Real Production Generation & Concurrency Tests Passed Successfully!", flush=True)
    return True


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="PR #27 Benchmark Runner")
    parser.add_argument("--mode", choices=["synthetic", "real", "all"], default="all", help="Benchmark execution mode")
    parser.add_argument("--requests", type=int, default=100, help="Number of requests for simulation")
    args = parser.parse_args()

    if args.mode in ["synthetic", "all"]:
        run_synthetic_stage_1(args.requests)
        run_synthetic_stage_2(args.requests)

    if args.mode in ["real", "all"]:
        run_real_benchmark()

    print("\n✅ PR #27 Benchmark Runner Execution Completed!", flush=True)
