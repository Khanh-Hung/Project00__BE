"""
=============================================================================
Project00 — Live GPU Dual-Reference Continuity Benchmark Runner (PR #11)
Executes:
  - Case A: Identity Anchor Only
  - Case B: Identity Anchor + Scene Reference
  - Case C: Identity + Scene + Dynamic Outfit Change (White -> Black Dress)
  - Case D: Identity + Scene + Spatial Position Shift (Sofa -> Window)
  - Case E: Full 8-Turn End-to-End Continuity Chained Generation (T1 -> T8)

Saves all images and produces an empirical verification matrix report.
=============================================================================
"""

import os
import sys
import json
import time
import urllib.request
import urllib.error
import base64

SERVER_URL = "https://internal-jar-jun-meets.trycloudflare.com/generate"
OUTPUT_DIR = os.path.join(os.path.dirname(__file__), "benchmark_outputs")
os.makedirs(OUTPUT_DIR, exist_ok=True)

CANONICAL_IDENTITY_URL = "https://files.catbox.moe/g2343q.png" # Elysia canonical sample

BENCHMARK_TURNS = [
    {
        "turn": 1,
        "name": "T1_Sofa_WhiteDress_Smile",
        "prompt": "masterpiece, best quality, 1girl, Elysia, long platinum blonde hair, emerald green eyes, delicate porcelain face, gentle smile, wearing White Dress, sitting at Sofa, in Living Room, Daytime, cinematic lighting, detailed background",
        "has_previous_scene": False
    },
    {
        "turn": 2,
        "name": "T2_Window_WhiteDress_Standing",
        "prompt": "masterpiece, best quality, 1girl, Elysia, long platinum blonde hair, emerald green eyes, delicate porcelain face, walking toward window, standing, wearing White Dress, at Beside Window, in Living Room, Daytime, cinematic lighting, detailed background",
        "has_previous_scene": True
    },
    {
        "turn": 3,
        "name": "T3_Window_WhiteDress_LookingAtUser",
        "prompt": "masterpiece, best quality, 1girl, Elysia, long platinum blonde hair, emerald green eyes, delicate porcelain face, looking at user, affectionate smile, wearing White Dress, at Beside Window, in Living Room, Daytime, cinematic lighting, detailed background",
        "has_previous_scene": True
    },
    {
        "turn": 4,
        "name": "T4_Window_BlackDress_Standing",
        "prompt": "masterpiece, best quality, 1girl, Elysia, long platinum blonde hair, emerald green eyes, delicate porcelain face, elegant pose, wearing Black Evening Gown, at Beside Window, in Living Room, Night, cinematic lighting, detailed background",
        "has_previous_scene": True
    },
    {
        "turn": 5,
        "name": "T5_Window_BlackDress_Sitting",
        "prompt": "masterpiece, best quality, 1girl, Elysia, long platinum blonde hair, emerald green eyes, delicate porcelain face, sitting beside window, gazing outside, wearing Black Evening Gown, at Beside Window, in Living Room, Night, cinematic lighting, detailed background",
        "has_previous_scene": True
    },
    {
        "turn": 6,
        "name": "T6_Sofa_BlackDress_Sitting",
        "prompt": "masterpiece, best quality, 1girl, Elysia, long platinum blonde hair, emerald green eyes, delicate porcelain face, sitting on sofa, relaxed posture, wearing Black Evening Gown, at Sofa, in Living Room, Night, cinematic lighting, detailed background",
        "has_previous_scene": True
    },
    {
        "turn": 7,
        "name": "T7_Sofa_BlackDress_HoldingTea",
        "prompt": "masterpiece, best quality, 1girl, Elysia, long platinum blonde hair, emerald green eyes, delicate porcelain face, holding Porcelain Tea Cup, sipping warm tea, wearing Black Evening Gown, at Sofa, in Living Room, Night, cinematic lighting, detailed background",
        "has_previous_scene": True
    },
    {
        "turn": 8,
        "name": "T8_Sofa_BlackDress_NoTea",
        "prompt": "masterpiece, best quality, 1girl, Elysia, long platinum blonde hair, emerald green eyes, delicate porcelain face, resting hands in lap, peaceful expression, wearing Black Evening Gown, at Sofa, in Living Room, Night, cinematic lighting, detailed background",
        "has_previous_scene": True
    }
]

def generate_image(payload: dict) -> tuple[str, float]:
    """Sends generation request to GPU server and returns (image_data_or_url, latency_sec)."""
    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        SERVER_URL,
        data=data,
        headers={"Content-Type": "application/json", "User-Agent": "Project00-Benchmark/1.0"}
    )
    start_time = time.time()
    try:
        with urllib.request.urlopen(req, timeout=90) as response:
            latency = time.time() - start_time
            res_body = json.loads(response.read().decode("utf-8"))
            img = res_body.get("image") or res_body.get("url") or res_body.get("image_url") or ""
            return img, latency
    except Exception as e:
        latency = time.time() - start_time
        print(f"  [ERROR] Generation failed after {latency:.2f}s: {e}")
        return "", latency

def save_image_artifact(img_str: str, file_path: str):
    """Saves image whether it is Base64 data URL or HTTP URL."""
    try:
        if img_str.startswith("data:image"):
            base64_data = img_str.split(",")[1]
            with open(file_path, "wb") as f:
                f.write(base64.b64decode(base64_data))
        elif img_str.startswith("http"):
            req = urllib.request.Request(img_str, headers={"User-Agent": "Mozilla/5.0"})
            with urllib.request.urlopen(req, timeout=30) as r, open(file_path, "wb") as f:
                f.write(r.read())
        elif len(img_str) > 100:
            with open(file_path, "wb") as f:
                f.write(base64.b64decode(img_str))
    except Exception as ex:
        print(f"  [WARN] Failed to save artifact {file_path}: {ex}")

def run_benchmark():
    print("=" * 80)
    print("🚀 STARTING LIVE GPU DUAL-REFERENCE CONTINUITY BENCHMARK MATRIX")
    print(f"Target GPU Server: {SERVER_URL}")
    print(f"Output Directory:  {OUTPUT_DIR}")
    print("=" * 80)

    results = []
    last_generated_image = None
    seed_base = 424242

    for item in BENCHMARK_TURNS:
        turn = item["turn"]
        name = item["name"]
        prompt = item["prompt"]
        has_prev = item["has_previous_scene"]
        
        prev_img = last_generated_image if has_prev else None
        
        payload = {
            "prompt": prompt,
            "negative_prompt": "lowres, bad anatomy, bad hands, text, error, missing fingers, extra digit, fewer digits, cropped, worst quality, low quality, normal quality, jpeg artifacts, signature, watermark, username, blurry, artist name",
            "width": 1024,
            "height": 1024,
            "num_inference_steps": 25,
            "guidance_scale": 7.0,
            "seed": seed_base + turn,
            "reference_image": CANONICAL_IDENTITY_URL,
            "previous_scene_image": prev_img
        }

        print(f"\n▶ Executing Turn {turn}/8: {name}")
        print(f"  - Identity Anchor: {CANONICAL_IDENTITY_URL}")
        print(f"  - Previous Scene:  {'Linked (Frame ' + str(turn-1) + ')' if prev_img else 'None (First Frame)'}")
        print(f"  - Seed:            {payload['seed']}")

        img_output, latency = generate_image(payload)
        status = "SUCCESS" if img_output else "FAILED"

        out_path = os.path.join(OUTPUT_DIR, f"{name}.png")
        if img_output:
            save_image_artifact(img_output, out_path)
            last_generated_image = img_output
            print(f"  ✓ Completed in {latency:.2f}s -> Saved: {out_path}")
        else:
            print(f"  ✗ Failed in {latency:.2f}s")

        results.append({
            "turn": turn,
            "name": name,
            "status": status,
            "latency_sec": round(latency, 2),
            "seed": payload["seed"],
            "has_identity_ref": True,
            "has_scene_ref": bool(prev_img),
            "saved_file": out_path if img_output else None
        })

    # Save summary report
    report_path = os.path.join(OUTPUT_DIR, "benchmark_report.json")
    with open(report_path, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=2)

    print("\n" + "=" * 80)
    print("📊 BENCHMARK EXECUTION SUMMARY:")
    print("=" * 80)
    for r in results:
        scene_flag = "Chain-Linked [N-1]" if r["has_scene_ref"] else "Root Anchor"
        print(f" Turn {r['turn']} [{r['name']}]: {r['status']} ({r['latency_sec']}s) | Scene: {scene_flag} | Seed: {r['seed']}")
    print("=" * 80)
    print(f"Report JSON written to: {report_path}")

if __name__ == "__main__":
    run_benchmark()
